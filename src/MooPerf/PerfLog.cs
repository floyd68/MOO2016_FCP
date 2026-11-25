using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

public static class MooPerfLog
{
    const double ThresholdMs = 15.0;
    static readonly object Lock = new object();
    static StreamWriter writer;
    [ThreadStatic] static int depth;
    [ThreadStatic] static Dictionary<string, Stat> stats;
    [ThreadStatic] static long lastDump;

    class Stat { public long count; public double total; public double max; public long alloc; public long gcIn; }

    // Allocation attribution (approximate): managed heap in use (GC.GetTotalMemory(false)) at Begin and End. Calls with
    // a collection in between are not measured (gcIn counts those collections instead: the deepest methods with gcIn
    // are where collections were triggered). "self" = this call minus its instrumented children. Heap growth is seen in
    // block-sized steps, so only totals over many calls are meaningful; other threads allocating at the same time are
    // attributed here as well (measure with the overlapped simulation off).
    [ThreadStatic] static long[] heapAt, childAlloc;
    [ThreadStatic] static int[] gcAt;

    static StreamWriter W()
    {
        if (writer == null)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Master of Orion");
            Directory.CreateDirectory(dir);
            writer = new StreamWriter(Path.Combine(dir, "perf_log.txt"), true);
            writer.AutoFlush = true;
            writer.WriteLine("==== session start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
        }
        return writer;
    }

    public static long Begin()
    {
        depth++;
        try
        {
            if (heapAt == null || depth >= heapAt.Length)
            {
                int n = Math.Max(64, depth * 2);
                Array.Resize(ref heapAt, n); Array.Resize(ref childAlloc, n); Array.Resize(ref gcAt, n);
            }
            if (depth >= 0) { heapAt[depth] = GC.GetTotalMemory(false); gcAt[depth] = GC.CollectionCount(0); childAlloc[depth] = 0; }
        }
        catch { }
        return Stopwatch.GetTimestamp();
    }

    public static void EndObj(long t0, string name, object self)
    {
        string n = name;
        try { if (self != null) n = name + "@" + self.GetType().Name; } catch { }
        End(t0, n);
    }

    public static void End(long t0, string name)
    {
        long now = Stopwatch.GetTimestamp();
        int d = depth;
        long incl = -1, gcs = 0;
        try
        {
            if (heapAt != null && d >= 0 && d < heapAt.Length)
            {
                int gcNow = GC.CollectionCount(0);
                gcs = gcNow - gcAt[d];
                if (gcs == 0) incl = GC.GetTotalMemory(false) - heapAt[d];
                if (incl >= 0 && d >= 1) childAlloc[d - 1] += incl;
            }
        }
        catch { }
        depth--;
        double ms = (now - t0) * 1000.0 / Stopwatch.Frequency;
        try
        {
            if (stats == null) { stats = new Dictionary<string, Stat>(); lastDump = now; }
            Stat s;
            if (!stats.TryGetValue(name, out s)) { s = new Stat(); stats[name] = s; }
            s.count++; s.total += ms; if (ms > s.max) s.max = ms;
            s.gcIn += gcs;
            if (incl >= 0) { long self = incl - childAlloc[d]; if (self > 0) s.alloc += self; }

            if (ms >= ThresholdMs)
            {
                lock (Lock)
                {
                    W().WriteLine(string.Format("{0:HH:mm:ss.fff} T{1,-3} d{2,-2} {3}{4} {5:F1} ms",
                        DateTime.Now, Thread.CurrentThread.ManagedThreadId, depth, new string(' ', depth * 2), name, ms));
                }
            }
            bool turnEnd = name == "Universe.AdvanceSimulation(0)" || name == "Universe.RunAI(0)";
            bool periodic = depth == 0 && (now - lastDump) > Stopwatch.Frequency * 20;
            if (turnEnd || periodic) Dump(turnEnd ? "after " + name : "periodic 20s", now);
        }
        catch { }
    }

    static void Dump(string why, long now)
    {
        var list = new List<KeyValuePair<string, Stat>>(stats);
        list.Sort((a, b) => b.Value.total.CompareTo(a.Value.total));
        lock (Lock)
        {
            var w = W();
            w.WriteLine(string.Format("---- STATS T{0} ({1}) top by total ms ----", Thread.CurrentThread.ManagedThreadId, why));
            int n = 0;
            foreach (var kv in list)
            {
                if (kv.Value.total < 20 || n++ >= 80) break;
                w.WriteLine(string.Format("  {0,10:F0} ms {1,9} calls {2,8:F2} avg {3,8:F1} max  {4}",
                    kv.Value.total, kv.Value.count, kv.Value.total / kv.Value.count, kv.Value.max, kv.Key));
            }
            w.WriteLine("---- end STATS ----");
            list.Sort((a, b) => b.Value.alloc.CompareTo(a.Value.alloc));
            w.WriteLine(string.Format("---- ALLOC T{0} ({1}) top by allocated MB (self, approximate) | GCs inside (inclusive) ----", Thread.CurrentThread.ManagedThreadId, why));
            n = 0;
            foreach (var kv in list)
            {
                if (kv.Value.alloc < (1 << 20) || n++ >= 50) break;
                w.WriteLine(string.Format("  {0,8:F1} MB {1,9} calls {2,8:F1} KB avg {3,4} GCs  {4}",
                    kv.Value.alloc / 1048576.0, kv.Value.count, kv.Value.alloc / 1024.0 / kv.Value.count, kv.Value.gcIn, kv.Key));
            }
            w.WriteLine("---- end ALLOC ----");
        }
        stats.Clear();
        lastDump = now;
    }
}
