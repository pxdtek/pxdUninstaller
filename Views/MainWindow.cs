using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using pxdUninstaller.Core;

namespace pxdUninstaller.Views;

/// <summary>
/// The whole app: a sidebar of groups (all, programs, Store apps, just for me, recent, no quiet mode, broken), the
/// program list with search and sort, the selected program's details with Uninstall / Uninstall and clean up, and the
/// clean-up page (MainWindow.Cleanup.cs). Built in code, pxd neon theme.
/// </summary>
public sealed partial class MainWindow : Window
{
    internal static IBrush Brush(string c) => new SolidColorBrush(Color.Parse(c));
    internal static readonly IBrush Muted = Brush("#8A92A6");
    internal static readonly IBrush Good = Brush("#19F58C");
    internal static readonly IBrush Warn = Brush("#F5B14C");
    internal static readonly IBrush Bad = Brush("#F2666A");
    internal static readonly IBrush TextB = Brush("#E8EAF0");
    internal static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    internal static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    private sealed record Group(string Name, string Glyph, string Blurb, Func<InstalledApp, bool> Has);

    private static readonly Group[] Groups =
    [
        new("All programs", "", "Everything installed on this PC.", _ => true),
        new("Desktop programs", "", "Programs from Apps & features.", a => a.Kind == AppKind.Program),
        new("Store apps", "", "Microsoft Store apps installed for you.", a => a.Kind == AppKind.Store),
        new("Just for me", "", "Installed for your account only.", a => a.JustForMe),
        new("Recently installed", "", "Installed in the last 30 days.", a => a.InstallDate is { } d && d > DateTime.Now.AddDays(-30)),
        new("No quiet uninstall", "", "Their uninstaller has no quiet mode, so they can't be removed from the command line.", a => !a.Plan.CanQuiet && !a.Orphaned),
        new("Broken entries", "", "The uninstaller is gone. Clean up what's left of them.", a => a.Orphaned),
    ];

