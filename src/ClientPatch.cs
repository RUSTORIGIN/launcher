using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

// Client delta update ("patch pack"): rebuilds a new client build from the files a player already
// has plus a small download, instead of re-downloading the whole client.
//
// Rust keeps nearly everything in a few multi-GB bundles that all change between builds, so a
// per-file update saves almost nothing - but most of the bytes INSIDE those bundles are unchanged.
// scripts\make_client_patch.ps1 compares two builds and writes a pack (a zip) holding:
//   recipe.txt  for every changed/new file: its size + SHA-256 and how to rebuild it, as a list of
//               "copy this byte range from an installed file" / "take the next N bytes of data.bin"
//   data.bin    only the bytes the old build does not contain, in the order the recipe uses them
//
// Applying is two steps so a failure can never leave a half-updated client:
//   Stage   rebuild every target into <install>\_update\files, hashing as it writes, and reject the
//           whole update unless each file matches its SHA-256. The installed client is only read.
//   Commit  move the staged files into place and remove the files the new build dropped.
//           Idempotent: if it is interrupted, calling it again finishes the job.
//
// The pack is SHA-256-verified by the launcher before it gets here (PatchSha256 in launcher.cfg), so
// the recipe is trusted input; the checks below are about a client that differs from the build the
// pack was made for (modified, corrupted, or a different version), not about a hostile pack.
// Pure logic, no UI - unit-tested by scripts\test_client_patch.ps1.

public sealed class PatchException : Exception
{
    public PatchException(string message) : base(message) { }
}

public static class ClientPatch
{
    public const string RecipeEntry = "recipe.txt";
    public const string DataEntry   = "data.bin";
    public const string StagingDir  = "_update";
    const string Magic = "rustorigin-patch-v1";

    public sealed class Op     { public bool Copy; public int Source; public long Offset; public long Length; }
    public sealed class Source { public long Size; public string Path; }
    public sealed class Target { public long Size; public string Sha256; public string Path; public List<Op> Ops = new List<Op>(); }

    public sealed class Recipe
    {
        public string From = "", To = "";
        public List<Source> Sources = new List<Source>();
        public List<Target> Files   = new List<Target>();
        public List<string> Deletes = new List<string>();
        public long TotalBytes;   // bytes of all rebuilt files (the free space staging needs)
        public long DataBytes;    // bytes taken from data.bin (the rest is copied from the install)
    }

