using System.Text;
using System.Text.RegularExpressions;

namespace pxdUninstaller.Core;

public static partial class Text
{
    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = -1;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return v >= 100 ? $"{v:0} {units[u]}" : v >= 10 ? $"{v:0.#} {units[u]}" : $"{v:0.##} {units[u]}";
    }

    public static string Plural(int n, string one, string? many = null) => $"{n:N0} {(n == 1 ? one : many ?? one + "s")}";

    /// <summary>Lower-case letters and digits only: "Mozilla Firefox (x64)" → "mozillafirefoxx64".</summary>
    public static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var b = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                b.Append(char.ToLowerInvariant(c));
        return b.ToString();
    }

    /// <summary>The program's name without version, bitness or language: "7-Zip 24.08 (x64)" → "7-Zip".</summary>
    public static string CleanName(string name)
    {
        var s = name;
        s = Bracketed().Replace(s, " ");
        s = VersionTail().Replace(s, " ");
        s = Bitness().Replace(s, " ");
        s = Lang().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim(' ', '-', '–', ',', '.');
        return s.Length == 0 ? name.Trim() : s;
    }

    /// <summary>The publisher without company endings: "Mozilla Corporation" → "Mozilla".</summary>
    public static string CleanPublisher(string publisher)
    {
        var s = Company().Replace(publisher, " ");
        s = Spaces().Replace(s, " ").Trim(' ', ',', '.', '-');
        return s;
    }

    /// <summary>
    /// Splits a command line into the program and its arguments. Handles quoted paths, unquoted paths with spaces
    /// ("C:\Program Files\X\uninst.exe /S") and bare names (MsiExec.exe /X{…}).
    /// </summary>
    public static (string Exe, string Args) SplitCommand(string command)
    {
        var cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        if (cmd.Length == 0) return ("", "");
        if (cmd[0] == '"')
        {
            var end = cmd.IndexOf('"', 1);
            return end < 0 ? (cmd.Trim('"'), "") : (cmd[1..end], cmd[(end + 1)..].Trim());
        }

        // Unquoted: the longest "...exe" prefix that exists, else the first one.
        string? first = null;
        foreach (Match m in ExeEnd().Matches(cmd))
        {
            var exe = cmd[..(m.Index + m.Length)];
            first ??= exe;
            if (exe.Contains('\\') && File.Exists(exe))
                return (exe, cmd[exe.Length..].Trim());
        }

        if (first is not null) return (first, cmd[first.Length..].Trim());
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    /// <summary>"C:\x\app.exe,0" → ("C:\x\app.exe", 0).</summary>
    public static (string Path, int Index) SplitIcon(string icon)
    {
        var s = Environment.ExpandEnvironmentVariables(icon.Trim().Trim('"'));
        var comma = s.LastIndexOf(',');
        if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out var idx))
            return (s[..comma].Trim().Trim('"'), idx);
        return (s, 0);
    }

    public static string? Guid(string s) => GuidRx().Match(s) is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    public static string Quote(string a) => a.Contains(' ') || a.Length == 0 ? "\"" + a + "\"" : a;

    /// <summary>The first path in a registry command value ("\"C:\x\a.exe\" --b", "C:\x\a.exe,0", "rundll32 C:\x\a.dll,Run").</summary>
    public static string? PathIn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = Environment.ExpandEnvironmentVariables(value.Trim());
        var colon = v.IndexOf(":\\", StringComparison.Ordinal);
        if (colon < 1) return null;
        var start = colon - 1;
        if (start > 0 && v[start - 1] == '"')
        {
            var end = v.IndexOf('"', start);
            return end < 0 ? v[start..] : v[start..end];
        }

        var rest = v[start..];
        var m = ExtEnd().Match(rest);
        if (m.Success) return rest[..(m.Index + m.Length)];
        var cut = rest.Length;
        foreach (var stop in new[] { " /", " -", ",", "\"" })
            if (rest.IndexOf(stop, StringComparison.Ordinal) is var i and > 0 && i < cut)
                cut = i;
        return rest[..cut].TrimEnd();
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")] private static partial Regex Bracketed();
    [GeneratedRegex(@"\s+v?\d+(\.\d+)+[a-z]?(\s|$)|\s+\d{4}(\s|$)", RegexOptions.IgnoreCase)] private static partial Regex VersionTail();
    [GeneratedRegex(@"\b(x64|x86|amd64|arm64|64-bit|32-bit|64 bit|32 bit|win64|win32)\b", RegexOptions.IgnoreCase)] private static partial Regex Bitness();
    [GeneratedRegex(@"\s-\s[a-z]{2}(-[a-z]{2})?$", RegexOptions.IgnoreCase)] private static partial Regex Lang();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"[,\.]?\s*\b(inc|incorporated|llc|ltd|limited|corp|corporation|co|company|gmbh|ag|s\.?a|b\.?v|pty|plc|srl|sas|kg)\b\.?", RegexOptions.IgnoreCase)] private static partial Regex Company();
    [GeneratedRegex(@"\.exe(?=\s|$)", RegexOptions.IgnoreCase)] private static partial Regex ExeEnd();
    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")] private static partial Regex GuidRx();
    [GeneratedRegex(@"\.(exe|dll|ocx|cpl|com|bat|cmd|ps1|lnk|sys|scr|msc|vbs|js|ax)(?=[\s,""]|$)", RegexOptions.IgnoreCase)] private static partial Regex ExtEnd();
}
