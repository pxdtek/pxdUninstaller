using System.Diagnostics;

namespace pxdUninstaller.Core;

/// <summary>Space a program takes on this PC, measured (not the installer's estimate).</summary>
public sealed record DiskUse(long ProgramBytes, long DataBytes, long OtherBytes, List<(string Path, long Size, string Group)> Places)
{
    public long Total => ProgramBytes + DataBytes + OtherBytes;
}

/// <summary>A service or process of the program that's running now, with what it uses.</summary>
public sealed record RunningItem(string Name, int Pid, string ExePath, double CpuPercent, long MemoryBytes, string? ServiceName, string? ServiceState)
{
    public bool IsService => ServiceName is not null;
}

/// <summary>What a program costs on this PC: its disk space, and its services and processes running now.</summary>
public static class Usage
{
    /// <summary>
    /// Measures the program's folders and data: its install folder(s), and the folders and files that clearly belong to
    /// it (AppData, ProgramData, LocalLow, Temp, Windows traces). Similar-name finds aren't counted.
    /// </summary>
    public static DiskUse Disk(List<Leftover> found)
    {
        long program = 0, data = 0, other = 0;
        var places = new List<(string, long, string)>();
        foreach (var l in found.Where(l => l.Match <= MatchLevel.Strong && l.Kind is LeftoverKind.Folder or LeftoverKind.File && l.Size > 0))
        {
            // Folders inside another counted folder are already in its size.
            if (found.Any(o => !ReferenceEquals(o, l) && o.Kind == LeftoverKind.Folder && o.Match <= MatchLevel.Strong && Places.Within(l.Path, o.Path) && !l.Path.Equals(o.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            switch (l.Group)
            {
                case "Program files":
                    program += l.Size;
                    break;
                case "App data":
                    data += l.Size;
                    break;
                default:
                    other += l.Size;
                    break;
            }

            places.Add((l.Path, l.Size, l.Group));
        }

        return new DiskUse(program, data, other, places.OrderByDescending(p => p.Item2).ToList());
    }

    /// <summary>Service names and states, from the service manager (replaced in the self-test).</summary>
    public static Func<string, (string State, int Pid)?> ServiceStatus { get; set; } = name =>
    {
        var (code, output) = Runner.Tool("sc.exe", $"queryex \"{name}\"", 10_000);
        if (code != 0) return null;
        string? state = null;
        var pid = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
                state = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            else if (line.StartsWith("PID", StringComparison.OrdinalIgnoreCase) && int.TryParse(line.Split(':').Last().Trim(), out var p))
                pid = p;
        }

        return state is null ? null : (state, pid);
    };

    /// <summary>
    /// The program's services (with their state) and every process running from its folders, with CPU (measured over
    /// <paramref name="sampleMs"/>) and memory (working set).
    /// </summary>
    public static List<RunningItem> Running(IReadOnlyList<string> dirs, IEnumerable<Leftover> services, int sampleMs = 700)
    {
        // Processes running from the program's folders.
        var procs = new Dictionary<int, (string Path, Process P)>();
        if (dirs.Count > 0)
        {
            foreach (var p in Process.GetProcesses())
            {
                var path = p.Id is 0 or 4 ? null : Hunter.ProcessPath(p.Id);
                if (path is not null && dirs.Any(d => Places.Within(path, d))) procs[p.Id] = (path, p);
                else p.Dispose();
            }
        }

        // Its services: running ones point at their process; stopped ones are listed too.
        var svc = new List<(string Name, string State, int Pid)>();
        foreach (var s in services.Where(s => s.Kind == LeftoverKind.Service && s.ServiceName is not null))
        {
            if (ServiceStatus(s.ServiceName!) is { } st) svc.Add((s.ServiceName!, st.State, st.Pid));
        }

        foreach (var (_, _, pid) in svc.Where(s => s.Pid > 0 && !procs.ContainsKey(s.Pid)))
        {
            try
            {
                var p = Process.GetProcessById(pid);
                procs[pid] = (Hunter.ProcessPath(pid) ?? "", p);
            }
            catch
            {
                /* gone already */
            }
        }

        // CPU: processor time used over the sample, as a share of all cores.
        var before = procs.ToDictionary(kv => kv.Key, kv => Cpu(kv.Value.P));
        var watch = Stopwatch.StartNew();
        if (procs.Count > 0) Thread.Sleep(sampleMs);
        var elapsed = watch.Elapsed.TotalMilliseconds * Environment.ProcessorCount;

        var list = new List<RunningItem>();
        foreach (var (pid, (path, p)) in procs)
        {
            using (p)
            {
                var cpu = before[pid] is { } a && Cpu(p) is { } b && elapsed > 0 ? Math.Max(0, (b - a).TotalMilliseconds / elapsed * 100) : 0;
                long mem = 0;
                try
                {
                    p.Refresh();
                    mem = p.WorkingSet64;
                }
                catch
                {
                    /* protected process */
                }

                var s = svc.FirstOrDefault(x => x.Pid == pid);
                string name;
                try
                {
                    name = path.Length > 0 ? Path.GetFileName(path) : p.ProcessName + ".exe";   // a service's path can be hidden without admin rights
                }
                catch
                {
                    name = $"process {pid}";
                }

                list.Add(new RunningItem(name, pid, path, cpu, mem, s.Name, s.Name is null ? null : s.State));
            }
        }

        foreach (var (name, state, _) in svc.Where(s => s.Pid == 0 || !procs.ContainsKey(s.Pid)))
            list.Add(new RunningItem(name, 0, "", 0, 0, name, state));

        return list.OrderByDescending(r => r.IsService).ThenByDescending(r => r.MemoryBytes).ToList();
    }

    private static TimeSpan? Cpu(Process p)
    {
        try
        {
            return p.TotalProcessorTime;
        }
        catch
        {
            return null;
        }
    }

    public static string Describe(RunningItem r) =>
        r.IsService
            ? r.Pid > 0 ? $"Service {r.ServiceName} ({r.ServiceState?.ToLowerInvariant()}), {r.Name}" : $"Service {r.ServiceName} ({r.ServiceState?.ToLowerInvariant()})"
            : r.Name;
}
