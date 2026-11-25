// BuildHelper - small helpers shared by build.cmd and build.sh.
//
//   originals <outDir> <Assembly-CSharp.dll> <Assembly-CSharp-firstpass.dll> <EmpireManagement.swf>
//       For each file, picks the given path or "<path>.orig" (the backup made by install), whichever has the expected
//       UCP 6.4b SHA-256, and copies it to <outDir>/Managed/... or <outDir>/StreamingAssets/... . Fails if neither matches.
//
//   extract <exportDir> <outDir>
//       From an FFDec "-format script:pcode -export script" export of the ORIGINAL SWF, writes the original body of every
//       patched method to <outDir>/<name>.orig.pcode (normalized: lines trimmed). Used when (re)creating patches.
//
//   apply <exportDir> <patchDir> <outDir>
//       Extracts every original body as above, applies <patchDir>/<name>.diff (unified diff, exact match required) and
//       writes <outDir>/<name>.pcode for FFDec -replace.
//
//   prepare-target <MasterOfOrion_Data> <new Assembly-CSharp.dll> <new Assembly-CSharp-firstpass.dll> <new EmpireManagement.swf>
//       Used by install before copying. Checks that every target file is either the UCP 6.4b original or already patched
//       by this project (with a UCP 6.4b "<file>.orig" backup), and creates/refreshes the .orig backups. Changes nothing
//       and fails with an explanation otherwise (UCP missing, other UCP version, game updated, ...).
//
//   mkdiff <original.pcode> <modified.pcode> <out.diff>
//       Writes a unified diff (1 line of context) of two normalized P-code files.
//
// Only the diffs are distributed; the original bytecode is taken from the user's own copy of the game.
using System.Security.Cryptography;
using System.Text;

static class Program
{
    // name, class file inside the export, method locator (trait name, or "ctor:<Class>" for the instance constructor)
    static readonly (string name, string file, string locator)[] Bodies =
    {
        ("ctor",       "scripts/ngd/ColonyWindow.pcode",        "ctor:ColonyWindow"),
        ("fill",       "scripts/ngd/ColonyWindow.pcode",        "FillList"),
        ("adj",        "scripts/ngd/ColonyWindow.pcode",        "adjustScrollbar"),
        ("buy",        "scripts/ngd/ColonyWindow.pcode",        "setBuyBtns"),
        ("scroll",     "scripts/ngd/ASRoot.pcode",              "CheckScrollableObjects"),
        ("enterframe", "scripts/ngd/ASRoot.pcode",              "OnEnterFrame"),
        ("sb_press",   "scripts/fl/controls/ScrollBar.pcode",   "thumbPressHandler"),
        ("sb_release", "scripts/fl/controls/ScrollBar.pcode",   "thumbReleaseHandler"),
    };

    // UCP 6.4b originals
    static readonly (string file, string sub, string sha)[] Originals =
    {
        ("Assembly-CSharp.dll",           "Managed",         "915c5eacf2314a64a7f39ac243c45333f07e96b70e5c1ec26b90888d64b5895a"),
        ("Assembly-CSharp-firstpass.dll", "Managed",         "ab936ff9ea0bd8157e5a5d503dc4def9ea13c731aa737f82c006bd5d38d039e6"),
        ("EmpireManagement.swf",          "StreamingAssets", "55d6a8489a816ae9c794affacedf6dfeb8603cfd92b782842d19f4cfb5932e51"),
    };

    static int Main(string[] a)
    {
        try
        {
            switch (a.Length > 0 ? a[0] : "")
            {
                case "originals" when a.Length == 5: return PickOriginals(a[1], new[] { a[2], a[3], a[4] });
                case "extract" when a.Length == 3: Extract(a[1], a[2]); return 0;
                case "apply" when a.Length == 4: Apply(a[1], a[2], a[3]); return 0;
                case "prepare-target" when a.Length == 5: return PrepareTarget(a[1], new[] { a[2], a[3], a[4] });
                case "mkdiff" when a.Length == 4: File.WriteAllText(a[3], MakeDiff(ReadNorm(a[1]), ReadNorm(a[2]), a[1], a[2])); return 0;
            }
            Console.Error.WriteLine("usage: originals <outDir> <acs> <firstpass> <swf> | extract <exportDir> <outDir> | apply <exportDir> <patchDir> <outDir>\n" +
                                    "     | prepare-target <MasterOfOrion_Data> <acs> <firstpass> <swf> | mkdiff <orig> <new> <out>");
            return 2;
        }
        catch (Exception e) { Console.Error.WriteLine("ERROR: " + e.Message); return 1; }
    }

