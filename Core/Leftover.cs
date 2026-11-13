namespace pxdUninstaller.Core;

public enum LeftoverKind { Uninstaller, Folder, File, Shortcut, RegistryKey, RegistryValue, Service, Task, FirewallRule }

/// <summary>How sure we are it belongs to the program.</summary>
public enum MatchLevel
{
    /// <summary>What the normal uninstall removes (selected from the start).</summary>
    Regular,

    /// <summary>Points into the program's folder or carries its exact name.</summary>
    Strong,

    /// <summary>A similar name only: check before removing.</summary>
    Possible,
}

/// <summary>One thing that belongs to a program: its uninstaller, a folder, a registry key, a service…</summary>
public sealed class Leftover
{
    public LeftoverKind Kind { get; init; }
    public MatchLevel Match { get; init; }

    /// <summary>Section in the list: see <see cref="Groups"/>.</summary>
    public string Group { get; init; } = "";

    /// <summary>What it is, as shown: a path, "HKCU\Software\X", a service name…</summary>
    public string Path { get; init; } = "";

    /// <summary>Why it's on the list, in plain words.</summary>
    public string Why { get; init; } = "";

    public long Size { get; set; }
    public bool Selected { get; set; }

    public RegPlace? Reg { get; init; }
    public string? ValueName { get; init; }
    public string? ServiceName { get; init; }
    public string? TaskName { get; init; }
    public string? RuleId { get; init; }

    /// <summary>The command, for the Uninstaller item.</summary>
    public string? Command { get; init; }

    public string Identity => $"{Kind}|{Path}|{ValueName}".ToLowerInvariant();

    public string MatchText => Match switch
    {
        MatchLevel.Regular => "Normal uninstall",
        MatchLevel.Strong => "Belongs to it",
        _ => "Similar name",
    };

    public static readonly string[] Groups =
        ["Uninstall", "Program files", "App data", "Shortcuts", "Registry", "Startup and services", "Scheduled tasks", "Firewall", "Windows traces"];

    public override string ToString() => $"[{Match}] {Group}: {Path}{(ValueName is null ? "" : " → " + ValueName)} ({Why})";
}
