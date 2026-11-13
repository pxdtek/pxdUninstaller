namespace pxdUninstaller.Core;

/// <summary>
/// The folders where programs keep files. The self-test swaps them for folders of its own, so it never looks at or
/// removes anything real.
/// </summary>
public sealed class Places
{
    public static Places Current { get; set; } = Real();

    public string ProgramFiles { get; init; } = "";
    public string ProgramFilesX86 { get; init; } = "";
    public string CommonFiles { get; init; } = "";
    public string CommonFilesX86 { get; init; } = "";
    public string ProgramData { get; init; } = "";
    public string AppData { get; init; } = "";
    public string LocalAppData { get; init; } = "";
    public string LocalLow { get; init; } = "";
    public string Documents { get; init; } = "";
    public string PublicDocuments { get; init; } = "";
    public string Temp { get; init; } = "";
    public string StartMenu { get; init; } = "";
    public string CommonStartMenu { get; init; } = "";
    public string Desktop { get; init; } = "";
    public string PublicDesktop { get; init; } = "";
    public string Windows { get; init; } = "";
    public string UserProfile { get; init; } = "";

    /// <summary>True in the self-test: no services, tasks, firewall or Store commands are run for real.</summary>
    public bool Test { get; init; }

    public string LocalPrograms => Path.Combine(LocalAppData, "Programs");
    public string Packages => Path.Combine(LocalAppData, "Packages");
    public string TaskbarPins => Path.Combine(AppData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
    public string QuickLaunch => Path.Combine(AppData, @"Microsoft\Internet Explorer\Quick Launch");
    public string Prefetch => Path.Combine(Windows, "Prefetch");
    public string CrashDumps => Path.Combine(LocalAppData, "CrashDumps");
    public string WerReports => Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive");

    public static Places Real()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var profile = F(Environment.SpecialFolder.UserProfile);
        return new Places
        {
            ProgramFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? F(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86 = F(Environment.SpecialFolder.ProgramFilesX86),
            CommonFiles = Environment.GetEnvironmentVariable("CommonProgramW6432") ?? F(Environment.SpecialFolder.CommonProgramFiles),
            CommonFilesX86 = F(Environment.SpecialFolder.CommonProgramFilesX86),
            ProgramData = F(Environment.SpecialFolder.CommonApplicationData),
            AppData = F(Environment.SpecialFolder.ApplicationData),
            LocalAppData = local,
            LocalLow = Path.Combine(profile, @"AppData\LocalLow"),
            Documents = F(Environment.SpecialFolder.MyDocuments),
            PublicDocuments = F(Environment.SpecialFolder.CommonDocuments),
            Temp = Path.GetTempPath().TrimEnd('\\'),
            StartMenu = F(Environment.SpecialFolder.Programs),
            CommonStartMenu = F(Environment.SpecialFolder.CommonPrograms),
            Desktop = F(Environment.SpecialFolder.DesktopDirectory),
            PublicDesktop = F(Environment.SpecialFolder.CommonDesktopDirectory),
            Windows = F(Environment.SpecialFolder.Windows),
            UserProfile = profile,
        };
    }

    /// <summary>A full set of empty folders under <paramref name="root"/> (self-test).</summary>
    public static Places Under(string root)
    {
        string D(string name)
        {
            var p = Path.Combine(root, name);
            Directory.CreateDirectory(p);
            return p;
        }

        return new Places
        {
            ProgramFiles = D("Program Files"),
            ProgramFilesX86 = D("Program Files (x86)"),
            CommonFiles = D(@"Program Files\Common Files"),
            CommonFilesX86 = D(@"Program Files (x86)\Common Files"),
            ProgramData = D("ProgramData"),
            AppData = D(@"Users\me\AppData\Roaming"),
            LocalAppData = D(@"Users\me\AppData\Local"),
            LocalLow = D(@"Users\me\AppData\LocalLow"),
            Documents = D(@"Users\me\Documents"),
            PublicDocuments = D(@"Users\Public\Documents"),
            Temp = D(@"Users\me\AppData\Local\Temp"),
            StartMenu = D(@"Users\me\AppData\Roaming\Microsoft\Windows\Start Menu\Programs"),
            CommonStartMenu = D(@"ProgramData\Microsoft\Windows\Start Menu\Programs"),
            Desktop = D(@"Users\me\Desktop"),
            PublicDesktop = D(@"Users\Public\Desktop"),
            Windows = D("Windows"),
            UserProfile = D(@"Users\me"),
            Test = true,
        };
    }

    /// <summary>
    /// Folders that are never removed, whatever matched: the roots above, the drive roots, the Windows folder and
    /// its contents, the user profile and the shell folders inside AppData.
    /// </summary>
    public bool IsProtected(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            return true;
        }

        if (full.Length <= 3) return true;   // C:\
        if (Within(full, Windows)) return true;
        string[] exact =
        [
            ProgramFiles, ProgramFilesX86, CommonFiles, CommonFilesX86, ProgramData, AppData, LocalAppData, LocalLow, LocalPrograms,
            Documents, PublicDocuments, Temp, StartMenu, CommonStartMenu, Desktop, PublicDesktop, UserProfile, Packages,
            Path.GetDirectoryName(UserProfile) ?? "", Path.Combine(UserProfile, "AppData"), TaskbarPins, QuickLaunch,
            Path.Combine(StartMenu, "Startup"), Path.Combine(CommonStartMenu, "Startup"),
            Path.Combine(ProgramData, "Microsoft"), Path.Combine(AppData, "Microsoft"), Path.Combine(LocalAppData, "Microsoft"),
            Path.Combine(ProgramFiles, "WindowsApps"), Path.Combine(ProgramFiles, "Windows Defender"), Path.Combine(ProgramFilesX86, "Microsoft"),
            Path.Combine(ProgramFiles, "Microsoft"), Path.Combine(ProgramData, "Package Cache"), Path.Combine(LocalAppData, "Package Cache"),
        ];
        if (exact.Any(e => e.Length > 0 && full.Equals(e.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return true;
        // Top-level folders of the profile (Downloads, Pictures, OneDrive…) hold the user's own files.
        if (UserProfile.Length > 0 && string.Equals(Path.GetDirectoryName(full), UserProfile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        // Anything holding one of the roots (C:\Users, C:\Users\me\AppData…) is protected too.
        return exact.Any(e => e.Length > 0 && Within(e, full) && !e.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool Within(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        var p = path.TrimEnd('\\');
        var f = folder.TrimEnd('\\');
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
