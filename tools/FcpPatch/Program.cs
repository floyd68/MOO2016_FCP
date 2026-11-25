// FcpPatch - binary release tool of MOO2016_FCP (.NET Framework 4.8, part of Windows 10/11)
//
//   make <original> <patched> <out.fcpd>
//       Maintainer side: writes a delta from a UCP 6.4b original to the file produced by the build.
//       SWF files are compared decompressed (the CWS zlib stream would differ completely).
//   install <game folder> <files folder>
//       User side: checks the game files (UCP 6.4b original, or already patched with a UCP 6.4b .orig backup), rebuilds the
//       patched files from the user's own originals, verifies their SHA-256 against files\manifest.txt, then backs up and
//       writes them. Changes nothing if any check fails.
//       The two DLLs are patched by FixPatcher.exe (the build's own Mono.Cecil patcher, .NET Framework 4.8 build): a binary
//       delta of a Cecil-rewritten DLL would mostly consist of the game's own code. The SWF uses a delta (<file>.fcpd).
//   uninstall <game folder>
//       Restores the .orig backups and removes MooFix.dll.
//
// Delta format (.fcpd): "FCPD" 1 <kind: 0 raw, 1 swf> <source sha256> <target sha256 (decompressed for swf)> <target length>
// followed by a gzip stream of operations: 1 = copy <varint offset> <varint length> from the source, 2 = add <varint length>
// <bytes>, 0 = end. The deltas contain the changed bytes only; the game code itself comes from the user's installation.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

static class Program
{
    // file, sub folder, UCP 6.4b SHA-256
    static readonly string[,] Targets =
    {
        { "Assembly-CSharp.dll",           "Managed",         "915c5eacf2314a64a7f39ac243c45333f07e96b70e5c1ec26b90888d64b5895a" },
        { "Assembly-CSharp-firstpass.dll", "Managed",         "ab936ff9ea0bd8157e5a5d503dc4def9ea13c731aa737f82c006bd5d38d039e6" },
        { "EmpireManagement.swf",          "StreamingAssets", "55d6a8489a816ae9c794affacedf6dfeb8603cfd92b782842d19f4cfb5932e51" },
    };

    static int Main(string[] a)
    {
        try
        {
            string cmd = a.Length > 0 ? a[0] : "";
            if (cmd == "make" && a.Length == 4) { Make(a[1], a[2], a[3]); return 0; }
            if (cmd == "install" && a.Length == 3) return Install(a[1], a[2]);
            if (cmd == "uninstall" && a.Length == 2) return Uninstall(a[1]);
            if (cmd == "hash" && a.Length == 2)
            {   // manifest value: SHA-256 of the file (SWF: of its decompressed content)
                byte[] d = File.ReadAllBytes(a[1]);
                Console.WriteLine(Sha256(IsCws(d) ? SwfDecompress(d) : d));
                return 0;
            }
            Console.Error.WriteLine("usage: FcpPatch make <original> <patched> <out.fcpd> | hash <file> | install <game folder> <files folder> | uninstall <game folder>");
            return 2;
        }
        catch (Exception e) { Console.Error.WriteLine("ERROR: " + e.Message); return 1; }
    }

    // ------------------------------------------------------------------ install / uninstall
    static string DataDir(string game)
    {
        string d = Path.Combine(game, "MasterOfOrion_Data");
        if (!File.Exists(Path.Combine(d, "Managed", "Assembly-CSharp.dll")))
            throw new Exception(game + " does not contain MasterOfOrion_Data\\Managed\\Assembly-CSharp.dll - is this the Master of Orion folder?");
        return d;
    }

    static bool GameRunning()
    {
        return Process.GetProcessesByName("MasterOfOrion").Length > 0;
    }

