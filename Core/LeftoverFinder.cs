using System.Xml.Linq;
using Microsoft.Win32;

namespace pxdUninstaller.Core;

/// <summary>
/// Finds everything that belongs to one program, everywhere programs keep things: its folders (Program Files,
/// ProgramData, AppData, LocalLow, Documents, Temp), shortcuts, registry keys, startup entries, services, scheduled
/// tasks, firewall rules, file types and Windows' own traces (MUI cache, compatibility records, prefetch, crash dumps).
/// Anything another installed program uses, and every system folder, is left out.
/// </summary>
public sealed class LeftoverFinder
{
    private readonly InstalledApp _app;
    private readonly Places _p;
    private readonly List<string> _dirs = [];
    private readonly HashSet<string> _exes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _strong = [];
    private readonly HashSet<string> _weak = [];
    private readonly HashSet<string> _publisher = [];
    private readonly HashSet<string> _others = [];
    private readonly List<string> _otherDirs = [];
    private readonly List<(string Dir, string Name)> _sharedDirs = [];
    private readonly Dictionary<string, Leftover> _found = [];

    /// <summary>Words too common to mean anything on their own.</summary>
    private static readonly HashSet<string> Generic =
    [
        "microsoft", "windows", "update", "updates", "updater", "common", "commonfiles", "system", "driver", "drivers", "runtime", "tools", "tool",
        "program", "programs", "setup", "installer", "install", "app", "apps", "application", "software", "data", "temp", "cache", "user",
        "users", "games", "game", "plugins", "plugin", "config", "settings", "logs", "log", "packages", "package", "shared", "service",
        "services", "client", "server", "helper", "launcher", "default", "local", "roaming", "documents", "desktop", "files", "browser",
        "player", "office", "studio", "redistributable", "framework", "sdk", "net", "uninstall", "uninstaller", "support", "assistant",
        "manager", "inc", "llc", "corp", "corporation", "company", "software", "intel", "amd", "nvidia", "google", "adobe", "apple",
    ];

