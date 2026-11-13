using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace pxdUninstaller.Core;

public sealed record RunResult(bool Started, int ExitCode, bool TimedOut, string? Error)
{
    /// <summary>Windows Installer and most setups: 0 = done, 3010/1641 = done, restart needed, 1605 = wasn't installed.</summary>
    public bool Ok => Started && !TimedOut && ExitCode is 0 or 3010 or 1641 or 1605;
    public bool RestartNeeded => ExitCode is 3010 or 1641;
    public bool Cancelled => ExitCode is 1602 or 1223;
}

/// <summary>
/// Runs an uninstaller and waits for all of it: the process and every process it starts (NSIS copies itself to Temp
/// and exits at once, for example), tracked with a job object.
/// </summary>
public static class Runner
{
    /// <summary>
    /// Runs <paramref name="commandLine"/>. <paramref name="hidden"/> hides console windows (quiet runs).
    /// <paramref name="finished"/> lets the wait end early once the first process has exited and the program is
    /// gone, so a "sorry to see you go" browser window the uninstaller opened doesn't keep us waiting.
    /// </summary>
    public static async Task<RunResult> RunAsync(string commandLine, bool hidden, TimeSpan timeout, Func<bool>? finished, CancellationToken ct)
    {
        return await Task.Run(() => Run(commandLine, hidden, timeout, finished, ct), ct).ConfigureAwait(false);
    }

    private static RunResult Run(string commandLine, bool hidden, TimeSpan timeout, Func<bool>? finished, CancellationToken ct)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        if (hidden)
        {
            si.dwFlags = 0x1;   // STARTF_USESHOWWINDOW
            si.wShowWindow = 0; // SW_HIDE
        }

        const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
        var flags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | (hidden ? CREATE_NO_WINDOW : 0);
        var cmd = new StringBuilder(commandLine, Math.Max(commandLine.Length + 1, 32768));
        if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, null, ref si, out var pi))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 740) return RunElevated(commandLine, hidden, timeout, ct);   // the uninstaller asks for administrator rights
            return new RunResult(false, -1, false, new Win32Exception(err).Message);
        }

        var job = CreateJobObjectW(IntPtr.Zero, null);
        var inJob = job != IntPtr.Zero && AssignProcessToJobObject(job, pi.hProcess);
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);

        try
        {
            var watch = Stopwatch.StartNew();
            TimeSpan? goneSince = null;
            while (true)
            {
                var mainExited = WaitForSingleObject(pi.hProcess, 250) == 0;
                if (mainExited)
                {
                    if (!inJob || ActiveProcesses(job) == 0) break;
                    if (finished is not null && finished())
                    {
                        goneSince ??= watch.Elapsed;
                        if (watch.Elapsed - goneSince > TimeSpan.FromSeconds(3)) break;
                    }
                    else
                    {
                        goneSince = null;
                    }
                }

                if (ct.IsCancellationRequested || watch.Elapsed > timeout)
                {
                    GetExitCodeProcess(pi.hProcess, out var c);
                    return new RunResult(true, (int)c, true, ct.IsCancellationRequested ? "stopped" : "timed out");
                }
            }

            GetExitCodeProcess(pi.hProcess, out var code);
            return new RunResult(true, (int)code, false, null);
        }
        finally
        {
            CloseHandle(pi.hProcess);
            if (job != IntPtr.Zero) CloseHandle(job);
        }
    }

    private static RunResult RunElevated(string commandLine, bool hidden, TimeSpan timeout, CancellationToken ct)
    {
        var (exe, args) = Text.SplitCommand(commandLine);
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true, Verb = "runas",
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            });
            if (p is null) return new RunResult(false, -1, false, "didn't start");
            var watch = Stopwatch.StartNew();
            while (!p.WaitForExit(250))
                if (ct.IsCancellationRequested || watch.Elapsed > timeout)
                    return new RunResult(true, -1, true, "timed out");
            return new RunResult(true, p.ExitCode, false, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new RunResult(false, 1223, false, "the administrator prompt was cancelled");
        }
        catch (Exception ex)
        {
            return new RunResult(false, -1, false, ex.Message);
        }
    }

    /// <summary>Runs a Windows tool (sc, schtasks, powershell) hidden and returns its exit code and output.</summary>
    public static (int Code, string Output) Tool(string exe, string args, int timeoutMs = 60_000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "timed out");
            }

            return (p.ExitCode, output.Result + error.Result);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static int ActiveProcesses(IntPtr job)
    {
        var info = new JOBOBJECT_BASIC_ACCOUNTING_INFORMATION();
        return QueryInformationJobObject(job, 1, ref info, Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), out _) ? (int)info.ActiveProcesses : 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
