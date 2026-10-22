using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Link checks for the install-folder lock (SecureInstallDir and the elevated --secure-folder helper). The ACL
// APIs find a folder by name and follow junctions and symlinks, so a folder reached through a link would have
// the link's target re-permissioned - another account's folder, say. These helpers refuse such paths and hold
// each entry open (no FILE_SHARE_DELETE) while it is re-permissioned, so nobody can rename it or swap a link in
// its place in between. WPF-free; tested by scripts\test_folder_pin.ps1.
public static class FolderPin
{
    // Read-data access is what makes the pin hold: Windows only enforces share modes against a handle that has
    // data (or delete) access, so an attributes-only handle would not stop a rename.
    const uint FILE_READ_DATA = 0x1, FILE_READ_ATTRIBUTES = 0x80;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    const int FileAttributeTagInfo = 9;
    // Junctions, mount points and symlinks are "name surrogates" (they point at another name); cloud-file,
    // dedup and similar reparse points are not, and are plain folders/files for this purpose.
    const uint NameSurrogate = 0x20000000;
    public const string LinkReason = "is or sits under a junction or link";

    [StructLayout(LayoutKind.Sequential)]
    struct FILE_ATTRIBUTE_TAG_INFO { public uint FileAttributes; public uint ReparseTag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_ATTRIBUTE_TAG_INFO info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder buf, uint size, uint flags);

    // Opens path itself - never what it points at - and keeps it from being renamed or deleted while the
    // handle is open. Null when it can't be opened (e.g. someone else holds it open for delete).
    public static SafeFileHandle Pin(string path, out bool isDir, out bool isLink)
    {
        isDir = false; isLink = false;
        var h = CreateFileW(path, FILE_READ_DATA | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, out info, (uint)Marshal.SizeOf(typeof(FILE_ATTRIBUTE_TAG_INFO))))
        { h.Dispose(); return null; }
        isDir = (info.FileAttributes & (uint)FileAttributes.Directory) != 0;
        isLink = (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 && (info.ReparseTag & NameSurrogate) != 0;
        return h;
    }

    // Pins the folder to be locked. Null (and why) unless dir is a real folder whose own path - every folder
    // above it included - leads to it without passing through a link.
    public static SafeFileHandle PinFolder(string dir, out string why)
    {
        string full;
        try { full = Path.GetFullPath(dir).TrimEnd('\\'); } catch { why = "is not a valid path"; return null; }
        bool isDir, isLink;
        var h = Pin(full, out isDir, out isLink);
        if (h == null) { why = "could not be opened (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")"; return null; }
        if (!isDir || isLink) { h.Dispose(); why = isLink ? LinkReason : "is not a folder"; return null; }
        string final = FinalPath(h);
        if (final == null || !string.Equals(final.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
        { h.Dispose(); why = "is reached through a link, a mapped or subst drive, or a short (8.3) name - use its full real path"; return null; }
        why = null;
        return h;
    }

    // True when dir or an existing folder above it is a junction or symlink, or that can't be told.
    // A cheap check before creating a folder; PinFolder is the one that counts.
    public static bool UnderLink(string dir)
    {
        string p;
        try { p = Path.GetFullPath(dir); } catch { return true; }
        // Path.GetDirectoryName returns null at the drive root ("C:\"), which ends the walk.
        for (; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            try
            {
                if (!Directory.Exists(p) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0) continue;
                bool isDir, isLink;
                var h = Pin(p, out isDir, out isLink);
                if (h == null) return true;
                h.Dispose();
                if (isLink) return true;
            }
            catch { return true; }
        }
        return false;
    }

    // The path the handle really has, "\\?\" prefix removed; null when it can't be read.
    static string FinalPath(SafeFileHandle h)
    {
        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0) return null;
        if (n >= sb.Capacity) { sb = new StringBuilder((int)n + 1); n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0); if (n == 0 || n >= sb.Capacity) return null; }
        string s = sb.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s.Substring(8);
        if (s.StartsWith(@"\\?\")) return s.Substring(4);
        return s;
    }
}