    /// <summary>Registry keys under SOFTWARE that are never offered, whatever their name.</summary>
    private static readonly HashSet<string> RegistryKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "WOW6432Node", "Clients", "RegisteredApplications", "ODBC", "Windows", "Intel", "Wow6432Node",
        "Partner", "DefaultUserEnvironment", "Khronos", "OEM", "Setup", "Description", "Hardware", "Mozilla Plugins",
    };

    public LeftoverFinder(InstalledApp app, IEnumerable<InstalledApp> all, Places? places = null)
    {
        _app = app;
        _p = places ?? Places.Current;
        foreach (var other in all)
        {
            if (ReferenceEquals(other, app) || other.Id == app.Id) continue;
            var n = Text.Norm(Text.CleanName(other.Name));
            if (n.Length >= 3) _others.Add(n);
            if (other.InstallLocation.Length > 3 && other.Kind == AppKind.Program)
            {
                _otherDirs.Add(other.InstallLocation.TrimEnd('\\'));
                if (Same(other.InstallLocation, app.InstallLocation)) _sharedDirs.Add((other.InstallLocation, other.Name));
            }

            // Folders other programs' uninstallers and icons live in (Blizzard games use Battle.net's Agent folder, for example).
            if (other.Kind == AppKind.Program)
                foreach (var path in new[] { Text.SplitCommand(other.UninstallString).Exe, Text.SplitIcon(other.DisplayIcon).Path })
                    if (path.Length > 3 && SafeFullyQualified(path) && System.IO.Path.GetDirectoryName(path) is { Length: > 3 } d)
                        _otherUsed.Add(d.TrimEnd('\\'));
        }

        BuildTerms();
    }

    private readonly HashSet<string> _otherUsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _derived = new(StringComparer.OrdinalIgnoreCase);

    private static bool SafeFullyQualified(string p)
    {
        try
        {
            return System.IO.Path.IsPathFullyQualified(p) && p.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A recorded install folder is ticked from the start only where programs normally live and not at the top of a drive.</summary>
    private bool UsualPlace(string dir)
    {
        if (UnderProgramRoot(dir)) return true;
        var root = System.IO.Path.GetPathRoot(dir) ?? "";
        var depth = dir.TrimEnd('\\')[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        return depth >= 2 && !Places.Within(dir, _p.UserProfile);
    }

    /// <summary>The program's install folders (as known before it was uninstalled).</summary>
    public IReadOnlyList<string> Dirs => _dirs;

    private static bool Same(string a, string b) =>
        a.Length > 0 && b.Length > 0 && a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private void BuildTerms()
    {
        void AddStrong(string? s)
        {
            var n = Text.Norm(s);
            if (n.Length >= 3 && !Generic.Contains(n)) _strong.Add(n);
        }

        var clean = Text.CleanName(_app.Name);
        AddStrong(_app.Name);
        AddStrong(clean);
        var pub = Text.CleanPublisher(_app.Publisher);
        foreach (var p in new[] { _app.Publisher, pub, pub.Split(' ').FirstOrDefault() })
        {
            var n = Text.Norm(p);
            if (n.Length >= 3 && !Generic.Contains(n)) _publisher.Add(n);
        }

        // "Mozilla Firefox" by Mozilla → also "Firefox".
        foreach (var p in _publisher)
        {
            var cn = Text.Norm(clean);
            if (cn.StartsWith(p) && cn.Length - p.Length >= 4) AddStrong(cn[p.Length..]);
        }

        if (_app.Kind == AppKind.Store && _app.PackageFullName is { } pkg)
        {
            var name = pkg.Split('_')[0];
            AddStrong(name);
            AddStrong(name.Split('.').Last());
        }

        // Install folders: the recorded one, else the folder the uninstaller or icon lives in (when it's a program's own folder).
        if (_app.InstallLocation.Length > 3) _dirs.Add(_app.InstallLocation.TrimEnd('\\'));
        if (_app.Kind == AppKind.Program)
        {
            foreach (var path in new[] { Text.SplitCommand(_app.UninstallString).Exe, Text.SplitIcon(_app.DisplayIcon).Path })
            {
                if (string.IsNullOrEmpty(path) || !System.IO.Path.IsPathFullyQualified(path)) continue;
                var dir = System.IO.Path.GetDirectoryName(path);
                if (dir is null || _p.IsProtected(dir) || _dirs.Any(d => Places.Within(dir, d))) continue;
                if (!UnderProgramRoot(dir)) continue;
                if (_otherDirs.Any(o => Places.Within(dir, o) || Places.Within(o, dir))) continue;   // another program's folder
                if (_otherUsed.Any(o => Places.Within(o, dir))) continue;                           // another program's uninstaller or icon is in it
                if (dir.Contains("InstallShield Installation Information", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
                    || dir.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase) || Places.Within(dir, _p.CommonFiles) || Places.Within(dir, _p.CommonFilesX86))
                    continue;
                // Squirrel keeps Update.exe one level above the app-x.y.z folders; that's the program's folder.
                _dirs.Add(dir.TrimEnd('\\'));
                _derived.Add(dir.TrimEnd('\\'));
            }
        }

        foreach (var d in _dirs)
        {
            AddStrong(System.IO.Path.GetFileName(d));
            if (!Directory.Exists(d)) continue;
            try
            {
                foreach (var exe in Directory.EnumerateFiles(d, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).Take(400))
                    _exes.Add(System.IO.Path.GetFileName(exe));
            }
            catch
            {
                /* unreadable folder */
            }
        }

        foreach (var s in _strong)
            if (s.Length >= 5)
                _weak.Add(s);
    }

    private bool UnderProgramRoot(string dir) =>
        new[] { _p.ProgramFiles, _p.ProgramFilesX86, _p.LocalPrograms, _p.AppData, _p.LocalAppData, _p.ProgramData }.Any(r => Places.Within(dir, r) && !Same(dir, r));

    private bool InDirs(string? path) => path is not null && _dirs.Any(d => Places.Within(path, d));

    /// <summary>How a folder or key name relates to the program: null when it doesn't.</summary>
    public MatchLevel? NameMatch(string name)
    {
        var n = Text.Norm(name);
        if (n.Length < 3 || Generic.Contains(n)) return null;
        if (_strong.Contains(n)) return MatchLevel.Strong;
        if (_others.Contains(n)) return null;   // another installed program's exact name
        foreach (var w in _weak)
            if ((n.StartsWith(w) || (n.Length >= 5 && w.StartsWith(n))) && Math.Abs(n.Length - w.Length) <= Math.Max(6, w.Length / 2))
                return MatchLevel.Possible;
        return null;
    }

    private bool PublisherMatch(string name) => _publisher.Contains(Text.Norm(name));

    private void Add(Leftover l)
    {
        if (_found.TryGetValue(l.Identity, out var have) && have.Match <= l.Match) return;
        l.Selected = l.Match == MatchLevel.Regular;
        _found[l.Identity] = l;
    }

    public List<Leftover> Find(CancellationToken ct = default)
    {
        _found.Clear();
        if (_app.Kind == AppKind.Program && !_app.Plan.Missing && _app.Plan.Command is not null)
        {
            Add(new Leftover
            {
                Kind = LeftoverKind.Uninstaller, Match = MatchLevel.Regular, Group = "Uninstall", Path = _app.Name,
                Command = _app.Plan.Command, Why = $"Runs its own uninstaller ({_app.Plan.Method})",
            });
        }

        void Step(Action a)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                a();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                /* one place failing never stops the rest */
            }
        }

        if (_app.Kind == AppKind.Store)
        {
            Step(StoreLeftovers);
        }
        else
        {
            Step(InstallFolders);
            Step(Folders);
            Step(Shortcuts);
            Step(UninstallEntry);
            Step(SoftwareKeys);
            Step(Startup);
            Step(AppPaths);
            Step(Services);
            Step(Firewall);
            Step(FileTypes);
            Step(Tasks);
            Step(Traces);
        }

        var list = _found.Values
            .OrderBy(l => Array.IndexOf(Leftover.Groups, l.Group))
            .ThenBy(l => l.Match)
            .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var l in list.Where(l => l.Kind == LeftoverKind.Folder))
        {
            ct.ThrowIfCancellationRequested();
            l.Size = FolderSize(l.Path);
        }

        return list;
    }

    // ---------- Store apps ----------

    private void StoreLeftovers()
    {
        Add(new Leftover
        {
            Kind = LeftoverKind.Uninstaller, Match = MatchLevel.Regular, Group = "Uninstall", Path = _app.Name,
            Command = _app.Plan.Command, Why = "Removes the Store app for this user (Remove-AppxPackage)",
        });
        if (_app.PackageFamilyName is { } family)
        {
            var data = System.IO.Path.Combine(_p.Packages, family);
            if (Directory.Exists(data))
                Add(new Leftover { Kind = LeftoverKind.Folder, Match = MatchLevel.Regular, Group = "App data", Path = data, Why = "The app's data and settings (removed with it)" });
        }

        Shortcuts();
    }

    // ---------- Folders ----------

    private void InstallFolders()
    {
        foreach (var d in _dirs)
        {
            if (!Directory.Exists(d) || _p.IsProtected(d)) continue;
            var shared = _sharedDirs.FirstOrDefault(s => Same(s.Dir, d));
            var otherInside = _otherDirs.FirstOrDefault(o => Places.Within(o, d) && !Same(o, d));
            if (otherInside is not null) continue;   // another program lives inside it
            var usedByOther = _otherUsed.Any(o => Places.Within(o, d));
            var derived = _derived.Contains(d);
            var (match, why) = shared.Name is not null ? (MatchLevel.Possible, $"Install folder, also used by {shared.Name}")
                : usedByOther ? (MatchLevel.Possible, "Install folder, but another program's uninstaller or icon is inside it")
                : derived ? (MatchLevel.Strong, "Folder its uninstaller or icon is in")
                : !UsualPlace(d) ? (MatchLevel.Strong, "Install folder (in an unusual place: check it first)")
                : (MatchLevel.Regular, "Install folder");
            Add(new Leftover { Kind = LeftoverKind.Folder, Group = "Program files", Path = d, Match = match, Why = why });
        }
    }

    private bool FolderAllowed(string path)
    {
        if (_p.IsProtected(path)) return false;
        if (_dirs.Any(d => Places.Within(path, d) && !Same(path, d))) return false;   // inside the install folder: covered by it
        foreach (var o in _otherDirs)
        {
            if (_dirs.Any(d => Same(d, o))) continue;
            if (Places.Within(o, path)) return false;                                   // holds another program
            if (Places.Within(path, o) && !_dirs.Any(d => Places.Within(o, d))) return false; // inside another program's folder
        }

        return true;
    }

    private void Folders()
    {
        (string Root, string Group, string What)[] roots =
        [
            (_p.ProgramFiles, "Program files", "Program Files"),
            (_p.ProgramFilesX86, "Program files", "Program Files (x86)"),
            (_p.LocalPrograms, "Program files", "your Programs folder"),
            (_p.CommonFiles, "Program files", "Common Files"),
            (_p.CommonFilesX86, "Program files", "Common Files (x86)"),
            (_p.ProgramData, "App data", "ProgramData (all users)"),
            (_p.AppData, "App data", "AppData\\Roaming"),
            (_p.LocalAppData, "App data", "AppData\\Local"),
            (_p.LocalLow, "App data", "AppData\\LocalLow"),
            (_p.Documents, "App data", "Documents"),
            (_p.PublicDocuments, "App data", "Public Documents"),
            (_p.Temp, "App data", "Temp"),
        ];
        foreach (var (root, group, what) in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (NameMatch(name) is { } m)
                {
                    if (!FolderAllowed(dir)) continue;
                    // Documents: only exact names (people keep their own files there).
                    var docs = Same(root, _p.Documents) || Same(root, _p.PublicDocuments);
                    if (docs && m != MatchLevel.Strong) continue;
                    Add(new Leftover
                    {
                        Kind = LeftoverKind.Folder, Group = group, Path = dir, Match = docs ? MatchLevel.Possible : m,
                        Why = docs ? $"Folder in {what} with its name (may hold your own files)" : $"Folder in {what} with its name",
                    });
                }
                else if (PublisherMatch(name))
                {
                    var inside = SafeDirs(dir).ToList();
                    var ours = inside.Where(i => NameMatch(System.IO.Path.GetFileName(i)) is not null && FolderAllowed(i)).ToList();
                    foreach (var i in ours)
                    {
                        Add(new Leftover
                        {
                            Kind = LeftoverKind.Folder, Group = group, Path = i, Match = NameMatch(System.IO.Path.GetFileName(i))!.Value,
                            Why = $"In the publisher's folder in {what}",
                        });
                    }

                    // The publisher's folder holds nothing else: offer it too.
                    var files = SafeFiles(dir).Any();
                    if (ours.Count > 0 && ours.Count == inside.Count && !files && FolderAllowed(dir))
                        Add(new Leftover { Kind = LeftoverKind.Folder, Group = group, Path = dir, Match = MatchLevel.Possible, Why = $"Publisher's folder in {what}, holds only this program" });
                }
            }
        }
    }

    // ---------- Shortcuts ----------

    private void Shortcuts()
    {
        (string Root, bool Deep, string What)[] roots =
        [
            (_p.StartMenu, true, "Start menu"), (_p.CommonStartMenu, true, "Start menu (all users)"),
            (_p.Desktop, false, "Desktop"), (_p.PublicDesktop, false, "Public desktop"),
            (_p.TaskbarPins, false, "Taskbar"), (_p.QuickLaunch, false, "Quick Launch"),
        ];
        foreach (var (root, deep, what) in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = deep, MaxRecursionDepth = 4, IgnoreInaccessible = true }).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var lnk in links)
            {
                var info = Native.ReadShortcut(lnk);
                var target = info?.Target ?? "";
                var name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                MatchLevel? m = null;
                string why = "";
                if (target.Length > 0 && InDirs(target))
                {
                    m = what.StartsWith("Start") || what.Contains("desktop", StringComparison.OrdinalIgnoreCase) || what == "Desktop" ? MatchLevel.Regular : MatchLevel.Strong;
                    why = $"{what} shortcut to the program";
                }
                else if (_app.Kind == AppKind.Store && _app.PackageFamilyName is { } fam && (info?.Args.Contains(fam, StringComparison.OrdinalIgnoreCase) == true || target.Contains(fam, StringComparison.OrdinalIgnoreCase)))
                {
                    m = MatchLevel.Strong;
                    why = $"{what} shortcut to the app";
                }
                else if (NameMatch(name) == MatchLevel.Strong && (target.Length == 0 || !File.Exists(target) && !Directory.Exists(target)))
                {
                    m = MatchLevel.Strong;
                    why = $"{what} shortcut with its name, pointing nowhere";
                }

                if (m is not null)
                    Add(new Leftover { Kind = LeftoverKind.Shortcut, Group = "Shortcuts", Path = lnk, Match = m.Value, Why = why });
            }

            // Start menu folders with the program's name.
            if (!deep) continue;
            foreach (var dir in SafeDirs(root))
            {
                if (NameMatch(System.IO.Path.GetFileName(dir)) is not { } m || _p.IsProtected(dir)) continue;
                var allOurs = SafeFiles(dir, recurse: true).All(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Native.ReadShortcut(f) is { } s && (InDirs(s.Target) || !File.Exists(s.Target)) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));
                Add(new Leftover
                {
                    Kind = LeftoverKind.Folder, Group = "Shortcuts", Path = dir,
                    Match = allOurs && m == MatchLevel.Strong ? (_dirs.Count > 0 ? MatchLevel.Regular : MatchLevel.Strong) : MatchLevel.Possible,
                    Why = $"{what} folder",
                });
            }
        }
    }

    // ---------- Registry ----------

    private void UninstallEntry()
    {
        if (_app.Key is not { } key) return;
        Add(new Leftover { Kind = LeftoverKind.RegistryKey, Group = "Registry", Path = key.Text, Reg = key, Match = MatchLevel.Regular, Why = "Its Apps & features entry" });
        if (_app.ProductCode is { } code && Packed(code) is { } packed)
        {
            foreach (var (place, why) in new[]
                     {
                         (new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes\Installer\Products\" + packed), "Windows Installer product record"),
                         (new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\" + packed), "Windows Installer product record"),
                         (new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Installer\Products\" + packed), "Windows Installer product record (this user)"),
                     })
            {
                if (Reg.Exists(place))
                    Add(new Leftover { Kind = LeftoverKind.RegistryKey, Group = "Registry", Path = place.Text, Reg = place, Match = MatchLevel.Regular, Why = why });
            }
        }
    }

    /// <summary>Windows Installer stores product codes "packed": each GUID part reversed.</summary>
    public static string? Packed(string guid)
    {
        var g = guid.Trim('{', '}').Replace("-", "");
        if (g.Length != 32) return null;
        static string Rev(string s) => new(s.Reverse().ToArray());
        var b = new System.Text.StringBuilder();
        b.Append(Rev(g[..8])).Append(Rev(g[8..12])).Append(Rev(g[12..16]));
        for (var i = 16; i < 32; i += 2) b.Append(g[i + 1]).Append(g[i]);
        return b.ToString().ToUpperInvariant();
    }

    private static readonly (RegistryHive Hive, RegistryView View)[] SoftwareViews =
        [(RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32)];

    private void SoftwareKeys()
    {
        foreach (var (hive, view) in SoftwareViews)
        {
            var software = new RegPlace(hive, view, hive == RegistryHive.CurrentUser ? "Software" : "SOFTWARE");
            foreach (var name in Reg.SubKeys(software))
            {
                if (RegistryKeep.Contains(name)) continue;
                var key = software.Child(name);
                if (NameMatch(name) is { } m)
                {
                    Add(new Leftover { Kind = LeftoverKind.RegistryKey, Group = "Registry", Path = key.Text, Reg = key, Match = m, Why = "Settings key with its name" });
                }
                else if (PublisherMatch(name))
                {
                    var inside = Reg.SubKeys(key);
                    var ours = inside.Where(i => NameMatch(i) is not null).ToList();
                    foreach (var i in ours)
                        Add(new Leftover { Kind = LeftoverKind.RegistryKey, Group = "Registry", Path = key.Child(i).Text, Reg = key.Child(i), Match = NameMatch(i)!.Value, Why = "Settings key under the publisher's key" });
                    if (ours.Count > 0 && ours.Count == inside.Length)
                        Add(new Leftover { Kind = LeftoverKind.RegistryKey, Group = "Registry", Path = key.Text, Reg = key, Match = MatchLevel.Possible, Why = "Publisher's key, holds only this program" });
                }
            }
        }
    }

    private static readonly (RegistryHive Hive, RegistryView View)[] RunViews =
        [(RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32)];

    private void Startup()
    {
        foreach (var (hive, view) in RunViews)
        {
            foreach (var sub in new[] { "Run", "RunOnce" })
            {
                var place = new RegPlace(hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\" + sub);
                using var k = Reg.Open(place);
                if (k is null) continue;
                foreach (var v in k.GetValueNames())
                {
                    var data = Reg.Str(k, v);
                    var path = Text.PathIn(data);
                    var hit = InDirs(path) || path is not null && _exes.Contains(System.IO.Path.GetFileName(path)) && !File.Exists(path);
                    if (!hit && !(NameMatch(v) == MatchLevel.Strong && (path is null || !File.Exists(path)))) continue;
                    Add(new Leftover { Kind = LeftoverKind.RegistryValue, Group = "Startup and services", Path = place.Text, ValueName = v, Reg = place, Match = MatchLevel.Strong, Why = $"Starts with Windows: {data}" });
                    // Task Manager's on/off switch for the same entry.
                    var approved = new RegPlace(hive, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + (view == RegistryView.Registry32 ? "Run32" : "Run"));
                    if (Reg.ValueExists(approved, v))
                        Add(new Leftover { Kind = LeftoverKind.RegistryValue, Group = "Startup and services", Path = approved.Text, ValueName = v, Reg = approved, Match = MatchLevel.Strong, Why = "Task Manager's startup switch for it" });
                }
            }
        }
    }

    private void AppPaths()
    {
        foreach (var (hive, view) in RunViews)
        {
            var root = new RegPlace(hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            foreach (var name in Reg.SubKeys(root))
            {
                using var k = Reg.Open(root.Child(name));
                var path = k is null ? null : Text.PathIn(Reg.Str(k, ""));
                if (InDirs(path) || _exes.Contains(name) && (path is null || !File.Exists(path)))
                    Add(new Leftover { Kind = LeftoverKind.RegistryKey, Group = "Registry", Path = root.Child(name).Text, Reg = root.Child(name), Match = MatchLevel.Strong, Why = $"Lets Windows find {name} by name" });
            }
        }
    }

    private void Services()
    {
        if (_dirs.Count == 0 && _exes.Count == 0) return;
        var root = new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services");
        foreach (var name in Reg.SubKeys(root))
        {
            using var k = Reg.Open(root.Child(name));
            if (k is null) continue;
            var image = Reg.Str(k, "ImagePath");
            var path = Text.PathIn(image);
            if (!InDirs(path)) continue;
            var display = Reg.Str(k, "DisplayName") ?? name;
            if (display.StartsWith('@')) display = Native.IndirectString(display) ?? name;
            Add(new Leftover
            {
                Kind = LeftoverKind.Service, Group = "Startup and services", Path = name, ServiceName = name, Reg = root.Child(name),
                Match = MatchLevel.Strong, Why = $"Windows service \"{display}\" runs {System.IO.Path.GetFileName(path)}",
            });
        }
    }

    private void Firewall()
    {
        if (_dirs.Count == 0) return;
        var place = new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
        using var k = Reg.Open(place);
        if (k is null) return;
        foreach (var id in k.GetValueNames())
        {
            var data = Reg.Str(k, id) ?? "";
            var app = Field(data, "App");
            if (app is null || !InDirs(Environment.ExpandEnvironmentVariables(app))) continue;
            var name = Field(data, "Name") ?? id;
            if (name.StartsWith('@')) name = Native.IndirectString(name) ?? name;
            var dir = Field(data, "Dir") == "Out" ? "outgoing" : "incoming";
            Add(new Leftover
            {
                Kind = LeftoverKind.FirewallRule, Group = "Firewall", Path = name, RuleId = id, Reg = place, ValueName = id,
                Match = MatchLevel.Strong, Why = $"Firewall rule ({dir}) for {System.IO.Path.GetFileName(app)}",
            });
        }
    }

    private static string? Field(string rule, string name)
    {
        foreach (var part in rule.Split('|'))
            if (part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return part[(name.Length + 1)..];
        return null;
    }

    private void FileTypes()
    {
        if (_dirs.Count == 0) return;
        foreach (var (hive, path) in new[] { (RegistryHive.CurrentUser, @"Software\Classes"), (RegistryHive.LocalMachine, @"SOFTWARE\Classes") })
        {
            var classes = new RegPlace(hive, RegistryView.Registry64, path);
            foreach (var name in Reg.SubKeys(classes))
            {
                if (name.StartsWith('.') || name.Equals("CLSID", StringComparison.OrdinalIgnoreCase) || name.Equals("Installer", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Local Settings", StringComparison.OrdinalIgnoreCase) || name.Equals("AppID", StringComparison.OrdinalIgnoreCase) || name == "*")
                    continue;
                var key = classes.Child(name);
                string? target;
                using (var cmd = Reg.Open(key.Child(@"shell\open\command")))
                    target = cmd is null ? null : Text.PathIn(Reg.Str(cmd, ""));
                if (target is null)
                {
                    using var icon = Reg.Open(key.Child("DefaultIcon"));
                    target = icon is null ? null : Text.PathIn(Reg.Str(icon, ""));
                }

                if (InDirs(target))
                    Add(new Leftover { Kind = LeftoverKind.RegistryKey, Group = "Registry", Path = key.Text, Reg = key, Match = MatchLevel.Strong, Why = $"File type or link handler that opens {System.IO.Path.GetFileName(target)}" });
            }

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                if (hive == RegistryHive.CurrentUser && view == RegistryView.Registry32) continue;
                var clsid = new RegPlace(hive, view, path + @"\CLSID");
                foreach (var id in Reg.SubKeys(clsid))
                {
                    foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                    {
                        using var s = Reg.Open(clsid.Child(id).Child(server));
                        if (s is null) continue;
                        var target = Text.PathIn(Reg.Str(s, ""));
                        if (!InDirs(target)) continue;
                        var key = clsid.Child(id);
                        using var named = Reg.Open(key);
                        var label = named is null ? null : Reg.Str(named, "");
                        Add(new Leftover { Kind = LeftoverKind.RegistryKey, Group = "Registry", Path = key.Text, Reg = key, Match = MatchLevel.Strong, Why = $"COM component{(label is null ? "" : $" \"{label}\"")} in {System.IO.Path.GetFileName(target)}" });
                        break;
                    }
                }
            }
        }
    }

    // ---------- Scheduled tasks ----------

    /// <summary>All tasks as (\Path\Name, commands): schtasks' XML for real, a tasks.xml next to the test folders in the self-test.</summary>
    public static Func<string?> TaskXml { get; set; } = () =>
    {
        var (code, output) = Runner.Tool("schtasks.exe", "/query /xml ONE", 60_000);
        return code == 0 ? output : null;
    };

    private void Tasks()
    {
        var xml = TaskXml();
        if (string.IsNullOrWhiteSpace(xml)) return;
        foreach (var (name, commands) in ParseTasks(xml))
        {
            if (name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
            var leaf = name[(name.LastIndexOf('\\') + 1)..];
            var hit = commands.FirstOrDefault(c => InDirs(Text.PathIn(c) ?? c) || _exes.Contains(System.IO.Path.GetFileName(Text.PathIn(c) ?? c)) && !File.Exists(Text.PathIn(c) ?? c));
            // Tasks are named "Firefox Default Browser Agent 308046B0…": a name starting with the program's name counts as similar.
            var nleaf = Text.Norm(leaf);
            MatchLevel? m = hit is not null ? MatchLevel.Strong
                : NameMatch(leaf) ?? (_weak.Any(w => nleaf.StartsWith(w)) ? MatchLevel.Possible : null);
            if (m is null) continue;
            Add(new Leftover
            {
                Kind = LeftoverKind.Task, Group = "Scheduled tasks", Path = name, TaskName = name, Match = m.Value,
                Why = hit is not null ? $"Runs {System.IO.Path.GetFileName(Text.PathIn(hit) ?? hit)}" : "Task with its name",
            });
        }
    }

    public static List<(string Name, List<string> Commands)> ParseTasks(string xml)
    {
        var list = new List<(string, List<string>)>();
        try
        {
            var doc = XDocument.Parse(xml.TrimStart('﻿'));
            string? name = null;
            foreach (var node in doc.Root!.Nodes())
            {
                if (node is XComment c)
                {
                    name = c.Value.Trim();
                }
                else if (node is XElement { Name.LocalName: "Task" } task && name is not null)
                {
                    var commands = task.Descendants().Where(e => e.Name.LocalName == "Command").Select(e => Environment.ExpandEnvironmentVariables(e.Value.Trim().Trim('"'))).ToList();
                    list.Add((name, commands));
                    name = null;
                }
            }
        }
        catch
        {
            /* unreadable output: no tasks */
        }

        return list;
    }

    // ---------- Windows' own traces ----------

    private void Traces()
    {
        if (_dirs.Count == 0) return;
        (RegPlace Place, string Why)[] valueLists =
        [
            (new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"), "Windows' cached name of the program"),
            (new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store"), "Program Compatibility Assistant record"),
            (new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"), "Compatibility setting"),
            (new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"), "Compatibility setting (all users)"),
            (new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched"), "Taskbar usage record"),
            (new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowJumpView"), "Jump list usage record"),
            (new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppLaunch"), "Launch count record"),
        ];
        foreach (var (place, why) in valueLists)
        {
            using var k = Reg.Open(place);
            if (k is null) continue;
            foreach (var v in k.GetValueNames())
            {
                var path = v.Contains(".FriendlyAppName") || v.Contains(".ApplicationCompany") ? v[..v.LastIndexOf('.')] : v;
                if (InDirs(path))
                    Add(new Leftover { Kind = LeftoverKind.RegistryValue, Group = "Windows traces", Path = place.Text, ValueName = v, Reg = place, Match = MatchLevel.Strong, Why = why });
            }
        }

        // Prefetch (needs administrator rights to read) and crash dumps of the program's exes.
        foreach (var exe in _exes)
        {
            foreach (var (dir, pattern, why) in new[]
                     {
                         (_p.Prefetch, exe.ToUpperInvariant() + "-*.pf", "Windows prefetch file"),
                         (_p.CrashDumps, exe + ".*.dmp", "Crash dump"),
                     })
            {
                foreach (var f in SafeFiles(dir, pattern: pattern))
                    Add(new Leftover { Kind = LeftoverKind.File, Group = "Windows traces", Path = f, Match = MatchLevel.Strong, Why = why, Size = SafeLength(f) });
            }

            foreach (var d in SafeDirs(_p.WerReports).Where(d => System.IO.Path.GetFileName(d).Contains("_" + exe + "_", StringComparison.OrdinalIgnoreCase)))
                Add(new Leftover { Kind = LeftoverKind.Folder, Group = "Windows traces", Path = d, Match = MatchLevel.Strong, Why = "Windows error report" });
        }
    }

    // ---------- Helpers ----------

    private static IEnumerable<string> SafeDirs(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList() : [];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFiles(string root, bool recurse = false, string pattern = "*")
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, pattern, new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = recurse, MaxRecursionDepth = 4 }).ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static long SafeLength(string f)
    {
        try
        {
            return new FileInfo(f).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Total size of a folder (stops counting after 50,000 files).</summary>
    public static long FolderSize(string dir)
    {
        long total = 0;
        var n = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                total += f.Length;
                if (++n > 50_000) break;
            }
        }
        catch
        {
            /* partial size */
        }

        return total;
    }
}
