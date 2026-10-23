using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Link checks for the install-folder lock (SecureInstallDir and the elevated --secure-folder helper). The ACL
// APIs find a folder by name and follow junctions and symlinks, so a folder reached through a link would have
// the link's target re-permissioned - another account's folder, say. These helpers refuse such paths and hold
// each entry open (no FILE_SHARE_DELETE) while it is re-permissioned, so nobody can rename it or swap a link in
// its place in between. Permissions are read and set through those handles, one object at a time: setting them
// by name (or with SetSecurityInfo) also rewrites the inherited permissions of everything inside, hard links to
// files outside the folder included. WPF-free; tested by scripts\test_folder_pin.ps1.
public static class FolderPin
{
    // Read-data access is what makes the pin hold: Windows only enforces share modes against a handle that has
    // data (or delete) access, so an attributes-only handle would not stop a rename.
    const uint FILE_READ_DATA = 0x1, FILE_READ_ATTRIBUTES = 0x80;
    const uint DELETE = 0x10000, READ_CONTROL = 0x20000, WRITE_DAC = 0x40000, WRITE_OWNER = 0x80000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    const uint CREATE_NEW = 1, OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    const int FileRenameInfo = 3, FileDispositionInfo = 4, FileAttributeTagInfo = 9, FileDispositionInfoEx = 21, FileRenameInfoEx = 22;
    // Replace what is at the new name in one step, even if it is open elsewhere (shared for delete) or read-only.
    const uint FILE_RENAME_REPLACE_IF_EXISTS = 0x1, FILE_RENAME_POSIX_SEMANTICS = 0x2, FILE_RENAME_IGNORE_READONLY_ATTRIBUTE = 0x40;
    const uint OWNER_SECURITY_INFORMATION = 1, DACL_SECURITY_INFORMATION = 4;
    // Delete the name now even if the file is open elsewhere, and even if it is read-only - without clearing
    // the read-only attribute, which a hard link shares with its other names.
    const uint FILE_DISPOSITION_DELETE = 0x1, FILE_DISPOSITION_POSIX_SEMANTICS = 0x2, FILE_DISPOSITION_IGNORE_READONLY = 0x10;
    // Junctions, mount points and symlinks are "name surrogates" (they point at another name); cloud-file,
    // dedup and similar reparse points are not, and are plain folders/files for this purpose.
    const uint NameSurrogate = 0x20000000;
    public const string LinkReason = "is or sits under a junction or link";

    [StructLayout(LayoutKind.Sequential)]
    struct FILE_ATTRIBUTE_TAG_INFO { public uint FileAttributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int Length; public IntPtr SecurityDescriptor; public int InheritHandle; }
    // FILETIMEs as uint pairs: a long would be 8-byte aligned and shift every field after FileAttributes.
    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFileWithSecurity(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_ATTRIBUTE_TAG_INFO info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder buf, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref uint info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref byte info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, byte[] info, uint size);
    // The kernel's own get/set (NtQuerySecurityObject / NtSetSecurityObject): they act on this one object only,
    // unlike SetSecurityInfo and SetNamedSecurityInfo, which also propagate to what is inside a folder.
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetKernelObjectSecurity(SafeFileHandle h, uint info, byte[] sd, uint length, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetKernelObjectSecurity(SafeFileHandle h, uint info, byte[] sd);

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
        return Pin(path, 0, out isDir, out isLink, out error);
    }

    // The same, with the handle also able to read and change the entry's permissions (GetSecurity/SetSecurity).
    // Rights the caller doesn't have (access denied) are left out rather than failing the pin: SetSecurity then
    // fails on that entry instead, and an entry that is only to be deleted needs none of them.
    public static SafeFileHandle PinForSecurity(string path, out bool isDir, out bool isLink, out int error)
    {
        isDir = false; isLink = false; error = 0;
        foreach (uint extra in new[] { READ_CONTROL | WRITE_DAC | WRITE_OWNER, READ_CONTROL | WRITE_DAC, READ_CONTROL })
        {
            var h = Pin(path, extra, out isDir, out isLink, out error);
            if (h != null || error != 5) return h;   // 5 = access denied
        }
        return null;
    }

