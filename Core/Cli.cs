using System.Security.Principal;
using System.Text.Json;

namespace pxdUninstaller.Core;

/// <summary>
/// The command line (pxdu.exe, or pxdUninstaller.exe with a command). Everything it removes is removed quietly: an
/// uninstaller with no quiet mode is never started, and the program is reported instead.
/// </summary>
public static class Cli
{
    public static readonly string[] Commands = ["list", "show", "leftovers", "remove", "forced", "which", "help", "/?", "-h", "--help"];

    /// <summary>%APPDATA%\pxdUninstaller (window placement, settings, backups), or PXDU_DATA (tests).</summary>
    public static string DataFolder =>
        Environment.GetEnvironmentVariable("PXDU_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pxdUninstaller");

    public static string BackupFolder => Path.Combine(DataFolder, "Backups");

    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // Exit codes
    public const int Ok = 0, Error = 1, NotFound = 2, NotQuiet = 3, Failed = 4, NeedsAdmin = 5;

    private static TextWriter Out => Console.Out;

    public static int Run(string[] args, string exeName = "pxdu")
    {
        if (args.Length == 0 || args[0] is "help" or "/?" or "-h" or "--help")
        {
            Help(exeName);
            return Ok;
        }

        var cmd = args[0].ToLowerInvariant();
        // Options that take a value: --csv file, --html file, --folder path.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rest = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is var a && (a.Equals("--csv", StringComparison.OrdinalIgnoreCase) || a.Equals("--html", StringComparison.OrdinalIgnoreCase) || a.Equals("--folder", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                values[a.ToLowerInvariant()] = args[++i];
            else
                rest.Add(args[i]);
        }

        var flags = rest.Where(a => a.StartsWith("--")).Select(a => a.ToLowerInvariant()).ToHashSet();
        var names = rest.Where(a => !a.StartsWith("--")).ToList();
        try
        {
            return cmd switch
            {
                "list" => List(names.FirstOrDefault(), flags.Contains("--json"), values),
                "show" => Show(names),
                "leftovers" => Leftovers(names, flags),
                "remove" => Remove(names, flags),
                "forced" => ForcedRemove(names.FirstOrDefault(), values.GetValueOrDefault("--folder"), flags),
                "which" => Which(names),
                _ => Unknown(cmd, exeName),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return Error;
        }
    }

    private static int Unknown(string cmd, string exe)
    {
        Console.Error.WriteLine($"Unknown command \"{cmd}\". Run \"{exe} help\".");
        return Error;
    }

    private static void Help(string exe)
    {
        Out.WriteLine($"""
            pxd Uninstaller {typeof(Cli).Assembly.GetName().Version?.ToString(3)}: removes programs quietly, with their leftovers.

              {exe} list [text] [--json]          Installed programs and Store apps (filter by text).
                  --csv file / --html file      save the list to a file instead
              {exe} show <program>                Details and how it uninstalls (quietly or not).
              {exe} which <process or exe path>   Which installed program a running program belongs to.
              {exe} leftovers <program> [--all]   What a deep uninstall would remove. Changes nothing.
              {exe} remove <program>... [options] Uninstall quietly. Programs whose uninstaller has no
                                                 quiet mode are skipped and reported.
                  --deep        also remove leftovers that clearly belong to it (folders, registry,
                                shortcuts, services, tasks, firewall rules)
                  --deep-all    also remove leftovers that only have a similar name
                  --restore-point  make a Windows restore point first
                  --permanent   delete files instead of using the Recycle Bin
                  --dry-run     show what would happen, change nothing
              {exe} forced <name> [--folder path] [--deep-all] [--dry-run]
                                                 Forced uninstall: remove what's left of a program
                                                 that isn't listed anymore (by name and folder).

            <program> is a name ("7-Zip"), part of one if it's unique, or an id from "list --json".
            Registry keys are saved to a .reg file before they're removed:
              {BackupFolder}
            Programs installed for all users need an elevated (administrator) prompt.

            Exit codes: 0 done, 2 not found or ambiguous, 3 can't be removed quietly,
                        4 something couldn't be removed, 5 administrator rights needed.
            """);
    }

    // ---------- list / show ----------