    static int Install(string game, string files)
    {
        if (GameRunning()) { Console.Error.WriteLine("Close the game first. Nothing was changed."); return 1; }
        string data = DataDir(game);
        string acs = Path.Combine(data, "Managed", "Assembly-CSharp.dll");
        bool patchInstalled = ContainsAscii(File.ReadAllBytes(acs), "MooFix");   // any version of this patch
        int n = Targets.GetLength(0);
        var outputs = new byte[n][];
        var manifest = ReadManifest(Path.Combine(files, "manifest.txt"));
        var backups = new List<string[]>();
        int bad = 0;
        for (int i = 0; i < n; i++)
        {
            string file = Targets[i, 0], sub = Targets[i, 1], ucp = Targets[i, 2];
            string cur = Path.Combine(data, sub, file), orig = cur + ".orig";
            string delta = Path.Combine(files, file + ".fcpd");
            if (!File.Exists(cur)) { Console.Error.WriteLine("ERROR: " + cur + " not found."); bad++; continue; }
            if (file.EndsWith(".swf") && !File.Exists(delta)) { Console.Error.WriteLine("ERROR: " + delta + " not found - incomplete download."); bad++; continue; }
            string hc = Sha256(File.ReadAllBytes(cur));
            string ho = File.Exists(orig) ? Sha256(File.ReadAllBytes(orig)) : null;
            string source;
            if (hc == ucp)
            {
                Console.WriteLine("  " + sub + "\\" + file + ": UCP 6.4b original");
                source = cur;
                if (ho != ucp) backups.Add(new[] { cur, orig });
            }
            else if (ho == ucp && patchInstalled)
            {
                Console.WriteLine("  " + sub + "\\" + file + ": patched earlier, UCP 6.4b backup present");
                source = orig;
            }
            else
            {
                bad++;
                Console.Error.WriteLine("ERROR: " + sub + "\\" + file + " is not the UCP 6.4b original (sha256 " + hc + ").");
                if (ho == ucp)
                    Console.Error.WriteLine("       The .orig backup is UCP 6.4b, but the current file is neither that nor this patch:\n" +
                                            "       the game was probably updated or verified by Steam. Reinstall UCP 6.4b, then install again.");
                else
                    Console.Error.WriteLine("       UCP 6.4b does not seem to be installed (or another UCP version is). Install UCP 6.4b\n" +
                                            "       (Steam Workshop 1109736522, javaInstaller.bat), then install again.");
                continue;
            }
            try
            {
                outputs[i] = File.Exists(delta) ? ApplyDelta(File.ReadAllBytes(source), File.ReadAllBytes(delta))
                                                 : RunFixPatcher(source, files, Path.Combine(data, "Managed"), file);
                string want;
                if (!manifest.TryGetValue(file, out want)) throw new Exception("not listed in manifest.txt");
                string got = Sha256(outputs[i]);
                if (file.EndsWith(".swf")) got = Sha256(SwfDecompress(outputs[i]));
                if (got != want) throw new Exception("the patched file does not match this release (sha256 " + got + ", expected " + want + ")");
            }
            catch (Exception e) { Console.Error.WriteLine("ERROR: " + file + ": " + e.Message); bad++; }
        }
        string mooFix = Path.Combine(files, "MooFix.dll");
        if (!File.Exists(mooFix)) { Console.Error.WriteLine("ERROR: " + mooFix + " not found - incomplete download."); bad++; }
        if (bad > 0) { Console.Error.WriteLine("Nothing was changed."); return 1; }

        foreach (var b in backups) { File.Copy(b[0], b[1], true); Console.WriteLine("  backup: " + b[1]); }
        for (int i = 0; i < n; i++)
        {
            string path = Path.Combine(data, Targets[i, 1], Targets[i, 0]);
            File.WriteAllBytes(path, outputs[i]);
            Console.WriteLine("  patched: " + path);
        }
        File.Copy(mooFix, Path.Combine(data, "Managed", "MooFix.dll"), true);
        string perf = Path.Combine(data, "Managed", "MooPerf.dll");
        if (File.Exists(perf)) File.Delete(perf);   // left over from a profiling build
        Console.WriteLine("  installed: " + Path.Combine(data, "Managed", "MooFix.dll"));
        return 0;
    }

