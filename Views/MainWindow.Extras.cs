using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using pxdUninstaller.Core;

namespace pxdUninstaller.Views;

/// <summary>
/// The extras: batch uninstall (several programs picked with Ctrl/Shift), forced uninstall (leftovers of a program
/// that isn't listed anymore), hunter (from an open window to its program) and exporting the list.
/// </summary>
public sealed partial class MainWindow
{
    // ---------- Batch ----------

    private void ShowBatchDetails()
    {
        var picked = _picked;
        var s = new StackPanel { Spacing = 10 };
        s.Children.Add(new TextBlock { Text = $"{Text.Plural(picked.Count, "program")} picked", FontSize = 17, FontWeight = FontWeight.Bold });
        var size = picked.Sum(a => a.SizeBytes);
        s.Children.Add(new TextBlock { Text = (size > 0 ? Text.Size(size) + "  ·  " : "") + "Ctrl-click to add or drop one, Shift-click for a run.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var rows = new StackPanel { Spacing = 4 };
        foreach (var a in picked.Take(12))
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,Auto") };
            var icon = AppIcons.View(a, 18);
            icon.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(icon);
            var name = new TextBlock { Text = a.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            g.Children.Add(name);
            var pill = a.Orphaned ? Pill("broken", "bad") : a.Plan.CanQuiet ? Pill("quiet", "good") : Pill("own window", "warn");
            Grid.SetColumn(pill, 2);
            g.Children.Add(pill);
            rows.Children.Add(g);
        }

        if (picked.Count > 12) rows.Children.Add(new TextBlock { Text = $"and {picked.Count - 12} more", Classes = { "muted" }, FontSize = 12 });
        s.Children.Add(rows);

        var loud = picked.Count(a => !a.Plan.CanQuiet && !a.Orphaned);
        var broken = picked.Count(a => a.Orphaned);
        var blocked = picked.Count(a => !CanTouch(a));
        var notes = new StringBuilder("They're removed one after another, quietly where they can be.");
        if (loud > 0) notes.Append($" {Text.Plural(loud, "uninstaller")} without a quiet mode will open {(loud == 1 ? "its own window" : "their own windows")}.");
        if (broken > 0) notes.Append($" {Text.Plural(broken, "broken entry", "broken entries")} only get cleaned up.");
        s.Children.Add(new TextBlock { Text = notes.ToString(), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = loud > 0 ? Warn : Muted });
        if (blocked > 0)
            s.Children.Add(new TextBlock { Text = $"{Text.Plural(blocked, "program")} installed for all users need administrator rights and will be skipped.", Foreground = Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var actions = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        if (_confirming)
        {
            actions.Children.Add(new TextBlock { Text = $"Uninstall {Text.Plural(picked.Count(a => !a.Orphaned && CanTouch(a)), "program")} now?", FontWeight = FontWeight.SemiBold });
            var yes = new Button { Content = "Yes, uninstall them", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            yes.Click += async (_, _) => await BatchUninstallAsync(picked.ToList());
            var no = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch };
            no.Click += (_, _) =>
            {
                _confirming = false;
                ShowDetails();
            };
            actions.Children.Add(yes);
            actions.Children.Add(no);
        }
        else
        {
            var un = new Button { Content = $"Uninstall {picked.Count} programs", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !_busy };
            un.Click += (_, _) =>
            {
                _confirming = true;
                ShowDetails();
            };
            actions.Children.Add(un);
            var deep = new Button { Content = "Uninstall and clean up…", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !_busy };
            ToolTip.SetTip(deep, "Shows each program's uninstaller and leftovers first, so you can pick what goes.");
            deep.Click += async (_, _) => await OpenBatchCleanupAsync(picked.ToList());
            actions.Children.Add(deep);
        }

        s.Children.Add(actions);
        _details.Child = new ScrollViewer { Content = s, Padding = new Thickness(0, 0, 14, 0) };
    }

    /// <summary>Uninstalls each program in turn (quietly when it can), then offers one clean-up page for all their leftovers.</summary>
    public async Task BatchUninstallAsync(List<InstalledApp> picked)
    {
        _confirming = false;
        var targets = picked.Where(a => !a.Orphaned && CanTouch(a)).ToList();
        if (targets.Count == 0) return;
        var all = _apps.ToList();
        var (title, _, foot, say) = LogPage($"Uninstalling {Text.Plural(targets.Count, "program")}…");
        SetBusy(true);
        var gone = new List<(InstalledApp App, LeftoverFinder Finder)>();
        var failed = new List<string>();
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var a = targets[i];
                _status.Text = $"{i + 1} of {targets.Count}: {a.Name}…";
                say($"── {i + 1}/{targets.Count}  {a.Name} ──");
                var finder = await Task.Run(() => new LeftoverFinder(a, all));
                var job = new UninstallJob(a, [new Leftover { Kind = LeftoverKind.Uninstaller, Match = MatchLevel.Regular, Group = "Uninstall", Path = a.Name, Command = a.Plan.Command }], Cli.BackupFolder)
                {
                    Quiet = true, MakeRestorePoint = WantRestorePoint(),
                };
                job.Log += say;
                var report = await job.RunAsync();
                if (report.Uninstalled) gone.Add((a, finder));
                else
                {
                    failed.Add(a.Name);
                    say(report.Message);
                }
            }
        }
        finally
        {
            SetBusy(false);
        }

        foreach (var (a, _) in gone) _apps.Remove(a);
        _selected = null;
        Refresh();
        title.Text = failed.Count == 0 ? $"All {targets.Count} uninstalled" : $"{gone.Count} of {targets.Count} uninstalled";
        title.Foreground = failed.Count == 0 ? Good : gone.Count > 0 ? Warn : Bad;
        say("");
        say(failed.Count == 0 ? "Done." : "Still installed: " + string.Join(", ", failed));
        _status.Text = title.Text + ".";

        if (gone.Count > 0)
        {
            var look = new Button { Content = "Look for leftovers", Classes = { "primary" } };
            look.Click += async (_, _) =>
            {
                ShowPage(Waiting("Looking for what they left behind…", string.Join(", ", gone.Select(g => g.App.Name))));
                SetBusy(true);
                var sets = await Task.Run(() => gone.Select(g => (g.App, g.Finder.Find().Where(l => l.Kind != LeftoverKind.Uninstaller && UninstallJob.Exists(l)).ToList())).ToList());
                SetBusy(false);
                ShowCleanup(sets, afterUninstall: true);
            };
            foot.Children.Add(look);
        }

        var back = new Button { Content = "Back to the list" };
        back.Click += (_, _) => CloseCleanup();
        foot.Children.Add(back);
    }

    /// <summary>Batch, the careful way: every program's uninstaller and leftovers on one page before anything runs.</summary>
    public async Task OpenBatchCleanupAsync(List<InstalledApp> picked, List<(InstalledApp, List<Leftover>)>? given = null)
    {
        var targets = picked.Where(CanTouch).ToList();
        if (targets.Count == 0) return;
        ShowPage(Waiting($"Looking for everything that belongs to {Text.Plural(targets.Count, "program")}…", string.Join(", ", targets.Select(a => a.Name))));
        var all = _apps.ToList();
        SetBusy(true);
        List<(InstalledApp, List<Leftover>)> sets;
        try
        {
            sets = given ?? await Task.Run(() => targets.Select(a => (a, new LeftoverFinder(a, all).Find())).ToList());
        }
        finally
        {
            SetBusy(false);
        }

        ShowCleanup(sets, afterUninstall: false);
    }

    // ---------- Forced uninstall ----------

    private readonly TextBox _forcedName = new() { PlaceholderText = "Program name, e.g. Old Scanner Tool" };
    private readonly TextBox _forcedFolder = new() { PlaceholderText = @"Its folder (optional), e.g. C:\Program Files\Old Scanner Tool" };
    private bool _forcedHooked;
    private readonly TextBlock _forcedError = new() { Foreground = Brush("#F5C97A"), TextWrapping = TextWrapping.Wrap, IsVisible = false };

    /// <summary>Forced uninstall: leftovers of a program that isn't listed anymore (or never was), by name and folder.</summary>
    public void ShowForced(string? name = null, string? folder = null)
    {
        if (_busy) return;
        if (name is not null) _forcedName.Text = name;
        if (folder is not null) _forcedFolder.Text = folder;
        _forcedError.IsVisible = false;
        Detach(_forcedName);
        Detach(_forcedFolder);
        Detach(_forcedError);
        var s = new StackPanel { Spacing = 10, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        s.Children.Add(new TextBlock { Text = "Forced uninstall", FontSize = 22, FontWeight = FontWeight.Bold });
        s.Children.Add(new TextBlock
        {
            Text = "For a program that's gone from the list but left things behind, or whose uninstaller failed. Type its name and, if you know it, its folder. "
                   + "pxd Uninstaller looks everywhere programs keep things and shows what it finds; nothing is removed until you choose.",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        });
        s.Children.Add(new TextBlock { Text = "NAME", Classes = { "label" }, Margin = new Thickness(2, 8, 0, 0) });
        s.Children.Add(_forcedName);
        s.Children.Add(new TextBlock { Text = "FOLDER", Classes = { "label" }, Margin = new Thickness(2, 4, 0, 0) });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(_forcedFolder);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The program's folder", AllowMultiple = false });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } p) _forcedFolder.Text = p;
        };
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        s.Children.Add(row);
        s.Children.Add(_forcedError);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var back = new Button { Content = "Back", Classes = { "ghost" } };
        back.Click += (_, _) => CloseCleanup();
        var go = new Button { Content = "Find leftovers", Classes = { "primary" } };
        go.Click += async (_, _) => await ForcedFindAsync();
        if (!_forcedHooked)
        {
            _forcedHooked = true;
            _forcedName.KeyDown += async (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) await ForcedFindAsync();
            };
        }
        buttons.Children.Add(back);
        buttons.Children.Add(go);
        s.Children.Add(buttons);
        ShowPage(new ScrollViewer { Content = s });
    }

    public async Task ForcedFindAsync()
    {
        var name = (_forcedName.Text ?? "").Trim();
        var folder = (_forcedFolder.Text ?? "").Trim();
        if (Forced.Problem(name, folder) is { } problem)
        {
            _forcedError.Text = problem;
            _forcedError.IsVisible = true;
            return;
        }

        if (folder.Length > 0 && !Directory.Exists(folder.Trim('"')))
        {
            _forcedError.Text = "That folder isn't there anymore. It's fine: leave it empty and search by name, or pick another.";
            _forcedError.IsVisible = true;
            return;
        }

        if (_apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } listed)
        {
            CloseCleanup();
            ShowGroup(0);
            Select(listed);
            ShowNotice($"\"{listed.Name}\" is still installed, so it's picked in the list: Uninstall and clean up runs its uninstaller first.");
            return;
        }

        await OpenCleanupAsync(Forced.App(name, folder.Length > 0 ? folder : null), null, afterUninstall: false, title: $"Forced uninstall: {name}");
    }