    static SafeFileHandle Pin(string path, uint extra, out bool isDir, out bool isLink, out int error)
    {
        isDir = false; isLink = false; error = 0;
        var h = CreateFileW(path, FILE_READ_DATA | FILE_READ_ATTRIBUTES | extra, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) { error = Marshal.GetLastWin32Error(); h.Dispose(); return null; }
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, out info, (uint)Marshal.SizeOf(typeof(FILE_ATTRIBUTE_TAG_INFO))))
        { error = Marshal.GetLastWin32Error(); h.Dispose(); return null; }
        isDir = (info.FileAttributes & (uint)FileAttributes.Directory) != 0;
        isLink = (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 && (info.ReparseTag & NameSurrogate) != 0;
        return h;
    }

    // Pins the folder to be locked (PinForSecurity). Null (and why) unless dir is a real folder whose own path -
    // every folder above it included - leads to it without passing through a link.
    public static SafeFileHandle PinFolder(string dir, out string why)
    {
        string full;
        try { full = Path.GetFullPath(dir).TrimEnd('\\'); } catch { why = "is not a valid path"; return null; }
        bool isDir, isLink; int error;
        var h = PinForSecurity(full, out isDir, out isLink, out error);
        if (h == null) { why = "could not be opened (" + new Win32Exception(error).Message + ")"; return null; }
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

    // Deletes dir and everything in it without ever following a link: each folder is pinned while it is emptied,
    // deepest first, and every entry is checked to really be in its folder (InFolder) before it is looked into or
    // deleted. Its owner may still have rights inside it and can turn a folder it empties into a junction, so a
    // path's middle can't be trusted - only the pinned folders. Links are removed as links, and read-only
    // entries (a planted tree may be made of them) go too (DeleteName). Throws when anything can't be removed.
    public static void DeleteTree(string dir)
    {
        string full = Path.GetFullPath(dir).TrimEnd('\\');
        string parent = Path.GetDirectoryName(full);
        bool isDir, isLink;
        using (var ph = parent == null ? null : Pin(parent, out isDir, out isLink))
        {
            if (ph == null) throw new IOException(dir + ": its folder could not be opened");
            var h = Pin(full, out isDir, out isLink);
            if (h == null) throw new IOException(dir + " could not be opened");
            try
            {
                if (!isDir || isLink) throw new IOException(dir + " is not a plain folder");   // never empty what a link points at
                InFolder(h, ph, full);
                EmptyTree(full, h);
            }
            finally { h.Dispose(); }
            DeleteName(full, ph);
        }
    }

    // dirHandle stays pinned while dir's entries go: no other account can rename dir or delete it, and every
    // folder above it holds a pinned folder, so only dir itself could become a junction meanwhile - InFolder
    // catches that on every entry.
    static void EmptyTree(string dir, SafeFileHandle dirHandle)
    {
        foreach (string path in Directory.GetFileSystemEntries(dir))
        {
            bool isDir, isLink;
            var h = Pin(path, out isDir, out isLink);
            if (h == null) throw new IOException(path + " could not be opened");
            try
            {
                InFolder(h, dirHandle, path);
                if (isDir && !isLink) EmptyTree(path, h);
            }
            finally { h.Dispose(); }   // a pinned entry can't be deleted, so unpin first
            DeleteName(path, dirHandle);   // a file, link or emptied folder: only that name, read-only or not
        }
    }

    // Throws unless h is really open on path's name inside the folder `folder` is open on: when that folder was
    // turned into a junction after it was opened, a path through it leads somewhere else, and the real paths differ.
    static void InFolder(SafeFileHandle h, SafeFileHandle folder, string path)
    {
        string f = FinalPath(folder), real = FinalPath(h);
        string want = f == null ? null : f.TrimEnd('\\') + "\\" + Path.GetFileName(path.TrimEnd('\\'));
        if (real == null || want == null || !string.Equals(real.TrimEnd('\\'), want, StringComparison.OrdinalIgnoreCase))
            throw new IOException(path + " is not in its folder any more (a link was put in its way)");
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
        using (CreateNewFile(path, text)) { }
    }

    // The same, but returns the new file still open, shared with no one: until it is closed nobody else can
    // rename or delete it, or open it to read or write its content. (An open for its permissions alone is not
    // stopped by sharing, only by the permissions - hence `dacl`.) SetSecurity can change its permissions and
    // RenameOver its name through its SafeFileHandle.
    public static FileStream CreateNewFile(string path, string text)
    {
        return CreateNewFile(path, text, null);
    }

    // The same, created with the permissions dacl (protected: nothing inherited from the folder) when it isn't
    // null, so there is no moment in which the folder's permissions give anyone rights on it.
    public static FileStream CreateNewFile(string path, string text, RawAcl dacl)
    {
        // only that name goes, read-only or not; a hard link's other name keeps its attributes too
        if (File.Exists(path)) DeleteName(path);
        // a link put at the name meanwhile, even a dangling one, is a name already taken: never followed
        const uint access = GENERIC_WRITE | DELETE | READ_CONTROL | WRITE_DAC;
        SafeFileHandle h;
        if (dacl == null) h = CreateFileW(path, access, 0, IntPtr.Zero, CREATE_NEW, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        else
        {
            var sd = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclProtected, null, null, null, dacl);
            var b = new byte[sd.BinaryLength];
            sd.GetBinaryForm(b, 0);
            var pinned = GCHandle.Alloc(b, GCHandleType.Pinned);
            try
            {
                var sa = new SECURITY_ATTRIBUTES { Length = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), SecurityDescriptor = pinned.AddrOfPinnedObject() };
                h = CreateFileWithSecurity(path, access, 0, ref sa, CREATE_NEW, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            }
            finally { pinned.Free(); }
        }
        if (h.IsInvalid) { int error = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(error); }
        FileStream fs;
        try { fs = new FileStream(h, FileAccess.Write); } catch { h.Dispose(); throw; }
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            fs.Write(b, 0, b.Length);
            fs.Flush();
        }
        catch { fs.Dispose(); throw; }
        return fs;
    }

    // Renames the file h is open on (it needs DELETE access: CreateNewFile) to newPath, in one step replacing
    // the file or link at that name - only that name, read-only or not. newPath never stops existing, and a handle
    // someone holds on the file it replaces keeps reaching only that file, never this one. Throws, changing
    // nothing, when the old one is open somewhere without delete sharing, or newPath is a folder.
    public static void RenameOver(SafeFileHandle h, string newPath)
    {
        // FILE_RENAME_INFO: Flags (ReplaceIfExists for the older class), pointer-aligned RootDirectory (none),
        // FileNameLength in bytes, then the name
        byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(newPath));
        int lengthAt = 2 * IntPtr.Size, nameAt = lengthAt + 4;
        var info = new byte[nameAt + name.Length + 2];
        BitConverter.GetBytes(FILE_RENAME_REPLACE_IF_EXISTS | FILE_RENAME_POSIX_SEMANTICS | FILE_RENAME_IGNORE_READONLY_ATTRIBUTE).CopyTo(info, 0);
        BitConverter.GetBytes(name.Length).CopyTo(info, lengthAt);
        name.CopyTo(info, nameAt);
        if (SetFileInformationByHandle(h, FileRenameInfoEx, info, (uint)info.Length)) return;
        // Windows before 10 1809: the older rename, which also replaces in one step, but not a read-only name
        BitConverter.GetBytes(1u).CopyTo(info, 0);
        if (!SetFileInformationByHandle(h, FileRenameInfo, info, (uint)info.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // How many names the file has: above 1, another name (a hard link) may be outside the folder being locked.
    public static int LinkCount(SafeFileHandle h)
    {
        BY_HANDLE_FILE_INFORMATION info;
        if (!GetFileInformationByHandle(h, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (int)info.NumberOfLinks;
    }

    // The owner and permissions of what h is open on (it needs READ_CONTROL: PinForSecurity).
    public static RawSecurityDescriptor GetSecurity(SafeFileHandle h)
    {
        uint info = OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, needed;
        GetKernelObjectSecurity(h, info, null, 0, out needed);
        if (needed == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var sd = new byte[needed];
        if (!GetKernelObjectSecurity(h, info, sd, needed, out needed)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new RawSecurityDescriptor(sd, 0);
    }

    // Sets the permissions (and the owner, unless null) of what h is open on, and of nothing else: what is
    // inside a folder keeps its own until it is set too. protect = inherit nothing from the folder above.
    public static void SetSecurity(SafeFileHandle h, SecurityIdentifier owner, RawAcl dacl, bool protect)
    {
        var flags = ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative | (protect ? ControlFlags.DiscretionaryAclProtected : ControlFlags.None);
        var sd = new RawSecurityDescriptor(flags, owner, null, null, dacl);
        var b = new byte[sd.BinaryLength];
        sd.GetBinaryForm(b, 0);
        uint info = DACL_SECURITY_INFORMATION | (owner != null ? OWNER_SECURITY_INFORMATION : 0);
        if (!SetKernelObjectSecurity(h, info, b)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // The permissions a file (isDir false) or folder inherits from a folder with permissions `parent`, by the
    // Windows inheritance rules - what Windows would give it, worked out here because SetSecurity doesn't.
    // CREATOR OWNER entries are not resolved to the owner: the lock's own permissions have none.
    public static RawAcl InheritedAcl(RawAcl parent, bool isDir)
    {
        var acl = new RawAcl(parent.Revision, parent.Count);
        foreach (GenericAce ace in parent)
        {
            var f = ace.AceFlags;
            bool oi = (f & AceFlags.ObjectInherit) != 0, ci = (f & AceFlags.ContainerInherit) != 0, np = (f & AceFlags.NoPropagateInherit) != 0;
            AceFlags flags;
            if (!isDir) { if (!oi) continue; flags = AceFlags.Inherited; }
            else if (ci) flags = AceFlags.Inherited | (np ? AceFlags.None : f & (AceFlags.ObjectInherit | AceFlags.ContainerInherit));
            else if (oi && !np) flags = AceFlags.Inherited | AceFlags.ObjectInherit | AceFlags.InheritOnly;   // passed on to files further down
            else continue;
            var copy = ace.Copy();
            copy.AceFlags = flags;
            acl.InsertAce(acl.Count, copy);
        }
        return acl;
    }

    // Deletes the name path - a file, a link or an empty folder, only that name: a hard link's other names and
    // their content, attributes and permissions are left alone. Never follows a link, at the end of the path or
    // in place of its folder (InFolder).
    public static void DeleteName(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\');
        string parent = Path.GetDirectoryName(full);
        bool isDir, isLink;
        using (var ph = parent == null ? null : Pin(parent, out isDir, out isLink))
        {
            if (ph == null) throw new IOException(path + ": its folder could not be opened");
            DeleteName(full, ph);
        }
    }

    static void DeleteName(string path, SafeFileHandle folder)
    {
        using (var h = CreateFileW(path, DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
        {
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            InFolder(h, folder, path);
            uint d = FILE_DISPOSITION_DELETE | FILE_DISPOSITION_POSIX_SEMANTICS | FILE_DISPOSITION_IGNORE_READONLY;
            if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref d, 4)) return;
            // Windows before 10 1809, or not NTFS: the older delete-on-close, on the same checked handle. It
            // refuses a read-only name, which then stays (and fails the lock).
            byte del = 1;
            if (!SetFileInformationByHandle(h, FileDispositionInfo, ref del, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
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