    static Dictionary<string, string> ReadManifest(string path)
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) throw new Exception(path + " not found - incomplete download.");
        foreach (string line in File.ReadAllLines(path))
        {
            string[] p = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 2 && !p[0].StartsWith("#")) m[p[0]] = p[1].ToLowerInvariant();
        }
        return m;
    }

    // FixPatcher patch <original> <MooFix.dll> <out> <Managed folder>, into a temporary folder
    static byte[] RunFixPatcher(string source, string files, string managed, string file)
    {
        string exe = Path.Combine(files, "FixPatcher.exe");
        if (!File.Exists(exe)) throw new Exception(exe + " not found - incomplete download.");
        string tmp = Path.Combine(Path.GetTempPath(), "MOO2016_FCP_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string outFile = Path.Combine(tmp, file);
            var psi = new ProcessStartInfo(exe, Quote("patch") + " " + Quote(source) + " " + Quote(Path.Combine(files, "MooFix.dll")) + " " +
                                                 Quote(outFile) + " " + Quote(managed))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd(), se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(outFile)) throw new Exception("FixPatcher failed:\n" + so + se);
            }
            return File.ReadAllBytes(outFile);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    static string Quote(string s) { return "\"" + s + "\""; }

    static int Uninstall(string game)
    {
        if (GameRunning()) { Console.Error.WriteLine("Close the game first."); return 1; }
        string data = DataDir(game);
        for (int i = 0; i < Targets.GetLength(0); i++)
        {
            string cur = Path.Combine(data, Targets[i, 1], Targets[i, 0]), orig = cur + ".orig";
            if (File.Exists(orig)) { File.Copy(orig, cur, true); Console.WriteLine("  restored " + Targets[i, 1] + "\\" + Targets[i, 0]); }
            else Console.WriteLine("  no backup for " + Targets[i, 1] + "\\" + Targets[i, 0] + " (skipped)");
        }
        foreach (string f in new[] { "MooFix.dll", "MooPerf.dll" })
        {
            string p = Path.Combine(data, "Managed", f);
            if (File.Exists(p)) File.Delete(p);
        }
        Console.WriteLine("Done. (Steam 'Verify integrity of game files' also restores the files, but then UCP must be reinstalled.)");
        return 0;
    }

    // ------------------------------------------------------------------ SWF (CWS = "CWS" ver len + zlib)
    static bool IsCws(byte[] d) { return d.Length > 8 && d[0] == 'C' && d[1] == 'W' && d[2] == 'S'; }

    static byte[] SwfDecompress(byte[] d)
    {
        if (!IsCws(d)) return d;
        int len = BitConverter.ToInt32(d, 4);
        var raw = new byte[len];
        raw[0] = (byte)'F'; raw[1] = (byte)'W'; raw[2] = (byte)'S'; raw[3] = d[3];
        Array.Copy(d, 4, raw, 4, 4);
        using (var ds = new DeflateStream(new MemoryStream(d, 10, d.Length - 10), CompressionMode.Decompress))   // skip the 2 zlib header bytes
        {
            int off = 8;
            while (off < len) { int r = ds.Read(raw, off, len - off); if (r <= 0) throw new Exception("truncated SWF"); off += r; }
        }
        return raw;
    }

    static byte[] SwfCompress(byte[] raw)
    {
        var ms = new MemoryStream();
        ms.WriteByte((byte)'C'); ms.WriteByte((byte)'W'); ms.WriteByte((byte)'S'); ms.WriteByte(raw[3]);
        ms.Write(raw, 4, 4);
        ms.WriteByte(0x78); ms.WriteByte(0x9C);
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(raw, 8, raw.Length - 8);
        uint s1 = 1, s2 = 0;                                   // adler32 of the compressed part
        for (int i = 8; i < raw.Length; i++) { s1 = (s1 + raw[i]) % 65521; s2 = (s2 + s1) % 65521; }
        uint ad = (s2 << 16) | s1;
        ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ delta
    const int Block = 32;

    static void Make(string origPath, string newPath, string outPath)
    {
        byte[] o = File.ReadAllBytes(origPath), t = File.ReadAllBytes(newPath);
        bool swf = IsCws(o) || IsCws(t);
        string srcSha = Sha256(o);
        if (swf) { o = SwfDecompress(o); t = SwfDecompress(t); }

        // index the source at block boundaries (every match of >= 2*Block-1 bytes contains an indexed block)
        var index = new Dictionary<uint, List<int>>();
        for (int p = 0; p + Block <= o.Length; p += Block)
        {
            uint h = Hash(o, p);
            List<int> l;
            if (!index.TryGetValue(h, out l)) { l = new List<int>(); index[h] = l; }
            if (l.Count < 64) l.Add(p);
        }
        var ops = new MemoryStream();
        int lit = 0, i = 0, copies = 0, copied = 0;
        uint rh = t.Length >= Block ? Hash(t, 0) : 0, pow = 1;
        for (int k = 0; k < Block - 1; k++) pow *= 31;
        while (i + Block <= t.Length)
        {
            List<int> cands;
            int bestLen = 0, bestSrc = -1, bestBack = 0;
            if (index.TryGetValue(rh, out cands))
            {
                foreach (int c in cands)
                {
                    int f = 0;
                    while (c + f < o.Length && i + f < t.Length && o[c + f] == t[i + f]) f++;
                    if (f < Block) continue;
                    int b = 0;
                    while (b < i - lit && c - b > 0 && o[c - b - 1] == t[i - b - 1]) b++;
                    if (f + b > bestLen) { bestLen = f + b; bestSrc = c - b; bestBack = b; }
                }
            }
            if (bestLen >= Block)
            {
                int start = i - bestBack;
                if (start > lit) WriteAdd(ops, t, lit, start - lit);
                ops.WriteByte(1); WriteVar(ops, bestSrc); WriteVar(ops, bestLen);
                copies++; copied += bestLen;
                i = start + bestLen; lit = i;
                if (i + Block <= t.Length) rh = Hash(t, i);
                continue;
            }
            if (i + Block < t.Length) rh = (rh - t[i] * pow) * 31 + t[i + Block];
            i++;
        }
        if (t.Length > lit) WriteAdd(ops, t, lit, t.Length - lit);
        ops.WriteByte(0);

        using (var fs = File.Create(outPath))
        {
            fs.Write(Encoding.ASCII.GetBytes("FCPD"), 0, 4);
            fs.WriteByte(1); fs.WriteByte((byte)(swf ? 1 : 0));
            fs.Write(HexBytes(srcSha), 0, 32);
            fs.Write(HexBytes(Sha256(t)), 0, 32);
            fs.Write(BitConverter.GetBytes((long)t.Length), 0, 8);
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal, true)) { ops.Position = 0; ops.CopyTo(gz); }
        }
        // self-check: applying the delta to the original must give the target
        byte[] back = ApplyDelta(File.ReadAllBytes(origPath), File.ReadAllBytes(outPath));
        if (swf) back = SwfDecompress(back);
        if (Sha256(back) != Sha256(t)) throw new Exception("self-check failed for " + outPath);
        Console.WriteLine(Path.GetFileName(outPath) + ": " + new FileInfo(outPath).Length / 1024 + " KB (target " + t.Length / 1024 +
                          " KB, " + (100.0 * copied / Math.Max(1, t.Length)).ToString("F1") + "% copied from the original in " + copies + " blocks)");
    }

    static byte[] ApplyDelta(byte[] src, byte[] delta)
    {
        if (delta.Length < 82 || Encoding.ASCII.GetString(delta, 0, 4) != "FCPD" || delta[4] != 1) throw new Exception("not a FCPD v1 delta");
        bool swf = delta[5] == 1;
        string srcSha = Hex(delta, 6, 32), dstSha = Hex(delta, 38, 32);
        long len = BitConverter.ToInt64(delta, 70);
        if (Sha256(src) != srcSha) throw new Exception("source file does not match the delta");
        byte[] s = swf ? SwfDecompress(src) : src;
        var t = new byte[len];
        int pos = 0;
        using (var gz = new GZipStream(new MemoryStream(delta, 78, delta.Length - 78), CompressionMode.Decompress))
        {
            while (true)
            {
                int op = gz.ReadByte();
                if (op == 0) break;
                if (op == 1)
                {
                    int off = ReadVar(gz), n = ReadVar(gz);
                    Array.Copy(s, off, t, pos, n); pos += n;
                }
                else if (op == 2)
                {
                    int n = ReadVar(gz);
                    int r = 0;
                    while (r < n) { int k = gz.Read(t, pos + r, n - r); if (k <= 0) throw new Exception("truncated delta"); r += k; }
                    pos += n;
                }
                else throw new Exception("corrupt delta");
            }
        }
        if (pos != len || Sha256(t) != dstSha) throw new Exception("result does not match the expected checksum");
        return swf ? SwfCompress(t) : t;
    }

    static uint Hash(byte[] d, int p)
    {
        uint h = 0;
        for (int k = 0; k < Block; k++) h = h * 31 + d[p + k];
        return h;
    }

    static void WriteAdd(Stream s, byte[] t, int off, int n)
    {
        s.WriteByte(2); WriteVar(s, n); s.Write(t, off, n);
    }

    static void WriteVar(Stream s, int v)
    {
        uint u = (uint)v;
        while (u >= 0x80) { s.WriteByte((byte)(u | 0x80)); u >>= 7; }
        s.WriteByte((byte)u);
    }

    static int ReadVar(Stream s)
    {
        int v = 0, shift = 0;
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) throw new Exception("truncated delta");
            v |= (b & 0x7F) << shift;
            if (b < 0x80) return v;
            shift += 7;
        }
    }

    // ------------------------------------------------------------------ helpers
    static string Sha256(byte[] d)
    {
        using (var h = SHA256.Create()) return Hex(h.ComputeHash(d), 0, 32);
    }

    static string Hex(byte[] d, int off, int n)
    {
        var sb = new StringBuilder(n * 2);
        for (int i = 0; i < n; i++) sb.Append(d[off + i].ToString("x2"));
        return sb.ToString();
    }

    static byte[] HexBytes(string h)
    {
        var b = new byte[h.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(2 * i, 2), 16);
        return b;
    }

    static bool ContainsAscii(byte[] data, string s)
    {
        byte[] p = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i + p.Length <= data.Length; i++)
        {
            int k = 0;
            while (k < p.Length && data[i + k] == p[k]) k++;
            if (k == p.Length) return true;
        }
        return false;
    }
}
