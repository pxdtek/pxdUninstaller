using System.Net;
using System.Text;

namespace pxdUninstaller.Core;

/// <summary>The program list as CSV (spreadsheets) or a self-contained HTML page (tickets, records).</summary>
public static class Export
{
    public static string For(string file, IEnumerable<InstalledApp> apps) =>
        file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? Csv(apps) : Html(apps, Environment.MachineName);

    public static string Csv(IEnumerable<InstalledApp> apps)
    {
        static string C(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var b = new StringBuilder("Name,Publisher,Version,Installed,Size (MB),Kind,For,Folder,Installer,Quiet uninstall,Uninstall command\r\n");
        foreach (var a in apps)
            b.Append(string.Join(",", C(a.Name), C(a.Publisher), C(a.Version), C(a.InstallDate?.ToString("yyyy-MM-dd")),
                a.SizeBytes > 0 ? (a.SizeBytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "",
                C(a.Kind == AppKind.Store ? "Store app" : "Program"), C(a.JustForMe ? "This user" : "All users"), C(a.InstallLocation), C(a.Plan.Method),
                C(a.Orphaned ? "broken" : a.Plan.CanQuiet ? "yes" : "no"), C(a.Plan.Command))).Append("\r\n");
        return b.ToString();
    }

    public static string Html(IEnumerable<InstalledApp> apps, string computer)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var list = apps.ToList();
        var b = new StringBuilder();
        b.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Installed programs - ").Append(H(computer)).Append("</title><style>")
            .Append("body{font:14px Segoe UI,sans-serif;background:#0e1015;color:#e8eaf0;margin:24px}h1{font-size:20px}p{color:#8a92a6}")
            .Append("table{border-collapse:collapse;width:100%}th,td{padding:6px 10px;border-bottom:1px solid #272c39;text-align:left;vertical-align:top}")
            .Append("th{color:#19f58c;font-size:12px;text-transform:uppercase;letter-spacing:.06em}td.n{text-align:right;white-space:nowrap}.no{color:#f5b14c}.bad{color:#f2666a}")
            .Append("</style></head><body><h1>Installed programs on ").Append(H(computer)).Append("</h1><p>")
            .Append(list.Count).Append(" programs, listed by pxd Uninstaller on ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(".</p>")
            .Append("<table><tr><th>Name</th><th>Publisher</th><th>Version</th><th>Installed</th><th>Size</th><th>Installer</th><th>Quiet</th></tr>");
        foreach (var a in list)
        {
            var quiet = a.Orphaned ? "<span class=bad>broken</span>" : a.Plan.CanQuiet ? "yes" : "<span class=no>no</span>";
            b.Append("<tr><td>").Append(H(a.Name)).Append("</td><td>").Append(H(a.Publisher)).Append("</td><td>").Append(H(a.Version))
                .Append("</td><td>").Append(a.InstallDate?.ToString("yyyy-MM-dd")).Append("</td><td class=n>").Append(H(a.SizeText))
                .Append("</td><td>").Append(H(a.Plan.Method)).Append("</td><td>").Append(quiet).Append("</td></tr>");
        }

        return b.Append("</table></body></html>").ToString();
    }
}

/// <summary>A Windows restore point before uninstalling (needs administrator rights and System Protection on drive C:).</summary>
public static class RestorePoint
{
    /// <summary>Null when it was made; otherwise why not, in plain words. Replaced in the self-test.</summary>
    public static Func<string, string?> Create { get; set; } = description =>
    {
        if (!Cli.IsElevated) return "needs administrator rights";
        var d = description.Replace("'", "''");
        if (d.Length > 60) d = d[..60];
        // Windows makes at most one restore point a day unless this is set to 0 for the call; put it back after.
        var script = "$k='HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore';" +
                     "$old=(Get-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency;" +
                     "Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord;" +
                     $"try {{ Checkpoint-Computer -Description '{d}' -RestorePointType APPLICATION_UNINSTALL -ErrorAction Stop; 'OK' }} catch {{ $_.Exception.Message }} finally {{" +
                     " if ($null -eq $old) { Remove-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue } else { Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value $old } }";
        var (code, output) = Runner.Tool("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), 180_000);
        var text = output.Trim();
        if (code == 0 && text.EndsWith("OK")) return null;
        return text.Contains("disabled", StringComparison.OrdinalIgnoreCase) || text.Contains("0x80070422")
            ? "System Protection is off for this PC"
            : text.Length > 0 ? text.Split('\n').Last().Trim() : "Windows didn't make one";
    };
}

/// <summary>Forced uninstall: a program that isn't listed anymore, described by its name and (optionally) its folder.</summary>
public static class Forced
{
    public static InstalledApp App(string name, string? folder, string? publisher = null) => new()
    {
        Id = "forced:" + name,
        Name = name.Trim(),
        Publisher = publisher?.Trim() ?? "",
        InstallLocation = (folder ?? "").Trim().Trim('"').TrimEnd('\\'),
        Kind = AppKind.Program,
        Plan = UninstallPlan.None,
    };

    /// <summary>Why the name or folder can't be used (too short, a Windows folder…), or null when fine.</summary>
    public static string? Problem(string name, string? folder)
    {
        if (Text.Norm(name).Length < 3) return "Type at least 3 letters of the program's name.";
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var f = folder.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(f)) return "The folder needs a full path, like C:\\Program Files\\Name.";
            if (Places.Current.IsProtected(f)) return "That's a Windows or system folder; it's never removed.";
        }

        return null;
    }
}
