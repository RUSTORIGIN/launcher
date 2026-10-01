using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

// Host-side builder for the client delta "patch pack" the launcher applies (src\ClientPatch.cs).
// Compiled on the fly by scripts\make_client_patch.ps1 (Add-Type) - it is NOT part of the launcher.
//
// How it finds what a player already has: every file of the OLD build is cut into content-defined
// chunks (a rolling hash picks the cut points, so identical data yields identical chunks even when
// it has moved to a different offset or a different file) and each chunk's MD5 is indexed. Each
// changed file of the NEW build is cut the same way; a chunk found in the index becomes a "copy
// this range of an installed file" op, anything else is appended to data.bin. Adjacent ops merge.
public static class ClientPatchBuilder
{
    // Chunk geometry: no cut before MinChunk bytes, then one on average every 2^CutBits bytes, forced at
    // MaxChunk. Smaller chunks lose less data around each edit but make a longer recipe. Measured on
    // Jan -> Apr 2021 (16.2 GB of changed files): 4 KB/16 KB chunks left 3.24 GB of new data, these
    // 1 KB/4 KB ones 2.84 GB, for the same build time.
    public static int MinChunk = 1024, MaxChunk = 65536, CutBits = 12;
    static ulong CutMask { get { return ulong.MaxValue << (64 - CutBits); } }
    static readonly ulong[] Gear = MakeGear();

