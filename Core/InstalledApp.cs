namespace pxdUninstaller.Core;

public enum AppKind { Program, Store }

/// <summary>One installed program (an Apps &amp; features entry) or Store app.</summary>
public sealed class InstalledApp
{
    /// <summary>Stable id: "HKLM64:KeyName", "HKLM32:KeyName", "HKCU:KeyName" or "store:PackageFullName".</summary>
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public AppKind Kind { get; init; }

    /// <summary>Installed for this user only (HKCU entry or Store app).</summary>
    public bool JustForMe { get; init; }

    public string InstallLocation { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string QuietUninstallString { get; set; } = "";
    public string DisplayIcon { get; set; } = "";
    public string? ProductCode { get; set; }
    public bool WindowsInstaller { get; set; }
    public DateTime? InstallDate { get; set; }
    public long SizeBytes { get; set; }
    public string? HelpLink { get; set; }

    /// <summary>The Apps &amp; features key (null for Store apps).</summary>
    public RegPlace? Key { get; init; }

    /// <summary>Extra values of the key that tell the installer apart (Inno, WiX bundle…).</summary>
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? StoreLogo { get; set; }

    /// <summary>How it comes off: filled in by <see cref="UninstallPlan.For"/>.</summary>
    public UninstallPlan Plan { get; set; } = UninstallPlan.None;

    public bool Orphaned => Plan.Missing;

    public string SizeText => SizeBytes <= 0 ? "" : Text.Size(SizeBytes);

    public override string ToString() => $"{Name} {Version}".Trim();
}
