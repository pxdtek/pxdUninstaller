using System.Text;
using Microsoft.Win32;

namespace pxdUninstaller.Core;

public sealed record StepResult(Leftover Item, StepOutcome Outcome, string Message);

public enum StepOutcome { Removed, AlreadyGone, AtRestart, Failed, Skipped }

public sealed class UninstallReport
{
    public InstalledApp App { get; init; } = null!;
    public bool UninstallerRan { get; set; }

    /// <summary>The program is gone from Apps &amp; features (or never needed its uninstaller run).</summary>
    public bool Uninstalled { get; set; }

    public bool Cancelled { get; set; }
    public bool RestartNeeded { get; set; }

    /// <summary>A quiet run was required (command line) but the uninstaller has no quiet mode: nothing was done.</summary>
    public bool NotQuiet { get; set; }

    public string Message { get; set; } = "";
    public List<StepResult> Steps { get; } = [];
    public string? BackupFolder { get; set; }

    public int Count(StepOutcome o) => Steps.Count(s => s.Outcome == o);
    public bool Ok => Uninstalled && Steps.All(s => s.Outcome != StepOutcome.Failed);
}

/// <summary>
/// Uninstalls one program: runs its uninstaller (normally, or quietly), checks it's really gone, then removes the
/// chosen leftovers. Registry keys and values are saved to a .reg file in the backups folder first, and files go to
/// the Recycle Bin, so everything can be brought back.
/// </summary>
public sealed class UninstallJob
{
    private readonly InstalledApp _app;
    private readonly List<Leftover> _items;
    private readonly string _backupRoot;
    private StringBuilder? _reg;
    private string? _backupDir;

    public UninstallJob(InstalledApp app, IEnumerable<Leftover> chosen, string backupRoot)
    {
        _app = app;
        _items = chosen.ToList();
        _backupRoot = backupRoot;
    }

    /// <summary>Run the uninstaller quietly (no windows) when it can be.</summary>
    public bool Quiet { get; init; }

    /// <summary>Quiet or nothing: an uninstaller with no quiet mode isn't started (command line).</summary>
    public bool MustBeQuiet { get; init; }

