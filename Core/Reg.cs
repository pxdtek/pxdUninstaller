using Microsoft.Win32;

namespace pxdUninstaller.Core;

/// <summary>A registry key's place: hive, 32/64-bit view and path. Shown as "HKLM\SOFTWARE\WOW6432Node\X" style text.</summary>
public sealed record RegPlace(RegistryHive Hive, RegistryView View, string Path)
{
    public string Text => $@"{Reg.Short(Hive)}\{Reg.Visible(Hive, View, Path)}";
    public RegPlace Child(string name) => this with { Path = Path.Length == 0 ? name : Path + "\\" + name };
    public string Name => Path[(Path.LastIndexOf('\\') + 1)..];
    public RegPlace? Parent => Path.LastIndexOf('\\') is var i and > 0 ? this with { Path = Path[..i] } : null;
}

/// <summary>
/// Registry access for the whole app. In self-test mode (<see cref="TestRoot"/>) every hive and view is a folder
/// under HKCU\&lt;TestRoot&gt;, so tests never read or change the real machine keys.
/// </summary>
public static class Reg
{
    public static string? TestRoot { get; set; }

    public static string Short(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        _ => hive.ToString(),
    };

    /// <summary>The path as regedit shows it: the 32-bit view of HKLM\SOFTWARE is HKLM\SOFTWARE\WOW6432Node.</summary>
    public static string Visible(RegistryHive hive, RegistryView view, string path)
    {
        if (hive == RegistryHive.LocalMachine && view == RegistryView.Registry32 && path.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(@"SOFTWARE\WOW6432Node", StringComparison.OrdinalIgnoreCase))
            return @"SOFTWARE\WOW6432Node\" + path[9..];
        return path;
    }

    private static (RegistryKey Base, string Path) Map(RegPlace p)
    {
        if (TestRoot is { } t)
        {
            var hive = Short(p.Hive) + (p.View == RegistryView.Registry32 ? "32" : "");
            return (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default), $@"{t}\{hive}\{p.Path}".TrimEnd('\\'));
        }

        return (RegistryKey.OpenBaseKey(p.Hive, p.View), p.Path);
    }

    public static RegistryKey? Open(RegPlace p, bool write = false)
    {
        try
        {
            var (b, path) = Map(p);
            using (b)
                return path.Length == 0 ? (write ? null : b) : b.OpenSubKey(path, write);
        }
        catch
        {
            return null;
        }
    }

    public static RegistryKey? Create(RegPlace p)
    {
        try
        {
            var (b, path) = Map(p);
            using (b)
                return b.CreateSubKey(path, true);
        }
        catch
        {
            return null;
        }
    }

    public static bool Exists(RegPlace p)
    {
        using var k = Open(p);
        return k is not null;
    }

    public static bool ValueExists(RegPlace p, string name)
    {
        using var k = Open(p);
        return k is not null && k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static string[] SubKeys(RegPlace p)
    {
        try
        {
            using var k = Open(p);
            return k?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static string? Str(RegistryKey k, string name)
    {
        try
        {
            return k.GetValue(name) switch
            {
                string s => s.Trim().Length == 0 ? null : s.Trim(),
                string[] a => string.Join(" ", a),
                int i => i.ToString(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Value text without expanding %vars% (the way it is stored).</summary>
    public static string? Raw(RegistryKey k, string name)
    {
        try
        {
            return k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch
        {
            return null;
        }
    }

    public static int? Int(RegistryKey k, string name)
    {
        try
        {
            return k.GetValue(name) switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var v) => v,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public static bool Delete(RegPlace p)
    {
        try
        {
            var (b, path) = Map(p);
            using (b)
            {
                b.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool DeleteValue(RegPlace p, string name)
    {
        try
        {
            using var k = Open(p, write: true);
            if (k is null) return true;
            k.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
