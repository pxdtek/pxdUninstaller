using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using pxdUninstaller.Core;

namespace pxdUninstaller.Views;

/// <summary>
/// "On this PC" in the details: the space the program really takes (measured: its folders, its data, Windows'
/// traces of it) and its services and processes running now with their CPU and memory, refreshed every few seconds.
/// </summary>
public sealed partial class MainWindow
{
    private sealed class UsageInfo
    {
        public required LeftoverFinder Finder { get; init; }
        public required List<Leftover> Found { get; init; }
        public required DiskUse Disk { get; init; }
        public List<RunningItem> Running { get; set; } = [];
        public DateTime RunningAt { get; set; }
    }

    private readonly Dictionary<string, UsageInfo> _usage = [];
    private readonly HashSet<string> _measuring = [];
    private readonly Border _usageHost = new();
    private DispatcherTimer? _usageTimer;
    private bool _runningBusy;

    /// <summary>Made-up usage for the README screenshots (null = measure for real).</summary>
    public static Func<InstalledApp, (DiskUse Disk, List<RunningItem> Running)?>? FakeUsage { get; set; }

    /// <summary>The measured disk use of a program, once measured (self-test).</summary>
    public DiskUse? MeasuredDisk(InstalledApp a) => _usage.TryGetValue(a.Id, out var u) ? u.Disk : null;

    public IReadOnlyList<RunningItem>? RunningOf(InstalledApp a) => _usage.TryGetValue(a.Id, out var u) ? u.Running : null;

    /// <summary>The "On this PC" block for the details panel; fills itself in when measuring is done.</summary>
    private Control UsageBlock(InstalledApp a)
    {
        MainWindow.Detach(_usageHost);
        RenderUsage(a);
        if (!_usage.ContainsKey(a.Id)) _ = MeasureAsync(a);
        EnsureUsageTimer();
        return _usageHost;
    }

    public async Task MeasureAsync(InstalledApp a)
    {
        if (!_measuring.Add(a.Id)) return;
        var all = _apps.ToList();
        try
        {
            UsageInfo info;
            if (FakeUsage?.Invoke(a) is { } fake)
            {
                info = new UsageInfo { Finder = new LeftoverFinder(a, all), Found = [], Disk = fake.Disk, Running = fake.Running, RunningAt = DateTime.Now };
            }
            else
            {
                info = await Task.Run(() =>
                {
                    var finder = new LeftoverFinder(a, all);
                    var found = finder.Find();
                    return new UsageInfo { Finder = finder, Found = found, Disk = Usage.Disk(found), Running = Usage.Running(finder.Dirs, found), RunningAt = DateTime.Now };
                });
            }

            _usage[a.Id] = info;
        }
        catch
        {
            /* couldn't measure: the block says so */
        }
        finally
        {
            _measuring.Remove(a.Id);
        }

        if (ReferenceEquals(_selected, a) && _picked.Count <= 1) RenderUsage(a);
    }

    private void EnsureUsageTimer()
    {
        if (_usageTimer is not null) return;
        _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _usageTimer.Tick += async (_, _) =>
        {
            if (_selected is not { } a || _picked.Count > 1 || _runningBusy || _busy || FakeUsage is not null || !IsVisible || WindowState == WindowState.Minimized) return;
            if (!_usage.TryGetValue(a.Id, out var info)) return;
            _runningBusy = true;
            try
            {
                info.Running = await Task.Run(() => Usage.Running(info.Finder.Dirs, info.Found));
                info.RunningAt = DateTime.Now;
            }
            catch
            {
                /* try again next tick */
            }
            finally
            {
                _runningBusy = false;
            }

            if (ReferenceEquals(_selected, a) && _picked.Count <= 1 && !_cleanupHost.IsVisible) RenderUsage(a);
        };
        _usageTimer.Start();
        Closed += (_, _) => _usageTimer?.Stop();
    }

