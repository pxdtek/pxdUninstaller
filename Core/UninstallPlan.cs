using System.Text;

namespace pxdUninstaller.Core;

/// <summary>
/// How a program comes off: the command its uninstaller runs normally, and the quiet one (no windows, no questions)
/// when the installer type is known to support it.
/// </summary>
public sealed class UninstallPlan
{
    public static readonly UninstallPlan None = new()
    {
        Method = "No uninstaller", Missing = true,
        WhyNotQuiet = "This entry has no uninstaller. Its leftovers can still be cleaned up.",
    };

    /// <summary>Installer type in words: "Windows Installer (MSI)", "Inno Setup", "NSIS"…</summary>
    public string Method { get; init; } = "";

    /// <summary>The normal uninstall command (the program's own uninstaller with its windows).</summary>
    public string? Command { get; init; }

    /// <summary>The same uninstall without windows or questions; null when there is no known way.</summary>
    public string? QuietCommand { get; init; }

    /// <summary>The uninstaller file is gone: only the leftovers can be cleaned up.</summary>
    public bool Missing { get; init; }

    /// <summary>Why it can't run quietly, in plain words.</summary>
    public string? WhyNotQuiet { get; init; }

    public bool IsMsi { get; init; }
    public bool IsStore { get; init; }

    public bool CanQuiet => QuietCommand is not null;