    // ---------------- originals ----------------
    static int PickOriginals(string outDir, string[] paths)
    {
        int bad = 0;
        for (int i = 0; i < Originals.Length; i++)
        {
            var (file, sub, want) = Originals[i];
            string picked = null; var seen = new List<string>();
            foreach (var cand in new[] { paths[i], paths[i] + ".orig" })
            {
                if (!File.Exists(cand)) continue;
                string got = Sha256(cand); seen.Add($"{cand} = {got}");
                if (got == want) { picked = cand; break; }
            }
            if (picked == null)
            {
                bad++;
                Console.Error.WriteLine($"ERROR: no UCP 6.4b original of {file} found (expected sha256 {want}).");
                foreach (var s in seen) Console.Error.WriteLine("       " + s);
                if (seen.Count == 0) Console.Error.WriteLine($"       missing: {paths[i]}");
                continue;
            }
            Directory.CreateDirectory(Path.Combine(outDir, sub));
            File.Copy(picked, Path.Combine(outDir, sub, file), true);
            Console.WriteLine($"  original {file}: {picked}");
        }
        if (bad > 0)
            Console.Error.WriteLine("The patch is built only from the UCP 6.4b files. Install UCP 6.4b (or restore it with uninstall), or set ORIG_MANAGED / ORIG_SWF.");
        return bad == 0 ? 0 : 1;
    }

    // ---------------- install target ----------------
    static int PrepareTarget(string gameData, string[] newFiles)
    {
        string acs = Path.Combine(gameData, "Managed", "Assembly-CSharp.dll");
        // a game DLL patched by this project references MooFix (any version of the patch)
        bool patchInstalled = File.Exists(acs) && ContainsAscii(File.ReadAllBytes(acs), "MooFix");
        var backups = new List<(string from, string to)>();
        int bad = 0;
        for (int i = 0; i < Originals.Length; i++)
        {
            var (file, sub, ucp) = Originals[i];
            string cur = Path.Combine(gameData, sub, file), orig = cur + ".orig";
            if (!File.Exists(cur)) { Console.Error.WriteLine($"ERROR: {cur} not found - is this the Master of Orion folder?"); bad++; continue; }
            if (!File.Exists(newFiles[i])) { Console.Error.WriteLine($"ERROR: {newFiles[i]} not found - incomplete build."); bad++; continue; }
            string hc = Sha256(cur), ho = File.Exists(orig) ? Sha256(orig) : null, hn = Sha256(newFiles[i]);
            if (hc == ucp)
            {
                Console.WriteLine($"  {sub}\\{file}: UCP 6.4b original");
                if (ho != ucp) backups.Add((cur, orig));
            }
            else if (ho == ucp && (hc == hn || patchInstalled))
                Console.WriteLine($"  {sub}\\{file}: patched {(hc == hn ? "(this build)" : "(earlier build)")}, UCP 6.4b backup present");
            else
            {
                bad++;
                Console.Error.WriteLine($"ERROR: {sub}\\{file} is not the UCP 6.4b original (sha256 {hc}).");
                if (ho == ucp)
                    Console.Error.WriteLine("       The .orig backup is UCP 6.4b, but the current file is neither that nor this patch:\n" +
                                            "       the game was probably updated or verified by Steam. Reinstall UCP 6.4b, then install again.");
                else
                    Console.Error.WriteLine("       UCP 6.4b does not seem to be installed (or another UCP version is). Install UCP 6.4b\n" +
                                            "       (Steam Workshop 1109736522, javaInstaller.bat), then build and install again.");
            }
        }
        if (bad > 0) { Console.Error.WriteLine("Nothing was changed."); return 1; }
        foreach (var (from, to) in backups)
        {
            File.Copy(from, to, true);
            Console.WriteLine($"  backup: {to}");
        }
        return 0;
    }