    private void RenderUsage(InstalledApp a)
    {
        var s = new StackPanel { Spacing = 6 };
        s.Children.Add(new TextBlock { Text = "ON THIS PC", Classes = { "label" }, Margin = new Thickness(0, 4, 0, 0) });
        if (!_usage.TryGetValue(a.Id, out var u))
        {
            s.Children.Add(new TextBlock { Text = _measuring.Contains(a.Id) || !SelfTestMode ? "Measuring its folders and looking for what's running…" : "Not measured.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            _usageHost.Child = s;
            return;
        }

        // Disk space.
        var d = u.Disk;
        var total = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        total.Children.Add(new TextBlock { Text = d.Total > 0 ? Text.Size(d.Total) : "Nothing on disk", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brush("#00C8FF") });
        total.Children.Add(new TextBlock { Text = "on disk", Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) });
        s.Children.Add(total);
        var parts = new List<string>();
        if (d.ProgramBytes > 0) parts.Add($"program {Text.Size(d.ProgramBytes)}");
        if (d.DataBytes > 0) parts.Add($"its data {Text.Size(d.DataBytes)}");
        if (d.OtherBytes > 0) parts.Add($"other {Text.Size(d.OtherBytes)}");
        if (parts.Count > 0) s.Children.Add(new TextBlock { Text = string.Join("  ·  ", parts), Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        foreach (var (path, size, _) in d.Places.Take(4))
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            g.Children.Add(new TextBlock { Text = path, FontFamily = Mono, FontSize = 11, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
            var sz = new TextBlock { Text = Text.Size(size), Classes = { "muted" }, FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(sz, 1);
            g.Children.Add(sz);
            ToolTip.SetTip(g, path);
            s.Children.Add(g);
        }

        if (d.Places.Count > 4) s.Children.Add(new TextBlock { Text = $"and {d.Places.Count - 4} more places (Uninstall and clean up lists them all)", Classes = { "muted" }, FontSize = 11 });

        // Running now.
        s.Children.Add(new TextBlock { Text = "RUNNING NOW", Classes = { "label" }, Margin = new Thickness(0, 8, 0, 0) });
        if (u.Running.Count == 0)
        {
            s.Children.Add(new TextBlock { Text = "Nothing of it is running.", Classes = { "muted" }, FontSize = 12 });
        }
        else
        {
            var cpu = u.Running.Sum(r => r.CpuPercent);
            var mem = u.Running.Sum(r => r.MemoryBytes);
            var procs = u.Running.Count(r => r.Pid > 0);
            s.Children.Add(new TextBlock
            {
                Text = procs == 0 ? "No processes running." : $"{Text.Plural(procs, "process", "processes")}  ·  CPU {cpu:0.0}%  ·  memory {Text.Size(mem)}",
                FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = cpu >= 10 || mem >= 1L << 30 ? Warn : TextB,
            });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            var row = 0;
            void Cell(string text, int col, IBrush? fg = null, bool right = false)
            {
                var t = new TextBlock { Text = text, FontSize = 11.5, Foreground = fg ?? TextB, Margin = new Thickness(col == 0 ? 0 : 10, 2, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left };
                Grid.SetRow(t, row);
                Grid.SetColumn(t, col);
                grid.Children.Add(t);
            }

            foreach (var r in u.Running.Take(8))
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var running = r.Pid > 0;
                var label = r.IsService ? $"⚙ {r.ServiceName}" + (running ? $"  ({r.Name})" : $"  ({r.ServiceState?.ToLowerInvariant()})") : r.Name;
                Cell(label, 0, running ? TextB : Muted);
                Cell(running ? $"{r.CpuPercent:0.0}%" : "", 1, r.CpuPercent >= 10 ? Warn : Muted, right: true);
                Cell(running ? Text.Size(r.MemoryBytes) : "", 2, r.MemoryBytes >= 512L << 20 ? Warn : Muted, right: true);
                row++;
            }

            s.Children.Add(grid);
            if (u.Running.Count > 8) s.Children.Add(new TextBlock { Text = $"and {u.Running.Count - 8} more", Classes = { "muted" }, FontSize = 11 });
            if (u.Running.Any(r => r.Pid > 0))
                s.Children.Add(new TextBlock { Text = "Its uninstaller may ask you to close these first.", Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        }

        _usageHost.Child = s;
    }
}
