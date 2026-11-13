using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace pxdUninstaller.Core;

/// <summary>A program window that's open now, and the file it runs from.</summary>
public sealed record OpenWindow(string Title, string ExePath, int ProcessId);

/// <summary>
/// Hunter: from something running (an open window or a process) to the installed program it belongs to.
/// </summary>
public static class Hunter
{
    /// <summary>Every visible, titled top-level window with its program file (one row per program file).</summary>
    public static List<OpenWindow> Windows()
    {
        var list = new List<OpenWindow>();
        var self = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || GetWindowTextLengthW(h) == 0) return true;
                if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, sb.Capacity);
                GetWindowThreadProcessId(h, out var pid);
                if (pid == self) return true;
                var path = ProcessPath((int)pid);
                if (path is null) return true;
                list.Add(new OpenWindow(sb.ToString(), path, (int)pid));
            }
            catch
            {
                /* window went away */
            }

            return true;
        }, IntPtr.Zero);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return list.Where(w => !Places.Within(w.ExePath, windows))
            .GroupBy(w => w.ExePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Program files of running processes named <paramref name="name"/> ("steam" or "steam.exe").</summary>
    public static List<string> ProcessPaths(string name)
    {
        var n = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        var paths = new List<string>();
        foreach (var p in Process.GetProcessesByName(n))
        {
            using (p)
                if (ProcessPath(p.Id) is { } path && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    paths.Add(path);
        }

        return paths;
    }

    /// <summary>The installed program a file belongs to: the deepest install folder holding it, else the program whose icon or uninstaller is next to it.</summary>
    public static InstalledApp? ForPath(string path, IEnumerable<InstalledApp> apps)
    {
        var list = apps.ToList();
        var byFolder = list.Where(a => a.InstallLocation.Length > 3 && Places.Within(path, a.InstallLocation))
            .OrderByDescending(a => a.InstallLocation.Length).FirstOrDefault();
        if (byFolder is not null) return byFolder;
        var dir = Path.GetDirectoryName(path) ?? "";
        foreach (var a in list.Where(a => a.Kind == AppKind.Program))
        {
            foreach (var p in new[] { Text.SplitIcon(a.DisplayIcon).Path, Text.SplitCommand(a.UninstallString).Exe })
            {
                if (p.Length < 4) continue;
                if (p.Equals(path, StringComparison.OrdinalIgnoreCase)) return a;
                var pd = Path.GetDirectoryName(p) ?? "";
                if (pd.Length > 3 && Places.Within(dir, pd) && !Places.Current.IsProtected(pd)) return a;
            }
        }

        return null;
    }

    public static string? ProcessPath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);
}
