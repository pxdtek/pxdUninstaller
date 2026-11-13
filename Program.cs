using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using pxdUninstaller.Core;

namespace pxdUninstaller;

internal static class Program
{
    public const string AppId = "pxdUninstaller";

    /// <summary>Where updates come from: this app's releases (asked at pxdtek.com first, GitHub as the fallback) and its Windows zip (the same asset the pxd Toolkit installs).</summary>
    public const string UpdateRepo = "pxdtek/pxdUninstaller";
    public const string UpdateAsset = @"^pxdUninstaller-.*-win-x64\.zip$";

    /// <summary>Runs that never update first: the build step, the elevated copy the app starts itself (the first copy already updated).</summary>
    public static readonly string[] NoUpdateArgs = ["--write-icon", "--elevated"];

    /// <summary>Why the app runs without administrator rights, for the notice in the window (null when elevated).</summary>
    public static string? LimitedReason { get; private set; }

    public static string DataFolder => Cli.DataFolder;

    private static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? After(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Command line (list / show / leftovers / remove): same as pxdu.exe, printed to the console that started us.
        if (args.Length > 0 && Cli.Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase))
        {
            Native.AttachConsole(-1);
            Console.WriteLine();
            return Cli.Run(args, "pxdUninstaller");
        }

        // pxd standard rule 6: an installed copy updates itself before it opens, however it was started. A no-op for dev
        // builds (bin\), --self-test, PXD_NO_UPDATE=1, offline or when neither pxdtek.com nor GitHub answers in time; when it updated, the new copy is already starting.
        if (!args.Any(a => NoUpdateArgs.Contains(a, StringComparer.OrdinalIgnoreCase))
            && pxdStandard.pxdUpdate.BeforeStart(UpdateRepo, UpdateAsset, args))
            return 0;

        if (After(args, "--write-icon") is { } icon)
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            BrandIcon.Write(icon);
            return 0;
        }

        if (After(args, "--self-test") is { } testDir)
        {
            SelfTest.Dir = Path.GetFullPath(testDir);
            SelfTest.Sample = Has(args, "--sample");
            Directory.CreateDirectory(SelfTest.Dir);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PXDU_DATA")))
                Environment.SetEnvironmentVariable("PXDU_DATA", Path.Combine(SelfTest.Dir, "data"));
            pxdStandard.pxdChrome.Configure(AppId, DataFolder);
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            using var done = new CancellationTokenSource();
            Dispatcher.UIThread.Post(async () =>
            {
                await SelfTest.RunAsync();
                done.Cancel();
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            return SelfTest.ExitCode;
        }

        if (!Cli.IsElevated && !Has(args, "--no-elevate"))
        {
            // Already open? Hand over. Otherwise ask Windows for administrator rights and let that copy run.
            if (Mutex.TryOpenExisting(@"Local\" + SingleInstance.Name(AppId), out var m))
            {
                m.Dispose();
                if (SingleInstance.Send(SingleInstance.Name(AppId), args)) return 0;
            }

            if (!Has(args, "--elevated"))
            {
                var why = Elevation.RestartElevated(args);
                if (why is null) return 0;
                LimitedReason = why == "cancelled"
                    ? "Running without administrator rights because the Windows prompt was cancelled. Programs installed for all users can't be removed."
                    : $"Couldn't get administrator rights ({why}). Programs installed for all users can't be removed.";
            }
            else
            {
                LimitedReason = "Running without administrator rights. Programs installed for all users can't be removed.";
            }
        }
        else if (!Cli.IsElevated)
        {
            LimitedReason = "Started with --no-elevate: only programs installed just for you can be removed.";
        }

        if (!SingleInstance.Claim(SingleInstance.Name(AppId), args))
            return 0;

        pxdStandard.pxdChrome.Configure(AppId, DataFolder);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .LogToTrace();
}
