using System;
using System.Collections.Generic;
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
    const uint FILE_READ_DATA = 0x1, FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    const int FileBasicInfo = 0, FileAttributeTagInfo = 9;
    // Junctions, mount points and symlinks are "name surrogates" (they point at another name); cloud-file,
    // dedup and similar reparse points are not, and are plain folders/files for this purpose.
    const uint NameSurrogate = 0x20000000;
    public const string LinkReason = "is or sits under a junction or link";

    [StructLayout(LayoutKind.Sequential)]
    struct FILE_ATTRIBUTE_TAG_INFO { public uint FileAttributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    struct FILE_BASIC_INFO { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_ATTRIBUTE_TAG_INFO info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_BASIC_INFO info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref FILE_BASIC_INFO info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder buf, uint size, uint flags);

    // Opens path itself - never what it points at - and keeps it from being renamed or deleted while the
    // handle is open. Null when it can't be opened (e.g. someone else holds it open for delete).
    public static SafeFileHandle Pin(string path, out bool isDir, out bool isLink)
    {
        int error;
        return Pin(path, out isDir, out isLink, out error);
    }

    // The same, with the Win32 error when it returns null (32/33: open in another program), read before
    // anything else can overwrite it.
    public static SafeFileHandle Pin(string path, out bool isDir, out bool isLink, out int error)
    {
        isDir = false; isLink = false; error = 0;
        var h = CreateFileW(path, FILE_READ_DATA | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) { error = Marshal.GetLastWin32Error(); h.Dispose(); return null; }
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, out info, (uint)Marshal.SizeOf(typeof(FILE_ATTRIBUTE_TAG_INFO))))
        { error = Marshal.GetLastWin32Error(); h.Dispose(); return null; }
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

    // Deletes dir and everything in it without ever following a link: each entry is pinned while it is
    // looked at (so it can't be swapped for a link meanwhile), links are removed as links, folders are emptied
    // deepest first and then removed by name, non-recursively. Read-only entries (folders and links included)
    // are made writable first. Throws when anything can't be removed.
    public static void DeleteTree(string dir)
    {
        bool isDir, isLink;
        var h = Pin(dir, out isDir, out isLink);
        if (h == null) throw new IOException(dir + " could not be opened");
        try
        {
            if (!isDir || isLink) throw new IOException(dir + " is not a plain folder");   // never empty what a link points at
            EmptyTree(dir);
        }
        finally { h.Dispose(); }
        ClearReadOnly(dir);
        Directory.Delete(dir, false);
    }

    static void EmptyTree(string dir)
    {
        foreach (string path in Directory.GetFileSystemEntries(dir))
        {
            bool isDir, isLink;
            var h = Pin(path, out isDir, out isLink);
            if (h == null) throw new IOException(path + " could not be opened");
            try { if (isDir && !isLink) EmptyTree(path); } finally { h.Dispose(); }   // a pinned entry can't be deleted, so unpin first
            ClearReadOnly(path);
            if (isDir) Directory.Delete(path, false);   // a link (or an emptied folder): RemoveDirectory never follows it
            else File.Delete(path);   // on a link only the link goes
        }
    }

    // RemoveDirectory and DeleteFile refuse a read-only entry - a folder or a junction too - so whoever planted a
    // tree could otherwise make it undeletable. The attribute is cleared through a handle on the name itself
    // (FILE_FLAG_OPEN_REPARSE_POINT), never on what a link there points at; the other attributes are kept.
    static void ClearReadOnly(string path)
    {
        using (var h = CreateFileW(path, FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
        {
            if (h.IsInvalid) throw new IOException(path + " could not be opened (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
            uint size = (uint)Marshal.SizeOf(typeof(FILE_BASIC_INFO));
            FILE_BASIC_INFO info;
            if (!GetFileInformationByHandleEx(h, FileBasicInfo, out info, size))
                throw new IOException(path + ": attributes could not be read (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
            if ((info.FileAttributes & (uint)FileAttributes.ReadOnly) == 0) return;
            // Only the attributes that can be set; zero times leave the times unchanged, and Normal stands for "none".
            const FileAttributes settable = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive
                | FileAttributes.NotContentIndexed | FileAttributes.Temporary | FileAttributes.Offline;
            uint keep = info.FileAttributes & (uint)settable;
            var set = new FILE_BASIC_INFO();
            set.FileAttributes = keep == 0 ? (uint)FileAttributes.Normal : keep;
            if (!SetFileInformationByHandle(h, FileBasicInfo, ref set, size))
                throw new IOException(path + ": read-only could not be cleared (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
        }
    }

    // Best effort (Uninstall): deletes what is in dir except the paths in `keep` (full paths, files or folders -
    // a kept folder is left whole) and leaves anything it can't delete. Never follows a junction or symlink:
    // deleting "inside" it would delete the folder it points at.
    public static void DeleteDirContents(string dir, ICollection<string> keep)
    {
        if (!Directory.Exists(dir)) return;
        try { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return; } catch { return; }
        foreach (string f in Directory.GetFiles(dir))
        {
            try
            {
                if (keep != null && keep.Contains(Path.GetFullPath(f))) continue;
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                File.Delete(f);
            }
            catch { }
        }
        foreach (string d in Directory.GetDirectories(dir))
        {
            try
            {
                if (keep != null && keep.Contains(Path.GetFullPath(d))) continue;
                DeleteDirContents(d, keep);
                if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d, false);
            }
            catch { }
        }
    }

    // Writes a new file at path - never into what a link or hard link there points at: whatever file is at
    // the name is removed first (only that name), and the file is created with CREATE_NEW, which fails if
    // anything (even a dangling link) is at the name by then.
    public static void WriteNewFile(string path, string text)
    {
        if (File.Exists(path))
        {
            // GetAttributes reads the name itself; clear read-only only on a real file, never through a link
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        byte[] b = Encoding.UTF8.GetBytes(text);
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.Write(b, 0, b.Length);
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
