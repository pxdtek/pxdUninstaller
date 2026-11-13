using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Microsoft.Win32;
using pxdUninstaller.Core;
using pxdUninstaller.Views;

namespace pxdUninstaller;

/// <summary>
/// --self-test DIR [--sample]: checks the command-line and path parsers and the installer-type detection, scans this
/// PC read-only (programs, Store apps, leftovers of a few of them), then installs two made-up programs into a sandbox
/// (registry under HKCU\Software\pxdUninstallerSelfTest, folders under DIR) and uninstalls them for real: quiet
/// uninstall, every kind of leftover, backups, the "can't be removed quietly" rule and the command line. Renders the
/// window to PNGs on the headless platform (nothing on screen). --sample = README screenshots. Exit code 0 = all passed.
/// </summary>
internal static class SelfTest
{
    public static string? Dir { get; set; }
    public static bool Sample { get; set; }
    public static bool Active { get; private set; }
    public static int ExitCode { get; private set; } = 1;

    private static readonly StringBuilder R = new();
    private static int _failed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        R.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : ": " + detail)}");
        if (!ok) _failed++;
    }

    public static async Task RunAsync()
    {
        Active = true;
        R.AppendLine($"pxd Uninstaller {typeof(SelfTest).Assembly.GetName().Version?.ToString(3)} self-test, {DateTime.Now:yyyy-MM-dd HH:mm}");
        R.AppendLine($"Elevated: {Cli.IsElevated}");
        R.AppendLine();
        try
        {
            Parsers();
            Plans();
            if (!Sample) RealScan();
            await Sandbox();
            await Render();
            await Placement();
            SingleInstanceCheck();
            await UpdateChecks();
        }
        catch (Exception ex)
        {
            Check("no crash", false, ex.ToString());
        }

        R.AppendLine();
        R.AppendLine(_failed == 0 ? "RESULT: all checks passed" : $"RESULT: {_failed} check(s) FAILED");
        ExitCode = _failed == 0 ? 0 : 1;
        await File.WriteAllTextAsync(Path.Combine(Dir!, "report.txt"), R.ToString());
    }

    // ---------- Parsers ----------

    private static void Parsers()
    {
        R.AppendLine("== Parsers ==");
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var notepad = Path.Combine(win, "notepad.exe");
        Check("Quoted command", Text.SplitCommand("\"C:\\Program Files\\X\\uninst.exe\" /S") == ("C:\\Program Files\\X\\uninst.exe", "/S"));
        Check("Unquoted path with spaces (exists)", Text.SplitCommand(notepad.Replace("notepad", "notepad") + " /x") == (notepad, "/x"), Text.SplitCommand(notepad + " /x").ToString());
        Check("Unquoted path with spaces (missing)", Text.SplitCommand(@"C:\Program Files\Gone App\unins000.exe /SILENT") == (@"C:\Program Files\Gone App\unins000.exe", "/SILENT"));
        Check("Bare msiexec", Text.SplitCommand("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}") == ("MsiExec.exe", "/X{11111111-2222-3333-4444-555555555555}"));
        Check("Icon with index", Text.SplitIcon("\"C:\\x\\app.exe\",-101") == ("C:\\x\\app.exe", -101));
        Check("Clean name", Text.CleanName("7-Zip 24.08 (x64)") == "7-Zip" && Text.CleanName("Mozilla Firefox (x64 en-US)") == "Mozilla Firefox" && Text.CleanName("Python 3.12.4 (64-bit)") == "Python",
            $"{Text.CleanName("7-Zip 24.08 (x64)")} | {Text.CleanName("Mozilla Firefox (x64 en-US)")} | {Text.CleanName("Python 3.12.4 (64-bit)")}");
        Check("Clean publisher", Text.CleanPublisher("Mozilla Corporation") == "Mozilla" && Text.CleanPublisher("Fakeco Software, Inc.") == "Fakeco Software", Text.CleanPublisher("Fakeco Software, Inc."));
        Check("Path in a Run value", Text.PathIn("\"C:\\Program Files\\A B\\a.exe\" --tray") == "C:\\Program Files\\A B\\a.exe" && Text.PathIn("C:\\Program Files\\A B\\a.exe -m") == "C:\\Program Files\\A B\\a.exe"
            && Text.PathIn("rundll32.exe C:\\X Y\\h.dll,Run") == "C:\\X Y\\h.dll" && Text.PathIn("C:\\X\\app.exe,0") == "C:\\X\\app.exe", Text.PathIn("rundll32.exe C:\\X Y\\h.dll,Run"));
        Check("Packed product code", LeftoverFinder.Packed("{12345678-ABCD-EF01-2345-6789ABCDEF01}") == "87654321DCBA10FE32547698BADCFE10", LeftoverFinder.Packed("{12345678-ABCD-EF01-2345-6789ABCDEF01}"));
        var tasks = LeftoverFinder.ParseTasks("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Tasks>\r\n<!-- \\A\\One -->\r\n<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit-task\"><Actions><Exec><Command>\"C:\\x\\a.exe\"</Command></Exec></Actions></Task>\r\n<!-- \\Two -->\r\n<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit-task\"><Actions><Exec><Command>%windir%\\b.exe</Command></Exec></Actions></Task>\r\n</Tasks>");
        Check("Scheduled task list", tasks.Count == 2 && tasks[0].Name == @"\A\One" && tasks[0].Commands[0] == @"C:\x\a.exe" && tasks[1].Commands[0].StartsWith(win, StringComparison.OrdinalIgnoreCase), string.Join("; ", tasks.Select(t => t.Name + "=" + string.Join(",", t.Commands))));
        var shellDir = Path.Combine(Dir!, "shell-delete");
        Directory.CreateDirectory(Path.Combine(shellDir, "sub"));
        File.WriteAllText(Path.Combine(shellDir, "sub", "a.txt"), "x");
        File.WriteAllText(Path.Combine(Dir!, "shell-delete.txt"), "x");
        Check("Shell delete (the Recycle Bin's call) works for a folder and a file",
            Native.ShellDelete(shellDir, toRecycleBin: false) && !Directory.Exists(shellDir) && Native.ShellDelete(Path.Combine(Dir!, "shell-delete.txt"), toRecycleBin: false));
        var p = Places.Real();
        Check("System folders are protected", p.IsProtected(p.ProgramFiles) && p.IsProtected(p.AppData) && p.IsProtected(Path.Combine(win, "System32")) && p.IsProtected(@"C:\") && p.IsProtected(p.UserProfile)
                                              && p.IsProtected(Path.GetDirectoryName(p.UserProfile)!) && !p.IsProtected(Path.Combine(p.ProgramFiles, "Some App")));
        R.AppendLine();
    }

    private static void Plans()
    {
        R.AppendLine("== Installer types ==");
        var d = Path.Combine(Dir!, "plans");
        Directory.CreateDirectory(d);
        string Exe(string name, string? content = null)
        {
            var f = Path.Combine(d, name);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, content ?? "MZ fake");
            return f;
        }

        UninstallPlan P(string uninstall, string key = "X", Dictionary<string, string>? values = null, bool msi = false, string? code = null, string quiet = "") =>
            UninstallPlan.For(new InstalledApp
            {
                Name = "X", UninstallString = uninstall, QuietUninstallString = quiet, Key = new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry64, AppScanner.UninstallPath + "\\" + key),
                Values = values ?? new(StringComparer.OrdinalIgnoreCase), WindowsInstaller = msi, ProductCode = code,
            });

        var code = "{11111111-2222-3333-4444-555555555555}";
        var msi = P($"MsiExec.exe /I{code}", code, msi: true, code: code);
        Check("MSI: quiet with /qn", msi is { IsMsi: true, CanQuiet: true } && msi.QuietCommand == $"MsiExec.exe /X{code} /qn /norestart" && msi.Command == $"MsiExec.exe /X{code}", msi.QuietCommand);
        var inno = P($"\"{Exe("inno\\unins000.exe")}\"", "Thing_is1");
        Check("Inno Setup: /VERYSILENT", inno.Method == "Inno Setup" && inno.QuietCommand!.EndsWith("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"), inno.QuietCommand);
        var nsis = P($"\"{Exe("nsis\\Uninstall Thing.exe", "MZ....Nullsoft Install System v3.08....")}\" /currentuser");
        Check("NSIS: /S", nsis.Method == "NSIS" && nsis.QuietCommand!.EndsWith("/currentuser /S"), nsis.QuietCommand);
        var sq = P($"\"{Exe("sq\\Update.exe")}\" --uninstall");
        Check("Squirrel: -s", sq.Method == "Squirrel" && sq.QuietCommand!.EndsWith("--uninstall -s"), sq.QuietCommand);
        var burn = P($"\"{Exe("burn\\bundle.exe")}\" /uninstall", values: new(StringComparer.OrdinalIgnoreCase) { ["BundleCachePath"] = "x" });
        Check("WiX bundle: /quiet", burn.Method == "WiX bundle" && burn.QuietCommand!.EndsWith("/uninstall /quiet /norestart"), burn.QuietCommand);
        var chrome = P($"\"{Exe("chrome\\setup.exe")}\" --uninstall --system-level");
        Check("Chromium: --force-uninstall", chrome.Method == "Chromium setup" && chrome.QuietCommand!.EndsWith("--force-uninstall"), chrome.QuietCommand);
        var ishield = P($"\"{Exe("InstallShield Installation Information\\{A}\\setup.exe")}\" -runfromtemp -removeonly");
        Check("InstallShield: no quiet mode", ishield is { CanQuiet: false, Method: "InstallShield" } && ishield.WhyNotQuiet!.Contains("answer file"));
        var own = P($"\"{Exe("own\\uninstall.exe")}\"");
        Check("Unknown uninstaller: no quiet mode", own is { CanQuiet: false, Missing: false } && own.WhyNotQuiet!.Contains("uninstall.exe"), own.WhyNotQuiet);
        var given = P($"\"{Exe("own2\\uninstall.exe")}\"", quiet: "\"x\" /quiet");
        Check("QuietUninstallString wins", given.QuietCommand == "\"x\" /quiet");
        var office = P($"\"{Exe("c2r\\OfficeClickToRun.exe")}\" scenario=install scenariosubtype=ARP productstoremove=O365HomePremRetail.16_en-us_x-none");
        Check("Office Click-to-Run: DisplayLevel=False", office.Method == "Office Click-to-Run" && office.QuietCommand!.EndsWith("DisplayLevel=False"), office.QuietCommand);
        var vs = P($"\"{Exe("Microsoft Visual Studio\\Installer\\setup.exe")}\" uninstall --installPath \"C:\\VS\"");
        Check("Visual Studio: --quiet", vs.Method == "Visual Studio Installer" && vs.QuietCommand!.EndsWith("--quiet --norestart"), vs.QuietCommand);
        var steam = P($"\"{Exe("steam\\steam.exe")}\" steam://uninstall/620980");
        Check("Steam game: no quiet mode, says why", steam is { CanQuiet: false, Method: "Steam game" } && steam.WhyNotQuiet!.Contains("Steam"));
        var gone = P(@"C:\Program Files\Gone For Good\unins000.exe");
        Check("Missing uninstaller: broken entry", gone is { Missing: true, CanQuiet: false });
        var store = UninstallPlan.For(new InstalledApp { Kind = AppKind.Store, PackageFullName = "A.B_1.0.0.0_x64__abc", Name = "B" });
        Check("Store app: Remove-AppxPackage", store is { IsStore: true, CanQuiet: true } && store.QuietCommand!.Contains("Remove-AppxPackage -Package 'A.B_1.0.0.0_x64__abc'"));
        R.AppendLine();
    }

    // ---------- This PC, read-only ----------

    private static void RealScan()
    {
        R.AppendLine("== This PC (read only) ==");
        var w = Stopwatch.StartNew();
        var apps = AppScanner.Scan();
        var t = w.Elapsed.TotalSeconds;
        Check("Programs found", apps.Count(a => a.Kind == AppKind.Program) > 5, $"{apps.Count(a => a.Kind == AppKind.Program)} programs, {apps.Count(a => a.Kind == AppKind.Store)} Store apps in {t:0.00} s");
        Check("Store apps found", apps.Any(a => a.Kind == AppKind.Store));
        Check("Store names resolved (no ms-resource)", apps.Where(a => a.Kind == AppKind.Store).All(a => !a.Name.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)));
        Check("No Windows updates listed", !apps.Any(a => a.Name.StartsWith("Security Update for") || a.Name.StartsWith("Update for")));
        var quiet = apps.Count(a => a.Plan.CanQuiet);
        R.AppendLine($"       quiet: {quiet}, not quiet: {apps.Count(a => !a.Plan.CanQuiet && !a.Orphaned)}, broken: {apps.Count(a => a.Orphaned)}");
        foreach (var g in apps.GroupBy(a => a.Plan.Method).OrderByDescending(g => g.Count()))
            R.AppendLine($"       {g.Key}: {g.Count()}");
        File.WriteAllLines(Path.Combine(Dir!, "apps.txt"), apps.Select(a => $"{a.Name} | {a.Version} | {a.Publisher} | {a.Plan.Method} | quiet={a.Plan.CanQuiet} | {a.InstallLocation} | {a.Id}"));

        // Leftovers of a few programs: only looked at, never removed.
        var sample = apps.Where(a => a.Kind == AppKind.Program && a.InstallLocation.Length > 0 && Directory.Exists(a.InstallLocation)).Take(6).ToList();
        var lines = new List<string>();
        foreach (var a in sample)
        {
            w.Restart();
            var found = new LeftoverFinder(a, apps).Find();
            lines.Add($"### {a.Name} ({w.Elapsed.TotalSeconds:0.0} s)");
            lines.AddRange(found.Select(f => "  " + f));
            Check($"Leftovers of {a.Name}: has its uninstall entry and folder", found.Any(f => f.Kind == LeftoverKind.RegistryKey && f.Match == MatchLevel.Regular)
                                                                                  && found.Any(f => f.Kind == LeftoverKind.Folder && f.Path.Equals(a.InstallLocation.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || f.Kind == LeftoverKind.Folder && f.Why.StartsWith("Install folder")),
                $"{found.Count} items in {w.Elapsed.TotalSeconds:0.0} s");
            Check($"Leftovers of {a.Name}: nothing protected offered", found.All(f => f.Kind is not (LeftoverKind.Folder or LeftoverKind.File) || !Places.Current.IsProtected(f.Path)));
        }

        File.WriteAllLines(Path.Combine(Dir!, "leftovers.txt"), lines);
        R.AppendLine();
    }

    // ---------- Sandbox: made-up programs, uninstalled for real ----------

    private static async Task Sandbox()
    {
        R.AppendLine("== Sandbox uninstall (test registry root + test folders) ==");
        var id = Guid.NewGuid().ToString("N")[..8];
        var regRoot = @"Software\pxdUninstallerSelfTest\" + id;
        var fs = Path.Combine(Dir!, "sandbox-" + id);
        var oldPlaces = Places.Current;
        var oldTasks = LeftoverFinder.TaskXml;
        var oldTaskExists = UninstallJob.TaskExists;
        var oldDeleteTask = UninstallJob.DeleteTask;
        var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\FakeNotes Update", @"\Other Thing" };
        var oldRestore = RestorePoint.Create;
        var restorePoints = new List<string>();
        try
        {
            RestorePoint.Create = d =>
            {
                restorePoints.Add(d);
                return null;
            };
            Reg.TestRoot = regRoot;
            var p = Places.Current = Places.Under(fs);
            var install = Path.Combine(p.ProgramFiles, "Fakeco", "Fake Notes");
            var other = Path.Combine(p.ProgramFiles, "Fakeco", "Other Tool");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(install, "FakeNotes.exe"), "MZ fake");
            File.WriteAllText(Path.Combine(install, "notes.dll"), new string('x', 50_000));
            File.WriteAllText(Path.Combine(other, "Other.exe"), "MZ other");

            var uninstallKey = new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry64, AppScanner.UninstallPath + @"\FakeNotes");
            var mappedKey = $@"HKCU\{regRoot}\HKLM\{AppScanner.UninstallPath}\FakeNotes";
            // The made-up uninstaller removes its folder and its Apps & features entry, like a real one.
            File.WriteAllText(Path.Combine(install, "uninstall.cmd"),
                $"@echo off\r\nreg delete \"{mappedKey}\" /f >nul 2>&1\r\ncd /d \"%TEMP%\"\r\nrmdir /s /q \"{install}\"\r\nexit /b 0\r\n");
            File.Copy(Path.Combine(install, "uninstall.cmd"), Path.Combine(fs, "fakenotes-uninstall.cmd"));
            using (var k = Reg.Create(uninstallKey)!)
            {
                k.SetValue("DisplayName", "Fake Notes 2.1 (x64)");
                k.SetValue("Publisher", "Fakeco Software, Inc.");
                k.SetValue("DisplayVersion", "2.1.0");
                k.SetValue("InstallLocation", install);
                k.SetValue("DisplayIcon", Path.Combine(install, "FakeNotes.exe") + ",0");
                k.SetValue("UninstallString", $"\"{Path.Combine(fs, "fakenotes-uninstall.cmd")}\"");
                k.SetValue("QuietUninstallString", $"\"{Path.Combine(fs, "fakenotes-uninstall.cmd")}\" /quiet");
                k.SetValue("EstimatedSize", 1234, RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }

            using (var k = Reg.Create(new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry64, AppScanner.UninstallPath + @"\OtherTool"))!)
            {
                k.SetValue("DisplayName", "Other Tool");
                k.SetValue("Publisher", "Fakeco Software, Inc.");
                k.SetValue("InstallLocation", other);
                k.SetValue("UninstallString", $"\"{Path.Combine(other, "Other.exe")}\" /uninstall");
            }

            // A program for this user whose uninstaller has no quiet mode, and a broken entry.
            using (var k = Reg.Create(new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, AppScanner.UninstallPath + @"\LoudApp"))!)
            {
                File.WriteAllText(Path.Combine(other, "loud-uninstall.exe"), "MZ loud");
                k.SetValue("DisplayName", "Loud App");
                k.SetValue("UninstallString", $"\"{Path.Combine(other, "loud-uninstall.exe")}\"");
            }

            using (var k = Reg.Create(new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, AppScanner.UninstallPath + @"\GhostApp"))!)
            {
                k.SetValue("DisplayName", "Ghost App");
                k.SetValue("UninstallString", Path.Combine(p.ProgramFiles, "Ghost App", "unins000.exe"));
                k.SetValue("InstallLocation", Path.Combine(p.ProgramFiles, "Ghost App"));
            }

            Directory.CreateDirectory(Path.Combine(p.ProgramFiles, "Ghost App"));
            File.WriteAllText(Path.Combine(p.ProgramFiles, "Ghost App", "left.txt"), "x");

            // Leftovers in every kind of place.
            void MakeDir(string path, string file = "settings.ini")
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, file), "x=1");
            }

            MakeDir(Path.Combine(p.AppData, "Fake Notes"));
            MakeDir(Path.Combine(p.LocalAppData, "Fakeco", "Fake Notes", "cache"));
            MakeDir(Path.Combine(p.ProgramData, "FakeNotes Helper"));
            MakeDir(Path.Combine(p.Documents, "Fake Notes"), "my note.txt");
            MakeDir(Path.Combine(p.AppData, "Notepad++"));
            MakeDir(Path.Combine(p.LocalLow, "Fake Notes"));
            Directory.CreateDirectory(Path.Combine(p.StartMenu, "Fake Notes"));
            Native.WriteShortcut(Path.Combine(p.StartMenu, "Fake Notes", "Fake Notes.lnk"), Path.Combine(install, "FakeNotes.exe"));
            Native.WriteShortcut(Path.Combine(p.Desktop, "Fake Notes.lnk"), Path.Combine(install, "FakeNotes.exe"));
            Native.WriteShortcut(Path.Combine(p.Desktop, "Other Tool.lnk"), Path.Combine(other, "Other.exe"));
            Directory.CreateDirectory(p.CrashDumps);
            File.WriteAllText(Path.Combine(p.CrashDumps, "FakeNotes.exe.4242.dmp"), "dump");

            RegPlace L(RegistryHive h, RegistryView v, string path) => new(h, v, path);
            void Set(RegPlace place, string name, object value)
            {
                using var k = Reg.Create(place)!;
                k.SetValue(name, value);
            }

            var exe = Path.Combine(install, "FakeNotes.exe");
            Set(L(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Fakeco Software\Fake Notes"), "Theme", "dark");
            Set(L(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Fake Notes"), "Path", install);
            Set(L(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Notepad++"), "x", "1");
            Set(L(RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"), "FakeNotesTray", $"\"{exe}\" --tray");
            Set(L(RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"), "OneDrive", @"C:\Elsewhere\OneDrive.exe /background");
            Set(L(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FakeNotes.exe"), "", exe);
            Set(L(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services\FakeNotesSvc"), "ImagePath", $"\"{exe}\" --service");
            Set(L(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"), "{FAKE-RULE-1}", $"v2.33|Action=Allow|Active=TRUE|Dir=In|Protocol=6|App={exe}|Name=Fake Notes sync|");
            Set(L(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"), "{OTHER-RULE}", @"v2.33|Action=Allow|Dir=In|App=C:\Elsewhere\x.exe|Name=Other|");
            Set(L(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Classes\FakeNotes.Document\shell\open\command"), "", $"\"{exe}\" \"%1\"");
            Set(L(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"), exe + ".FriendlyAppName", "Fake Notes");

            LeftoverFinder.TaskXml = () => "<?xml version=\"1.0\"?><Tasks><!-- \\FakeNotes Update --><Task xmlns=\"x\"><Actions><Exec><Command>\"" + exe + "\"</Command><Arguments>/update</Arguments></Exec></Actions></Task>"
                                           + "<!-- \\Other Thing --><Task xmlns=\"x\"><Actions><Exec><Command>C:\\Elsewhere\\o.exe</Command></Exec></Actions></Task></Tasks>";
            UninstallJob.TaskExists = name => tasks.Contains(name);
            UninstallJob.DeleteTask = name => tasks.Remove(name);

            var apps = AppScanner.Scan();
            var fake = apps.FirstOrDefault(a => a.Name.StartsWith("Fake Notes"));
            Check("Sandbox programs listed", apps.Count == 4 && fake is not null, string.Join(", ", apps.Select(a => a.Name)));
            if (fake is null) return;
            Check("Fake Notes: details read", fake is { Publisher: "Fakeco Software, Inc.", Version: "2.1.0", SizeBytes: 1234 * 1024L, JustForMe: false } && fake.InstallDate?.Date == DateTime.Today && fake.Plan.CanQuiet,
                $"{fake.Publisher} {fake.Version} {fake.SizeBytes} {fake.Plan}");
            var broken = apps.First(a => a.Name == "Ghost App");
            Check("Ghost App is a broken entry", broken.Orphaned);

            var found = new LeftoverFinder(fake, apps).Find();
            File.WriteAllLines(Path.Combine(fs, "found.txt"), found.Select(f => f.ToString()));
            bool Has(Func<Leftover, bool> f, MatchLevel level) => found.Any(x => f(x) && x.Match == level);
            Check("Finds: its uninstaller (ticked)", found.Any(f => f is { Kind: LeftoverKind.Uninstaller, Selected: true }));
            Check("Finds: install folder (normal, ticked)", found.Any(f => f.Kind == LeftoverKind.Folder && f.Path == install && f is { Match: MatchLevel.Regular, Selected: true }));
            Check("Finds: Apps & features entry (normal)", Has(f => f.Kind == LeftoverKind.RegistryKey && f.Path.EndsWith(@"Uninstall\FakeNotes"), MatchLevel.Regular));
            Check("Finds: shortcuts (normal)", Has(f => f.Kind == LeftoverKind.Shortcut && f.Path.EndsWith(@"Desktop\Fake Notes.lnk"), MatchLevel.Regular)
                                               && Has(f => f.Kind == LeftoverKind.Shortcut && f.Path.EndsWith(@"Fake Notes\Fake Notes.lnk"), MatchLevel.Regular));
            Check("Finds: Start menu folder", found.Any(f => f.Kind == LeftoverKind.Folder && f.Path == Path.Combine(p.StartMenu, "Fake Notes")));
            Check("Finds: AppData folder (belongs, not ticked)", found.Any(f => f.Path == Path.Combine(p.AppData, "Fake Notes") && f is { Match: MatchLevel.Strong, Selected: false }));
            Check("Finds: folder under the publisher's folder", Has(f => f.Path == Path.Combine(p.LocalAppData, "Fakeco", "Fake Notes"), MatchLevel.Strong));
            Check("Finds: LocalLow", Has(f => f.Path == Path.Combine(p.LocalLow, "Fake Notes"), MatchLevel.Strong));
            Check("Finds: similar name in ProgramData (similar)", Has(f => f.Path == Path.Combine(p.ProgramData, "FakeNotes Helper"), MatchLevel.Possible));
            Check("Finds: Documents folder only as similar", Has(f => f.Path == Path.Combine(p.Documents, "Fake Notes"), MatchLevel.Possible));
            Check("Finds: settings keys", Has(f => f.Path.EndsWith(@"Software\Fakeco Software\Fake Notes"), MatchLevel.Strong) && Has(f => f.Path.EndsWith(@"SOFTWARE\WOW6432Node\Fake Notes"), MatchLevel.Strong));
            Check("Finds: publisher's key holding only it (similar)", Has(f => f.Path.EndsWith(@"Software\Fakeco Software"), MatchLevel.Possible));
            Check("Finds: Run entry + App Paths + service + firewall rule + file type + task",
                found.Any(f => f.ValueName == "FakeNotesTray") && found.Any(f => f.Path.EndsWith(@"App Paths\FakeNotes.exe")) && found.Any(f => f is { Kind: LeftoverKind.Service, ServiceName: "FakeNotesSvc" })
                && found.Any(f => f is { Kind: LeftoverKind.FirewallRule, RuleId: "{FAKE-RULE-1}" }) && found.Any(f => f.Path.EndsWith(@"Classes\FakeNotes.Document")) && found.Any(f => f is { Kind: LeftoverKind.Task, TaskName: @"\FakeNotes Update" }));
            Check("Finds: Windows traces (MUI cache, crash dump)", found.Any(f => f.Group == "Windows traces" && f.Kind == LeftoverKind.RegistryValue) && found.Any(f => f.Path.EndsWith("FakeNotes.exe.4242.dmp")));
            Check("Leaves alone: other programs and their files", !found.Any(f => f.Path.Contains("Other") || f.Path.Contains("Notepad++") || f.Path.Contains("OneDrive") || f.ValueName == "OneDrive" || f.RuleId == "{OTHER-RULE}"
                                                                               || f.Path == Path.Combine(p.ProgramFiles, "Fakeco") || f.TaskName == @"\Other Thing"),
                string.Join("; ", found.Where(f => f.Path.Contains("Other") || f.Path == Path.Combine(p.ProgramFiles, "Fakeco"))));
            Check("Finds: publisher's folder holding only it (similar)", Has(f => f.Path == Path.Combine(p.LocalAppData, "Fakeco"), MatchLevel.Possible));
            Check("Only normal-uninstall items start ticked", found.Where(f => f.Selected).All(f => f.Match == MatchLevel.Regular));
            Check("Folder sizes counted", found.First(f => f.Path == install).Size >= 50_000);

            // Remove: the uninstaller quietly, plus everything that belongs to it (not the similar-name ones).
            foreach (var f in found) f.Selected = f.Match <= MatchLevel.Strong;
            var log = new List<string>();
            var job = new UninstallJob(fake, found.Where(f => f.Selected), Cli.BackupFolder) { Quiet = true, MustBeQuiet = true, Timeout = TimeSpan.FromSeconds(60), MakeRestorePoint = true };
            job.Log += log.Add;
            var report = await job.RunAsync();
            File.WriteAllLines(Path.Combine(fs, "log.txt"), log);
            Check("Restore point asked for first (faked here)", restorePoints.Count == 1 && restorePoints[0].Contains("Fake Notes") && log.IndexOf("Restore point made.") is >= 0 and < 3, string.Join(" / ", log.Take(3)));
            Check("Quiet uninstall ran and the program is gone", report is { UninstallerRan: true, Uninstalled: true } && !Reg.Exists(uninstallKey), report.Message);
            Check("Everything chosen removed", report.Ok && found.Where(f => f.Selected && f.Kind != LeftoverKind.Uninstaller).All(f => !UninstallJob.Exists(f)),
                string.Join("; ", report.Steps.Where(s => s.Outcome == StepOutcome.Failed).Select(s => UninstallJob.Describe(s.Item) + ": " + s.Message)));
            Check("Similar-name items kept (not chosen)", Directory.Exists(Path.Combine(p.ProgramData, "FakeNotes Helper")) && Directory.Exists(Path.Combine(p.Documents, "Fake Notes")));
            Check("Others kept", Directory.Exists(other) && Directory.Exists(Path.Combine(p.AppData, "Notepad++")) && Reg.ValueExists(L(RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"), "OneDrive")
                                  && tasks.Contains(@"\Other Thing") && File.Exists(Path.Combine(p.Desktop, "Other Tool.lnk")));
            Check("Empty publisher folder removed after its last program folder", !Directory.Exists(Path.Combine(p.LocalAppData, "Fakeco")));
            var reg = report.BackupFolder is { } bdir ? Path.Combine(bdir, "registry.reg") : "";
            var regText = File.Exists(reg) ? await File.ReadAllTextAsync(reg) : "";
            Check("Registry backed up first (.reg)", regText.StartsWith("Windows Registry Editor Version 5.00") && regText.Contains("Fake Notes") && regText.Contains("FakeNotesTray") && regText.Contains("\"Theme\"=\"dark\""), reg);
            Check("Backup lists what was removed", report.BackupFolder is { } b2 && File.ReadAllText(Path.Combine(b2, "removed.txt")).Contains("FakeNotesSvc") && !File.Exists(Path.Combine(b2, "removing.txt")));
            Check("Folders went through the shell delete", report.Steps.Any(s => s.Item.Kind == LeftoverKind.Folder && s.Outcome == StepOutcome.Removed) && log.Any(l => l.Contains("Removed") && l.Contains("AppData")));

            // Restoring the .reg puts a removed key back (regedit's format is right).
            var restore = Runner.Tool("reg.exe", $"import \"{reg}\"", 30_000);
            Check("Backup .reg imports back", restore.Code == 0 && Reg.Exists(L(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Fakeco Software\Fake Notes")), restore.Output.Trim());

            // No quiet mode: the command line refuses rather than opening a window.
            var loud = apps.First(a => a.Name == "Loud App");
            var loudJob = await new UninstallJob(loud, new LeftoverFinder(loud, apps).Find(), Cli.BackupFolder) { MustBeQuiet = true }.RunAsync();
            Check("No quiet mode: nothing started, reported", loudJob is { NotQuiet: true, UninstallerRan: false } && loudJob.Message.Contains("can't be removed quietly") && Reg.Exists(loud.Key!), loudJob.Message);

            // Broken entry: clean-up only.
            var ghostFound = new LeftoverFinder(broken, apps).Find();
            Check("Broken entry: no uninstaller step, entry + folder offered", ghostFound.All(f => f.Kind != LeftoverKind.Uninstaller) && ghostFound.Any(f => f.Kind == LeftoverKind.RegistryKey && f.Selected) && ghostFound.Any(f => f.Path.EndsWith("Ghost App") && f.Selected));

            // Command line, against the same sandbox.
            var (code, text) = CliRun("list");
            Check("CLI list", code == 0 && text.Contains("Loud App") && text.Contains("Ghost App") && !text.Contains("Fake Notes"), text.Split('\n').Length + " lines");
            (code, text) = CliRun("remove", "Loud App");
            Check("CLI remove: no quiet mode → skipped, exit 3", code == Cli.NotQuiet && text.Contains("SKIPPED") && text.Contains("can't be removed quietly"), text.Trim());
            (code, text) = CliRun("leftovers", "Ghost");
            Check("CLI leftovers: lists, changes nothing", code == 0 && text.Contains("Ghost App") && Reg.Exists(broken.Key!), text.Trim());
            (code, text) = CliRun("remove", "Ghost App", "--dry-run");
            Check("CLI dry run", code == 0 && text.Contains("WOULD REMOVE") && Reg.Exists(broken.Key!));
            (code, text) = CliRun("remove", "Ghost App");
            Check("CLI remove of a broken entry: cleaned up", code == 0 && !Reg.Exists(broken.Key!) && !Directory.Exists(Path.Combine(p.ProgramFiles, "Ghost App")), text.Trim());
            (code, text) = CliRun("remove", "nothing-like-this");
            Check("CLI: not found → exit 2", code == Cli.NotFound && text.Contains("NOT FOUND"));
            (code, text) = CliRun("remove", "Other Tool");
            Check("CLI: machine-wide program needs an elevated prompt (or has no quiet mode)", code is Cli.NeedsAdmin or Cli.NotQuiet && Directory.Exists(other), text.Trim());

            // Forced uninstall: Fake Notes is gone from the list, but "similar" leftovers stayed behind.
            Check("Forced: refuses short names and Windows folders", Forced.Problem("ab", null) is not null && Forced.Problem("Fake Notes", p.Windows) is not null && Forced.Problem("Fake Notes", null) is null);
            var forced = new LeftoverFinder(Forced.App("Fake Notes", null), AppScanner.Scan()).Find();
            Check("Forced: finds what's left by name, no uninstaller step", forced.All(f => f.Kind != LeftoverKind.Uninstaller) && forced.Any(f => f.Path == Path.Combine(p.ProgramData, "FakeNotes Helper"))
                                                                         && forced.Any(f => f.Path == Path.Combine(p.Documents, "Fake Notes")), string.Join("; ", forced.Select(f => f.Path)));
            (code, text) = CliRun("forced", "Fake Notes", "--deep-all", "--dry-run");
            Check("CLI forced --dry-run lists, changes nothing", code == 0 && text.Contains("FakeNotes Helper") && Directory.Exists(Path.Combine(p.ProgramData, "FakeNotes Helper")), text.Trim());
            (code, text) = CliRun("forced", "Fake Notes", "--deep-all");
            Check("CLI forced removes them", code == 0 && !Directory.Exists(Path.Combine(p.ProgramData, "FakeNotes Helper")) && !Directory.Exists(Path.Combine(p.Documents, "Fake Notes")), text.Trim());

            // Hunter: from a running program's file to its installed program.
            var now = AppScanner.Scan();
            Check("Hunter: a file in a program's folder", Hunter.ForPath(Path.Combine(other, "Other.exe"), now)?.Name == "Other Tool");
            Check("Hunter: a file elsewhere belongs to nothing", Hunter.ForPath(Path.Combine(fs, "portable", "tool.exe"), now) is null);
            (code, text) = CliRun("which", Path.Combine(other, "Other.exe"));
            Check("CLI which", code == 0 && text.Contains("belongs to Other Tool"), text.Trim());

            // Export.
            var exportFile = Path.Combine(fs, "programs.csv");
            (code, text) = CliRun("list", "--csv", exportFile);
            var csv = File.Exists(exportFile) ? await File.ReadAllTextAsync(exportFile) : "";
            Check("CLI list --csv", code == 0 && csv.TrimStart('﻿').StartsWith("Name,Publisher") && csv.Contains("\"Other Tool\",\"Fakeco Software, Inc.\""), csv.Split('\n').FirstOrDefault());
            var html = Export.Html(now, "TEST-PC");
            Check("HTML export", html.Contains("<title>Installed programs - TEST-PC</title>") && html.Contains("Other Tool") && html.Contains("Fakeco Software, Inc."));
        }
        finally
        {
            Reg.TestRoot = null;
            RestorePoint.Create = oldRestore;
            Places.Current = oldPlaces;
            LeftoverFinder.TaskXml = oldTasks;
            UninstallJob.TaskExists = oldTaskExists;
            UninstallJob.DeleteTask = oldDeleteTask;
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(regRoot, throwOnMissingSubKey: false);
                using var parent = Registry.CurrentUser.OpenSubKey(@"Software\pxdUninstallerSelfTest", true);
                if (parent is { SubKeyCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\pxdUninstallerSelfTest", false);
            }
            catch
            {
                /* left for the next run */
            }

            Check("Sandbox registry removed", Registry.CurrentUser.OpenSubKey(regRoot) is null);
            R.AppendLine();
        }
    }

    private static (int Code, string Text) CliRun(params string[] args)
    {
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            return (Cli.Run(args), sw.ToString());
        }
        finally
        {
            Console.SetOut(old);
        }
    }

    // ---------- Window ----------

    public static List<InstalledApp> SampleApps()
    {
        InstalledApp A(string name, string pub, string ver, double mb, int daysAgo, string method, bool quiet, AppKind kind = AppKind.Program, bool mine = false, bool missing = false)
        {
            var a = new InstalledApp
            {
                Id = "sample:" + name, Name = name, Publisher = pub, Version = ver, Kind = kind, JustForMe = mine || kind == AppKind.Store,
                SizeBytes = (long)(mb * 1024 * 1024), InstallDate = DateTime.Today.AddDays(-daysAgo),
                InstallLocation = kind == AppKind.Store ? "" : mine ? $@"C:\Users\alex\AppData\Local\{Text.CleanName(name)}" : $@"C:\Program Files\{Text.CleanName(name)}",
            };
            a.Plan = missing
                ? new UninstallPlan { Method = "Uninstaller missing", Missing = true, WhyNotQuiet = "Its uninstaller (unins000.exe) is no longer there. The leftovers can still be cleaned up." }
                : new UninstallPlan
                {
                    Method = method, Command = method == "Windows Installer (MSI)" ? "MsiExec.exe /X{6F3C2A51-7C4B-4E1A-9B0E-2D8F5A6C1E33}" : $"\"C:\\Program Files\\{Text.CleanName(name)}\\uninstall.exe\"",
                    QuietCommand = quiet ? (method == "Windows Installer (MSI)" ? "MsiExec.exe /X{6F3C2A51-7C4B-4E1A-9B0E-2D8F5A6C1E33} /qn /norestart" : $"\"C:\\Program Files\\{Text.CleanName(name)}\\uninstall.exe\" /S") : null,
                    WhyNotQuiet = quiet ? null : "Its uninstaller (uninstall.exe) has no known quiet mode.", IsMsi = method.Contains("MSI"), IsStore = kind == AppKind.Store,
                };
            return a;
        }

        return
        [
            A("7-Zip 24.08 (x64)", "Igor Pavlov", "24.08", 5.9, 210, "NSIS", true),
            A("Audacity 3.7.1", "Audacity Team", "3.7.1", 92, 44, "Inno Setup", true),
            A("Blender", "Blender Foundation", "4.2.3", 1024, 12, "Windows Installer (MSI)", true),
            A("Discord", "Discord Inc.", "1.0.9172", 410, 3, "Squirrel", true, mine: true),
            A("GIMP 2.10.38", "The GIMP Team", "2.10.38", 410, 300, "Inno Setup", true),
            A("Git", "The Git Development Community", "2.47.0", 330, 90, "Inno Setup", true),
            A("HP Smart Print Helper", "HP Inc.", "51.3", 46, 600, "InstallShield", false),
            A("Mozilla Firefox (x64 en-US)", "Mozilla", "132.0.1", 250, 5, "NSIS", true),
            A("Notepad++ (64-bit x64)", "Notepad++ Team", "8.7.1", 15, 120, "NSIS", true),
            A("OBS Studio", "OBS Project", "30.2.3", 520, 60, "NSIS", true),
            A("Old Scanner Tool", "Contoso", "1.2", 0, 1500, "", false, missing: true),
            A("PowerToys (Preview) x64", "Microsoft Corporation", "0.86.0", 760, 20, "WiX bundle", true),
            A("Spotify", "Spotify AB", "1.2.50.335.0", 0, 33, "Store app", true, AppKind.Store),
            A("Steam", "Valve Corporation", "2.10.91.91", 900, 400, "NSIS", true),
            A("VLC media player", "VideoLAN", "3.0.21", 160, 250, "NSIS", true),
            A("WhatsApp", "WhatsApp Inc.", "2.2445.4.0", 0, 9, "Store app", true, AppKind.Store),
            A("Zoom Workplace (64-bit)", "Zoom Video Communications, Inc.", "6.2.6", 300, 15, "Windows Installer (MSI)", true),
            A("Visualizer Pro", "Fabrikam", "5.1", 2048, 70, "Own uninstaller", false),
        ];
    }

    private static List<Leftover> SampleLeftovers()
    {
        Leftover L(LeftoverKind k, string group, string path, MatchLevel m, string why, long size = 0, string? value = null) =>
            new() { Kind = k, Group = group, Path = path, Match = m, Why = why, Size = size, Selected = m == MatchLevel.Regular, ValueName = value, ServiceName = k == LeftoverKind.Service ? path : null, TaskName = k == LeftoverKind.Task ? path : null };
        return
        [
            L(LeftoverKind.Uninstaller, "Uninstall", "OBS Studio", MatchLevel.Regular, "Runs its own uninstaller (NSIS)"),
            L(LeftoverKind.Folder, "Program files", @"C:\Program Files\obs-studio", MatchLevel.Regular, "Install folder", 545_000_000),
            L(LeftoverKind.Folder, "App data", @"C:\Users\alex\AppData\Roaming\obs-studio", MatchLevel.Strong, "Folder in AppData\\Roaming with its name", 38_400_000),
            L(LeftoverKind.Folder, "App data", @"C:\ProgramData\obs-studio-hook", MatchLevel.Possible, "Folder in ProgramData (all users) with its name", 2_100_000),
            L(LeftoverKind.Shortcut, "Shortcuts", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\OBS Studio\OBS Studio (64bit).lnk", MatchLevel.Regular, "Start menu (all users) shortcut to the program"),
            L(LeftoverKind.Shortcut, "Shortcuts", @"C:\Users\Public\Desktop\OBS Studio.lnk", MatchLevel.Regular, "Public desktop shortcut to the program"),
            L(LeftoverKind.RegistryKey, "Registry", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OBS Studio", MatchLevel.Regular, "Its Apps & features entry"),
            L(LeftoverKind.RegistryKey, "Registry", @"HKLM\SOFTWARE\OBS Studio", MatchLevel.Strong, "Settings key with its name"),
            L(LeftoverKind.RegistryKey, "Registry", @"HKCU\Software\Classes\OBS.Scene", MatchLevel.Strong, "File type or link handler that opens obs64.exe"),
            L(LeftoverKind.RegistryKey, "Registry", @"HKLM\SOFTWARE\Classes\CLSID\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}", MatchLevel.Strong, "COM component \"OBS Virtual Camera\" in obs-virtualcam-module64.dll"),
            L(LeftoverKind.RegistryValue, "Startup and services", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", MatchLevel.Strong, "Starts with Windows: \"C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe\" --minimize-to-tray", value: "OBS Studio"),
            L(LeftoverKind.Task, "Scheduled tasks", @"\OBS Studio Update", MatchLevel.Possible, "Task with its name"),
            L(LeftoverKind.FirewallRule, "Firewall", "obs64.exe", MatchLevel.Strong, "Firewall rule (incoming) for obs64.exe"),
            L(LeftoverKind.RegistryValue, "Windows traces", @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", MatchLevel.Strong, "Windows' cached name of the program", value: @"C:\Program Files\obs-studio\bin\64bit\obs64.exe.FriendlyAppName"),
            L(LeftoverKind.File, "Windows traces", @"C:\Windows\Prefetch\OBS64.EXE-5A9C2B11.pf", MatchLevel.Strong, "Windows prefetch file", 61_000),
        ];
    }

    private static MainWindow OffScreen(List<InstalledApp>? apps = null) => new(apps ?? SampleApps())
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Position = new PixelPoint(-30000, -30000),
        ShowActivated = false,
        ShowInTaskbar = false,
        SelfTestMode = true,
    };

    private static void Shot(Window w, string file)
    {
        w.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var bmp = new RenderTargetBitmap(new PixelSize((int)w.Width, (int)w.Height));
        bmp.Render(w);
        var dir = Sample ? Path.Combine(Dir!, "screenshots") : Dir!;
        Directory.CreateDirectory(dir);
        bmp.Save(Path.Combine(dir, file), PngBitmapEncoderOptions.Default);
    }

    private static async Task Render()
    {
        R.AppendLine("== Window ==");
        var apps = SampleApps();
        var w = OffScreen(apps);
        w.Show();
        await Task.Delay(100);
        Check("Lists every program", w.Shown == apps.Count, $"{w.Shown} shown");
        w.ShowGroup(2);
        Check("Store apps group", w.Shown == 2);
        w.ShowGroup(5);
        Check("No quiet uninstall group", w.Shown == 2, w.Shown.ToString());
        w.ShowGroup(6);
        Check("Broken entries group", w.Shown == 1);
        w.ShowGroup(0);
        var obs = apps.First(a => a.Name == "OBS Studio");
        w.Select(obs);
        await Task.Delay(50);
        Check("Selecting shows details", w.Selected == obs);
        Shot(w, "programs.png");

        w.Select(apps.First(a => a.Name == "Visualizer Pro"));
        await Task.Delay(50);
        Shot(w, "no-quiet.png");

        w.Select(obs);
        await w.OpenCleanupAsync(obs, null, false, SampleLeftovers());
        await Task.Delay(50);
        Check("Clean-up page opens with normal items ticked", w.CleanupShowing && w.Leftovers.Where(l => l.Selected).All(l => l.Match == MatchLevel.Regular) && w.Leftovers.Count(l => l.Selected) == 5);
        Shot(w, "cleanup.png");
        w.CloseCleanup();
        Check("Back to the list", !w.CleanupShowing);

        // Batch: several picked.
        var batch = apps.Where(a => a.Name is "Audacity 3.7.1" or "Discord" or "OBS Studio" or "Visualizer Pro").ToList();
        w.SelectMany(batch);
        await Task.Delay(50);
        Check("Batch: several programs picked", w.Picked.Count == 4, w.Picked.Count.ToString());
        Shot(w, "batch.png");
        await w.OpenBatchCleanupAsync(batch, [(batch[2], SampleLeftovers()), (batch[1], [new Leftover { Kind = LeftoverKind.Uninstaller, Match = MatchLevel.Regular, Group = "Uninstall", Path = "Discord", Why = "Runs its own uninstaller (Squirrel)", Selected = true },
            new Leftover { Kind = LeftoverKind.Folder, Match = MatchLevel.Strong, Group = "App data", Path = @"C:\Users\alex\AppData\Roaming\discord", Why = "Folder in AppData\\Roaming with its name", Size = 310_000_000 }])]);
        Check("Batch clean-up page shows every program", w.CleanupShowing && w.Leftovers.Count == SampleLeftovers().Count + 2);
        w.CloseCleanup();

        // Forced uninstall and Hunter pages.
        w.ShowForced("Old Scanner Tool", @"C:\Program Files\Old Scanner");
        await Task.Delay(30);
        Check("Forced page opens", w.CleanupShowing);
        Shot(w, "forced.png");
        MainWindow.WindowSource = () =>
        [
            new OpenWindow("OBS 30.2.3 - Profile: Untitled - Scenes: Untitled", @"C:\Program Files\OBS Studio\bin\64bit\obs64.exe", 1),
            new OpenWindow("Discord", @"C:\Users\alex\AppData\Local\Discord\app-1.0.9172\Discord.exe", 2),
            new OpenWindow("Mozilla Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", 3),
            new OpenWindow("putty", @"C:\Users\alex\Downloads\putty.exe", 4),
        ];
        w.ShowHunter();
        await Task.Delay(30);
        Shot(w, "hunter.png");
        MainWindow.WindowSource = Hunter.Windows;
        Check("Real open windows listed without errors", Hunter.Windows() is { } real && real.All(r => r.ExePath.Length > 0), $"{Hunter.Windows().Count} windows");
        w.CloseCleanup();
        w.Close();
        R.AppendLine();
    }

    private static async Task Placement()
    {
        R.AppendLine("== Window placement ==");
        const string key = "selftest";
        var file = Path.Combine(Program.DataFolder, "windows.json");
        if (File.Exists(file)) File.Delete(file);
        var a = OffScreen();
        pxdStandard.pxdChrome.RememberPlacement(a, key);
        a.Show();
        await Task.Delay(50);
        a.Width = 1100;
        a.Height = 700;
        a.Position = new PixelPoint(-29000, -29000);
        a.UpdateLayout();
        await Task.Delay(50);
        a.Close();
        var json = File.Exists(file) ? await File.ReadAllTextAsync(file) : "";
        Check("Placement saved", json.Contains("\"selftest\"") && json.Contains("\"Width\":1100") && json.Contains("\"Height\":700"), json);
        var b = OffScreen();
        pxdStandard.pxdChrome.RememberPlacement(b, key);
        Check("Placement restored (size), off-screen spot ignored", b.Width == 1100 && b.Height == 700 && b.Position == new PixelPoint(-30000, -30000), $"{b.Width}x{b.Height} at {b.Position}");
        R.AppendLine();
    }

    private static void SingleInstanceCheck()
    {
        R.AppendLine("== Single instance ==");
        var name = "pxdUninstaller-selftest-" + Guid.NewGuid().ToString("N")[..8];
        string[]? got = null;
        using var done = new ManualResetEventSlim();
        using (SingleInstance.Listen(name, args => { got = args; done.Set(); }))
        {
            var sent = SingleInstance.Send(name, ["--from-second-start"]);
            done.Wait(3000);
            Check("Second start reaches the running copy", sent && got is ["--from-second-start"], got is null ? "nothing received" : string.Join("|", got));
        }

        R.AppendLine();
    }

    private static async Task UpdateChecks()
    {
        R.AppendLine("== Update before start (local fake release, temp folders) ==");
        foreach (var (name, ok, detail) in await SelfTestUpdate.RunAsync(Dir!))
            Check(name, ok, detail.Length > 0 ? detail : null);
        R.AppendLine();
    }
}