    static ulong[] MakeGear()
    {
        var g = new ulong[256]; ulong x = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < 256; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; g[i] = x; }
        return g;
    }

    struct Key : IEquatable<Key>
    {
        public ulong A, B;
        public bool Equals(Key o) { return A == o.A && B == o.B; }
        public override bool Equals(object o) { return o is Key && Equals((Key)o); }
        public override int GetHashCode() { return (int)(A ^ (A >> 32)); }
    }

    struct Loc { public int File; public long Offset; }

    public sealed class Result
    {
        public int UnchangedFiles, RebuiltFiles, DeletedFiles, Sources;
        public long RebuiltBytes, CopiedBytes, DataBytes, PackBytes;
        public string PackSha256;
    }

    // Calls onChunk(buffer, start, length, offsetInFile) for each chunk. Returns the file's SHA-256
    // when wantSha is set (null otherwise - the old build is only indexed, never hashed whole).
    static string Chunk(string path, bool wantSha, Action<byte[], int, int, long> onChunk)
    {
        var buf = new byte[8 << 20];
        int min = MinChunk, max = MaxChunk; ulong mask = CutMask;
        using (var sha = NewSha())
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            int have = 0, start = 0; long baseOff = 0; bool eof = false;
            while (true)
            {
                int n;
                while (!eof && have < buf.Length)
                {
                    n = fs.Read(buf, have, buf.Length - have);
                    if (n <= 0) { eof = true; break; }
                    if (wantSha) sha.TransformBlock(buf, have, n, null, 0);
                    have += n;
                }
                while (start < have)
                {
                    int avail = have - start;
                    if (!eof && avail < max) break;               // need more data to place the cut
                    int limit = Math.Min(avail, max), len = limit;
                    ulong roll = 0;
                    // the gear hash only remembers its last 64 bytes, so start rolling just before the minimum
                    for (int i = Math.Min(limit, Math.Max(0, min - 64)); i < limit; i++)
                    {
                        roll = (roll << 1) + Gear[buf[start + i]];
                        if (i + 1 >= min && (roll & mask) == 0) { len = i + 1; break; }
                    }
                    onChunk(buf, start, len, baseOff + start);
                    start += len;
                }
                if (eof && start >= have) break;
                int tail = have - start;
                Buffer.BlockCopy(buf, start, buf, 0, tail);
                baseOff += start; have = tail; start = 0;
            }
            if (!wantSha) return null;
            sha.TransformFinalBlock(buf, 0, 0);
            return Hex(sha.Hash);
        }
    }

    // The native SHA-256 is several times faster than the managed one on multi-GB files. Looked up
    // by name so this also compiles under PowerShell 7 / .NET 8, where SHA256.Create() is native anyway.
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

    static Key KeyOf(MD5 md5, byte[] buf, int start, int len)
    {
        byte[] h = md5.ComputeHash(buf, start, len);
        return new Key { A = BitConverter.ToUInt64(h, 0), B = BitConverter.ToUInt64(h, 8) };
    }

    // oldRel / newRel: the files each build SHIPS, relative to its folder with '/' separators (the
    // caller applies the same exclusions as package_client.ps1).
    public static Result Build(string oldDir, string[] oldRel, string newDir, string[] newRel,
                               string from, string to, string outPack, bool optimal, Action<string> log)
    {
        var res = new Result();
        var md5 = MD5.Create();

        // 1) index every chunk of the old build
        var index = new Dictionary<Key, Loc>();
        var oldSize = new long[oldRel.Length];
        for (int f = 0; f < oldRel.Length; f++)
        {
            int fi = f;
            string path = Path.Combine(oldDir, oldRel[f].Replace('/', '\\'));
            oldSize[f] = new FileInfo(path).Length;
            Chunk(path, false, (buf, start, len, off) =>
            {
                Key k = KeyOf(md5, buf, start, len);
                if (!index.ContainsKey(k)) index[k] = new Loc { File = fi, Offset = off };
            });
            if (log != null && oldSize[f] > (256L << 20)) log("indexed " + oldRel[f]);
        }
        var oldIndexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int f = 0; f < oldRel.Length; f++) oldIndexOf[oldRel[f]] = f;

        // 2) rebuild recipe for each changed/new file; new bytes stream straight into data.bin
        var sourceId = new Dictionary<int, int>();          // old file index -> source number in the recipe
        var sourceLines = new List<string>();
        var body = new StringBuilder();
        var newSet = new HashSet<string>(newRel, StringComparer.OrdinalIgnoreCase);

        if (File.Exists(outPack)) File.Delete(outPack);
        using (var zfs = new FileStream(outPack, FileMode.CreateNew))
        using (var zip = new ZipArchive(zfs, ZipArchiveMode.Create))
        {
            var dataEntry = zip.CreateEntry(ClientPatch.DataEntry, optimal ? CompressionLevel.Optimal : CompressionLevel.Fastest);
            using (var data = dataEntry.Open())
            {
                foreach (string rel in newRel)
                {
                    string path = Path.Combine(newDir, rel.Replace('/', '\\'));
                    long size = new FileInfo(path).Length;
                    int oi;
                    // identical to the installed file (same path, size and SHA-256): nothing to do
                    if (oldIndexOf.TryGetValue(rel, out oi) && oldSize[oi] == size
                        && Sha256Of(Path.Combine(oldDir, oldRel[oi].Replace('/', '\\'))) == Sha256Of(path))
                    { res.UnchangedFiles++; continue; }

                    var ops = new StringBuilder();
                    bool lastCopy = false; int lastSrc = -1; long lastOff = 0, lastLen = 0;   // the op being merged
                    long copied = 0, fresh = 0;

                    Action flush = () =>
                    {
                        if (lastLen == 0) return;
                        if (lastCopy) ops.Append("c ").Append(lastSrc).Append(' ').Append(lastOff).Append(' ').Append(lastLen).Append('\n');
                        else ops.Append("d ").Append(lastLen).Append('\n');
                        lastLen = 0;
                    };

                    string sha = Chunk(path, true, (buf, start, len, off) =>
                    {
                        Loc loc;
                        if (index.TryGetValue(KeyOf(md5, buf, start, len), out loc))
                        {
                            int sid;
                            if (!sourceId.TryGetValue(loc.File, out sid))
                            {
                                sid = sourceLines.Count; sourceId[loc.File] = sid;
                                sourceLines.Add("source " + oldSize[loc.File] + " " + oldRel[loc.File]);
                            }
                            if (lastLen > 0 && lastCopy && lastSrc == sid && lastOff + lastLen == loc.Offset) lastLen += len;
                            else { flush(); lastCopy = true; lastSrc = sid; lastOff = loc.Offset; lastLen = len; }
                            copied += len;
                        }
                        else
                        {
                            if (lastLen > 0 && !lastCopy) lastLen += len;
                            else { flush(); lastCopy = false; lastLen = len; }
                            data.Write(buf, start, len);
                            fresh += len;
                        }
                    });
                    flush();

                    body.Append("file ").Append(size).Append(' ').Append(sha).Append(' ').Append(rel).Append('\n').Append(ops);
                    res.RebuiltFiles++; res.RebuiltBytes += size; res.CopiedBytes += copied; res.DataBytes += fresh;
                    if (log != null && size > (64L << 20))
                        log(string.Format("{0,-52} {1,7:N0} MB, {2,6:N0} MB new", rel, size / 1048576.0, fresh / 1048576.0));
                }
            }

            foreach (string rel in oldRel)
                if (!newSet.Contains(rel)) { body.Append("delete ").Append(rel).Append('\n'); res.DeletedFiles++; }

            var recipe = new StringBuilder();
            recipe.Append("rustorigin-patch-v1\n").Append("from ").Append(from).Append('\n').Append("to ").Append(to).Append('\n');
            foreach (string s in sourceLines) recipe.Append(s).Append('\n');
            recipe.Append(body).Append("end\n");
            var recipeEntry = zip.CreateEntry(ClientPatch.RecipeEntry, CompressionLevel.Optimal);
            using (var w = new StreamWriter(recipeEntry.Open(), new UTF8Encoding(false))) w.Write(recipe.ToString());
        }

        res.Sources = sourceLines.Count;
        res.PackBytes = new FileInfo(outPack).Length;
        res.PackSha256 = Sha256Of(outPack);
        return res;
    }

    static string Sha256Of(string path)
    {
        using (var sha = NewSha())
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            return Hex(sha.ComputeHash(fs));
    }

    static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