    static bool ContainsAscii(byte[] data, string s)
    {
        var p = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i + p.Length <= data.Length; i++)
        {
            int k = 0;
            while (k < p.Length && data[i + k] == p[k]) k++;
            if (k == p.Length) return true;
        }
        return false;
    }

    static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    // ---------------- P-code bodies ----------------
    static List<string> ReadNorm(string path) =>
        File.ReadAllLines(path).Select(l => l.Trim()).ToList();

    static List<string> ExtractBody(string exportDir, string file, string locator)
    {
        var lines = ReadNorm(Path.Combine(exportDir, file));
        int at = -1;
        for (int i = 0; i < lines.Count && at < 0; i++)
        {
            bool hit = locator.StartsWith("ctor:")
                ? lines[i] == $"public function {locator.Substring(5)}()"
                : lines[i].StartsWith("trait method ") && lines[i].EndsWith($"\"{locator}\")");
            if (hit) at = i;
        }
        if (at < 0) throw new Exception($"{locator} not found in {file}");
        int start = at + 1;
        while (start < lines.Count && lines[start] != "method") start++;
        int depth = 0;
        for (int j = start; j < lines.Count; j++)
        {
            if (lines[j] == "method") depth++;
            else if (lines[j] == "end ; method" && --depth == 0) return lines.GetRange(start, j - start + 1);
        }
        throw new Exception($"end of method {locator} not found in {file}");
    }

    static void Extract(string exportDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var (name, file, loc) in Bodies)
            WriteLines(Path.Combine(outDir, name + ".orig.pcode"), ExtractBody(exportDir, file, loc));
    }

    static void Apply(string exportDir, string patchDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var (name, file, loc) in Bodies)
        {
            var orig = ExtractBody(exportDir, file, loc);
            var result = ApplyDiff(orig, File.ReadAllLines(Path.Combine(patchDir, name + ".diff")), name);
            WriteLines(Path.Combine(outDir, name + ".pcode"), result);
            Console.WriteLine($"  {name}: {orig.Count} -> {result.Count} lines");
        }
    }

    static void WriteLines(string path, List<string> lines) =>
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

    // ---------------- unified diff ----------------
    static List<string> ApplyDiff(List<string> orig, string[] diff, string name)
    {
        var res = new List<string>();
        int pos = 0; // next unconsumed original line (0-based)
        for (int i = 0; i < diff.Length; i++)
        {
            string h = diff[i];
            if (!h.StartsWith("@@")) continue;
            // @@ -a,b +c,d @@
            var parts = h.Split(' ');
            var range = parts[1].Substring(1).Split(',');
            int a = int.Parse(range[0]);
            bool pureInsert = range.Length > 1 && int.Parse(range[1]) == 0;
            int start = pureInsert ? a : Math.Max(a - 1, 0); // "-a,0" inserts after line a
            if (start < pos) throw new Exception($"{name}.diff: overlapping hunk at line {i + 1}");
            while (pos < start) res.Add(orig[pos++]);
            for (i++; i < diff.Length && !diff[i].StartsWith("@@"); i++)
            {
                string l = diff[i];
                if (l.Length == 0 || l.StartsWith("\\")) continue;
                char k = l[0]; string body = l.Substring(1).Trim();
                if (k == '+') { res.Add(body); continue; }
                if (k != ' ' && k != '-') continue;
                if (pos >= orig.Count || orig[pos] != body)
                    throw new Exception($"{name}.diff line {i + 1} does not match the original body (line {pos + 1}: \"{(pos < orig.Count ? orig[pos] : "<eof>")}\"). Different SWF version?");
                if (k == ' ') res.Add(body);
                pos++;
            }
            i--;
        }
        while (pos < orig.Count) res.Add(orig[pos++]);
        return res;
    }

    // 1 line of context keeps as little original bytecode as possible in the distributed diffs;
    // hunk positions are still checked exactly by ApplyDiff.
    static string MakeDiff(List<string> a, List<string> b, string an, string bn, int ctx = 1)
    {
        // LCS table (bodies are at most a few thousand lines)
        int n = a.Count, m = b.Count;
        var L = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                L[i, j] = a[i] == b[j] ? L[i + 1, j + 1] + 1 : Math.Max(L[i + 1, j], L[i, j + 1]);
        var ops = new List<(char k, int ai, int bi)>(); // ai/bi = index in a/b
        { int i = 0, j = 0;
          while (i < n || j < m)
          {
              if (i < n && j < m && a[i] == b[j]) { ops.Add((' ', i, j)); i++; j++; }
              else if (j < m && (i == n || L[i, j + 1] >= L[i + 1, j])) { ops.Add(('+', i, j)); j++; }
              else { ops.Add(('-', i, j)); i++; }
          } }
        var sb = new StringBuilder();
        sb.Append($"--- {Path.GetFileName(an)}\n+++ {Path.GetFileName(bn)}\n");
        int p = 0;
        while (p < ops.Count)
        {
            if (ops[p].k == ' ') { p++; continue; }
            int hs = Math.Max(0, p - ctx), he = p;
            // extend the hunk while the next change is within 2*ctx equal lines
            while (true)
            {
                while (he < ops.Count && ops[he].k != ' ') he++;
                int q = he; while (q < ops.Count && ops[q].k == ' ') q++;
                if (q < ops.Count && q - he <= 2 * ctx) { he = q; continue; }
                he = Math.Min(ops.Count, he + ctx); break;
            }
            int aStart = ops[hs].ai, bStart = ops[hs].bi;
            int aLen = ops.Skip(hs).Take(he - hs).Count(o => o.k != '+');
            int bLen = ops.Skip(hs).Take(he - hs).Count(o => o.k != '-');
            sb.Append($"@@ -{(aLen == 0 ? aStart : aStart + 1)},{aLen} +{(bLen == 0 ? bStart : bStart + 1)},{bLen} @@\n");
            for (int r = hs; r < he; r++)
                sb.Append(ops[r].k).Append(ops[r].k == '+' ? b[ops[r].bi] : a[ops[r].ai]).Append('\n');
            p = he;
        }
        return sb.ToString();
    }
}
