using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace pxdUninstaller.Core;

/// <summary>
/// Finds everything installed: the Apps &amp; features entries (machine 64-bit, machine 32-bit and this user's) the
/// way Windows lists them, plus this user's Store apps. Windows updates, hidden system components and frameworks are
/// left out.
/// </summary>
public static partial class AppScanner
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string StorePath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static readonly (string Prefix, RegPlace Root, bool JustForMe)[] Roots =
    [
        ("HKLM64", new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry64, UninstallPath), false),
        ("HKLM32", new RegPlace(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallPath), false),
        ("HKCU", new RegPlace(RegistryHive.CurrentUser, RegistryView.Registry64, UninstallPath), true),
    ];

    public static List<InstalledApp> Scan(bool includeStore = true)
    {
        var list = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (prefix, root, mine) in Roots)
        {
            foreach (var name in Reg.SubKeys(root))
            {
                var app = Read(prefix, root.Child(name), mine);
                if (app is null) continue;
                // The same program written to both views (or both hives) shows once.
                if (!seen.Add(Text.Norm(app.Name) + "|" + app.Version + "|" + Text.Norm(app.Publisher))) continue;
                list.Add(app);
            }
        }

        if (includeStore && Reg.TestRoot is null)
            list.AddRange(StoreApps());
        foreach (var a in list) a.Plan = UninstallPlan.For(a);
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>One Apps &amp; features entry, or null when Windows wouldn't list it either.</summary>
    public static InstalledApp? Read(string prefix, RegPlace key, bool justForMe)
    {
        using var k = Reg.Open(key);
        if (k is null) return null;
        var name = Reg.Str(k, "DisplayName");
        if (name is null) return null;
        if (Reg.Int(k, "SystemComponent") == 1) return null;
        if (Reg.Str(k, "ParentKeyName") is not null) return null;   // an update of another entry
        var release = Reg.Str(k, "ReleaseType") ?? "";
        if (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
            || release.Contains("Service Pack", StringComparison.OrdinalIgnoreCase))
            return null;
        if (UpdateName().IsMatch(name) || KbKey().IsMatch(key.Name)) return null;

        var uninstall = Reg.Str(k, "UninstallString") ?? "";
        var quiet = Reg.Str(k, "QuietUninstallString") ?? "";
        var msi = Reg.Int(k, "WindowsInstaller") == 1;
        var code = Text.Guid(key.Name) is { } g && key.Name.Length == 38 ? g : null;
        if (uninstall.Length == 0 && quiet.Length == 0 && !(msi && code is not null)) return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in k.GetValueNames())
            if (Reg.Str(k, v) is { } s)
                values[v] = s;

        var location = Reg.Str(k, "InstallLocation") ?? (values.TryGetValue("Inno Setup: App Path", out var inno) ? inno : "");
        location = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"')).TrimEnd('\\');
        long size = Reg.Int(k, "EstimatedSize") is { } kb and > 0 ? kb * 1024L : 0;

        return new InstalledApp
        {
            Id = prefix + ":" + key.Name,
            Name = name,
            Publisher = Reg.Str(k, "Publisher") ?? "",
            Version = Reg.Str(k, "DisplayVersion") ?? "",
            Kind = AppKind.Program,
            JustForMe = justForMe,
            InstallLocation = location,
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            DisplayIcon = Reg.Str(k, "DisplayIcon") ?? "",
            ProductCode = msi ? code : null,
            WindowsInstaller = msi,
            InstallDate = Date(Reg.Str(k, "InstallDate")),
            SizeBytes = size,
            HelpLink = Reg.Str(k, "URLInfoAbout") ?? Reg.Str(k, "HelpLink"),
            Key = key,
            Values = values,
        };
    }

    private static DateTime? Date(string? s)
    {
        if (s is null) return null;
        string[] formats = ["yyyyMMdd", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "d.M.yyyy", "dd.MM.yyyy", "yyyy/MM/dd"];
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year > 1990 && d <= DateTime.Now.AddDays(2) ? d : null;
    }

    /// <summary>Store apps this user has, newest version of each, without frameworks, resource packs or system apps.</summary>
    public static List<InstalledApp> StoreApps()
    {
        var root = new RegPlace(RegistryHive.CurrentUser, RegistryView.Default, StorePath);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var best = new Dictionary<string, (Version V, InstalledApp App)>(StringComparer.OrdinalIgnoreCase);
        foreach (var full in Reg.SubKeys(root))
        {
            try
            {
                var place = root.Child(full);
                using var k = Reg.Open(place);
                if (k is null || k.SubKeyCount == 0) continue;   // no apps inside: framework or resource pack
                var folder = Reg.Str(k, "PackageRootFolder");
                if (folder is null || Places.Within(folder, windows) || !Directory.Exists(folder)) continue;
                var parts = full.Split('_');
                if (parts.Length != 5) continue;
                var pkgName = parts[0];
                if (SystemPackage().IsMatch(pkgName)) continue;
                var family = parts[0] + "_" + parts[4];
                var version = System.Version.TryParse(parts[1], out var pv) ? pv : new Version(0, 0);

                var manifest = Manifest(folder);
                if (manifest.Framework || manifest.Resource) continue;
                var display = Resolve(full, pkgName, Reg.Str(k, "DisplayName") ?? manifest.DisplayName) ?? pkgName;
                if (display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) display = pkgName;
                var publisher = Resolve(full, pkgName, manifest.Publisher) ?? "";
                if (publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) publisher = "";

                var app = new InstalledApp
                {
                    Id = "store:" + full,
                    Name = display,
                    Publisher = publisher,
                    Version = parts[1],
                    Kind = AppKind.Store,
                    JustForMe = true,
                    InstallLocation = folder,
                    PackageFullName = full,
                    PackageFamilyName = family,
                    StoreLogo = manifest.Logo is { } logo ? Logo(folder, logo) : null,
                    InstallDate = SafeCreated(folder),
                };
                if (!best.TryGetValue(family, out var have) || version > have.V)
                    best[family] = (version, app);
            }
            catch
            {
                /* one broken package never stops the list */
            }
        }

        return best.Values.Select(b => b.App).ToList();
    }

    private static DateTime? SafeCreated(string folder)
    {
        try
        {
            return Directory.GetCreationTime(folder);
        }
        catch
        {
            return null;
        }
    }

    private static string? Resolve(string fullName, string pkgName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value;
        var res = value["ms-resource:".Length..];
        string[] tries = res.StartsWith("//")
            ? [$"@{{{fullName}?{value}}}"]
            : res.Contains('/')
                ? [$"@{{{fullName}?ms-resource://{pkgName}/{res.TrimStart('/')}}}", $"@{{{fullName}?ms-resource://{pkgName}/Resources/{res.TrimStart('/')}}}"]
                : [$"@{{{fullName}?ms-resource://{pkgName}/Resources/{res}}}", $"@{{{fullName}?ms-resource://{pkgName}/{res}}}"];
        foreach (var t in tries)
            if (Native.IndirectString(t) is { Length: > 0 } s && !s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                return s;
        return value;
    }

    private record struct ManifestInfo(bool Framework, bool Resource, string? DisplayName, string? Publisher, string? Logo);

    private static ManifestInfo Manifest(string folder)
    {
        try
        {
            var file = Path.Combine(folder, "AppxManifest.xml");
            if (!File.Exists(file)) return default;
            var doc = XDocument.Load(file);
            var props = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            string? P(string name) => props?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            return new ManifestInfo(
                string.Equals(P("Framework"), "true", StringComparison.OrdinalIgnoreCase),
                string.Equals(P("ResourcePackage"), "true", StringComparison.OrdinalIgnoreCase),
                P("DisplayName"), P("PublisherDisplayName"), P("Logo"));
        }
        catch
        {
            return default;
        }
    }

    /// <summary>The store logo file: the manifest names "Assets\StoreLogo.png", the package holds StoreLogo.scale-100.png etc.</summary>
    private static string? Logo(string folder, string logo)
    {
        try
        {
            var full = Path.Combine(folder, logo);
            if (File.Exists(full)) return full;
            var dir = Path.GetDirectoryName(full)!;
            if (!Directory.Exists(dir)) return null;
            var stem = Path.GetFileNameWithoutExtension(full);
            var ext = Path.GetExtension(full);
            var files = Directory.GetFiles(dir, stem + ".*" + ext);
            string[] prefer = [".scale-200", ".scale-150", ".scale-125", ".scale-100", ".targetsize-48", ".targetsize-64"];
            foreach (var p in prefer)
                if (files.FirstOrDefault(f => Path.GetFileName(f).Contains(p, StringComparison.OrdinalIgnoreCase) && !f.Contains("contrast", StringComparison.OrdinalIgnoreCase)) is { } hit)
                    return hit;
            return files.FirstOrDefault(f => !f.Contains("contrast", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Is the program still installed (its Apps &amp; features entry or Store package still there)?</summary>
    public static bool StillInstalled(InstalledApp app)
    {
        if (app.Kind == AppKind.Store)
            return app.PackageFullName is { } pkg && Reg.Exists(new RegPlace(RegistryHive.CurrentUser, RegistryView.Default, StorePath).Child(pkg))
                   && Directory.Exists(app.InstallLocation);
        return app.Key is { } key && Reg.Exists(key);
    }

    [GeneratedRegex(@"^(Security Update|Update|Hotfix) for |^Service Pack \d", RegexOptions.IgnoreCase)] private static partial Regex UpdateName();
    [GeneratedRegex(@"^KB\d{6,}", RegexOptions.IgnoreCase)] private static partial Regex KbKey();
    [GeneratedRegex(@"^(Microsoft\.(UI\.Xaml|VCLibs|NET\.|DirectX|Services\.Store|WindowsAppRuntime|WinAppRuntime|LanguageExperiencePack|AAD\.BrokerPlugin|AccountsControl|AsyncTextService|BioEnrollment|CredDialogHost|ECApp|LockApp|Win32WebViewHost|XboxGameCallableUI|SecHealthUI|WebpImageExtension|HEIFImageExtension|VP9VideoExtensions|WebMediaExtensions|RawImageExtension|AV1VideoExtension|HEVCVideoExtension|MPEG2VideoExtension|ApplicationCompatibilityEnhancements|OneDriveSync|StorePurchaseApp|DesktopAppInstaller|WindowsStore)\b)|^(windows\.|InputApp|NcsiUwpApp|c5e2524a-|E2A4F912-|F46D4000-|1527c705-)", RegexOptions.IgnoreCase)]
    private static partial Regex SystemPackage();
}