    // ---------- recipe ----------
    // rustorigin-patch-v1
    // from <client version>            to <client version>
    // source <size> <path>             an installed file used as a copy source; index = order listed
    // file <size> <sha256> <path>      a file to rebuild, followed by its ops:
    //   c <source> <offset> <length>     copy a byte range of a source
    //   d <length>                       take the next <length> bytes of data.bin
    // delete <path>                    a file the new build no longer has
    // end
    public static Recipe ParseRecipe(TextReader reader)
    {
        var r = new Recipe();
        string line = reader.ReadLine();
        if (line == null || line.Trim() != Magic) throw new PatchException("not a client patch recipe");
        Target cur = null; long curBytes = 0; bool ended = false;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (ended) throw new PatchException("recipe has content after 'end'");
            int sp = line.IndexOf(' ');
            string kind = sp < 0 ? line : line.Substring(0, sp);
            string rest = sp < 0 ? "" : line.Substring(sp + 1);
            if (kind == "c" || kind == "d")
            {
                if (cur == null) throw new PatchException("recipe op outside a file");
                string[] p = rest.Split(' ');
                var op = new Op();
                if (kind == "c")
                {
                    if (p.Length != 3) throw new PatchException("bad copy op");
                    op.Copy = true; op.Source = (int)Num(p[0]); op.Offset = Num(p[1]); op.Length = Num(p[2]);
                    if (op.Source >= r.Sources.Count) throw new PatchException("copy op names an unknown source");
                    if (op.Offset + op.Length > r.Sources[op.Source].Size) throw new PatchException("copy op runs past the end of its source");
                }
                else
                {
                    if (p.Length != 1) throw new PatchException("bad data op");
                    op.Length = Num(p[0]); r.DataBytes += op.Length;
                }
                if (op.Length == 0) throw new PatchException("empty recipe op");
                cur.Ops.Add(op); curBytes += op.Length;
                continue;
            }
            CloseFile(cur, curBytes); cur = null; curBytes = 0;
            switch (kind)
            {
                case "from": r.From = rest.Trim(); break;
                case "to":   r.To = rest.Trim(); break;
                case "source":
                {
                    string[] p = rest.Split(new[] { ' ' }, 2);
                    if (p.Length != 2) throw new PatchException("bad source line");
                    r.Sources.Add(new Source { Size = Num(p[0]), Path = SafeRelPath(p[1]) });
                    break;
                }
                case "file":
                {
                    string[] p = rest.Split(new[] { ' ' }, 3);
                    if (p.Length != 3 || p[1].Length != 64) throw new PatchException("bad file line");
                    cur = new Target { Size = Num(p[0]), Sha256 = p[1].ToLowerInvariant(), Path = SafeRelPath(p[2]) };
                    r.Files.Add(cur); r.TotalBytes += cur.Size;
                    break;
                }
                case "delete": r.Deletes.Add(SafeRelPath(rest)); break;
                case "end": ended = true; break;
                default: throw new PatchException("unknown recipe line: " + kind);
            }
        }
        CloseFile(cur, curBytes);
        if (!ended) throw new PatchException("recipe is truncated");
        return r;
    }

    static void CloseFile(Target t, long opBytes)
    {
        if (t != null && opBytes != t.Size) throw new PatchException("recipe ops for " + t.Path + " do not add up to its size");
    }

    static long Num(string s)
    {
        long v;
        if (!long.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out v))
            throw new PatchException("bad number in recipe: " + s);
        return v;
    }

    // A recipe path must stay inside the install folder and must not touch the launcher's own files.
    // Returns it with Windows separators.
    public static string SafeRelPath(string rel)
    {
        string p = (rel ?? "").Trim().Replace('/', '\\');
        if (p.Length == 0 || p.StartsWith("\\") || p.IndexOf(':') >= 0) throw new PatchException("unsafe path in recipe: " + rel);
        string[] parts = p.Split('\\');
        foreach (string part in parts)
            if (part.Length == 0 || part == "." || part == "..") throw new PatchException("unsafe path in recipe: " + rel);
        string first = parts[0].ToLowerInvariant();
        if (first == StagingDir || first == "_download" || first == ".rustorigin-installed"
            || (parts.Length == 1 && (first == "rustoriginlauncher.exe" || first == "uninstall.exe")))
            throw new PatchException("recipe touches a launcher file: " + rel);
        return p;
    }

    // ---------- install marker (<install>\.rustorigin-installed) ----------
    // Line 1 is the install time (all an older launcher ever wrote); "client=<version>" records
    // which client build is installed. No client line = an install from before versions were tracked.
    public static string ReadMarkerClient(string markerPath)
    {
        try
        {
            foreach (string line in File.ReadAllLines(markerPath))
                if (line.StartsWith("client=", StringComparison.OrdinalIgnoreCase)) return line.Substring(7).Trim();
        }
        catch { }
        return "";
    }

    public static string MarkerText(string client)
    {
        string t = DateTime.Now.ToString("o");
        return string.IsNullOrEmpty(client) ? t : t + "\r\nclient=" + client.Trim();
    }

    // PatchProbe = "relative\path|size": a cheap check, made BEFORE downloading the pack, that the
    // installed client is the build the pack upgrades from. Blank = no probe (always try the pack).
    public static bool ProbeMatches(string installDir, string probe)
    {
        if (string.IsNullOrEmpty(probe) || probe.Trim().Length == 0) return true;
        try
        {
            string[] p = probe.Split('|');
            long size;
            if (p.Length != 2 || !long.TryParse(p[1].Trim(), out size)) return false;
            var fi = new FileInfo(Path.Combine(installDir, SafeRelPath(p[0])));
            return fi.Exists && fi.Length == size;
        }
        catch { return false; }
    }

    // ---------- stage ----------
    static string StagingPath(string installDir) { return Path.Combine(installDir, StagingDir); }
    static string ReadyPath(string installDir)   { return Path.Combine(StagingPath(installDir), "ready"); }

    // First reason the installed client can't be the pack's source build, or null when it can.
    public static string CheckSources(Recipe recipe, string installDir)
    {
        foreach (Source s in recipe.Sources)
        {
            var fi = new FileInfo(Path.Combine(installDir, s.Path));
            if (!fi.Exists) return "installed file is missing: " + s.Path;
            if (fi.Length != s.Size) return "installed file differs from the expected build: " + s.Path;
        }
        return null;
    }

    // Rebuilds every changed file into <install>\_update\files and verifies each against its SHA-256.
    // The installed client is only read. Throws PatchException when the install is not the build the
    // pack was made for - the caller then falls back to the full download - IOException for a problem
    // a retry can fix (no free space, a locked file), and OperationCanceledException when cancelled()
    // turns true. Staging is discarded on any failure.
    public static Recipe Stage(string packPath, string installDir, Func<bool> cancelled, Action<long, long> progress)
    {
        string staging = StagingPath(installDir), files = Path.Combine(staging, "files");
        DiscardStaged(installDir);
        var open = new Dictionary<int, FileStream>();
        bool ok = false;
        try
        {
            using (var zip = ZipFile.OpenRead(packPath))
            {
                var recipeEntry = zip.GetEntry(RecipeEntry); var dataEntry = zip.GetEntry(DataEntry);
                if (recipeEntry == null || dataEntry == null) throw new PatchException("patch pack is missing " + RecipeEntry + " or " + DataEntry);
                Recipe recipe;
                using (var sr = new StreamReader(recipeEntry.Open(), Encoding.UTF8)) recipe = ParseRecipe(sr);

                string why = CheckSources(recipe, installDir);
                if (why != null) throw new PatchException(why);
                long free = -1;
                try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(installDir))).AvailableFreeSpace; } catch { }
                // Not a PatchException: the full download needs at least as much room, so falling back
                // to it would only fail later, after a much bigger download.
                if (free >= 0 && free < recipe.TotalBytes + (512L << 20))
                    throw new IOException("Not enough free space to update: need " + ((recipe.TotalBytes >> 20) + 512) + " MB on drive "
                        + Path.GetPathRoot(Path.GetFullPath(installDir)) + ", but only " + (free >> 20) + " MB is free. Free up space and try again.");

                string root = Path.GetFullPath(files) + "\\";
                var buf = new byte[1 << 20];
                long done = 0;
                using (var data = dataEntry.Open())
                {
                    foreach (Target t in recipe.Files)
                    {
                        string outPath = Path.GetFullPath(Path.Combine(files, t.Path));
                        if (!outPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new PatchException("unsafe path in recipe: " + t.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                        string hash;
                        using (var sha = NewSha())
                        using (var dst =new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                        {
                            foreach (Op op in t.Ops)
                            {
                                Stream src;
                                if (op.Copy)
                                {
                                    FileStream fs;
                                    if (!open.TryGetValue(op.Source, out fs))
                                    {
                                        fs = new FileStream(Path.Combine(installDir, recipe.Sources[op.Source].Path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                                        open[op.Source] = fs;
                                    }
                                    fs.Seek(op.Offset, SeekOrigin.Begin);
                                    src = fs;
                                }
                                else src = data;

                                long left = op.Length;
                                while (left > 0)
                                {
                                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                                    int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                                    if (n <= 0) throw new PatchException(op.Copy ? "installed file is shorter than expected: " + recipe.Sources[op.Source].Path : "patch data ended early");
                                    dst.Write(buf, 0, n);
                                    sha.TransformBlock(buf, 0, n, null, 0);
                                    left -= n; done += n;
                                    if (progress != null) progress(done, recipe.TotalBytes);
                                }
                            }
                            sha.TransformFinalBlock(buf, 0, 0);
                            hash = Hex(sha.Hash);
                            dst.Flush(true);   // on disk before "ready" can exist: a power cut must not leave a staged file with a missing tail
                        }
                        if (hash != t.Sha256)
                            throw new PatchException("rebuilt " + t.Path + " does not match its SHA-256 - the installed client differs from the build this update was made for");
                    }
                }

                File.WriteAllLines(Path.Combine(staging, "delete.txt"), recipe.Deletes.ToArray());
                // written last, and flushed: staging is complete and verified
                using (var rf = new FileStream(ReadyPath(installDir), FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] to = Encoding.UTF8.GetBytes(recipe.To);
                    rf.Write(to, 0, to.Length); rf.Flush(true);
                }
                ok = true;
                return recipe;
            }
        }
        catch (InvalidDataException ex) { throw new PatchException("patch pack is corrupt: " + ex.Message); }
        finally
        {
            foreach (FileStream fs in open.Values) { try { fs.Dispose(); } catch { } }
            if (!ok) DiscardStaged(installDir);
        }
    }

    // ---------- commit ----------
    // True when a fully staged + verified update is waiting to be moved into place.
    public static bool HasPendingCommit(string installDir)
    {
        try { return File.Exists(ReadyPath(installDir)); } catch { return false; }
    }

    // Moves the staged files over the installed ones, deletes the files the new build dropped, then
    // records the new client version in the install marker. Returns that version. Safe to call
    // again after an interruption: files already moved are simply no longer in staging, and "ready"
    // is only removed AFTER the marker is written - so a crash at any point either leaves the update
    // pending (the next call finishes it) or leaves a current, correctly labelled client. The game
    // must not be running. markerPath may be null (no marker is written).
    public static string Commit(string installDir, string markerPath)
    {
        string staging = StagingPath(installDir), files = Path.Combine(staging, "files");
        if (!HasPendingCommit(installDir)) throw new PatchException("no staged update to commit");
        string to = File.ReadAllText(ReadyPath(installDir)).Trim();

        if (Directory.Exists(files))
        {
            string root = Path.GetFullPath(files);
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(installDir, SafeRelPath(f.Substring(root.Length + 1)));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (File.Exists(dest))
                {
                    try { File.SetAttributes(dest, FileAttributes.Normal); } catch { }
                    File.Delete(dest);
                }
                File.Move(f, dest);
            }
        }

        string deleteList = Path.Combine(staging, "delete.txt");
        if (File.Exists(deleteList))
            foreach (string rel in File.ReadAllLines(deleteList))
            {
                if (rel.Trim().Length == 0) continue;
                try
                {
                    string p = Path.Combine(installDir, SafeRelPath(rel));
                    if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); }
                }
                catch { }   // a leftover file from the old build is harmless
            }

        if (markerPath != null) File.WriteAllText(markerPath, MarkerText(to));
        File.Delete(ReadyPath(installDir));   // the update is no longer pending
        // Removing what is left of staging is cosmetic: something else holding the folder open
        // (Explorer, antivirus, an indexer) must not turn a finished update into a failure.
        try { Directory.Delete(staging, true); } catch { }
        return to;
    }

    // Drops an unfinished (or unwanted) staged update. The installed client is untouched.
    public static void DiscardStaged(string installDir)
    {
        try { string s = StagingPath(installDir); if (Directory.Exists(s)) Directory.Delete(s, true); } catch { }
    }

    // The native SHA-256 is several times faster than the managed one, which matters when hashing
    // ~16 GB of rebuilt bundles on a player's machine.
    // Looked up by name so this file also compiles where the type lives elsewhere (PowerShell 7 /
    // .NET 8, used by CI to run the tests) - there SHA256.Create() is already native.
    static SHA256 NewSha()
    {
        try
        {
            Type cng = Type.GetType("System.Security.Cryptography.SHA256Cng, System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
            if (cng != null) return (SHA256)Activator.CreateInstance(cng);
        }
        catch { }
        return SHA256.Create();
    }

    static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