    private readonly Settings _settings;
    private List<InstalledApp> _apps = [];
    private readonly Dictionary<int, Button> _nav = [];
    private readonly Dictionary<int, TextBlock> _navCounts = [];
    private readonly Border _notice = new() { Classes = { "notice" }, IsVisible = false, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _noticeText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _noticeAction = new() { Classes = { "primary" }, MinHeight = 28, Padding = new Thickness(12, 2), Margin = new Thickness(8, 0, 0, 0), IsVisible = false };
    private Action? _noticeDo;
    private readonly TextBlock _status = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Width = 200, IsIndeterminate = true, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.Bold };
    private readonly TextBlock _subtitle = new() { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBox _search = new() { PlaceholderText = "Search name or publisher…", VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _sort = new() { MinWidth = 170, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ListBox _list = new() { Classes = { "log" } };
    private readonly Border _details = new() { Classes = { "card" }, Padding = new Thickness(18), Margin = new Thickness(14, 0, 0, 0) };
    private readonly TextBlock _empty = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(30), MaxWidth = 420 };
    private readonly ToggleSwitch _quiet = new() { OnContent = "Quiet uninstall", OffContent = "Quiet uninstall" };
    private readonly ToggleSwitch _restore = new() { OnContent = "Restore point first", OffContent = "Restore point first" };
    private List<InstalledApp> _picked = [];
    private readonly Grid _browse = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    private readonly Border _cleanupHost = new() { IsVisible = false };
    private InstalledApp? _selected;
    private bool _refreshing;
    private bool _busy;
    private bool _confirming;
    private int _group;

    public bool SelfTestMode { get; set; }
    public IReadOnlyList<InstalledApp> Apps => _apps;
    public InstalledApp? Selected => _selected;
    public bool Busy => _busy;
    public int Shown => _list.ItemCount;

    public MainWindow() : this(null)
    {
    }

    /// <param name="apps">A fixed list (self-test, screenshots); null scans this PC.</param>
    public MainWindow(List<InstalledApp>? apps)
    {
        _settings = Settings.Load();
        Title = "pxd Uninstaller";
        Icon = BrandIcon.Window();
        Width = 1240;
        Height = 820;
        MinWidth = 1000;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*") };
        body.Children.Add(BuildSidebar());
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(22, 16, 22, 10) };
        Grid.SetColumn(main, 1);
        body.Children.Add(main);

        var ng = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        ng.Children.Add(_noticeText);
        _noticeAction.Click += (_, _) =>
        {
            var act = _noticeDo;
            _notice.IsVisible = false;
            act?.Invoke();
        };
        Grid.SetColumn(_noticeAction, 1);
        ng.Children.Add(_noticeAction);
        var dismiss = new Button { Content = "Dismiss", Classes = { "ghost" }, MinHeight = 28, Padding = new Thickness(10, 2), Margin = new Thickness(8, 0, 0, 0) };
        dismiss.Click += (_, _) => _notice.IsVisible = false;
        Grid.SetColumn(dismiss, 2);
        ng.Children.Add(dismiss);
        _notice.Child = ng;
        main.Children.Add(_notice);

        // Browse (header, toolbar, list + details) or the clean-up page, in the same place.
        var stage = new Grid();
        Grid.SetRow(stage, 1);
        main.Children.Add(stage);
        var top = new StackPanel();
        top.Children.Add(BuildHeader());
        top.Children.Add(BuildToolbar());
        _browse.Children.Add(top);
        var area = BuildListArea();
        Grid.SetRow(area, 1);
        _browse.Children.Add(area);
        stage.Children.Add(_browse);
        stage.Children.Add(_cleanupHost);

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(_status);
        Grid.SetColumn(_progress, 1);
        bar.Children.Add(_progress);
        Grid.SetRow(bar, 2);
        main.Children.Add(bar);

        Content = pxdStandard.pxdChrome.Wrap(this, body, Title, BrandIcon.Render(36));
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5 && !_busy) _ = LoadAsync();
            else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)) _search.Focus();
        };

        if (Program.LimitedReason is { } why)
            ShowNotice(why, action: "Restart as administrator", act: () =>
            {
                if (Elevation.RestartElevated(["--elevated"]) is null) Close();
            });

        if (apps is not null)
        {
            SetApps(apps);
        }
        else
        {
            _status.Text = "Looking for installed programs…";
            Opened += async (_, _) => await LoadAsync();
        }

        ShowGroup(0);
    }

    // ---------- Shell ----------

    private Control BuildSidebar()
    {
        var dock = new DockPanel { Background = Brush("#0B0E13") };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(16, 16, 16, 18) };
        head.Children.Add(new Image { Source = BrandIcon.Render(72), Width = 36, Height = 36 });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "pxd", FontSize = 13, FontWeight = FontWeight.Bold, LetterSpacing = 1.4, Classes = { "neon" } });
        names.Children.Add(new TextBlock { Text = "UNINSTALLER", FontSize = 13, FontWeight = FontWeight.Bold, LetterSpacing = 1.4, Classes = { "neon" } });
        head.Children.Add(names);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        var foot = new StackPanel { Spacing = 8, Margin = new Thickness(12, 8, 12, 14) };
        _quiet.IsChecked = _settings.Quiet;
        ToolTip.SetTip(_quiet, "Run uninstallers without their windows when they support it (MSI, Inno Setup, NSIS, Squirrel, WiX, Store apps). Others still show their own window.");
        _quiet.IsCheckedChanged += (_, _) =>
        {
            _settings.Quiet = _quiet.IsChecked == true;
            _settings.Save();
            ShowDetails();
        };
        foot.Children.Add(_quiet);
        _restore.IsChecked = _settings.RestorePoint;
        ToolTip.SetTip(_restore, "Make a Windows restore point before uninstalling or removing leftovers (once per run). Needs administrator rights and System Protection on.");
        _restore.IsCheckedChanged += (_, _) =>
        {
            _settings.RestorePoint = _restore.IsChecked == true;
            _settings.Save();
        };
        foot.Children.Add(_restore);
        var forced = new Button { Content = "Forced uninstall…", HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTip.SetTip(forced, "Clean up what's left of a program that isn't in the list anymore: type its name and pick its folder.");
        forced.Click += (_, _) => ShowForced();
        foot.Children.Add(forced);
        var backups = new Button { Content = "Open backups", HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTip.SetTip(backups, "Registry keys are saved here (as .reg files) before they're removed. Files go to the Recycle Bin.");
        backups.Click += (_, _) =>
        {
            Directory.CreateDirectory(Cli.BackupFolder);
            Start("explorer.exe", Text.Quote(Cli.BackupFolder));
        };
        foot.Children.Add(backups);
        foot.Children.Add(new TextBlock { Text = $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}", Classes = { "muted" }, FontSize = 11, Margin = new Thickness(4, 2, 0, 0) });
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);

        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0) };
        nav.Children.Add(new TextBlock { Text = "SHOW", Classes = { "label" }, Margin = new Thickness(12, 0, 0, 6) });
        for (var i = 0; i < Groups.Length; i++)
        {
            var index = i;
            var g = Groups[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,Auto") };
            row.Children.Add(new TextBlock { Text = g.Glyph, FontFamily = Glyphs, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = g.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var count = new TextBlock { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(count, 2);
            row.Children.Add(count);
            _navCounts[i] = count;
            var b = new Button
            {
                Content = row, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 8), MinHeight = 40,
            };
            b.Click += (_, _) => ShowGroup(index);
            _nav[i] = b;
            nav.Children.Add(b);
        }

        dock.Children.Add(new ScrollViewer { Content = nav });
        return dock;
    }

    private Control BuildHeader()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var left = new StackPanel();
        left.Children.Add(_title);
        left.Children.Add(_subtitle);
        g.Children.Add(left);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var hunt = new Button { Content = "Find by window" };
        ToolTip.SetTip(hunt, "Hunter: pick a program that's open now and jump to it in the list.");
        hunt.Click += (_, _) => ShowHunter();
        buttons.Children.Add(hunt);
        var export = new Button { Content = "Export" };
        ToolTip.SetTip(export, "Save the programs shown as an HTML page or a CSV spreadsheet.");
        export.Click += async (_, _) => await ExportAsync();
        buttons.Children.Add(export);
        var refresh = new Button { Content = "Refresh" };
        ToolTip.SetTip(refresh, "Look again for installed programs (F5).");
        refresh.Click += async (_, _) =>
        {
            if (!_busy) await LoadAsync();
        };
        buttons.Children.Add(refresh);
        Grid.SetColumn(buttons, 1);
        g.Children.Add(buttons);
        return g;
    }

    private static readonly string[] Sorts = ["Sort: name", "Sort: size", "Sort: newest", "Sort: publisher"];

    private Control BuildToolbar()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        _search.TextChanged += (_, _) => Refresh();
        g.Children.Add(_search);
        foreach (var s in Sorts) _sort.Items.Add(s);
        _sort.SelectedIndex = Math.Clamp(_settings.Sort, 0, Sorts.Length - 1);
        _sort.SelectionChanged += (_, _) =>
        {
            _settings.Sort = _sort.SelectedIndex;
            _settings.Save();
            Refresh();
        };
        Grid.SetColumn(_sort, 1);
        g.Children.Add(_sort);
        return g;
    }

    private Control BuildListArea()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,390") };
        _list.ItemTemplate = new FuncDataTemplate<InstalledApp>((a, _) => a is null ? new Panel() : Row(a), supportsRecycling: false);
        // Ctrl- or Shift-click picks several programs for a batch uninstall.
        _list.SelectionMode = SelectionMode.Multiple;
        _list.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _picked = _list.SelectedItems?.OfType<InstalledApp>().ToList() ?? [];
            _selected = _picked.Count == 1 ? _picked[0] : _list.SelectedItem as InstalledApp;
            _confirming = false;
            ShowDetails();
        };
        _list.DoubleTapped += (_, _) =>
        {
            if (_selected is not null && !_busy) AskUninstall();
        };
        var host = new Grid();
        host.Children.Add(_list);
        host.Children.Add(_empty);
        g.Children.Add(host);
        Grid.SetColumn(_details, 1);
        g.Children.Add(_details);
        ShowDetails();
        return g;
    }

    private Control Row(InstalledApp a)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,Auto,96"), Margin = new Thickness(4, 6), Background = Brushes.Transparent };
        var icon = AppIcons.View(a, 30);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        g.Children.Add(icon);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = a.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = string.Join("  ·  ", new[] { a.Publisher, a.Version }.Where(s => s.Length > 0));
        mid.Children.Add(new TextBlock { Text = sub.Length == 0 ? " " : sub, Classes = { "muted" }, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        if (a.Orphaned) tags.Children.Add(Pill("Broken", "bad"));
        else if (a.Kind == AppKind.Store) tags.Children.Add(Pill("Store", "info"));
        if (a.SizeText.Length > 0) tags.Children.Add(new TextBlock { Text = a.SizeText, Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 60, TextAlignment = TextAlignment.Right });
        Grid.SetColumn(tags, 2);
        g.Children.Add(tags);
        var date = new TextBlock { Text = a.InstallDate?.ToString("yyyy-MM-dd") ?? "", Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(date, 3);
        g.Children.Add(date);
        return g;
    }

    internal static Border Pill(string text, string kind) => new() { Classes = { "pill", kind }, Child = new TextBlock { Text = text } };

    public void ShowNotice(string text, bool good = false, string? action = null, Action? act = null)
    {
        _notice.Background = Brush(good ? "#0C2A1D" : "#2A2210");
        _notice.BorderBrush = Brush(good ? "#146B44" : "#7A5A1A");
        _noticeText.Foreground = Brush(good ? "#7DF5BC" : "#F5C97A");
        _noticeText.Text = text;
        _noticeAction.Content = action;
        _noticeAction.IsVisible = action is not null;
        _noticeDo = act;
        _notice.IsVisible = true;
    }

    public bool NoticeShowing => _notice.IsVisible;
    public string NoticeText => _noticeText.Text ?? "";

    // ---------- Loading and listing ----------

    public async Task LoadAsync()
    {
        _busy = true;
        _progress.IsVisible = true;
        _status.Text = "Looking for installed programs…";
        var watch = Stopwatch.StartNew();
        var apps = await Task.Run(() =>
        {
            var list = AppScanner.Scan();
            AppIcons.Preload(list);
            return list;
        });
        _busy = false;
        _progress.IsVisible = false;
        SetApps(apps);
        _status.Text = $"Found {Text.Plural(apps.Count, "program")} in {watch.Elapsed.TotalSeconds:0.0} s.";
    }

    public void SetApps(List<InstalledApp> apps)
    {
        var keep = _selected?.Id;
        _apps = apps;
        _selected = keep is null ? null : apps.FirstOrDefault(a => a.Id == keep);
        Refresh();
    }

    public void Select(InstalledApp a) => _list.SelectedItem = a;

    /// <summary>Several programs picked (as Ctrl-click does).</summary>
    public void SelectMany(IEnumerable<InstalledApp> apps)
    {
        _list.SelectedItems?.Clear();
        foreach (var a in apps) _list.SelectedItems?.Add(a);
    }

    public IReadOnlyList<InstalledApp> Picked => _picked;

    public void ShowGroup(int index)
    {
        _group = index;
        foreach (var (i, b) in _nav)
        {
            if (i == index) b.Classes.Add("selected-nav");
            else b.Classes.Remove("selected-nav");
            if (i == index) b.Foreground = Good;
            else b.ClearValue(ForegroundProperty);
        }

        CloseCleanup();
        Refresh();
    }

    private IEnumerable<InstalledApp> Filtered()
    {
        var q = (_search.Text ?? "").Trim();
        var list = _apps.Where(Groups[_group].Has);
        if (q.Length > 0)
            list = list.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase));
        return _sort.SelectedIndex switch
        {
            1 => list.OrderByDescending(a => a.SizeBytes).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => list.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            3 => list.OrderBy(a => a.Publisher.Length == 0 ? "￿" : a.Publisher, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
        };
    }

    public void Refresh()
    {
        for (var i = 0; i < Groups.Length; i++)
            _navCounts[i].Text = _apps.Count(Groups[i].Has).ToString("N0");
        var shown = Filtered().ToList();
        _refreshing = true;
        _list.ItemsSource = shown;
        _list.SelectedItem = _selected is not null && shown.Contains(_selected) ? _selected : null;
        _refreshing = false;
        _picked = _list.SelectedItem is InstalledApp one ? [one] : [];
        if (_list.SelectedItem is null && _selected is not null && !shown.Contains(_selected))
        {
            _selected = null;
            ShowDetails();
        }

        var g = Groups[_group];
        _title.Text = g.Name;
        long total = shown.Sum(a => a.SizeBytes);
        _subtitle.Text = $"{Text.Plural(shown.Count, "program")}{(total > 0 ? $"  ·  {Text.Size(total)}" : "")}  ·  {g.Blurb}";
        _empty.IsVisible = shown.Count == 0 && !_busy;
        _empty.Text = _apps.Count == 0 ? "Nothing listed yet." : (_search.Text ?? "").Length > 0 ? "Nothing matches your search." : "Nothing in this group.";
        ShowDetails();
    }

    // ---------- Details ----------

    private void ShowDetails()
    {
        if (_picked.Count > 1)
        {
            ShowBatchDetails();
            return;
        }

        var a = _selected;
        if (a is null)
        {
            _details.Child = new TextBlock
            {
                Text = "Pick a program to see how it uninstalls.\n\nUninstall runs its own uninstaller. Uninstall and clean up also finds what it leaves behind (folders, registry, shortcuts, services, tasks, firewall rules) so you can pick what goes.",
                Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }

        var s = new StackPanel { Spacing = 10 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var icon = AppIcons.View(a, 52);
        icon.VerticalAlignment = VerticalAlignment.Top;
        head.Children.Add(icon);
        var names = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = a.Name, FontSize = 17, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        if (a.Publisher.Length > 0) names.Children.Add(new TextBlock { Text = a.Publisher, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        s.Children.Add(head);

        var pills = new WrapPanel { Orientation = Orientation.Horizontal };
        void P(string t, string k)
        {
            var p = Pill(t, k);
            p.Margin = new Thickness(0, 0, 6, 6);
            pills.Children.Add(p);
        }

        P(a.Plan.Method, a.Orphaned ? "bad" : "info");
        if (!a.Orphaned) P(a.Plan.CanQuiet ? "Quiet uninstall: yes" : "Quiet uninstall: no", a.Plan.CanQuiet ? "good" : "warn");
        P(a.JustForMe ? "Just for me" : "All users", "");
        s.Children.Add(pills);

        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*") };
        var row = 0;
        void F(string k, string? v, bool mono = false)
        {
            if (string.IsNullOrEmpty(v)) return;
            facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var kt = new TextBlock { Text = k, Classes = { "muted" }, FontSize = 12, Margin = new Thickness(0, 3) };
            Grid.SetRow(kt, row);
            facts.Children.Add(kt);
            var vt = new TextBlock { Text = v, FontSize = mono ? 11.5 : 12.5, FontFamily = mono ? Mono : FontFamily.Default, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3) };
            Grid.SetRow(vt, row);
            Grid.SetColumn(vt, 1);
            facts.Children.Add(vt);
            row++;
        }

        F("Version", a.Version);
        F("Installed", a.InstallDate?.ToString("d MMMM yyyy"));
        F("Size", a.SizeText);
        F("Folder", a.InstallLocation, mono: true);
        F("Uninstall", a.Orphaned ? null : (_settings.Quiet && a.Plan.CanQuiet ? a.Plan.QuietCommand : a.Plan.Command), mono: true);
        s.Children.Add(facts);

        if (!a.Plan.CanQuiet && a.Plan.WhyNotQuiet is { } why)
            s.Children.Add(new TextBlock { Text = why, Foreground = a.Orphaned ? Bad : Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var actions = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        if (_confirming)
        {
            var quiet = _settings.Quiet && a.Plan.CanQuiet;
            actions.Children.Add(new TextBlock
            {
                Text = quiet ? $"Remove {a.Name} quietly now?" : $"Run {a.Name}'s uninstaller now? Its own window will open.",
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            var yes = new Button { Content = "Yes, uninstall", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            yes.Click += async (_, _) => await UninstallAsync(a);
            var no = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch };
            no.Click += (_, _) =>
            {
                _confirming = false;
                ShowDetails();
            };
            actions.Children.Add(yes);
            actions.Children.Add(no);
        }
        else if (a.Orphaned)
        {
            var clean = new Button { Content = "Clean up leftovers…", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !_busy };
            clean.Click += async (_, _) => await OpenCleanupAsync(a, finder: null, afterUninstall: false);
            actions.Children.Add(clean);
        }
        else
        {
            var un = new Button { Content = "Uninstall", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !_busy && CanTouch(a) };
            ToolTip.SetTip(un, "Runs the program's own uninstaller, the normal way.");
            un.Click += (_, _) => AskUninstall();
            actions.Children.Add(un);
            var deep = new Button { Content = "Uninstall and clean up…", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !_busy && CanTouch(a) };
            ToolTip.SetTip(deep, "Shows everything that belongs to it first: folders, registry, shortcuts, services, tasks, firewall rules. You pick what goes.");
            deep.Click += async (_, _) => await OpenCleanupAsync(a, finder: null, afterUninstall: false);
            actions.Children.Add(deep);
            if (!CanTouch(a))
                actions.Children.Add(new TextBlock { Text = "Installed for all users: needs administrator rights.", Foreground = Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        var more = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (a.InstallLocation.Length > 0 && Directory.Exists(a.InstallLocation))
        {
            var open = new Button { Content = "Open folder", Classes = { "ghost" }, Margin = new Thickness(0, 0, 6, 0) };
            open.Click += (_, _) => Start("explorer.exe", Text.Quote(a.InstallLocation));
            more.Children.Add(open);
        }

        if (a.HelpLink is { } link && link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var web = new Button { Content = "Website", Classes = { "ghost" } };
            web.Click += (_, _) => Start(link, "");
            more.Children.Add(web);
        }

        actions.Children.Add(more);
        s.Children.Add(actions);
        _details.Child = new ScrollViewer { Content = s };
    }

    private DateTime _restoreAt = DateTime.MinValue;

    /// <summary>A restore point is made when switched on, at most once every 15 minutes (a batch or a busy session makes one).</summary>
    private bool WantRestorePoint()
    {
        if (!_settings.RestorePoint) return false;
        if (DateTime.Now - _restoreAt < TimeSpan.FromMinutes(15)) return false;
        _restoreAt = DateTime.Now;
        return true;
    }

    /// <summary>Machine-wide programs need administrator rights.</summary>
    private static bool CanTouch(InstalledApp a) => a.JustForMe || Cli.IsElevated || SelfTest.Active;

    private void AskUninstall()
    {
        if (_selected is null || _selected.Orphaned || !CanTouch(_selected)) return;
        _confirming = true;
        ShowDetails();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _progress.IsVisible = busy;
        if (status is not null) _status.Text = status;
        ShowDetails();
    }

    /// <summary>The normal uninstall: the program's own uninstaller (quietly when that's switched on), then an offer to look for leftovers.</summary>
    public async Task UninstallAsync(InstalledApp a)
    {
        _confirming = false;
        var quiet = _settings.Quiet && a.Plan.CanQuiet;
        SetBusy(true, quiet ? $"Removing {a.Name} quietly…" : $"Waiting for {a.Name}'s uninstaller…");
        var all = _apps.ToList();
        var finder = await Task.Run(() => new LeftoverFinder(a, all));   // made before: it remembers the program's folders and files
        var job = new UninstallJob(a, [new Leftover { Kind = LeftoverKind.Uninstaller, Match = MatchLevel.Regular, Group = "Uninstall", Path = a.Name, Command = a.Plan.Command }], Cli.BackupFolder)
        {
            Quiet = quiet, MakeRestorePoint = WantRestorePoint(),
        };
        job.Log += s => Dispatcher.UIThread.Post(() => _status.Text = s);
        UninstallReport report;
        try
        {
            report = await job.RunAsync();
        }
        finally
        {
            SetBusy(false);
        }

        if (!report.Uninstalled)
        {
            ShowNotice(report.Message);
            _status.Text = report.Message;
            return;
        }

        _apps.Remove(a);
        _selected = null;
        Refresh();
        _status.Text = report.Message;
        ShowNotice(report.Message + " Want to look for anything it left behind?", good: true, action: "Look for leftovers",
            act: () => _ = OpenCleanupAsync(a, finder, afterUninstall: true));
    }

    internal static void Start(string exe, string args)
    {
        if (SelfTest.Active) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch
        {
            /* nothing to open */
        }
    }

    // ---------- Settings ----------

    private sealed class Settings
    {
        public bool Quiet { get; set; }
        public bool RestorePoint { get; set; } = true;
        public int Sort { get; set; }

        private static string File => Path.Combine(Program.DataFolder, "settings.json");

        public static Settings Load()
        {
            try
            {
                return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new() : new();
            }
            catch
            {
                return new();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Program.DataFolder);
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this));
            }
            catch
            {
            }
        }
    }
}