    // ---------- Hunter ----------

    /// <summary>Open windows to pick from (replaced in the self-test for screenshots).</summary>
    public static Func<List<OpenWindow>> WindowSource { get; set; } = Hunter.Windows;

    /// <summary>Hunter: pick an open window and jump to the program it belongs to.</summary>
    public void ShowHunter()
    {
        if (_busy) return;
        var windows = WindowSource();
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock { Text = "Find by window", FontSize = 22, FontWeight = FontWeight.Bold });
        head.Children.Add(new TextBlock { Text = "Pick a program that's open now to jump to it in the list. Not sure what's running in the background? Open it first, then come back here.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        page.Children.Add(head);

        var list = new StackPanel { Spacing = 6 };
        if (windows.Count == 0)
            list.Children.Add(new TextBlock { Text = "No program windows are open.", Classes = { "muted" }, Margin = new Thickness(4, 20) });
        foreach (var w in windows)
        {
            var app = Hunter.ForPath(w.ExePath, _apps);
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = w.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            left.Children.Add(new TextBlock { Text = w.ExePath, FontFamily = Mono, FontSize = 11.5, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            g.Children.Add(left);
            var pill = app is null ? Pill("not installed (portable)", "warn") : Pill(app.Name, "good");
            pill.Margin = new Thickness(12, 0, 0, 0);
            pill.MaxWidth = 280;
            Grid.SetColumn(pill, 1);
            g.Children.Add(pill);
            var b = new Button { Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 8) };
            b.Click += (_, _) => HuntPicked(w, app);
            list.Children.Add(b);
        }

        var scroll = new ScrollViewer { Content = list, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetRow(scroll, 1);
        page.Children.Add(scroll);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var back = new Button { Content = "Back", Classes = { "ghost" } };
        back.Click += (_, _) => CloseCleanup();
        var again = new Button { Content = "Look again" };
        again.Click += (_, _) => ShowHunter();
        foot.Children.Add(back);
        foot.Children.Add(again);
        Grid.SetRow(foot, 2);
        page.Children.Add(foot);
        ShowPage(page);
    }

    private void HuntPicked(OpenWindow w, InstalledApp? app)
    {
        if (app is not null)
        {
            CloseCleanup();
            _search.Text = "";
            ShowGroup(0);
            Select(app);
            ShowNotice($"\"{w.Title}\" is {app.Name}.", good: true);
            return;
        }

        // Not installed: offer to clean it up by its folder (portable programs keep everything there).
        var dir = Path.GetDirectoryName(w.ExePath);
        ShowForced(Path.GetFileNameWithoutExtension(w.ExePath), dir is not null && !Places.Current.IsProtected(dir) ? dir : "");
        ShowNotice($"\"{w.Title}\" isn't an installed program (it runs from {w.ExePath}). Close it first if you want to remove what it left.");
    }

    // ---------- Export ----------

    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the program list",
            SuggestedFileName = $"Programs on {Environment.MachineName}",
            DefaultExtension = "html",
            FileTypeChoices = [new FilePickerFileType("Web page") { Patterns = ["*.html"] }, new FilePickerFileType("Spreadsheet (CSV)") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var shown = Filtered().ToList();
        try
        {
            await File.WriteAllTextAsync(path, Export.For(path, shown), new UTF8Encoding(true));
            ShowNotice($"Saved {Text.Plural(shown.Count, "program")} to {path}.", good: true, action: "Open", act: () => Start(path, ""));
        }
        catch (Exception ex)
        {
            ShowNotice("Couldn't save the list: " + ex.Message);
        }
    }
}
