using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using pxdUninstaller.Core;

namespace pxdUninstaller.Views;

/// <summary>
/// The clean-up page: everything found that belongs to one program (or to each program of a batch), grouped
/// (uninstall, program files, app data, shortcuts, registry, startup and services, tasks, firewall, Windows traces),
/// each with a tick box. What the normal uninstall removes is ticked from the start; the rest is the user's choice.
/// </summary>
public sealed partial class MainWindow
{
    private List<(InstalledApp App, List<Leftover> Items)> _cleanSets = [];
    private bool _afterUninstall;
    private string? _cleanTitle;
    private readonly TextBlock _cleanSummary = new() { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _cleanQuiet = new() { Content = "Run the uninstaller quietly (no windows)", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cleanGo = new() { Content = "Remove selected", Classes = { "primary" }, MinWidth = 150 };
    private readonly List<(Leftover Item, CheckBox Box)> _boxes = [];
    private UninstallReport? _lastReport;

    public bool CleanupShowing => _cleanupHost.IsVisible;
    public IReadOnlyList<Leftover> Leftovers => _cleanSets.SelectMany(s => s.Items).ToList();
    public UninstallReport? LastReport => _lastReport;

    public async Task OpenCleanupAsync(InstalledApp a, LeftoverFinder? finder, bool afterUninstall, List<Leftover>? given = null, string? title = null)
    {
        _afterUninstall = afterUninstall;
        ShowPage(Waiting($"Looking for everything that belongs to {a.Name}…",
            "Program Files, ProgramData, AppData, shortcuts, the registry, services, scheduled tasks, firewall rules and Windows' own records."));
        var all = _apps.ToList();
        SetBusy(true, $"Looking for leftovers of {a.Name}…");
        List<Leftover> found;
        try
        {
            found = given ?? await Task.Run(() =>
            {
                var f = finder ?? new LeftoverFinder(a, all);
                var list = f.Find();
                return afterUninstall ? list.Where(l => l.Kind != LeftoverKind.Uninstaller && UninstallJob.Exists(l)).ToList() : list;
            });
        }
        finally
        {
            SetBusy(false);
        }

        _status.Text = $"Found {Text.Plural(found.Count, "item")} for {a.Name}.";
        ShowCleanup([(a, found)], afterUninstall, title);
    }

    /// <summary>The clean-up page for one or more programs (a batch shows each program's leftovers under its name).</summary>
    public void ShowCleanup(List<(InstalledApp App, List<Leftover> Items)> sets, bool afterUninstall, string? title = null)
    {
        _cleanSets = sets;
        _afterUninstall = afterUninstall;
        _cleanTitle = title;
        BuildCleanupPage();
    }

    private void ShowPage(Control page)
    {
        _browse.IsVisible = false;
        _cleanupHost.IsVisible = true;
        _cleanupHost.Child = page;
    }

    public void CloseCleanup()
    {
        if (_busy && _cleanupHost.IsVisible) return;
        _cleanupHost.IsVisible = false;
        _cleanupHost.Child = null;
        _browse.IsVisible = true;
        _cleanSets = [];
        _boxes.Clear();
    }

    private static Control Waiting(string title, string text)
    {
        var s = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
        s.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 260, HorizontalAlignment = HorizontalAlignment.Center });
        s.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        s.Children.Add(new TextBlock { Text = text, Classes = { "muted" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        return s;
    }

    /// <summary>Takes a reused control off the page it was on, so it can go on a new one.</summary>
    internal static T Detach<T>(T c) where T : Control
    {
        switch (c.Parent)
        {
            case Panel p:
                p.Children.Remove(c);
                break;
            case ContentControl cc:
                cc.Content = null;
                break;
            case Decorator d:
                d.Child = null;
                break;
        }

        return c;
    }

    private void BuildCleanupPage()
    {
        _boxes.Clear();
        Detach(_cleanSummary);
        Detach(_cleanQuiet);
        Detach(_cleanGo);
        var many = _cleanSets.Count > 1;
        var first = _cleanSets.FirstOrDefault().App;
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 12) };
        if (!many && first is not null)
        {
            var icon = AppIcons.View(first, 40);
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 14, 0);
            head.Children.Add(icon);
        }

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var heading = _cleanTitle ?? (many ? $"Leftovers of {Text.Plural(_cleanSets.Count, "program")}" : (_afterUninstall ? "Leftovers of " : "Clean up ") + first?.Name);
        names.Children.Add(new TextBlock { Text = heading, FontSize = 22, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        names.Children.Add(new TextBlock
        {
            Text = _afterUninstall
                ? "The uninstall is done. This is what's still on the PC. Pick what to remove."
                : "Pick what to remove. What the normal uninstall removes is already ticked; tick more to clean up after it.",
            Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        page.Children.Add(head);

        var tools = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        void Tick(string label, string tip, Func<Leftover, bool> on)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), MinHeight = 32, Padding = new Thickness(12, 4) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) =>
            {
                foreach (var (item, box) in _boxes) box.IsChecked = on(item);
            };
            tools.Children.Add(b);
        }

        Tick("Normal uninstall", "Only what the normal uninstall removes.", l => l.Match == MatchLevel.Regular);
        Tick("Everything that belongs to it", "Plus folders, keys and entries that point into its folder or carry its exact name.", l => l.Match <= MatchLevel.Strong);
        Tick("Everything found", "Plus things that only have a similar name. Check those before removing.", _ => true);
        Tick("Nothing", "Untick everything.", _ => false);
        Grid.SetRow(tools, 1);
        page.Children.Add(tools);

        var list = new StackPanel { Spacing = 10 };
        if (_cleanSets.All(s => s.Items.Count == 0))
        {
            list.Children.Add(new TextBlock
            {
                Text = _afterUninstall ? "Nothing was left behind. It's all gone." : "Nothing was found.",
                FontSize = 15, Foreground = Good, Margin = new Thickness(4, 20), HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        foreach (var (app, items) in _cleanSets)
        {
            foreach (var group in items.GroupBy(l => l.Group))
            {
                var card = new Border { Classes = { "card" }, Padding = new Thickness(14, 10) };
                var cs = new StackPanel { Spacing = 2 };
                var gh = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
                if (many) gh.Children.Add(new TextBlock { Text = app.Name, FontWeight = FontWeight.Bold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                gh.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
                gh.Children.Add(new TextBlock { Text = group.Count().ToString(), Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                cs.Children.Add(gh);
                foreach (var item in group) cs.Children.Add(ItemRow(item));
                card.Child = cs;
                list.Children.Add(card);
            }
        }

        var scroll = new ScrollViewer { Content = list, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetRow(scroll, 2);
        page.Children.Add(scroll);

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        var back = new Button { Content = "Back", Classes = { "ghost" } };
        back.Click += (_, _) => CloseCleanup();
        foot.Children.Add(back);
        var mid = new StackPanel { Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        mid.Children.Add(_cleanSummary);
        _cleanQuiet.IsVisible = _cleanSets.Any(s => s.Items.Any(l => l.Kind == LeftoverKind.Uninstaller) && s.App.Plan.CanQuiet && !s.App.Plan.IsStore);
        _cleanQuiet.IsChecked = _settings.Quiet;
        mid.Children.Add(_cleanQuiet);
        Grid.SetColumn(mid, 1);
        foot.Children.Add(mid);
        _cleanGo.Click -= CleanGoClick;
        _cleanGo.Click += CleanGoClick;
        Grid.SetColumn(_cleanGo, 3);
        foot.Children.Add(_cleanGo);
        Grid.SetRow(foot, 3);
        page.Children.Add(foot);

        ShowPage(page);
        UpdateCleanSummary();
    }

    private Control ItemRow(Leftover item)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,78"), Margin = new Thickness(0, 3) };
        var box = new CheckBox { IsChecked = item.Selected, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 6, 0) };
        box.IsCheckedChanged += (_, _) =>
        {
            item.Selected = box.IsChecked == true;
            UpdateCleanSummary();
        };
        _boxes.Add((item, box));
        g.Children.Add(box);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var main = item.Kind switch
        {
            LeftoverKind.Uninstaller => "Run its uninstaller",
            LeftoverKind.RegistryValue => $"{item.Path}  →  {item.ValueName}",
            LeftoverKind.Service => $"Service: {item.ServiceName}",
            LeftoverKind.Task => $"Task: {item.TaskName}",
            LeftoverKind.FirewallRule => $"Rule: {item.Path}",
            _ => item.Path,
        };
        var isUninstaller = item.Kind == LeftoverKind.Uninstaller;
        text.Children.Add(new TextBlock { Text = main, FontFamily = isUninstaller ? FontFamily.Default : Mono, FontSize = isUninstaller ? 13 : 12, FontWeight = isUninstaller ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = item.Why, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        // Clicking the text ticks the box too.
        text.Background = Brushes.Transparent;
        text.PointerPressed += (_, _) => box.IsChecked = box.IsChecked != true;
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var pill = Pill(item.MatchText, item.Match switch { MatchLevel.Regular => "good", MatchLevel.Strong => "info", _ => "warn" });
        pill.Margin = new Thickness(8, 0);
        pill.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(pill, 2);
        g.Children.Add(pill);
        var size = new TextBlock { Text = item.Size > 0 ? Text.Size(item.Size) : "", Classes = { "muted" }, FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetColumn(size, 3);
        g.Children.Add(size);
        return g;
    }

    private void UpdateCleanSummary()
    {
        var all = Leftovers;
        var chosen = all.Where(l => l.Selected).ToList();
        var size = chosen.Sum(l => l.Size);
        var uninstalls = chosen.Count(l => l.Kind == LeftoverKind.Uninstaller);
        var others = chosen.Count(l => l.Kind != LeftoverKind.Uninstaller);
        var skipped = all.Count(l => l.Kind == LeftoverKind.Uninstaller) - uninstalls;
        _cleanSummary.Text = chosen.Count == 0
            ? "Nothing ticked."
            : (uninstalls > 1 ? $"Uninstall {uninstalls} programs, then remove " : uninstalls == 1 ? "Uninstall, then remove " : "Remove ") + Text.Plural(others, "item") + (size > 0 ? $" ({Text.Size(size)})" : "") + "."
              + (skipped > 0 ? "  Unticked uninstallers: those programs stay installed; only their ticked leftovers go." : "")
              + "  Registry is backed up first; files go to the Recycle Bin.";
        _cleanGo.Content = uninstalls > 0 ? "Uninstall and remove" : "Remove selected";
        _cleanGo.IsEnabled = chosen.Count > 0 && !_busy;
        _cleanQuiet.IsEnabled = uninstalls > 0;
    }

    private async void CleanGoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await RunCleanupAsync();

    /// <summary>A page with a live log, for running uninstallers and removals.</summary>
    private (TextBlock Title, System.Collections.ObjectModel.ObservableCollection<string> Lines, StackPanel Foot, Action<string> Say) LogPage(string title)
    {
        var log = new ListBox { Classes = { "log" } };
        var lines = new System.Collections.ObjectModel.ObservableCollection<string>();
        log.ItemsSource = lines;
        log.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap }, supportsRecycling: false);
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var t = new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 12) };
        page.Children.Add(t);
        Grid.SetRow(log, 1);
        page.Children.Add(log);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(foot, 2);
        page.Children.Add(foot);
        ShowPage(page);
        void Say(string s) => Dispatcher.UIThread.Post(() =>
        {
            lines.Add(s);
            log.ScrollIntoView(lines.Count - 1);
        });
        return (t, lines, foot, Say);
    }

    /// <summary>Runs each ticked uninstaller and removes the ticked leftovers, showing a live log.</summary>
    public async Task<UninstallReport?> RunCleanupAsync()
    {
        if (_busy) return null;
        var work = _cleanSets.Select(s => (s.App, Items: s.Items.Where(l => l.Selected).ToList())).Where(s => s.Items.Count > 0).ToList();
        if (work.Count == 0) return null;

        var quiet = _cleanQuiet.IsChecked == true;
        var (title, lines, foot, say) = LogPage(work.Count == 1 ? $"Removing {work[0].App.Name}…" : $"Removing {Text.Plural(work.Count, "program")}…");
        SetBusy(true, "Removing…");
        var reports = new List<UninstallReport>();
        try
        {
            foreach (var (app, items) in work)
            {
                if (work.Count > 1) say($"── {app.Name} ──");
                var hasUninstaller = items.Any(i => i.Kind == LeftoverKind.Uninstaller);
                _status.Text = hasUninstaller ? (quiet && app.Plan.CanQuiet ? $"Removing {app.Name} quietly…" : $"Waiting for {app.Name}'s uninstaller…") : $"Removing leftovers of {app.Name}…";
                var job = new UninstallJob(app, items, Cli.BackupFolder) { Quiet = quiet || app.Plan.IsStore, MakeRestorePoint = WantRestorePoint() };
                job.Log += say;
                try
                {
                    reports.Add(await job.RunAsync());
                }
                catch (Exception ex)
                {
                    reports.Add(new UninstallReport { App = app, Message = "Stopped: " + ex.Message });
                }

                say(reports[^1].Message);
            }
        }
        finally
        {
            SetBusy(false);
        }

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);   // let the last log lines land
        _lastReport = reports[^1];
        var ok = reports.All(r => r.Ok);
        title.Text = work.Count > 1
            ? (ok ? $"All {work.Count} done" : $"{reports.Count(r => r.Ok)} of {work.Count} done")
            : ok ? (reports[0].UninstallerRan ? $"{work[0].App.Name} is gone" : $"{work[0].App.Name} is cleaned up") : reports[0].Uninstalled ? $"{work[0].App.Name}: mostly done" : $"{work[0].App.Name} is still installed";
        title.Foreground = ok ? Good : reports.Any(r => r.Uninstalled) ? Warn : Bad;
        _status.Text = work.Count > 1 ? $"{reports.Count(r => r.Ok)} of {work.Count} programs done." : reports[0].Message;

        var back = new Button { Content = "Back to the list", Classes = { "primary" } };
        back.Click += (_, _) => CloseCleanup();
        foot.Children.Add(back);
        if (reports.LastOrDefault(r => r.BackupFolder is not null)?.BackupFolder is { } dir)
        {
            var open = new Button { Content = "Open backup" };
            ToolTip.SetTip(open, "registry.reg puts the removed registry keys back; removed.txt lists everything. Files are in the Recycle Bin.");
            open.Click += (_, _) => Start("explorer.exe", Text.Quote(work.Count > 1 ? Cli.BackupFolder : dir));
            foot.Children.Add(open);
        }

        // Gone from Apps & features (or its broken entry was removed): off the list.
        foreach (var (app, _) in work)
        {
            if (!AppScanner.StillInstalled(app) || app.Orphaned && app.Key is { } k && !Reg.Exists(k))
            {
                _apps.Remove(app);
                if (ReferenceEquals(_selected, app)) _selected = null;
            }
        }

        Refresh();
        return _lastReport;
    }
}