    public static UninstallPlan For(InstalledApp a)
    {
        if (a.Kind == AppKind.Store && a.PackageFullName is { } pkg)
        {
            var cmd = StoreCommand(pkg);
            return new UninstallPlan { Method = "Store app", Command = cmd, QuietCommand = cmd, IsStore = true };
        }

        // Windows Installer: msiexec takes the product code; /qn is fully quiet.
        var code = a.ProductCode;
        if (code is null && a.UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            code = Text.Guid(a.UninstallString);
        if (code is not null && (a.WindowsInstaller || a.UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || a.UninstallString.Length == 0))
        {
            return new UninstallPlan
            {
                Method = "Windows Installer (MSI)", IsMsi = true,
                Command = $"MsiExec.exe /X{code}",
                QuietCommand = $"MsiExec.exe /X{code} /qn /norestart",
            };
        }

        var source = a.UninstallString.Length > 0 ? a.UninstallString : a.QuietUninstallString;
        if (source.Length == 0) return None;

        var (exe, args) = Text.SplitCommand(source);
        var full = Resolve(exe);
        if (full is null)
        {
            return new UninstallPlan
            {
                Method = "Uninstaller missing", Missing = true, Command = null,
                WhyNotQuiet = $"Its uninstaller ({Path.GetFileName(exe)}) is no longer there. The leftovers can still be cleaned up.",
            };
        }

        var normal = Text.Quote(full) + (args.Length > 0 ? " " + args : "");
        var file = Path.GetFileName(full);
        string? quiet = null;
        string method;
        string? why = null;

        if (a.QuietUninstallString.Length > 0)
        {
            method = "Own uninstaller";
            quiet = a.QuietUninstallString;
        }
        else if (a.Key?.Name.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) == true || a.Values.ContainsKey("Inno Setup: App Path")
                 || System.Text.RegularExpressions.Regex.IsMatch(file, @"^unins\d{3}\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            method = "Inno Setup";
            quiet = Add(normal, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
        }
        else if (file.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            method = "Squirrel";
            quiet = Add(normal, "-s");
        }
        else if (a.Values.ContainsKey("BundleCachePath") || a.Values.ContainsKey("BundleUpgradeCode") || a.Values.ContainsKey("BundleProviderKey"))
        {
            method = "WiX bundle";
            quiet = Add(args.Contains("/uninstall", StringComparison.OrdinalIgnoreCase) ? normal : Add(normal, "/uninstall"), "/quiet /norestart");
        }
        else if (file.Equals("setup.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            method = "Chromium setup";
            quiet = Add(normal, "--force-uninstall");
        }
        else if (file.Equals("winget.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            method = "winget";
            quiet = Add(normal, "--silent --disable-interactivity --accept-source-agreements");
        }
        else if (file.Equals("OfficeClickToRun.exe", StringComparison.OrdinalIgnoreCase))
        {
            method = "Office Click-to-Run";
            quiet = Add(normal, "DisplayLevel=False");
        }
        else if (file.Equals("OneDriveSetup.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("/uninstall", StringComparison.OrdinalIgnoreCase))
        {
            method = "OneDrive setup";
            quiet = normal;   // OneDrive's own uninstall shows no windows
        }
        else if (full.Contains(@"Microsoft Visual Studio\Installer", StringComparison.OrdinalIgnoreCase) && args.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            method = "Visual Studio Installer";
            quiet = Add(normal, "--quiet --norestart");
        }
        else if (file.Equals("sdbinst.exe", StringComparison.OrdinalIgnoreCase))
        {
            method = "Compatibility fix";
            quiet = Add(normal, "-q");
        }
        else if (args.Contains("steam://uninstall", StringComparison.OrdinalIgnoreCase))
        {
            method = "Steam game";
            why = "Steam asks before it removes a game; it has no quiet mode.";
        }
        else if (args.Contains("--uninstall-app-id", StringComparison.OrdinalIgnoreCase))
        {
            method = "Browser web app";
            why = "Web apps are removed by their browser, which asks first.";
        }
        else if (full.Contains("InstallShield Installation Information", StringComparison.OrdinalIgnoreCase))
        {
            method = "InstallShield";
            why = "InstallShield setups only run quietly with an answer file recorded on this PC.";
        }
        else if (IsNsis(full))
        {
            method = "NSIS";
            quiet = Add(normal, "/S");
        }
        else
        {
            method = "Own uninstaller";
            why = $"Its uninstaller ({file}) has no known quiet mode.";
        }

        return new UninstallPlan { Method = method, Command = normal, QuietCommand = quiet, WhyNotQuiet = quiet is null ? why : null };
    }

    public static string StoreCommand(string packageFullName) =>
        $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Remove-AppxPackage -Package '{packageFullName.Replace("'", "''")}'\"";

    private static string Add(string command, string extra)
    {
        // Don't add a switch that's already there.
        var have = command.Split([' ', '"'], StringSplitOptions.RemoveEmptyEntries);
        var parts = extra.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !have.Contains(p, StringComparer.OrdinalIgnoreCase));
        var add = string.Join(' ', parts);
        return add.Length == 0 ? command : command + " " + add;
    }

    /// <summary>The uninstaller's full path, or null when it doesn't exist. Bare names are looked up in System32.</summary>
    public static string? Resolve(string exe)
    {
        if (exe.Length == 0) return null;
        try
        {
            if (Path.IsPathFullyQualified(exe))
                return File.Exists(exe) ? exe : null;
            // System32, Windows, then PATH and the app execution aliases (winget lives there).
            var dirs = new List<string> { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps"));
            foreach (var dir in dirs)
            {
                if (dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                var p = Path.Combine(dir, exe);
                if (File.Exists(p)) return p;
                if (!exe.Contains('.') && File.Exists(p + ".exe")) return p + ".exe";
            }
        }
        catch
        {
            /* bad characters: treat as missing */
        }

        return null;
    }

    /// <summary>NSIS uninstallers carry "Nullsoft" in their header and manifest.</summary>
    public static bool IsNsis(string exe)
    {
        try
        {
            using var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(f.Length, 4 * 1024 * 1024);
            var buf = new byte[len];
            f.ReadExactly(buf, 0, len);
            var span = buf.AsSpan();
            return span.IndexOf("Nullsoft"u8) >= 0 || span.IndexOf("NullsoftInst"u8) >= 0;
        }
        catch
        {
            return false;
        }
    }

    public override string ToString() => new StringBuilder(Method).Append(CanQuiet ? " (quiet)" : "").ToString();
}
