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

    class Stat { public long count; public double total; public double max; }

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
        depth--;
        double ms = (now - t0) * 1000.0 / Stopwatch.Frequency;
        try
        {
            if (stats == null) { stats = new Dictionary<string, Stat>(); lastDump = now; }
            Stat s;
            if (!stats.TryGetValue(name, out s)) { s = new Stat(); stats[name] = s; }
            s.count++; s.total += ms; if (ms > s.max) s.max = ms;

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
        }
        stats.Clear();
        lastDump = now;
    }
}