    /// <summary>Delete files for good instead of using the Recycle Bin.</summary>
    public bool Permanent { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Make a Windows restore point before anything is changed.</summary>
    public bool MakeRestorePoint { get; init; }

    public event Action<string>? Log;

    private void Say(string s) => Log?.Invoke(s);

    public async Task<UninstallReport> RunAsync(CancellationToken ct = default)
    {
        var report = new UninstallReport { App = _app };
        var uninstaller = _items.FirstOrDefault(i => i.Kind == LeftoverKind.Uninstaller);
        if (uninstaller is not null && MustBeQuiet && !_app.Plan.CanQuiet)
        {
            report.NotQuiet = true;
            report.Message = $"{_app.Name} can't be removed quietly. {_app.Plan.WhyNotQuiet} Use the pxd Uninstaller window to run its uninstaller.";
            return report;
        }

        if (MakeRestorePoint && _items.Count > 0)
        {
            Say("Making a Windows restore point…");
            var why = await Task.Run(() => RestorePoint.Create($"pxd Uninstaller: {_app.Name}"), ct);
            Say(why is null ? "Restore point made." : $"No restore point ({why}). Going on; registry backups are still made.");
        }

        if (uninstaller is not null)
        {
            var quiet = Quiet || MustBeQuiet;
            if (quiet && !_app.Plan.CanQuiet && MustBeQuiet)
            {
                report.NotQuiet = true;
                report.Message = $"{_app.Name} can't be removed quietly. {_app.Plan.WhyNotQuiet} Use the pxd Uninstaller window to run its uninstaller.";
                return report;
            }

            var command = quiet && _app.Plan.CanQuiet ? _app.Plan.QuietCommand! : _app.Plan.Command!;
            var runQuiet = quiet && _app.Plan.CanQuiet;
            Say(runQuiet ? $"Removing {_app.Name} quietly…" : $"Running {_app.Name}'s uninstaller…");
            Say("  " + command);
            report.UninstallerRan = true;
            var result = await Runner.RunAsync(command, runQuiet || _app.Plan.IsStore, Timeout, () => !AppScanner.StillInstalled(_app), ct);
            report.RestartNeeded = result.RestartNeeded;
            var still = AppScanner.StillInstalled(_app);
            report.Uninstalled = !still;
            if (still)
            {
                report.Cancelled = result.Cancelled || (result.Ok && !result.TimedOut);
                report.Message = !result.Started ? $"The uninstaller didn't start: {result.Error}"
                    : result.TimedOut ? "The uninstaller didn't finish in time."
                    : result.Cancelled ? "The uninstall was cancelled."
                    : result.Ok ? $"The uninstaller closed, but {_app.Name} is still installed (cancelled, or it finishes after a restart)."
                    : $"The uninstaller stopped with error {result.ExitCode}.";
                Say(report.Message);
                Say("Leftovers were not touched, because the program is still installed.");
                return report;
            }

            Say(result.RestartNeeded ? $"{_app.Name} was uninstalled. Windows needs a restart to finish." : $"{_app.Name} was uninstalled.");
        }
        else
        {
            report.Uninstalled = true;   // cleaning up leftovers only (no uninstaller chosen, or none left)
        }

        foreach (var item in Order(_items.Where(i => i.Kind != LeftoverKind.Uninstaller)))
        {
            ct.ThrowIfCancellationRequested();
            var step = Remove(item);
            report.Steps.Add(step);
            Say(step.Outcome switch
            {
                StepOutcome.Removed => $"Removed {Describe(item)}",
                StepOutcome.AlreadyGone => $"Already gone: {Describe(item)}",
                StepOutcome.AtRestart => $"In use, removed at the next restart: {Describe(item)}",
                StepOutcome.Skipped => $"Skipped {Describe(item)}: {step.Message}",
                _ => $"Couldn't remove {Describe(item)}: {step.Message}",
            });
        }

        RemoveEmptyParents(report);
        FinishBackup(report);
        if (report.Count(StepOutcome.AtRestart) > 0) report.RestartNeeded = true;
        var removed = report.Count(StepOutcome.Removed);
        var failed = report.Count(StepOutcome.Failed);
        report.Message = (uninstaller is null ? $"Cleaned up {_app.Name}." : $"{_app.Name} was uninstalled.")
                         + (removed > 0 ? $" {Text.Plural(removed, "leftover")} removed." : "")
                         + (failed > 0 ? $" {Text.Plural(failed, "item")} couldn't be removed." : "")
                         + (report.RestartNeeded ? " Restart Windows to finish." : "");
        return report;
    }

    public static string Describe(Leftover l) => l.Kind switch
    {
        LeftoverKind.RegistryValue => $"{l.Path} → {l.ValueName}",
        LeftoverKind.Service => $"service {l.ServiceName}",
        LeftoverKind.Task => $"task {l.TaskName}",
        LeftoverKind.FirewallRule => $"firewall rule \"{l.Path}\"",
        _ => l.Path,
    };

    /// <summary>Services and tasks first (they hold files open), then registry, then files, deepest folders first.</summary>
    private static IEnumerable<Leftover> Order(IEnumerable<Leftover> items) =>
        items.OrderBy(i => i.Kind switch
            {
                LeftoverKind.Service => 0,
                LeftoverKind.Task => 1,
                LeftoverKind.FirewallRule => 2,
                LeftoverKind.RegistryValue => 3,
                LeftoverKind.RegistryKey => 4,
                LeftoverKind.Shortcut => 5,
                LeftoverKind.File => 6,
                _ => 7,
            })
            .ThenByDescending(i => i.Path.Count(c => c == '\\'));

    // ---------- Existence ----------

    public static Func<string, bool> TaskExists { get; set; } = name => Runner.Tool("schtasks.exe", $"/query /tn \"{name}\"", 20_000).Code == 0;
    public static Func<string, bool> DeleteTask { get; set; } = name => Runner.Tool("schtasks.exe", $"/delete /tn \"{name}\" /f", 30_000).Code == 0;

    public static Func<string, bool> DeleteService { get; set; } = name =>
    {
        Runner.Tool("sc.exe", $"stop \"{name}\"", 30_000);
        for (var i = 0; i < 20 && Runner.Tool("sc.exe", $"query \"{name}\"", 10_000).Output.Contains("STOP_PENDING"); i++)
            Thread.Sleep(500);
        var (code, output) = Runner.Tool("sc.exe", $"delete \"{name}\"", 30_000);
        return code == 0 || output.Contains("1072");   // 1072 = marked for deletion
    };

    public static Func<string, bool> DeleteFirewallRule { get; set; } = id =>
        Runner.Tool("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Remove-NetFirewallRule -Name '{id.Replace("'", "''")}' -ErrorAction Stop\"", 60_000).Code == 0;

    public static bool Exists(Leftover l) => l.Kind switch
    {
        LeftoverKind.Folder => Directory.Exists(l.Path),
        LeftoverKind.File or LeftoverKind.Shortcut => File.Exists(l.Path),
        LeftoverKind.RegistryKey => l.Reg is { } r && Reg.Exists(r),
        LeftoverKind.RegistryValue or LeftoverKind.FirewallRule => l.Reg is { } r && l.ValueName is { } v && Reg.ValueExists(r, v),
        LeftoverKind.Service => l.Reg is { } r && Reg.Exists(r),
        LeftoverKind.Task => l.TaskName is { } t && TaskExists(t),
        _ => false,
    };

    // ---------- Removing ----------

    private StepResult Remove(Leftover l)
    {
        try
        {
            if (!Exists(l)) return new StepResult(l, StepOutcome.AlreadyGone, "");
            if (l.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut && Places.Current.IsProtected(l.Path))
                return new StepResult(l, StepOutcome.Skipped, "a Windows or system folder is never removed");

            switch (l.Kind)
            {
                case LeftoverKind.RegistryKey:
                    BackupKey(l.Reg!);
                    return Reg.Delete(l.Reg!) && !Reg.Exists(l.Reg!) ? Done(l) : Fail(l, "access denied");

                case LeftoverKind.RegistryValue:
                    BackupValue(l.Reg!, l.ValueName!);
                    return Reg.DeleteValue(l.Reg!, l.ValueName!) && !Reg.ValueExists(l.Reg!, l.ValueName!) ? Done(l) : Fail(l, "access denied");

                case LeftoverKind.Service:
                    BackupKey(l.Reg!);
                    if (Places.Current.Test) return Reg.Delete(l.Reg!) ? Done(l) : Fail(l, "couldn't delete");
                    return DeleteService(l.ServiceName!) ? Done(l) : Fail(l, "Windows refused (administrator rights needed?)");

                case LeftoverKind.Task:
                    return DeleteTask(l.TaskName!) ? Done(l) : Fail(l, "Windows refused (administrator rights needed?)");

                case LeftoverKind.FirewallRule:
                    BackupValue(l.Reg!, l.ValueName!);
                    if (!Places.Current.Test && DeleteFirewallRule(l.RuleId!)) return Done(l);
                    return Reg.DeleteValue(l.Reg!, l.ValueName!) && !Reg.ValueExists(l.Reg!, l.ValueName!) ? Done(l) : Fail(l, "administrator rights needed");

                case LeftoverKind.File or LeftoverKind.Shortcut:
                    return DeleteFile(l);

                case LeftoverKind.Folder:
                    return DeleteFolder(l);
            }

            return new StepResult(l, StepOutcome.Skipped, "nothing to do");
        }
        catch (Exception ex)
        {
            return Fail(l, ex.Message);
        }
    }

    private static StepResult Done(Leftover l) => new(l, StepOutcome.Removed, "");
    private static StepResult Fail(Leftover l, string why) => new(l, StepOutcome.Failed, why);

    private bool Recycle(string path) => !Permanent && !Places.Current.Test && Native.Recycle(path);

    private StepResult DeleteFile(Leftover l)
    {
        Note($"file  {l.Path}");
        if (Recycle(l.Path)) return Done(l);
        try
        {
            File.SetAttributes(l.Path, FileAttributes.Normal);
            File.Delete(l.Path);
            return Done(l);
        }
        catch (Exception ex)
        {
            return Native.DeleteAtRestart(l.Path) ? new StepResult(l, StepOutcome.AtRestart, "") : Fail(l, ex.Message);
        }
    }

    private StepResult DeleteFolder(Leftover l)
    {
        Note($"folder {l.Path}");
        if (Recycle(l.Path)) return Done(l);
        // The Recycle Bin said no (too big, in use, another drive): delete what can be, the rest at restart.
        var left = 0;
        var atRestart = 0;
        foreach (var f in SafeAll(l.Path, files: true))
        {
            try
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
            catch
            {
                left++;
                if (Native.DeleteAtRestart(f)) atRestart++;
            }
        }

        foreach (var d in SafeAll(l.Path, files: false).OrderByDescending(d => d.Length).Append(l.Path))
        {
            try
            {
                Directory.Delete(d, false);
            }
            catch
            {
                Native.DeleteAtRestart(d);
            }
        }

        if (!Directory.Exists(l.Path)) return Done(l);
        if (left > 0 && atRestart == left) return new StepResult(l, StepOutcome.AtRestart, "");
        return Fail(l, left > 0 ? $"{Text.Plural(left, "file")} in use or protected" : "access denied");
    }

    private static IEnumerable<string> SafeAll(string dir, bool files)
    {
        try
        {
            var o = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            return files ? Directory.EnumerateFiles(dir, "*", o).ToList() : Directory.EnumerateDirectories(dir, "*", o).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>After removing a folder, its parent (a publisher's folder, a Start menu folder) goes too when it's now empty.</summary>
    private void RemoveEmptyParents(UninstallReport report)
    {
        var p = Places.Current;
        foreach (var step in report.Steps.Where(s => s.Outcome == StepOutcome.Removed && s.Item.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut).ToList())
        {
            var parent = Path.GetDirectoryName(step.Item.Path);
            if (parent is null || p.IsProtected(parent)) continue;
            try
            {
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                    Say($"Removed the empty folder {parent}");
                }
            }
            catch
            {
                /* not empty after all, or in use */
            }
        }
    }

    // ---------- Backups ----------

    private string BackupDir()
    {
        if (_backupDir is not null) return _backupDir;
        var safe = string.Concat(_app.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length > 60) safe = safe[..60];
        _backupDir = Path.Combine(_backupRoot, $"{DateTime.Now:yyyy-MM-dd HHmmss} {safe}");
        Directory.CreateDirectory(_backupDir);
        return _backupDir;
    }

    private readonly StringBuilder _notes = new();

    private void Note(string s) => _notes.AppendLine(s);

    private StringBuilder RegText() => _reg ??= new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");

    private static string RegName(RegPlace p) => Reg.TestRoot is { } t
        ? $@"HKEY_CURRENT_USER\{t}\{Reg.Short(p.Hive)}{(p.View == RegistryView.Registry32 ? "32" : "")}\{p.Path}"
        : $@"{(p.Hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : p.Hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : p.Hive.ToString())}\{Reg.Visible(p.Hive, p.View, p.Path)}";

    private void BackupKey(RegPlace place)
    {
        using var k = Reg.Open(place);
        if (k is null) return;
        Note($"key   {place.Text}");
        WriteKey(k, RegName(place), 0);
    }

    private void WriteKey(RegistryKey k, string name, int depth)
    {
        if (depth > 40) return;
        var b = RegText();
        b.Append('[').Append(name).Append("]\r\n");
        foreach (var v in k.GetValueNames())
            b.Append(ValueLine(k, v));
        b.Append("\r\n");
        foreach (var sub in k.GetSubKeyNames())
        {
            try
            {
                using var s = k.OpenSubKey(sub);
                if (s is not null) WriteKey(s, name + "\\" + sub, depth + 1);
            }
            catch
            {
                /* unreadable subkey */
            }
        }
    }

    private void BackupValue(RegPlace place, string value)
    {
        using var k = Reg.Open(place);
        if (k is null) return;
        Note($"value {place.Text} → {value}");
        var b = RegText();
        b.Append('[').Append(RegName(place)).Append("]\r\n").Append(ValueLine(k, value)).Append("\r\n");
    }

    private static string ValueLine(RegistryKey k, string name)
    {
        var key = name.Length == 0 ? "@" : "\"" + Esc(name) + "\"";
        try
        {
            var kind = k.GetValueKind(name);
            var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return kind switch
            {
                RegistryValueKind.String => $"{key}=\"{Esc((string)v!)}\"\r\n",
                RegistryValueKind.DWord => $"{key}=dword:{unchecked((uint)(int)v!):x8}\r\n",
                RegistryValueKind.QWord => $"{key}=hex(b):{Hex(BitConverter.GetBytes((long)v!))}\r\n",
                RegistryValueKind.ExpandString => $"{key}=hex(2):{Hex(Encoding.Unicode.GetBytes((string)v! + "\0"))}\r\n",
                RegistryValueKind.MultiString => $"{key}=hex(7):{Hex(Encoding.Unicode.GetBytes(string.Concat(((string[])v!).Select(s => s + "\0")) + "\0"))}\r\n",
                RegistryValueKind.Binary => $"{key}=hex:{Hex((byte[])v!)}\r\n",
                _ => v is byte[] raw ? $"{key}=hex:{Hex(raw)}\r\n" : "",
            };
        }
        catch
        {
            return "";
        }
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Hex(byte[] bytes) => string.Join(",", bytes.Select(b => b.ToString("x2")));

    private void FinishBackup(UninstallReport report)
    {
        if (_reg is null && _notes.Length == 0) return;
        var dir = BackupDir();
        if (_reg is not null) File.WriteAllText(Path.Combine(dir, "registry.reg"), _reg.ToString(), Encoding.Unicode);
        var log = new StringBuilder();
        log.AppendLine($"pxd Uninstaller: {_app.Name} {_app.Version}".TrimEnd());
        log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine();
        log.AppendLine("Registry keys and values are saved in registry.reg (double-click it to put them back).");
        log.AppendLine(Permanent || Places.Current.Test ? "Files and folders were deleted." : "Files and folders went to the Recycle Bin.");
        log.AppendLine();
        foreach (var s in report.Steps)
            log.AppendLine($"{s.Outcome,-12} {Describe(s.Item)}{(s.Message.Length > 0 ? "  (" + s.Message + ")" : "")}");
        File.WriteAllText(Path.Combine(dir, "removed.txt"), log.ToString());
        report.BackupFolder = dir;
    }
}