    private static int List(string? filter, bool json, Dictionary<string, string> values)
    {
        var apps = AppScanner.Scan();
        if (filter is not null)
            apps = apps.Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var key in new[] { "--csv", "--html" })
        {
            if (!values.TryGetValue(key, out var file)) continue;
            file = Path.GetFullPath(file);
            File.WriteAllText(file, key == "--csv" ? Export.Csv(apps) : Export.Html(apps, Environment.MachineName), new System.Text.UTF8Encoding(true));
            Out.WriteLine($"Saved {Text.Plural(apps.Count, "program")} to {file}");
            return Ok;
        }
        if (json)
        {
            var rows = apps.Select(a => new
            {
                a.Id, a.Name, a.Publisher, a.Version, Kind = a.Kind.ToString(), a.JustForMe, a.InstallLocation,
                InstallDate = a.InstallDate?.ToString("yyyy-MM-dd"), a.SizeBytes, Method = a.Plan.Method, Quiet = a.Plan.CanQuiet, Orphaned = a.Orphaned,
            });
            Out.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return Ok;
        }

        var w = Math.Clamp(apps.Count == 0 ? 20 : apps.Max(a => a.Name.Length), 20, 48);
        Out.WriteLine($"{"Name".PadRight(w)}  {"Version",-16} {"Size",9}  Quiet  Publisher");
        Out.WriteLine(new string('-', w + 50));
        foreach (var a in apps)
        {
            var name = a.Name.Length > w ? a.Name[..(w - 1)] + "…" : a.Name;
            var ver = a.Version.Length > 16 ? a.Version[..15] + "…" : a.Version;
            var quiet = a.Orphaned ? "gone" : a.Plan.CanQuiet ? "yes" : "NO";
            Out.WriteLine($"{name.PadRight(w)}  {ver,-16} {a.SizeText,9}  {quiet,-5}  {a.Publisher}");
        }

