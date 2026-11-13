using System.Runtime.InteropServices;
using System.Text;

namespace pxdUninstaller.Core;

internal static partial class Native
{
    // ---------- Strings ----------

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int outBufSize, IntPtr reserved);

    /// <summary>Resolves "@{Package?ms-resource://…}" and "@file.dll,-123" strings; null when it can't.</summary>
    public static string? IndirectString(string source)
    {
        try
        {
            var sb = new StringBuilder(1024);
            return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Files ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCT64
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT64 op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_NOCONFIRMMKDIR = 0x200;

    /// <summary>Moves a file or folder to the Recycle Bin with no windows. True when it's gone.</summary>
    public static bool Recycle(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT64
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR,
            };
            var r = SHFileOperation(ref op);
            return r == 0 && op.fAnyOperationsAborted == 0 && !File.Exists(path) && !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string? newName, int flags);

    /// <summary>Asks Windows to delete the file or (empty) folder at the next restart. Needs administrator rights.</summary>
    public static bool DeleteAtRestart(string path)
    {
        try
        {
            return MoveFileEx(path, null, 0x4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
        }
        catch
        {
            return false;
        }
    }

    // ---------- Shortcuts ----------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <summary>A .lnk file's target and arguments (raw, never resolved, so a missing target doesn't trigger a search).</summary>
    public static (string Target, string Args)? ReadShortcut(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sl = (IShellLinkW)link;
            var target = new StringBuilder(1024);
            sl.GetPath(target, target.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            var args = new StringBuilder(2048);
            sl.GetArguments(args, args.Capacity);
            return (Environment.ExpandEnvironmentVariables(target.ToString()), args.ToString());
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>Writes a .lnk (self-test only).</summary>
    public static void WriteShortcut(string lnk, string target)
    {
        var link = new ShellLink();
        try
        {
            ((IShellLinkW)link).SetPath(target);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    // ---------- Console ----------

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(int processId);
}