        Out.WriteLine();
        Out.WriteLine($"{Text.Plural(apps.Count, "program")}. {apps.Count(a => !a.Plan.CanQuiet && !a.Orphaned)} can't be removed quietly; {apps.Count(a => a.Orphaned)} have no uninstaller left.");
        return Ok;
    }

    private static int Show(List<string> names)
    {
        var apps = AppScanner.Scan();
        var rc = Ok;
        foreach (var n in names)
        {
            var a = Find(apps, n, ref rc);
            if (a is null) continue;
            Out.WriteLine(a.Name);
            void Row(string k, string? v)
            {
                if (!string.IsNullOrEmpty(v)) Out.WriteLine($"  {k,-14}{v}");
            }

            Row("Id", a.Id);
            Row("Publisher", a.Publisher);
            Row("Version", a.Version);
            Row("Installed", a.InstallDate?.ToString("yyyy-MM-dd"));
            Row("Size", a.SizeText);
            Row("For", a.JustForMe ? "this user" : "all users");
            Row("Folder", a.InstallLocation);
            Row("Installer", a.Plan.Method);
            Row("Uninstall", a.Plan.Command);
            Row("Quiet", a.Plan.QuietCommand ?? "not possible: " + a.Plan.WhyNotQuiet);
            var finder = new LeftoverFinder(a, apps);
            var found = finder.Find();
            var disk = Usage.Disk(found);
            Row("On disk", disk.Total == 0 ? "nothing measured" : $"{Text.Size(disk.Total)} (program {Text.Size(disk.ProgramBytes)}, its data {Text.Size(disk.DataBytes)}, other {Text.Size(disk.OtherBytes)})");
            var running = Usage.Running(finder.Dirs, found);
            if (running.Count == 0)
            {
                Row("Running", "nothing");
            }
            else
            {
                Row("Running", $"{running.Count(r => r.Pid > 0)} process(es), CPU {running.Sum(r => r.CpuPercent):0.0}%, memory {Text.Size(running.Sum(r => r.MemoryBytes))}");
                foreach (var r in running)
                    Out.WriteLine($"                {Usage.Describe(r)}" + (r.Pid > 0 ? $"  pid {r.Pid}, CPU {r.CpuPercent:0.0}%, {Text.Size(r.MemoryBytes)}" : ""));
            }

            Out.WriteLine();
        }

        return rc;
    }

    // ---------- leftovers / remove ----------

    private static List<Leftover> Choose(List<Leftover> found, HashSet<string> flags, bool orphan)
    {
        var max = flags.Contains("--deep-all") || flags.Contains("--all") ? MatchLevel.Possible
            : flags.Contains("--deep") ? MatchLevel.Strong
            : MatchLevel.Regular;
        // A plain remove runs the uninstaller only; a program whose uninstaller is gone gets its normal leftovers cleaned.
        if (max == MatchLevel.Regular && !orphan) return found.Where(f => f.Kind == LeftoverKind.Uninstaller).ToList();
        return found.Where(f => f.Match <= max).ToList();
    }

    private static int Leftovers(List<string> names, HashSet<string> flags)
    {
        var apps = AppScanner.Scan();
        var rc = Ok;
        foreach (var n in names)
        {
            var a = Find(apps, n, ref rc);
            if (a is null) continue;
            var found = new LeftoverFinder(a, apps).Find();
            if (!flags.Contains("--all")) found = found.Where(f => f.Match <= MatchLevel.Strong).ToList();
            Out.WriteLine($"{a.Name}: {Text.Plural(found.Count, "item")}");
            foreach (var g in found.GroupBy(f => f.Group))
            {
                Out.WriteLine($"  {g.Key}");
                foreach (var f in g)
                    Out.WriteLine($"    [{Tag(f.Match)}] {UninstallJob.Describe(f)}{(f.Size > 0 ? $"  ({Text.Size(f.Size)})" : "")}  - {f.Why}");
            }

            Out.WriteLine();
        }

        Out.WriteLine("normal = removed by a plain uninstall, deep = --deep, similar = --deep-all");
        return rc;
    }

    private static string Tag(MatchLevel m) => m switch
    {
        MatchLevel.Regular => "normal ",
        MatchLevel.Strong => "deep   ",
        _ => "similar",
    };

    private static int Remove(List<string> names, HashSet<string> flags)
    {
        if (names.Count == 0)
        {
            Console.Error.WriteLine("Name the program to remove, e.g.  remove \"7-Zip\"");
            return Error;
        }

        var apps = AppScanner.Scan();
        var rc = Ok;
        var dry = flags.Contains("--dry-run");
        var madeRestorePoint = false;   // one restore point for the whole run
        foreach (var n in names)
        {
            var a = Find(apps, n, ref rc);
            if (a is null) continue;

            if (!a.Orphaned && !a.Plan.CanQuiet)
            {
                Out.WriteLine($"SKIPPED  {a.Name}: can't be removed quietly. {a.Plan.WhyNotQuiet}");
                Out.WriteLine("         Remove it from the pxd Uninstaller window instead.");
                rc = Math.Max(rc, NotQuiet);
                continue;
            }

            if (!a.JustForMe && !IsElevated)
            {
                Out.WriteLine($"SKIPPED  {a.Name}: installed for all users, so it needs an elevated (administrator) prompt.");
                rc = Math.Max(rc, NeedsAdmin);
                continue;
            }

            var finder = new LeftoverFinder(a, apps);
            var chosen = Choose(finder.Find(), flags, a.Orphaned);
            if (dry)
            {
                Out.WriteLine($"WOULD REMOVE  {a.Name}");
                foreach (var c in chosen)
                    Out.WriteLine(c.Kind == LeftoverKind.Uninstaller ? $"  run quietly: {a.Plan.QuietCommand}" : $"  {UninstallJob.Describe(c)}");
                continue;
            }

            var job = new UninstallJob(a, chosen, BackupFolder)
            {
                MustBeQuiet = true, Quiet = true, Permanent = flags.Contains("--permanent"), Timeout = TimeSpan.FromMinutes(20),
                MakeRestorePoint = flags.Contains("--restore-point") && !madeRestorePoint,
            };
            madeRestorePoint |= job.MakeRestorePoint;
            job.Log += s => Out.WriteLine("  " + s);
            Out.WriteLine($"REMOVING {a.Name}");
            var report = job.RunAsync().GetAwaiter().GetResult();
            if (report.NotQuiet)
            {
                Out.WriteLine($"SKIPPED  {report.Message}");
                rc = Math.Max(rc, NotQuiet);
                continue;
            }

            Out.WriteLine((report.Ok ? "DONE     " : "PROBLEM  ") + report.Message);
            if (report.BackupFolder is not null) Out.WriteLine($"         Backup: {report.BackupFolder}");
            if (!report.Ok) rc = Math.Max(rc, Failed);
        }

        return rc;
    }

    private static int ForcedRemove(string? name, string? folder, HashSet<string> flags)
    {
        if (name is null)
        {
            Console.Error.WriteLine("Name the program, e.g.  forced \"Old Tool\" --folder \"C:\\Program Files\\Old Tool\"");
            return Error;
        }

        if (Forced.Problem(name, folder) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return Error;
        }

        var apps = AppScanner.Scan();
        if (apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } listed)
            Out.WriteLine($"Note: \"{listed.Name}\" is still installed; \"remove\" runs its uninstaller first.");
        var app = Forced.App(name, folder);
        var max = flags.Contains("--deep-all") ? MatchLevel.Possible : MatchLevel.Strong;
        var chosen = new LeftoverFinder(app, apps).Find().Where(f => f.Match <= max && f.Kind != LeftoverKind.Uninstaller).ToList();
        if (chosen.Count == 0)
        {
            Out.WriteLine($"Nothing left of \"{name}\" was found.");
            return Ok;
        }

        if (!IsElevated && chosen.Any(c => c.Reg?.Hive == Microsoft.Win32.RegistryHive.LocalMachine || c.Kind is LeftoverKind.Service or LeftoverKind.FirewallRule
                                           || c.Path.StartsWith(Places.Current.ProgramFiles, StringComparison.OrdinalIgnoreCase) || c.Path.StartsWith(Places.Current.ProgramFilesX86, StringComparison.OrdinalIgnoreCase)
                                           || c.Path.StartsWith(Places.Current.ProgramData, StringComparison.OrdinalIgnoreCase)))
            Out.WriteLine("Note: not elevated, so leftovers for all users may not come off.");

        if (flags.Contains("--dry-run"))
        {
            Out.WriteLine($"WOULD REMOVE  what's left of {name}");
            foreach (var c in chosen) Out.WriteLine($"  [{Tag(c.Match)}] {UninstallJob.Describe(c)}");
            return Ok;
        }

        var job = new UninstallJob(app, chosen, BackupFolder) { Permanent = flags.Contains("--permanent"), MakeRestorePoint = flags.Contains("--restore-point") };
        job.Log += s => Out.WriteLine("  " + s);
        Out.WriteLine($"REMOVING what's left of {name}");
        var report = job.RunAsync().GetAwaiter().GetResult();
        Out.WriteLine((report.Ok ? "DONE     " : "PROBLEM  ") + report.Message);
        if (report.BackupFolder is not null) Out.WriteLine($"         Backup: {report.BackupFolder}");
        return report.Ok ? Ok : Failed;
    }

    private static int Which(List<string> names)
    {
        var apps = AppScanner.Scan();
        var rc = Ok;
        foreach (var n in names)
        {
            var paths = Path.IsPathFullyQualified(n) ? [n] : Hunter.ProcessPaths(n);
            if (paths.Count == 0)
            {
                Out.WriteLine($"NOT RUNNING  \"{n}\": no process with that name (give the full path to an exe instead).");
                rc = Math.Max(rc, NotFound);
                continue;
            }

            foreach (var p in paths)
            {
                var a = Hunter.ForPath(p, apps);
                Out.WriteLine(a is null ? $"{p}\n  not part of any installed program (portable, or part of Windows)" : $"{p}\n  belongs to {a.Name}   [{a.Id}]");
                if (a is null) rc = Math.Max(rc, NotFound);
            }
        }

        return rc;
    }

    /// <summary>Finds a program by id, exact name, or a unique part of its name.</summary>
    public static InstalledApp? Find(List<InstalledApp> apps, string query, ref int rc)
    {
        var hit = apps.FirstOrDefault(a => a.Id.Equals(query, StringComparison.OrdinalIgnoreCase))
                  ?? apps.FirstOrDefault(a => a.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;
        var some = apps.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (some.Count == 1) return some[0];
        if (some.Count == 0)
        {
            Out.WriteLine($"NOT FOUND  \"{query}\": nothing installed with that name.");
        }
        else
        {
            Out.WriteLine($"AMBIGUOUS  \"{query}\" matches {some.Count} programs; use the full name or the id:");
            foreach (var s in some.Take(15)) Out.WriteLine($"  {s.Name}   [{s.Id}]");
        }

        rc = Math.Max(rc, NotFound);
        return null;
    }
}
