// MooFix: performance fixes for Master of Orion (2016) + UCP.
// Every hook is a "prefix": return true = skip original (result in out param), false = run original.
// Calling an original from here: set bypass = true immediately before the call; the callee's prefix consumes it.
using System;
using System.Collections.Generic;
using System.IO;
using Backend.Civilizations;
using Backend.Core;
using Backend.Fleets;
using Backend.Galaxy;
using Backend.Settlements;

public static class MooFix
{
    // ------------------------------------------------------------------ infra
    [ThreadStatic] static bool bypass;
    static bool Bypassed() { if (bypass) { bypass = false; return true; } return false; }

    public static readonly bool Verify;
    public static readonly bool Overlap;   // experimental: overlapped turn simulation (moofix_overlap.txt)
    static readonly object logLock = new object();
    static StreamWriter log;
    static int mismatchCount;

    static string docDir;

    static MooFix()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Master of Orion");
            docDir = dir;
            Directory.CreateDirectory(dir);
            Verify = File.Exists(Path.Combine(dir, "moofix_verify.txt"));
            Overlap = File.Exists(Path.Combine(dir, "moofix_overlap.txt"));
            log = new StreamWriter(Path.Combine(dir, "moofix_log.txt"), true);
            log.AutoFlush = true;
        }
        catch { }
        Log("MooFix loaded. verify=" + Verify + " overlap=" + Overlap);
    }

    public static void Log(string s)
    {
        try { lock (logLock) { if (log != null) log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); } } catch { }
    }

    [ThreadStatic] static int verifyCounter;
    static bool Sample() { return (++verifyCounter & 15) == 0; }

    static void LogEx(string where, Exception e) { Log("EXCEPTION in " + where + ": " + e); }

    static void Mismatch(string what, object expected, object got)
    {
        mismatchCount++;
        if (mismatchCount <= 200) Log("MISMATCH " + what + " original=" + expected + " fix=" + got);
    }

    // ------------------------------------------------------------------ UI-scope caches
    // Valid only while no game state is mutated: UI refresh code and UpdateNotifications.
    [ThreadStatic] static int scopeDepth;
    [ThreadStatic] static Dictionary<MajorCivilization, float> secCache;
    [ThreadStatic] static Dictionary<Civilization, float> researchCache;
    [ThreadStatic] static Dictionary<Civilization, EmpireIndex> indexCache;
    [ThreadStatic] static Dictionary<Colony, List<IBuildable>> buildablesCacheQ;   // ignoreQueue = false
    [ThreadStatic] static Dictionary<Colony, List<IBuildable>> buildablesCacheNQ;  // ignoreQueue = true

    static void Enter() { scopeDepth++; }
    static void Exit() { if (--scopeDepth <= 0) { scopeDepth = 0; ClearCaches(); } }
    static void ClearCaches()
    {
        if (secCache != null) secCache.Clear();
        if (researchCache != null) researchCache.Clear();
        if (indexCache != null) indexCache.Clear();
        if (buildablesCacheQ != null) buildablesCacheQ.Clear();
        if (buildablesCacheNQ != null) buildablesCacheNQ.Clear();
        if (tradeCountCache != null) tradeCountCache.Clear();
        if (tradeRawCache != null) tradeRawCache.Clear();
        if (popEtaCache != null) popEtaCache.Clear();
    }

    // Colony.GetPopulationGrowthETA: refreshed into every observer's KnownColonyData (ReportChange, visibility updates,
    // load-time scans), i.e. the same colony is evaluated once per observing civilization. Cached per colony inside
    // cache scopes (no game state changes there).
    [ThreadStatic] static Dictionary<Colony, int> popEtaCache;

    public static bool Pre_Colony_GetPopulationGrowthETA(Colony self, out int result)
    {
        result = 0;
        if (Bypassed() || scopeDepth == 0) return false;
        try
        {
            if (popEtaCache == null) popEtaCache = new Dictionary<Colony, int>();
            if (popEtaCache.TryGetValue(self, out result))
            {
                if (Verify && Sample()) { bypass = true; int o = self.GetPopulationGrowthETA(); if (o != result) Mismatch("GetPopulationGrowthETA", o, result); result = o; }
                return true;
            }
            bypass = true;
            result = self.GetPopulationGrowthETA();
            popEtaCache[self] = result;
            return true;
        }
        catch (Exception e) { LogEx("GetPopulationGrowthETA", e); return false; }
    }

    class EmpireIndex
    {
        public HashSet<ColonyStructureType> built = new HashSet<ColonyStructureType>();
        public HashSet<ColonyStructureType> upgradeOf = new HashSet<ColonyStructureType>();
        public Dictionary<string, int> queueCount = new Dictionary<string, int>();
        public Dictionary<string, int> queueFirstID = new Dictionary<string, int>();
    }

    static EmpireIndex GetIndex(Civilization civ)
    {
        if (indexCache == null) indexCache = new Dictionary<Civilization, EmpireIndex>();
        EmpireIndex idx;
        if (indexCache.TryGetValue(civ, out idx)) return idx;
        idx = new EmpireIndex();
        var keys = new HashSet<string>();
        foreach (Colony c in civ.colonies)
        {
            if (c.IsInRevolt) continue;
            foreach (ColonyStructureType s in c.structures)
            {
                if (s == null) continue;
                idx.built.Add(s);
                int guard = 0;
                for (ColonyStructureType u = s.upgrades; !ReferenceEquals(u, null) && guard < 64; u = u.upgrades, guard++)
                    idx.upgradeOf.Add(u);
            }
            keys.Clear();
            foreach (ProductionQueueItem item in c.productionQueue)
            {
                IBuildable b = item.Buildable;
                string id = b.GetIdentifierAsString();
                if (id != null) keys.Add(id);
                ShipBlueprint bp = b as ShipBlueprint;
                if (bp != null && bp.hull != null && bp.hull.key != null) keys.Add(bp.hull.key);
            }
            foreach (string k in keys)
            {
                int n;
                idx.queueCount.TryGetValue(k, out n);
                idx.queueCount[k] = n + 1;
                if (n == 0) idx.queueFirstID[k] = c.ID;
            }
        }
        indexCache[civ] = idx;
        return idx;
    }

    // Universe.DispatchAction: any action mutates state -> drop caches of this thread.
    public static bool Pre_Universe_DispatchAction(Universe self, GameAction action)
    {
        if (Bypassed()) return false;
        if (scopeDepth > 0) ClearCaches();
        if (pathCache != null) pathCache.Clear();
        dispatchDepth++;
        try { bypass = true; self.DispatchAction(action); }
        finally
        {
            dispatchDepth--;
            if (pathCache != null) pathCache.Clear();
            if (scopeDepth > 0) ClearCaches();
        }
        return true;
    }

    // ------------------------------------------------------------------ AI path cache
    // During the AI stage (Universe.RunAI, server thread) the AI asks for the same paths many times
    // (e.g. rating every known planet several times per turn). Paths only depend on map/knowledge state, which only
    // changes through actions (all AI orders go through DispatchAction) or turn simulation. Cache is cleared before
    // and after every action and never filled while an action is running.
    [ThreadStatic] static int aiDepth;
    [ThreadStatic] static int dispatchDepth;
    [ThreadStatic] static Dictionary<PathKey, List<NavigationNode>> pathCache;
    [ThreadStatic] static long pathHits, pathMisses, pathMissTicks, pathFailCount, pathFailTicks, aiStartTicks;

    struct PathKey : IEquatable<PathKey>
    {
        public NavigationNode from, to; public Civilization civ; public int flags;
        public bool Equals(PathKey o) { return ReferenceEquals(from, o.from) && ReferenceEquals(to, o.to) && ReferenceEquals(civ, o.civ) && flags == o.flags; }
        public override bool Equals(object o) { return o is PathKey && Equals((PathKey)o); }
        public override int GetHashCode()
        {
            unchecked { return ((System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(from) * 397) ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(to)) * 31 ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(civ) ^ (flags << 28); }
        }
    }

    public static bool Pre_Universe_RunAI(Universe self)
    {
        if (Bypassed()) return false;
        if (aiDepth == 0) aiStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        aiDepth++;
        try { bypass = true; self.RunAI(); }
        finally
        {
            if (--aiDepth <= 0)
            {
                aiDepth = 0;
                if (pathCache != null) pathCache.Clear();
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double aiMs = (System.Diagnostics.Stopwatch.GetTimestamp() - aiStartTicks) * f;
                if (aiMs > 50 || pathHits + pathMisses > 0)
                    Log("AI RunAI " + aiMs.ToString("F0") + "ms | verified fast paths=" + fastCount + " mismatches=" + fastMismatch + " | PlotPath hits=" + pathHits + " computed=" + pathMisses + " (" + (pathMissTicks * f).ToString("F0") + "ms) of which unreachable=" + pathFailCount + " (" + (pathFailTicks * f).ToString("F0") + "ms)");
                pathHits = 0; pathMisses = 0; pathMissTicks = 0; pathFailCount = 0; pathFailTicks = 0;
            }
        }
        return true;
    }

    // ---- Faithful re-implementation of PathFinder.FindPath + NavigationNode.GetNeighbors for Universe.PlotPath
    // (civ-based search: travelerFleet == null, hostilesThreshold == 0, cost 1 per hop).
    // Same algorithm, same game PriorityList/Node classes (identical tie-breaking), same neighbor order and rules;
    // only values that cannot change during one search are computed once (per search / per node).
    class NodeInfo { public bool hasJG; public Civilization jgOwner; public Star star; }
    [ThreadStatic] static Dictionary<NavigationNode, NodeInfo> fInfo;
    [ThreadStatic] static List<NavigationNode> fNeigh, fCells;
    [ThreadStatic] static Dictionary<NavigationNode, int> fIdx;

    // ------------------------------------------------------------------ shared faithful A* core
    // Mirror of AStar.PathFinder.FindPath used by FastPlotPath (GalaxyMap) and StarSeparation (StarGraph): the same game
    // PriorityList<AStar.Node> and Node objects, the same push/pop sequence and f/g values, so ties are broken exactly
    // like the original. Cells are numbered by the graph; open/closed sets are mark arrays instead of hash sets (only
    // membership tests, no effect on order). Both graphs have cost 1 per hop.
    interface IFaithfulGraph
    {
        int IndexOf(AStar.ICell cell);                                   // stable number of a cell (may assign one)
        AStar.ICell Cell(int index);
        float Heuristic(int index);                                      // graph.GetHeuristic(cell, end)
        void Neighbors(int index, AStar.ICell parent, List<int> outIdx); // graph.GetNeighbors(cell, parent), same order
    }

    sealed class AStarState
    {
        public readonly PriorityList<AStar.Node> heap = new PriorityList<AStar.Node>();
        public readonly List<int> neigh = new List<int>();
        public AStar.Node[] nodes = new AStar.Node[0];
        public int[] nodeMark = new int[0], openMark = new int[0], closedMark = new int[0];
        public int mark;

        public void Ensure(int n)
        {
            if (n <= nodes.Length) return;
            int size = Math.Max(n, Math.Max(64, nodes.Length * 2));
            int old = nodes.Length;
            Array.Resize(ref nodes, size); Array.Resize(ref nodeMark, size); Array.Resize(ref openMark, size); Array.Resize(ref closedMark, size);
            for (int i = old; i < size; i++) nodes[i] = new AStar.Node();
        }

        public void Begin()
        {
            if (++mark == int.MaxValue)
            {
                Array.Clear(nodeMark, 0, nodeMark.Length); Array.Clear(openMark, 0, openMark.Length); Array.Clear(closedMark, 0, closedMark.Length);
                mark = 1;
            }
            heap.Clear();
        }

        // PathFinder.GetNode: the node of a cell, initialized on first use in this search
        public AStar.Node Node(int i, AStar.ICell cell)
        {
            Ensure(i + 1);
            AStar.Node n = nodes[i];
            if (nodeMark[i] != mark) { n.Initialize(cell, null, 0f, 0f); nodeMark[i] = mark; }
            return n;
        }
    }
    [ThreadStatic] static AStarState astar;
    static AStarState AState() { if (astar == null) astar = new AStarState(); return astar; }

    // returns true when 'end' was reached; 'last' = end, or (not reached) the cell with the smallest heuristic
    static bool FaithfulAStar<G>(ref G g, AStarState s, int start, int end, AStar.ICell startParent, out int last) where G : struct, IFaithfulGraph
    {
        s.Begin();
        PriorityList<AStar.Node> heap = s.heap;
        AStar.Node node = s.Node(start, g.Cell(start));
        node.Initialize(g.Cell(start), null, g.Heuristic(start), 0f);
        node.parent = startParent;                                       // FindPath(..., last)
        heap.Push(node);
        int best = -1;
        float bestH = float.MaxValue;
        while (heap.Count != 0)
        {
            AStar.Node node2 = null;
            int c2 = -1;
            while (heap.Count > 0)
            {
                node2 = heap.Pop();
                c2 = g.IndexOf(node2.cell);
                s.openMark[c2] = 0;
                if (s.closedMark[c2] != s.mark) break;
            }
            s.closedMark[c2] = s.mark;
            float h = g.Heuristic(c2);
            if (h < bestH) { bestH = h; best = c2; }
            if (c2 == end) { last = c2; return true; }
            List<int> nb = s.neigh;
            nb.Clear();
            g.Neighbors(c2, node2.parent, nb);
            AStar.ICell cell2 = node2.cell;
            for (int t = 0; t < nb.Count; t++)
            {
                int k = nb[t];
                s.Ensure(k + 1);
                if (s.closedMark[k] == s.mark) continue;
                AStar.Node node4 = s.Node(k, g.Cell(k));
                float cost = node2.g + 1f;
                if (s.openMark[k] != s.mark || cost < node4.g)
                {
                    node4.parent = cell2;
                    node4.g = cost;
                    node4.f = cost + g.Heuristic(k);
                    s.openMark[k] = s.mark;
                    heap.Push(node4);
                }
            }
        }
        last = best;
        return false;
    }

    // number of cells on the path start..last (follows the parents like FindPath builds outPath)
    static int FaithfulPathLength<G>(ref G g, AStarState s, int last, int start) where G : struct, IFaithfulGraph
    {
        int len = 1;
        for (int c = last; c != start; c = g.IndexOf(s.nodes[c].parent)) len++;
        return len;
    }

    static List<NavigationNode> FaithfulPath<G>(ref G g, AStarState s, int last, int start) where G : struct, IFaithfulGraph
    {
        var path = new List<NavigationNode>();
        for (int c = last; ; c = g.IndexOf(s.nodes[c].parent))
        {
            path.Add((NavigationNode)g.Cell(c));
            if (c == start) break;
        }
        path.Reverse();
        return path;
    }

    // Universe.PlotPath graph: GalaxyMap prepared for a civilization (neighbour rules in FNeighbors)
    struct GalaxyGraph : IFaithfulGraph
    {
        public GalaxyMap gm; public NavigationNode end; public Civilization owner; public bool canNavAdv, omni;
        public List<NavigationNode> cells, tmp; public Dictionary<NavigationNode, int> idx;
        public int IndexOf(AStar.ICell cell)
        {
            NavigationNode n = (NavigationNode)cell;
            int i;
            if (!idx.TryGetValue(n, out i)) { i = cells.Count; cells.Add(n); idx.Add(n, i); }
            return i;
        }
        public AStar.ICell Cell(int index) { return cells[index]; }
        public float Heuristic(int index) { return gm.GetHeuristic(cells[index], end); }
        public void Neighbors(int index, AStar.ICell parent, List<int> outIdx)
        {
            FNeighbors(cells[index], gm.destination, parent as NavigationNode, gm, owner, gm.bIgnoreBlocks, gm.bIgnoreAdvancedWarpgates, canNavAdv, omni, gm.bUseKnownData, tmp);
            for (int i = 0; i < tmp.Count; i++) outIdx.Add(IndexOf(tmp[i]));
        }
    }

    // GetStarSeparationLevels graph: StarGraph with traveler == null (neighbours precomputed per star, same order)
    struct StarSepGraph : IFaithfulGraph
    {
        public List<Star> stars; public Dictionary<Star, int> index; public int[][] neigh; public GameCore.Utils.Vec3[] pos; public int end;
        public int IndexOf(AStar.ICell cell) { return index[(Star)cell]; }
        public AStar.ICell Cell(int i) { return stars[i]; }
        public float Heuristic(int i) { return (pos[end] - pos[i]).LengthSq(); }   // StarGraph: (to.position - from.position).LengthSq()
        public void Neighbors(int i, AStar.ICell parent, List<int> outIdx) { int[] nb = neigh[i]; for (int t = 0; t < nb.Length; t++) outIdx.Add(nb[t]); }
    }

    static NodeInfo Info(NavigationNode n)
    {
        NodeInfo i;
        if (fInfo.TryGetValue(n, out i)) return i;
        i = new NodeInfo();
        i.hasJG = n.HasJumpGate();
        if (i.hasJG) i.jgOwner = n.JumpGateOwner();
        i.star = n.GetStar();
        fInfo[n] = i;
        return i;
    }

    // mirrors NavigationNode.GetNeighbors(target, last) for a GalaxyMap prepared with Prepare(civ, ...)
    static void FNeighbors(NavigationNode self, NavigationNode target, NavigationNode last, GalaxyMap gm, Civilization owner,
                           bool bIgnoreBlocks, bool bIgnoreAdv, bool bCanNavAdv, bool bOmni, bool bUseKnownData, List<NavigationNode> outList)
    {
        outList.Clear();
        if (last == null) last = gm.orbiting;
        NodeInfo si = Info(self);
        bool ownerIsAntaran = owner.GetType() == typeof(AntaranCivilization);
        List<NavigationNode> conns = self.connections;
        for (int i = 0; i < conns.Count; i++)
        {
            NavigationNode connection = conns[i];
            if (connection.GetType() == typeof(AntaranSpawnPortal) && !ownerIsAntaran) continue;
            // original: canMoveAcross = gm.orbiting == null && ...; orbiting is the start node (non-null) for civ searches
            bool canMoveAcross = gm.orbiting == null && last == gm.lastSafe && last.IsBlockedFor(owner, bUseKnownData) && last.GetStar() != connection.GetStar() && !connection.IsBlockedFor(owner, bUseKnownData);
            if (!bIgnoreBlocks && !self.CanMoveAcross(last, connection, owner, bUseKnownData) && !canMoveAcross) continue;
            NodeInfo ci = Info(connection);
            bool hasJumpGateConnection = si.hasJG && ci.hasJG && si.jgOwner == ci.jgOwner;
            bool isJumpGateConnection = false, isJumpGateConnectionFromAlly = false;
            if (hasJumpGateConnection)
            {
                int cnt = 0;
                for (int k = 0; k < conns.Count; k++) if (conns[k] == connection) cnt++;
                isJumpGateConnection = cnt == 1;
                Civilization jgo = si.jgOwner;
                isJumpGateConnectionFromAlly = owner.HasRelationshipWith(jgo) && owner.GetRelationshipWith(jgo).IsAllied;
            }
            if ((isJumpGateConnection && si.jgOwner != owner && !isJumpGateConnectionFromAlly) || (!bOmni && !connection.WasDiscovered(owner)) || (connection is Anomaly && connection != target) || (connection is AntaranSpawnPortal && connection != target))
                continue;
            bool bMovingThroughDifferentSystems = si.star != ci.star;
            if ((hasJumpGateConnection && (si.jgOwner == owner || isJumpGateConnectionFromAlly)) || bIgnoreAdv || bCanNavAdv || !bMovingThroughDifferentSystems || !(self is Warpgate) || !(connection is Warpgate) || !((Warpgate)connection).IsAdvanced())
                outList.Add(connection); // hostilesThreshold == 0 for Universe.PlotPath
        }
    }

    // mirrors Universe.PlotPath -> GalaxyMap.Prepare(civ, ...) -> PathFinder.FindPath(from, to, ..., last: from)
    static List<NavigationNode> FastPlotPath(Universe u, NavigationNode from, NavigationNode to, Civilization civ, bool bIgnoreAdv, bool bIgnoreBlocks, bool bUseKnownData)
    {
        GalaxyMap gm = u.nodePathFinder.graph as GalaxyMap;
        if (gm == null) return null; // caller falls back to the original
        gm.Prepare(civ, from, to, bIgnoreAdv, bIgnoreBlocks, bUseKnownData, false, 0f);
        if (fInfo == null)
        {
            fInfo = new Dictionary<NavigationNode, NodeInfo>(); fNeigh = new List<NavigationNode>();
            fCells = new List<NavigationNode>(); fIdx = new Dictionary<NavigationNode, int>();
        }
        fInfo.Clear(); fCells.Clear(); fIdx.Clear();
        Civilization owner = gm.traveler;
        var g = new GalaxyGraph
        {
            gm = gm, end = to, owner = owner,
            canNavAdv = owner.HasAchievement("techachievement_advanced_navigation"),
            omni = owner.IsAIControlled() || gm.bConnectionOmniscient,
            cells = fCells, tmp = fNeigh, idx = fIdx
        };
        int start = g.IndexOf(from), end = g.IndexOf(to);
        AStarState s = AState();
        int last;
        FaithfulAStar(ref g, s, start, end, from, out last);          // FindPath(..., last: from)
        return FaithfulPath(ref g, s, last, start);
    }

    [ThreadStatic] static long fastTicks, fastCount, fastMismatch;

    public static bool Pre_Universe_PlotPath(Universe self, NavigationNode from, NavigationNode to, Civilization civ, bool bIgnoreAdvancedWarpgates, bool bIgnoreBlocks, bool bUseKnownData, out List<NavigationNode> result)
    {
        result = null;
        if (Bypassed()) return false;
        if (aiDepth == 0 || dispatchDepth > 0 || from == null || to == null || civ == null) return false;
        try
        {
            if (pathCache == null) pathCache = new Dictionary<PathKey, List<NavigationNode>>();
            PathKey key = new PathKey { from = from, to = to, civ = civ, flags = (bIgnoreAdvancedWarpgates ? 1 : 0) | (bIgnoreBlocks ? 2 : 0) | (bUseKnownData ? 4 : 0) };
            List<NavigationNode> cached;
            if (pathCache.TryGetValue(key, out cached))
            {
                pathHits++;
                if (Verify && Sample())
                {
                    bypass = true;
                    List<NavigationNode> o = self.PlotPath(from, to, civ, bIgnoreAdvancedWarpgates, bIgnoreBlocks, bUseKnownData);
                    bool same = o.Count == cached.Count;
                    for (int i = 0; same && i < o.Count; i++) same = ReferenceEquals(o[i], cached[i]);
                    if (!same) Mismatch("PlotPath " + from.ID + "->" + to.ID, o.Count, cached.Count);
                    result = o;
                    return true;
                }
                result = new List<NavigationNode>(cached);
                return true;
            }
            pathMisses++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            result = FastPlotPath(self, from, to, civ, bIgnoreAdvancedWarpgates, bIgnoreBlocks, bUseKnownData);
            if (result == null)
            {
                bypass = true;
                result = self.PlotPath(from, to, civ, bIgnoreAdvancedWarpgates, bIgnoreBlocks, bUseKnownData);
            }
            else if (Verify && Sample())
            {
                bypass = true;
                List<NavigationNode> o = self.PlotPath(from, to, civ, bIgnoreAdvancedWarpgates, bIgnoreBlocks, bUseKnownData);
                bool same = o.Count == result.Count;
                for (int i = 0; same && i < o.Count; i++) same = ReferenceEquals(o[i], result[i]);
                fastCount++;
                if (!same) { fastMismatch++; Mismatch("FastPlotPath " + from.ID + "->" + to.ID + " flags=" + bIgnoreAdvancedWarpgates + bIgnoreBlocks + bUseKnownData, o.Count, result.Count); }
                result = o;
            }
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            pathMissTicks += dt;
            if (result == null || result.Count == 0 || !ReferenceEquals(result[result.Count - 1], to)) { pathFailCount++; pathFailTicks += dt; }
            if (result != null) pathCache[key] = new List<NavigationNode>(result);
            return true;
        }
        catch (Exception e) { LogEx("PlotPath", e); return false; }
    }

    public static bool Pre_Civilization_HasBuiltStructure(Civilization self, string structureKey, out bool result)
    {
        result = false;
        if (Bypassed() || scopeDepth == 0) return false;
        try
        {
            ColonyStructureType s = SerializedData<ColonyStructureType>.Get(structureKey);
            if (ReferenceEquals(s, null)) return false;
            result = GetIndex(self).built.Contains(s);
            if (Verify && Sample()) { bypass = true; bool o = self.HasBuiltStructure(structureKey); if (o != result) Mismatch("HasBuiltStructure " + structureKey, o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("HasBuiltStructure", e); return false; }
    }

    public static bool Pre_Civilization_HasBuiltUpgrade(Civilization self, string structureKey, out bool result)
    {
        result = false;
        if (Bypassed() || scopeDepth == 0) return false;
        try
        {
            ColonyStructureType s = SerializedData<ColonyStructureType>.Get(structureKey);
            if (ReferenceEquals(s, null)) return false;
            result = GetIndex(self).upgradeOf.Contains(s);
            if (Verify && Sample()) { bypass = true; bool o = self.HasBuiltUpgrade(structureKey); if (o != result) Mismatch("HasBuiltUpgrade " + structureKey, o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("HasBuiltUpgrade", e); return false; }
    }

    public static bool Pre_Civilization_HasBuildableInQueue(Civilization self, string key, Colony otherThan, out bool result)
    {
        result = false;
        if (Bypassed() || scopeDepth == 0 || key == null || otherThan == null) return false;
        try
        {
            EmpireIndex idx = GetIndex(self);
            int n;
            idx.queueCount.TryGetValue(key, out n);
            result = n >= 2 || (n == 1 && idx.queueFirstID[key] != otherThan.ID);
            if (Verify && Sample()) { bypass = true; bool o = self.HasBuildableInQueue(key, otherThan); if (o != result) Mismatch("HasBuildableInQueue " + key, o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("HasBuildableInQueue", e); return false; }
    }

    public static bool Pre_MajorCivilization_GetEmpireSecurity(MajorCivilization self, out float result)
    {
        result = 0f;
        if (Bypassed() || scopeDepth == 0) return false;
        try
        {
            if (secCache == null) secCache = new Dictionary<MajorCivilization, float>();
            if (secCache.TryGetValue(self, out result))
            {
                if (Verify && Sample()) { bypass = true; float o = self.GetEmpireSecurity(); if (o != result) Mismatch("GetEmpireSecurity", o, result); result = o; }
                return true;
            }
            bypass = true;
            result = self.GetEmpireSecurity();
            secCache[self] = result;
            return true;
        }
        catch (Exception e) { LogEx("GetEmpireSecurity", e); return false; }
    }

    public static bool Pre_Civilization_CalculateResearchPoints(Civilization self, out float result)
    {
        result = 0f;
        if (Bypassed() || scopeDepth == 0) return false;
        try
        {
            if (researchCache == null) researchCache = new Dictionary<Civilization, float>();
            if (researchCache.TryGetValue(self, out result))
            {
                if (Verify && Sample()) { bypass = true; float o = self.CalculateResearchPoints(); if (o != result) Mismatch("CalculateResearchPoints", o, result); result = o; }
                return true;
            }
            bypass = true;
            result = self.CalculateResearchPoints();
            researchCache[self] = result;
            return true;
        }
        catch (Exception e) { LogEx("CalculateResearchPoints", e); return false; }
    }

    // Tradeable (strategic) resources: Civilization.HasTradeableResource -> GetTradeableResourceCount ->
    // GetRawTradeableResourceProduction scans every colony and development (plus all deal items) and is asked once per
    // resource-requiring ship module by ShipBlueprint cost calculations, i.e. per buildable when the planet screen
    // computes production ETAs. Cached per (civilization, resource) inside UI scopes, where game state cannot change.
    struct TradeKey : IEquatable<TradeKey>
    {
        public Civilization civ; public PlanetaryResourceType res;
        public bool Equals(TradeKey o) { return ReferenceEquals(civ, o.civ) && ReferenceEquals(res, o.res); }
        public override bool Equals(object o) { return o is TradeKey && Equals((TradeKey)o); }
        public override int GetHashCode()
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(civ) * 397 ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(res);
        }
    }
    [ThreadStatic] static Dictionary<TradeKey, int> tradeCountCache;
    [ThreadStatic] static Dictionary<TradeKey, int> tradeRawCache;

    public static bool Pre_Civilization_GetTradeableResourceCount(Civilization self, PlanetaryResourceType resource, out int result)
    {
        result = 0;
        if (Bypassed() || scopeDepth == 0 || resource == null) return false;
        try
        {
            if (tradeCountCache == null) tradeCountCache = new Dictionary<TradeKey, int>();
            var k = new TradeKey { civ = self, res = resource };
            if (tradeCountCache.TryGetValue(k, out result))
            {
                if (Verify && Sample()) { bypass = true; int o = self.GetTradeableResourceCount(resource); if (o != result) Mismatch("GetTradeableResourceCount", o, result); result = o; }
                return true;
            }
            bypass = true;
            result = self.GetTradeableResourceCount(resource);
            tradeCountCache[k] = result;
            return true;
        }
        catch (Exception e) { LogEx("GetTradeableResourceCount", e); return false; }
    }

    public static bool Pre_Civilization_GetRawTradeableResourceProduction(Civilization self, PlanetaryResourceType resource, out int result)
    {
        result = 0;
        if (Bypassed() || scopeDepth == 0 || resource == null) return false;
        try
        {
            if (tradeRawCache == null) tradeRawCache = new Dictionary<TradeKey, int>();
            var k = new TradeKey { civ = self, res = resource };
            if (tradeRawCache.TryGetValue(k, out result))
            {
                if (Verify && Sample()) { bypass = true; int o = self.GetRawTradeableResourceProduction(resource); if (o != result) Mismatch("GetRawTradeableResourceProduction", o, result); result = o; }
                return true;
            }
            bypass = true;
            result = self.GetRawTradeableResourceProduction(resource);
            tradeRawCache[k] = result;
            return true;
        }
        catch (Exception e) { LogEx("GetRawTradeableResourceProduction", e); return false; }
    }

    // Citizen drag & drop (ActionUpdateProductionWorkers) makes the colony send MessageRefreshPlanet(All) (RefreshGrid,
    // SetFoodDelta), which rebuilds the whole planet screen (build options, focus options, buildings, leader, ...).
    // Moving workers only changes the job grid, the production values/ETAs and the production item, which is exactly what
    // the game's own partial refresh RefreshType.ProductionGrid redraws. Only messages sent while that action runs on the
    // client are downgraded; any other full refresh in the same frame still wins when refreshes are merged.
    [ThreadStatic] static int workerActionDepth;

    public static bool Pre_Colony_RunUpdateProductionWorkers(Colony self, ActionUpdateProductionWorkers upw)
    {
        if (Bypassed() || self.universe == null || self.universe.IsServer) return false;
        workerActionDepth++;
        try { bypass = true; self.RunUpdateProductionWorkers(upw); }
        finally { workerActionDepth--; }
        return true;
    }

    public static bool Pre_Universe_DispatchMessage(Universe self, Message msg)
    {
        if (obsMode != 0) obsMode = 0;   // UpdateObsoleteBlueprintsInProductionQueue only dispatches after its loop
        if (workerActionDepth == 0) return false;
        var m = msg as MessageRefreshPlanet;
        if (m != null && m.refreshType == MessageRefreshPlanet.RefreshType.All) m.refreshType = MessageRefreshPlanet.RefreshType.ProductionGrid;
        return false;
    }

    // ---- Colony.UpdateObsoleteBlueprintsInProductionQueue runs for every colony each time a blueprint becomes obsolete
    // (blueprint screen / research), on the server and on the client. For every queue item it recomputes
    // GetAvailableBuildables(ignoreQueue: true) only to test Contains(item). That list cannot change during the loop: with
    // ignoreQueue it ignores this colony's queue (the empire-wide checks exclude this colony), and the loop only edits this
    // colony's queue items / workInProgress. Moreover the result only matters for ship items (ShipBlueprint,
    // ShipMassProductionItem, ShipRaceProductionItem); for any other unavailable item the loop does nothing.
    //   - no ship item in the queue: availability is irrelevant -> an empty list is returned (no computation)
    //   - otherwise: computed once per call and reused
    // Only inside that loop: the method dispatches its messages after the loop, which ends the special mode.
    [ThreadStatic] static Colony obsColony;
    [ThreadStatic] static int obsMode;               // 0 off, 1 no ship items, 2 compute once
    [ThreadStatic] static List<IBuildable> obsList;
    static readonly List<IBuildable> emptyBuildables = new List<IBuildable>();   // never modified

    public static bool Pre_Colony_UpdateObsoleteBlueprintsInProductionQueue(Colony self, List<int> obsoleteIDs)
    {
        if (Bypassed()) return false;
        Colony pc = obsColony; int pm = obsMode; List<IBuildable> pl = obsList;
        bool ships = false;
        var q = self.productionQueue;
        for (int i = 0; i < q.Count && !ships; i++)
        {
            IBuildable b = q[i].Buildable;
            ships = b is ShipBlueprint || b is ShipMassProductionItem || b is ShipRaceProductionItem;
        }
        obsColony = self; obsMode = ships ? 2 : 1; obsList = null;
        try { bypass = true; self.UpdateObsoleteBlueprintsInProductionQueue(obsoleteIDs); }
        finally { obsColony = pc; obsMode = pm; obsList = pl; }
        return true;
    }

    public static bool Pre_Colony_GetAvailableBuildables(Colony self, bool ignoreQueue, out List<IBuildable> result)
    {
        result = null;
        if (Bypassed()) return false;
        if (ignoreQueue && obsMode != 0 && ReferenceEquals(self, obsColony))
        {
            if (obsMode == 1) { result = emptyBuildables; return true; }
            if (obsList == null) { bypass = true; obsList = self.GetAvailableBuildables(true); }
            result = obsList;
            return true;
        }
        if (scopeDepth == 0) return false;
        try
        {
            Dictionary<Colony, List<IBuildable>> cache;
            if (ignoreQueue) { if (buildablesCacheNQ == null) buildablesCacheNQ = new Dictionary<Colony, List<IBuildable>>(); cache = buildablesCacheNQ; }
            else { if (buildablesCacheQ == null) buildablesCacheQ = new Dictionary<Colony, List<IBuildable>>(); cache = buildablesCacheQ; }
            List<IBuildable> cached;
            if (!cache.TryGetValue(self, out cached))
            {
                bypass = true;
                cached = self.GetAvailableBuildables(ignoreQueue);
                cache[self] = new List<IBuildable>(cached);
                result = cached;
                return true;
            }
            if (Verify && Sample())
            {
                bypass = true;
                List<IBuildable> o = self.GetAvailableBuildables(ignoreQueue);
                bool same = o.Count == cached.Count;
                // ShipRaceProductionItem is re-created on every call, so compare type + identifier
                for (int i = 0; same && i < o.Count; i++)
                    same = ReferenceEquals(o[i], cached[i]) ||
                           (o[i] != null && cached[i] != null && o[i].GetType() == cached[i].GetType() && o[i].GetIdentifierAsString() == cached[i].GetIdentifierAsString());
                if (!same) Mismatch("GetAvailableBuildables", o.Count, cached.Count);
                result = o;
                return true;
            }
            result = new List<IBuildable>(cached);
            return true;
        }
        catch (Exception e) { LogEx("GetAvailableBuildables", e); return false; }
    }

    // ------------------------------------------------------------------ scope entry points
    public static bool Pre_SWFEmpireManagement_LoadColoniesData(SWFEmpireManagement self)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.LoadColoniesData(); }
        finally { Exit(); }
        return true;
    }

    public static bool Pre_SWFEmpireManagement_UpdateSpecificColony(SWFEmpireManagement self, Civilization civ, Colony colony, bool colonyFlagged)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.UpdateSpecificColony(civ, colony, colonyFlagged); }
        finally { Exit(); }
        return true;
    }

    // Research screens: every technology node asks GetTechResearchETA -> CalculateResearchPoints, which recomputes the
    // empire research output over all colonies (238 times = 4.5 s when the tech tree opened). Cache scope.
    public static bool Pre_SWFTechTree_SendTechTree(SWFTechTree self)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.SendTechTree(); }
        finally { Exit(); }
        return true;
    }

    public static bool Pre_SWFResearchManagement_SendResearchData(SWFResearchManagement self, bool isRefresh)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.SendResearchData(isRefresh); }
        finally { Exit(); }
        return true;
    }

    public static bool Pre_SWFPlanet_Refresh(SWFPlanet self)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.Refresh(); }
        finally { Exit(); }
        return true;
    }

    public static bool Pre_SWFEmpireTopBar_SendEmpireData(SWFEmpireTopBar self, SWFEmpireTopBar.DataToUpdate toUpdate)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.SendEmpireData(toUpdate); }
        finally { Exit(); }
        return true;
    }

    public static bool Pre_MajorCivilization_UpdateNotifications(MajorCivilization self)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.UpdateNotifications(); }
        finally { Exit(); }
        return true;
    }

    // Known-data refresh paths: KnownColonyData.Refresh -> GetSecurityRating -> GetEmpireSecurity (O(N)) per observer.
    public static bool Pre_NavigationNode_ReportChange(NavigationNode self)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.ReportChange(); }
        finally { Exit(); }
        return true;
    }

    public static bool Pre_Civilization_RefreshVisibleKnownNodes(Civilization self)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.RefreshVisibleKnownNodes(); }
        finally { Exit(); }
        return true;
    }

    // hooked only on the MajorCivilization override (virtual: hooking the base too would recurse)
    public static bool Pre_MajorCivilization_ScanNavigationNodesInRange(MajorCivilization self, IPositionScanner scanner)
    {
        if (Bypassed()) return false;
        Enter();
        try { bypass = true; self.ScanNavigationNodesInRange(scanner); }
        finally { Exit(); }
        return true;
    }

    // Colony.GetStructureSecurityBonus: same summation order as the original, without LINQ / list allocation.
    public static bool Pre_Colony_GetStructureSecurityBonus(Colony self, out float result)
    {
        result = 0f;
        if (Bypassed()) return false;
        try
        {
            CelestialBody placement = self.Placement;
            Civilization owner = self.Owner;
            if (placement == null || owner == null) return false;
            Star star = placement.GetStar();
            if (star == null) return false;
            float num = 0f;
            foreach (ColonyStructureType s in self.structures)
                if (s.securityBonus != 0f) num += s.SecurityBonus;
            foreach (Planet p in star.GetPlanets())
            {
                if (p == null) return false; // original would throw here; let it
                Colony c = p.Colony;
                if (c == null || c.Owner == null || c.Owner.ID != owner.ID) continue;
                foreach (ColonyStructureType s in c.structures)
                    if (s.systemSecurityBonus != 0f) num += s.systemSecurityBonus;
            }
            result = num;
            if (Verify && Sample()) { bypass = true; float o = self.GetStructureSecurityBonus(); if (o != result) Mismatch("GetStructureSecurityBonus", o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("GetStructureSecurityBonus", e); return false; }
    }

    // ------------------------------------------------------------------ Scaleform hit test (firstpass)
    // Movie.DoHitTest on the F1 movie costs ~230 ms. SFCamera.OnGUI calls it every Repaint (focus check) and
    // SFManager.HandleMouseEvent calls it on every mouse down/up. Reuse results for movies measured to be expensive.
    class HitCache { public float x, y; public int type; public bool result; public long when; public double costMs; public bool valid; public float prevX = float.NaN, prevY = float.NaN; public long restSince; public int misses; public int trueCount; }
    static readonly Dictionary<Scaleform.Movie, HitCache> hitCache = new Dictionary<Scaleform.Movie, HitCache>();
    const double ExpensiveHitMs = 20.0;
    static int f1HitTrue;
    static readonly long RestTicks = System.Diagnostics.Stopwatch.Frequency * 12 / 100; // 120 ms
    static readonly long ThrottleTicks = System.Diagnostics.Stopwatch.Frequency * 2; // reuse up to 2 s while the mouse is moving

    public static bool Pre_Movie_DoHitTest(Scaleform.Movie self, float x, float y, Scaleform.HitTestType hitTestType, out bool result)
    {
        result = false;
        if (Bypassed()) return false;
        try
        {
            HitCache c;
            if (!hitCache.TryGetValue(self, out c)) { if (hitCache.Count > 200) hitCache.Clear(); c = new HitCache(); hitCache[self] = c; }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            // The full-screen F1 movie: every measured hit test (shapes and bounds, any position) returned true at a
            // fixed ~200 ms native cost. After 3 confirmed true results, answer true without calling native code.
            if (f1HitTrue >= 3 && self is SWFEmpireManagement)
            {
                result = true;
                return true;
            }
            if (c.valid && c.costMs >= ExpensiveHitMs && c.type == (int)hitTestType)
            {
                bool samePos = c.x == x && c.y == y;
                UnityEngine.Event ev = UnityEngine.Event.current;
                bool repaint = ev != null && ev.type == UnityEngine.EventType.Repaint;
                // per-frame focus check: while the mouse moves, reuse; once it rests, compute at the resting point
                // (so a following click at that point is a cache hit)
                bool resting = false;
                if (repaint)
                {
                    if (c.prevX == x && c.prevY == y) { if (c.restSince == 0) c.restSince = now; resting = now - c.restSince >= RestTicks; }
                    else c.restSince = 0;
                    c.prevX = x; c.prevY = y;
                }
                if (samePos || (repaint && !resting && now - c.when < ThrottleTicks))
                {
                    result = c.result;
                    return true;
                }
            }
            bypass = true;
            result = self.DoHitTest(x, y, hitTestType);
            long after = System.Diagnostics.Stopwatch.GetTimestamp();
            c.costMs = (after - now) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (c.costMs >= ExpensiveHitMs && (c.misses++ & 7) == 0)
            {
                // diagnostic: is a bounds test cheap and equivalent here?
                long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bypass = true;
                bool rb = self.DoHitTest(x, y, Scaleform.HitTestType.HitTest_Bounds);
                double bms = (System.Diagnostics.Stopwatch.GetTimestamp() - b0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Log("HITDIAG " + self.GetType().Name + " at " + x + "," + y + " type=" + hitTestType + " " + c.costMs.ToString("F1") + "ms res=" + result + " | bounds " + bms.ToString("F1") + "ms res=" + rb + " ev=" + (UnityEngine.Event.current != null ? UnityEngine.Event.current.type.ToString() : "null"));
            }
            c.x = x; c.y = y; c.type = (int)hitTestType; c.result = result; c.when = after; c.valid = true;
            if (self is SWFEmpireManagement) { if (result) { if (f1HitTrue >= 0) f1HitTrue++; } else { f1HitTrue = int.MinValue; Log("HIT false on SWFEmpireManagement at " + x + "," + y + " -> always-true shortcut disabled"); } }
            return true;
        }
        catch (Exception e) { LogEx("DoHitTest", e); return false; }
    }

    // MajorCivilization.CheckDeficit only calls tutorials.ShowAdvice("advice_deficit"), a unique advice.
    // Once it was shown (showAgainDictionary[key] == false) ShowAdvice is a no-op, so the O(N) income/upkeep
    // computation (called per colony from RefreshGrid) can be skipped with identical results.
    public static bool Pre_MajorCivilization_CheckDeficit(MajorCivilization self)
    {
        if (Bypassed()) return false;
        try
        {
            bool show;
            var t = self.tutorials;
            if (t != null && t.showAgainDictionary != null && t.showAgainDictionary.TryGetValue("advice_deficit", out show) && !show)
                return true;
            if (clientSimDepth > 0 && self.universe != null && !self.universe.IsServer)
            {
                if (deficitPending == null) deficitPending = new List<MajorCivilization>();
                if (!deficitPending.Contains(self)) deficitPending.Add(self);
                deficitMerged++;
                return true;
            }
        }
        catch (Exception e) { LogEx("CheckDeficit", e); }
        return false;
    }

    // ------------------------------------------------------------------ simulation fixes (always on)
    // Colony.GetStructuresMoraleBoost: original scans all owner colonies to find the ones in the same star.
    public static bool Pre_Colony_GetStructuresMoraleBoost(Colony self, out float result)
    {
        result = 0f;
        if (Bypassed()) return false;
        try
        {
            Civilization owner = self.Owner;
            Planet planet = self.ColonizedPlanet;
            if (owner == null || planet == null) return false;
            Star star = planet.GetStar();
            if (star == null) return false;
            float num = 0f;
            foreach (ColonyStructureType s in self.structures) num += s.moraleMultiplierBonus;
            float num2 = 0f;
            int nonZero = 0;
            foreach (Planet p in star.GetPlanets())
            {
                if (p == null) continue;
                Colony c = p.Colony;
                if (c == null || !ReferenceEquals(c.Owner, owner)) continue;
                foreach (ColonyStructureType s in c.structures)
                {
                    float v = s.moraleMultiplierBonusSystem;
                    if (v != 0f) { nonZero++; num2 += v; }
                }
            }
            if (nonZero >= 3) return false; // summation order could matter -> use original
            result = num + num2;
            if (Verify && Sample()) { bypass = true; float o = self.GetStructuresMoraleBoost(); if (o != result) Mismatch("GetStructuresMoraleBoost " + planet.Name, o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("GetStructuresMoraleBoost", e); return false; }
    }

    // Awareness.Is*Mature: original builds the full available-structure list (empire-wide scans) 3x per colony per turn.
    static bool AnyAvailable(Colony c, Predicate<ColonyStructureType> pred)
    {
        foreach (ColonyStructureType s in c.Owner.knownStructures)
            if (pred(s) && c.MeetsStructureAvailabilityFilters(s) && c.IBuildableRequirementsMet(s)) return true;
        return false;
    }

    static readonly Predicate<ColonyStructureType> ProdPred = s => s.baseProductionBonus > 0f || s.productionTileBonus > 0 || s.totalProductionMultiplierBonus > 0f || s.workerProductionBonus > 0f;
    static readonly Predicate<ColonyStructureType> ResPred = s => s.baseResearchBonus > 0f || s.researchTileBonus > 0 || s.workerResearchBonus > 0f || s.totalResearchMultiplierBonus > 0f;
    static readonly Predicate<ColonyStructureType> LifePred = s => s.baseLifeSupportBonus > 0f || s.totalFoodMultiplierBonus > 0f || s.populationGrowthMultiplierBonus > 0f || s.foodTileBonus > 0 || s.maxPopulationBonus > 0f;

    public static bool Pre_Awareness_IsProductionMature(Colony c, out bool result) { return Mature(c, ProdPred, 0, out result); }
    public static bool Pre_Awareness_IsResearchMature(Colony c, out bool result) { return Mature(c, ResPred, 1, out result); }
    public static bool Pre_Awareness_IsLifeMature(Colony c, out bool result) { return Mature(c, LifePred, 2, out result); }

    static bool Mature(Colony c, Predicate<ColonyStructureType> pred, int which, out bool result)
    {
        result = false;
        if (Bypassed()) return false;
        try
        {
            result = !AnyAvailable(c, pred);
            if (Verify && Sample())
            {
                bypass = true;
                bool o = which == 0 ? AI.Awareness.IsProductionMature(c) : which == 1 ? AI.Awareness.IsResearchMature(c) : AI.Awareness.IsLifeMature(c);
                if (o != result) Mismatch("IsMature" + which, o, result);
                result = o;
            }
            return true;
        }
        catch (Exception e) { LogEx("IsMature", e); return false; }
    }

    // ------------------------------------------------------------------ UI refresh coalescing (main thread)
    class PlanetPending { public bool all; public MessageRefreshPlanet.RefreshType flags; public int planetID; public Civilization civ; }
    class EmpPending { public MessageRefreshEmpire full; public Dictionary<int, MessageRefreshPlanet> planets = new Dictionary<int, MessageRefreshPlanet>(); public List<int> order = new List<int>(); }

    static readonly Dictionary<SWFPlanet, PlanetPending> planetPending = new Dictionary<SWFPlanet, PlanetPending>();
    static readonly Dictionary<SWFEmpireTopBar, int> topPending = new Dictionary<SWFEmpireTopBar, int>();
    static readonly HashSet<SWFEmpireBottomBar> bottomPending = new HashSet<SWFEmpireBottomBar>();
    static readonly Dictionary<SWFEmpireManagement, EmpPending> empPending = new Dictionary<SWFEmpireManagement, EmpPending>();
    static bool flushing;

    public static bool Pre_SWFPlanet_OnMessage(SWFPlanet self, Message message)
    {
        if (Bypassed()) return false;
        try
        {
            MessageRefreshPlanet m = message as MessageRefreshPlanet;
            if (m == null || self.planet == null || m.planetID != self.planet.ID) return false;
            PlanetPending p;
            if (!planetPending.TryGetValue(self, out p)) { p = new PlanetPending(); planetPending[self] = p; }
            p.planetID = m.planetID;
            p.civ = m.relevantCivilization;
            if (m.refreshType == MessageRefreshPlanet.RefreshType.All) p.all = true; else p.flags |= m.refreshType;
            return true;
        }
        catch (Exception e) { LogEx("SWFPlanet.OnMessage", e); return false; }
    }

    static int TopBit(string id)
    {
        switch (id)
        {
            case "UpdateEmpireData": return (int)SWFEmpireTopBar.DataToUpdate.ALL;
            case "UpdateEmpireCredits": return (int)SWFEmpireTopBar.DataToUpdate.CREDITS;
            case "UpdateEmpireResearch": return (int)SWFEmpireTopBar.DataToUpdate.RESEARCH;
            case "UpdateEmpireStage": return (int)SWFEmpireTopBar.DataToUpdate.STAGE;
            case "UpdateEmpireTurn": return (int)SWFEmpireTopBar.DataToUpdate.TURN;
            case "UpdateResources": return (int)SWFEmpireTopBar.DataToUpdate.RESOURCES;
        }
        return -1;
    }

    public static bool Pre_SWFEmpireTopBar_OnMessage(SWFEmpireTopBar self, Message message)
    {
        if (Bypassed()) return false;
        try
        {
            if (message == null || message.GetType() != typeof(Message)) return false;
            int bit = TopBit(message.identifier);
            if (bit < 0) return false;
            int mask;
            topPending.TryGetValue(self, out mask);
            topPending[self] = mask | (1 << bit);
            return true;
        }
        catch (Exception e) { LogEx("SWFEmpireTopBar.OnMessage", e); return false; }
    }

    public static bool Pre_SWFEmpireBottomBar_OnMessage(SWFEmpireBottomBar self, Message message)
    {
        if (Bypassed()) return false;
        try
        {
            if (message == null || message.GetType() != typeof(Message) || message.identifier != "RefreshNotifications") return false;
            bottomPending.Add(self);
            return true;
        }
        catch (Exception e) { LogEx("SWFEmpireBottomBar.OnMessage", e); return false; }
    }

    public static bool Pre_SWFEmpireManagement_OnMessage(SWFEmpireManagement self, Message msg)
    {
        if (Bypassed()) return false;
        try
        {
            MessageRefreshEmpire re = msg as MessageRefreshEmpire;
            MessageRefreshPlanet rp = msg as MessageRefreshPlanet;
            if (re == null && rp == null) return false;
            EmpPending p;
            if (!empPending.TryGetValue(self, out p)) { p = new EmpPending(); empPending[self] = p; }
            if (re != null) { p.full = re; return true; }
            MessageRefreshPlanet prev;
            if (p.planets.TryGetValue(rp.planetID, out prev))
                p.planets[rp.planetID] = new MessageRefreshPlanet(rp.planetID, rp.relevantCivilization, prev.refreshType | rp.refreshType);
            else { p.planets[rp.planetID] = rp; p.order.Add(rp.planetID); }
            return true;
        }
        catch (Exception e) { LogEx("SWFEmpireManagement.OnMessage", e); return false; }
    }

    // Diagnostic channel from patched ASRoot.OnEnterFrame: "MOOPERF:<CheckTooltips ms>:<CheckScrollableObjects ms>"
    public static bool Pre_SWFMovie_PlaySound(SWFMovie self, string soundKey, string callee)
    {
        if (soundKey != null && soundKey.StartsWith("MOOPERF"))
        {
            Log("FLASH " + self.GetType().Name + " " + soundKey);
            return true;
        }
        return false;
    }

    public static bool Pre_SWFMovie_OnRemoved(SWFMovie self, UIState byState) { Forget(self); return false; }
    public static bool Pre_SWFEmpireManagement_OnRemoved(SWFEmpireManagement self, UIState byState) { Forget(self); return false; }

    static void Forget(SWFMovie m)
    {
        try
        {
            SWFPlanet sp = m as SWFPlanet; if (sp != null) planetPending.Remove(sp);
            SWFEmpireTopBar tb = m as SWFEmpireTopBar; if (tb != null) topPending.Remove(tb);
            SWFEmpireBottomBar bb = m as SWFEmpireBottomBar; if (bb != null) bottomPending.Remove(bb);
            SWFEmpireManagement em = m as SWFEmpireManagement; if (em != null) empPending.Remove(em);
        }
        catch { }
    }

    // Main-thread stall monitor (log only): a gap of >= 250 ms between two LateUpdate calls means the UI did not render
    // for that long. Server state changes are logged as well, so the log shows the timeline of a turn.
    static long lastLateUpdate;
    static int lastServerState = -1;

    static void MonitorFrame()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        int st = -1;
        try { var srv = Server.Get(); if (srv != null) st = (int)srv.GetState(); } catch { }
        if (lastLateUpdate != 0)
        {
            double gap = (now - lastLateUpdate) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (gap >= 250)
            {
                string scan = scanSkipped + scanEvaluated > 100000 ? " | node scans skipped " + scanSkipped + " / evaluated " + scanEvaluated : "";
                Log("FRAME stall " + gap.ToString("F0") + "ms (server " + (st < 0 ? "-" : ((ServerState)st).ToString()) + ")" + scan);
                if (scan.Length > 0) { scanSkipped = 0; scanEvaluated = 0; }
            }
        }
        if (st != lastServerState)
        {
            if (lastServerState >= 0 || st >= 0) Log("SERVER state " + (st < 0 ? "-" : ((ServerState)st).ToString()));
            lastServerState = st;
        }
        lastLateUpdate = now;
    }

    // ---- Bottom bar notifications: VisualActionsSequencer processes a chunk of queued turn-result messages in one
    // frame and calls SWFEmpireBottomBar.RefreshNotifications for every MessageNotificationReceived. Each call rebuilds
    // the whole list in Flash (0.26-0.6 s with many notifications), so a battle or an event that produces ~100
    // notifications froze the screen for 17-20 s. Refreshes requested while one chunk is processed are merged into a
    // single refresh at the end of the chunk (same final list).
    static int seqDepth, notifDeferred;
    static bool notifPending, notifSound;

    public static bool Pre_VisualActionsSequencer_ProcessQueueableMessages(VisualActionsSequencer self, out bool result)
    {
        result = false;
        if (Bypassed()) return false;
        seqDepth++;
        try { bypass = true; result = self.ProcessQueueableMessages(); }
        finally
        {
            if (--seqDepth == 0 && notifPending)
            {
                bool snd = notifSound; int n = notifDeferred;
                notifPending = false; notifSound = false; notifDeferred = 0;
                try
                {
                    UIState st = UIManager.Get().GetCurrentState();
                    SWFEmpireBottomBar bar = st != null ? st.GetSWF<SWFEmpireBottomBar>() : null;
                    if (bar != null)
                    {
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        bypass = true; bar.RefreshNotifications(snd);
                        if (n > 1) Log("NOTIF " + n + " notification refreshes merged into 1 (" + ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + "ms)");
                    }
                }
                catch (Exception e) { LogEx("notification flush", e); }
            }
        }
        return true;
    }

    public static bool Pre_SWFEmpireBottomBar_RefreshNotifications(SWFEmpireBottomBar self, bool playSound)
    {
        if (Bypassed()) return false;
        if (seqDepth == 0) return false;
        notifPending = true; notifSound |= playSound; notifDeferred++;
        return true;
    }

    // ---- Radar range outline: RadarZone.RefreshAll unions every colony/development/fleet scanner circle (Clipper) and
    // rebuilds the meshes: 0.55-0.7 s with ~350 colonies. It is requested whenever one of the player's fleet animations
    // ends, i.e. in many consecutive frames while fleets move during turn processing. Requests are debounced: the
    // rebuild runs once requests stop for 0.5 s, and at the latest 2 s after the first one (same final outline).
    static readonly Dictionary<RadarZone, long[]> radarPending = new Dictionary<RadarZone, long[]>(); // [first, last]
    static int radarMerged;

    public static bool Pre_RadarZone_RefreshAll(RadarZone self)
    {
        if (Bypassed()) return false;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long[] t;
        if (radarPending.TryGetValue(self, out t)) { t[1] = now; radarMerged++; }
        else radarPending[self] = new long[] { now, now };
        return true;
    }

    static void FlushRadar()
    {
        if (radarPending.Count == 0) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        List<RadarZone> due = null;
        foreach (var kv in radarPending)
            if (now - kv.Value[1] >= f / 2 || now - kv.Value[0] >= f * 2) { if (due == null) due = new List<RadarZone>(); due.Add(kv.Key); }
        if (due == null) return;
        foreach (RadarZone rz in due)
        {
            radarPending.Remove(rz);
            if ((UnityEngine.Object)rz == null) continue;   // destroyed
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bypass = true; rz.RefreshAll();
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / f;
                if (radarMerged > 0 || ms >= 100) Log("RADAR rebuilt once for " + (radarMerged + 1) + " requests (" + ms.ToString("F0") + "ms)");
            }
            catch (Exception e) { LogEx("radar flush", e); }
        }
        radarMerged = 0;
    }

    public static bool Pre_UIManager_LateUpdate(UIManager self)
    {
        try { MonitorFrame(); } catch { }
        try { FlushRadar(); } catch (Exception e) { LogEx("radar", e); }
        if (flushing) return false;
        if (planetPending.Count == 0 && topPending.Count == 0 && bottomPending.Count == 0 && empPending.Count == 0) return false;
        flushing = true;
        Enter();
        try
        {
            if (planetPending.Count > 0)
            {
                var list = new List<KeyValuePair<SWFPlanet, PlanetPending>>(planetPending);
                planetPending.Clear();
                foreach (var kv in list)
                {
                    try
                    {
                        SWFPlanet sp = kv.Key; PlanetPending p = kv.Value;
                        if (sp.planet == null || sp.planet.ID != p.planetID) continue;
                        UIState cur = UIManager.Get().GetCurrentState();
                        if (cur == null || !cur.movies.Contains(sp)) continue;
                        MessageRefreshPlanet.RefreshType t = p.all ? MessageRefreshPlanet.RefreshType.All : p.flags;
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        bypass = true;
                        sp.OnMessage(new MessageRefreshPlanet(p.planetID, p.civ, t));
                        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        if (ms >= 5) Log("PLANET refresh " + t + " " + ms.ToString("F0") + "ms");
                    }
                    catch (Exception e) { LogEx("flush planet", e); }
                }
            }
            if (empPending.Count > 0)
            {
                var list = new List<KeyValuePair<SWFEmpireManagement, EmpPending>>(empPending);
                empPending.Clear();
                foreach (var kv in list)
                {
                    SWFEmpireManagement em = kv.Key; EmpPending p = kv.Value;
                    try { if (p.full != null) { bypass = true; em.OnMessage(p.full); } }
                    catch (Exception e) { LogEx("flush empire full", e); }
                    foreach (int id in p.order)
                    {
                        try { bypass = true; em.OnMessage(p.planets[id]); }
                        catch (Exception e) { LogEx("flush empire planet", e); }
                    }
                }
            }
            // top/bottom bar last: the flushes above may have queued more top bar updates
            if (topPending.Count > 0)
            {
                var list = new List<KeyValuePair<SWFEmpireTopBar, int>>(topPending);
                topPending.Clear();
                foreach (var kv in list)
                {
                    try
                    {
                        int mask = kv.Value;
                        if ((mask & (1 << (int)SWFEmpireTopBar.DataToUpdate.ALL)) != 0) kv.Key.SendEmpireData(SWFEmpireTopBar.DataToUpdate.ALL);
                        else
                            for (int b = (int)SWFEmpireTopBar.DataToUpdate.CREDITS; b < (int)SWFEmpireTopBar.DataToUpdate.ALL; b++)
                                if ((mask & (1 << b)) != 0) kv.Key.SendEmpireData((SWFEmpireTopBar.DataToUpdate)b);
                    }
                    catch (Exception e) { LogEx("flush topbar", e); }
                }
                topPending.Clear(); // anything queued by SendEmpireData itself is already covered
            }
            if (bottomPending.Count > 0)
            {
                var list = new List<SWFEmpireBottomBar>(bottomPending);
                bottomPending.Clear();
                foreach (var bb in list)
                {
                    try { bb.RefreshNotifications(); }
                    catch (Exception e) { LogEx("flush bottombar", e); }
                }
            }
        }
        catch (Exception e) { LogEx("flush", e); }
        finally { Exit(); flushing = false; }
        return false;
    }

    // ------------------------------------------------------------------ overlapped turn simulation (experimental)
    // Single player runs the turn simulation twice, one after the other: the server thread simulates
    // (Universe.ServerFinishTurn), then broadcasts ProcessingComplete with its checksum, and only then the client (main
    // thread) re-simulates its own copy and compares checksums (Client.AdvanceSimulation).
    // The server broadcasts AdvanceUniverseTurn right before it starts simulating, when the client already has every
    // action of the turn. With moofix_overlap.txt present, the client starts its simulation on that message, so both
    // simulations run at the same time; on ProcessingComplete only the checksums are compared. A mismatch (or an
    // exception) goes through the game's own single-player recovery (clone of the server universe).
    // Shared static state the two simulations would race on is serialized: checksum calculation (static CheckSum
    // helpers in many classes) and RandomEvent.Create (static pastEvents list).
    static readonly object checksumLock = new object();
    static readonly object randomEventLock = new object();
    static Universe ovUniverse;           // main thread only
    static bool ovReady, ovFailed;
    static int ovChecksum, ovTurn;
    static double ovMs;
    static List<string> ovBetween = new List<string>();
    [ThreadStatic] static bool ovSkipSim, ovForceChecksum;
    [ThreadStatic] static int ovForcedValue;

    public static bool Pre_Universe_RunUniverseAction(Universe self, GameAction action)
    {
        if (!Overlap || self.IsServer) return false;
        if (Bypassed()) return false;
        var nt = action as ActionNewTurn;
        if (ovReady && (nt == null || nt.status != Universe.ServerStatus.ProcessingComplete))
            ovBetween.Add(nt != null ? "NewTurn." + nt.status : action.GetType().Name);
        if (nt == null || nt.status != Universe.ServerStatus.AdvanceUniverseTurn) return false;
        bypass = true; self.RunUniverseAction(action);   // original handling first (UI status message)
        try { StartEarlySimulation(self); } catch (Exception e) { LogEx("overlap start", e); }
        return true;
    }

    static void StartEarlySimulation(Universe u)
    {
        var client = Client.Get();
        if (client == null || client.OutOfSync || client.universe != u || !NetworkLayer.Get().IsSinglePlayer()) return;
        if (!OverlapThisTurn(u.turn)) { Log("OVERLAP off for turn " + u.turn + " (sequential, for comparison)"); return; }
        ovReady = false; ovFailed = false; ovBetween.Clear();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            u.AdvanceSimulation();
            ovChecksum = CheckSum.CalculateCheckSum(u);
        }
        catch (Exception e) { LogEx("overlap simulation", e); ovFailed = true; }
        ovMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        ovUniverse = u; ovTurn = u.turn; ovReady = true;
    }

    // Re-read every turn: the file may be deleted to turn overlapping off without restarting (the locks stay active,
    // which is harmless). If it contains "alternate", only even turns overlap, so consecutive turns can be compared.
    static bool OverlapThisTurn(int turn)
    {
        try
        {
            string f = Path.Combine(docDir, "moofix_overlap.txt");
            if (!File.Exists(f)) return false;
            if (File.ReadAllText(f).IndexOf("alternate", StringComparison.OrdinalIgnoreCase) >= 0) return turn % 2 == 0;
            return true;
        }
        catch { return false; }
    }

    public static bool Pre_Client_AdvanceSimulation(Client self, int serverChecksum)
    {
        if (Bypassed()) return false;
        if (!ovReady)
        {   // normal game path; timed for comparison when the overlap feature is installed
            if (!Overlap) return false;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bypass = true; self.AdvanceSimulation(serverChecksum);
            Log("OVERLAP off: client simulation after the server took " + ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + "ms");
            return true;
        }
        ovReady = false;
        string between = ovBetween.Count == 0 ? "none" : string.Join(",", ovBetween.ToArray());
        ovBetween.Clear();
        if (self.universe != ovUniverse || self.universe.turn != ovTurn)
        {   // the early result belongs to another universe/turn (e.g. a game was loaded): discard it
            Log("OVERLAP discarded stale early simulation");
            return false;
        }
        if (!ovFailed && ovChecksum == serverChecksum)
        {
            Log("OVERLAP turn " + ovTurn + " client simulation " + ovMs.ToString("F0") + "ms ran alongside the server; checksum ok; actions in between: " + between);
            return true;
        }
        Log("OVERLAP turn " + ovTurn + (ovFailed ? " early simulation failed" : " CHECKSUM MISMATCH") + "; actions in between: " + between + " -> game recovery");
        // run the original for its mismatch/recovery branch without simulating a second time
        ovSkipSim = true; ovForceChecksum = true; ovForcedValue = ovFailed ? ~serverChecksum : ovChecksum;
        try { bypass = true; self.AdvanceSimulation(serverChecksum); }
        finally { ovSkipSim = false; ovForceChecksum = false; }
        return true;
    }

    public static bool Pre_Universe_AdvanceSimulation(Universe self)
    {
        if (Bypassed()) return false;
        if (ovSkipSim) { ovSkipSim = false; return true; }
        if (Overlap && self.IsServer)
        {   // timing only, for the log
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bypass = true; self.AdvanceSimulation();
            Log("OVERLAP server simulation " + ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + "ms (turn " + self.turn + ")");
            return true;
        }
        if (!self.IsServer)
        {   // client simulation: deficit advice checks are merged (see Pre_MajorCivilization_CheckDeficit)
            clientSimDepth++;
            try { bypass = true; self.AdvanceSimulation(); }
            finally { if (--clientSimDepth == 0) FlushDeficitChecks(); }
            return true;
        }
        return false;
    }

    // ---- Deficit advice during the client's turn simulation: Colony.RefreshGrid (client only, once per colony whose
    // workers change) calls Owner.CheckDeficit(), which recomputes the whole empire income (O(colonies)) only to decide
    // whether to show the "deficit" advice (UI). 300+ calls per turn = O(N^2), 1.7 s on the main thread. While the
    // client simulates, the checks are recorded and run once per civilization when the simulation ends. Game state is
    // not affected (advice only); the advice now reflects the end-of-turn balance instead of intermediate states.
    [ThreadStatic] static int clientSimDepth;
    [ThreadStatic] static List<MajorCivilization> deficitPending;
    [ThreadStatic] static int deficitMerged;

    static void FlushDeficitChecks()
    {
        if (deficitPending == null || deficitPending.Count == 0) return;
        var list = new List<MajorCivilization>(deficitPending);
        int n = deficitMerged;
        deficitPending.Clear(); deficitMerged = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (MajorCivilization c in list)
        {
            try { c.CheckDeficit(); } catch (Exception e) { LogEx("deficit flush", e); }
        }
        Log("DEFICIT " + n + " advice checks during the simulation merged into " + list.Count + " (" + ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + "ms)");
    }

    public static bool Pre_CheckSum_CalculateCheckSum(IChecksum cs, out int result)
    {
        result = 0;
        if (Bypassed()) return false;
        if (ovForceChecksum) { ovForceChecksum = false; result = ovForcedValue; return true; }
        if (!Overlap) return false;
        lock (checksumLock) { bypass = true; result = CheckSum.CalculateCheckSum(cs); }
        return true;
    }

    public static bool Pre_RandomEvent_Create(Backend.RandomEvents.RandomEvent self, Universe localUniverse, int randomChance,
        int occurrenceChance, Civilization civ, Colony colony, out Backend.RandomEvents.RandomEventBase result)
    {
        result = null;
        if (!Overlap) return false;
        if (Bypassed()) return false;
        lock (randomEventLock) { bypass = true; result = self.Create(localUniverse, randomChance, occurrenceChance, civ, colony); }
        return true;
    }

    // ------------------------------------------------------------------ scanner node detection
    // Civilization.ScanNavigationNodesInRange(scanner) evaluates EVERY star of the galaxy plus every discovered node of
    // every explored star for one scanner (colony / development / fleet). Called for all scanners at game load, on
    // espionage "reveal colonies" (UpdateOverallVisibility) etc.: ~1,800 scanners x thousands of nodes = 12.6 M
    // visibility tests (several seconds, twice: server and client universe).
    // EvaluateScanDetection(scanner, valid, node) only acts when "visible" != "already detected by this scanner", and
    // "visible" always requires scanner.DetectPosition(...) (a pure distance test). So a node that is clearly out of the
    // scanner's range and not detected by it is a no-op and is skipped. Every other node goes through the original
    // EvaluateScanDetection, in the original order. Borderline distances (float precision) are never skipped.
    // Full re-implementation of the loop (never calls the original: the MajorCivilization override calls base).
    public static bool Pre_Civilization_ScanNavigationNodesInRange(Civilization self, IPositionScanner scanner)
    {
        if (Bypassed()) return false;
        Universe u = self.universe;
        if (!u.isCreated) return true;
        bool isValidScanner = !scanner.IsDeleted() && scanner.GetOwner().ID == self.ID;
        float range = -1f;                                   // < 0: unknown scanner type, no filtering
        GameCore.Utils.Vec3 sp = default(GameCore.Utils.Vec3);
        try
        {
            Settlement st = scanner as Settlement;
            Fleet fl = scanner as Fleet;
            if (st != null && st.Placement != null) { range = st.GetScannerRange(); sp = st.Placement.Position; }
            else if (fl != null) { range = fl.GetScannerRange(); sp = fl.Position; }
        }
        catch (Exception e) { range = -1f; if (!scanSetupErrorLogged) { scanSetupErrorLogged = true; LogEx("scan filter setup (filtering disabled for this scanner)", e); } }
        if (range < 0f || float.IsNaN(range) || float.IsInfinity(range)) range = -1f;

        foreach (Star star in u.Stars)
            ScanOne(self, scanner, isValidScanner, star, range, sp, true);
        foreach (Star exploredStar in self.ExploredStars)
        {
            ScanOne(self, scanner, isValidScanner, exploredStar, range, sp, true);
            // original: foreach child in Childs (= children list); foreach ep in GetAllInterstelarEndPoints()
            // (= connections.Where(m => m.GetStar() != child.GetStar())). Same lists, same order, without the
            // per-child LINQ enumerator allocations (1,800 scanners x every discovered child).
            List<NavigationNode> kids = exploredStar.children;
            for (int ci = 0; ci < kids.Count; ci++)
            {
                NavigationNode child = kids[ci];
                if (!child.WasDiscovered(self)) continue;
                ScanOne(self, scanner, isValidScanner, child, range, sp, child is Star);
                List<NavigationNode> conns = child.connections;
                Star childStar = child.GetStar();
                for (int k = 0; k < conns.Count; k++)
                {
                    NavigationNode ep = conns[k];
                    if (ep.GetStar() != childStar) ScanOne(self, scanner, isValidScanner, ep, range, sp, ep is Star);
                }
            }
        }
        foreach (Anomaly anomaly in u.anomalies)
        {
            if (!anomaly.consumed) ScanOne(self, scanner, isValidScanner, anomaly, range, sp, false);
        }
        return true;
    }

    static long scanSkipped, scanEvaluated;
    static bool scanSetupErrorLogged;

    static void ScanOne(Civilization self, IPositionScanner scanner, bool valid, NavigationNode node, float range, GameCore.Utils.Vec3 sp, bool isStar)
    {
        if (range >= 0f && node != null)
        {
            bool skip = false;
            try
            {
                float thr; float dist;
                if (isStar)
                {   // original: scanner.DetectPosition(star) = (range + detectionDistance) * multiplier >= |pos - detectionPos|
                    thr = (range + node.GetDetectionDistance()) * node.GetDetectionRangeMultiplier();
                    dist = (sp - node.GetDetectionPosition()).Length();
                }
                else
                {   // original: scanner.DetectPosition(body.Position) = range >= |pos - body.Position|
                    thr = range;
                    dist = (sp - node.Position).Length();
                }
                skip = dist > thr * 1.0001f + 0.01f && !node.IsAlreadyDetectedBy(scanner);
            }
            catch { skip = false; }   // anything unexpected: evaluate with the original code
            if (skip)
            {
                if (Verify && Sample() && node is CelestialBody && valid && self.IsCelestialBodyVisibleBy(node as CelestialBody, scanner))
                    Mismatch("ScanNavigationNodesInRange skip", true, false);
                scanSkipped++;
                return;
            }
        }
        scanEvaluated++;
        self.EvaluateScanDetection(scanner, valid, node);
    }

    // ------------------------------------------------------------------ star separation matrix (load)
    // SpatialData (built in Universe.PostInit, i.e. on every load, for the client and the server universe) fills an
    // all-pairs matrix with Universe.GetStarSeparationLevels(a, b): one A* search per ordered pair (~62,500 for 250
    // stars) through StarGraph (cost 1 per hop, heuristic = squared Euclidean distance, which is NOT admissible, so the
    // result is not the shortest path and cannot be replaced by BFS). Faithful re-implementation of
    // PathFinder.FindPath for this graph: same game PriorityList/Node classes and push/pop sequence (identical
    // tie-breaking), same neighbour order (Star.GetNeighbouringStars(null), computed once per star instead of once per
    // expansion), arrays instead of hash sets. The result also fills Universe's separation cache exactly like the
    // original (civ == null entries). Values are computed first; nothing is written if anything fails (original runs).
    // The search itself is the shared FaithfulAStar core (StarSepGraph).

    public static bool Pre_SpatialData_BuildStarsTravelDistance(SpatialData self, Universe u)
    {
        if (Bypassed()) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int n; int[] values; bool[] isNew; List<Star> stars;
        StarGraph sg;
        try
        {
            sg = u.starPathFinder.graph as StarGraph;
            if (sg == null) return false;
            stars = u.Stars;
            n = stars.Count;
            var index = new Dictionary<Star, int>(n);
            for (int i = 0; i < n; i++) { if (stars[i] == null || index.ContainsKey(stars[i])) return false; index.Add(stars[i], i); }
            var neigh = new int[n][];
            var tmp = new List<int>();
            for (int i = 0; i < n; i++)
            {
                tmp.Clear();
                foreach (Star s in stars[i].GetNeighbouringStars(null))
                {
                    int k;
                    if (s == null || !index.TryGetValue(s, out k)) return false;   // unexpected topology: original
                    tmp.Add(k);
                }
                neigh[i] = tmp.ToArray();
            }
            var pos = new GameCore.Utils.Vec3[n];
            for (int i = 0; i < n; i++) pos[i] = stars[i].Position;
            // The key's Equals compares hash codes only, so different pairs can collide. The original checks the cache
            // before every pair, so a colliding pair gets the value of the earlier pair and is not added. Mirror that
            // sequential behaviour on a private copy (same comparer); the real cache is only written below.
            var cache = new Dictionary<Universe.GetStarSeparationLevelsParams, int>(u.cachGetStarSeparationCache);
            values = new int[n * n]; isNew = new bool[n * n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    int v;
                    if (stars[i].ID == stars[j].ID) v = 0;
                    else
                    {
                        var key = new Universe.GetStarSeparationLevelsParams(stars[i], stars[j]);
                        if (!cache.TryGetValue(key, out v))
                        {
                            v = StarSeparation(stars, index, neigh, pos, i, j);
                            cache.Add(key, v);
                            isNew[i * n + j] = true;
                        }
                    }
                    values[i * n + j] = v;
                }
            if (Verify)
            {
                int checkedPairs = 0, bad = 0;
                sg.traveler = null; sg.bAvailableConnections = false;
                for (int p = 0; p < n * n; p += 16)
                {
                    if (!isNew[p]) continue;
                    int o = OriginalStarSeparation(u, stars[p / n], stars[p % n]);
                    checkedPairs++;
                    if (o != values[p]) { bad++; Mismatch("StarSeparation " + stars[p / n].ID + "->" + stars[p % n].ID, o, values[p]); }
                }
                Log("STARDIST verify: " + checkedPairs + " pairs compared with the original A*, " + bad + " mismatches");
            }
        }
        catch (Exception e) { LogEx("BuildStarsTravelDistance", e); return false; }

        // apply (same effects as the original loop; the keys marked new cannot collide with each other or the cache)
        var dict = u.cachGetStarSeparationCache;
        bool any = false;
        self.starsTravelDistance = new int[n, n];
        self.maxSystemSeparation = 0;
        try
        {
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    int v = values[i * n + j];
                    if (isNew[i * n + j]) { dict.Add(new Universe.GetStarSeparationLevelsParams(stars[i], stars[j]), v); any = true; }
                    self.starsTravelDistance[i, j] = v;
                    self.maxSystemSeparation = UnityEngine.Mathf.Max(self.maxSystemSeparation, v);
                }
        }
        catch (Exception e) { LogEx("BuildStarsTravelDistance apply", e); throw; }
        if (any) { sg.traveler = null; sg.bAvailableConnections = false; }
        Client.Log("For a system with " + n + " stars, max travel distance is " + self.maxSystemSeparation);
        Log("STARDIST " + n + " stars: separation matrix in " + ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + "ms");
        return true;
    }

    // ---- Territory: SpatialData.BuildTerritory (twice per turn on server and client, also on structure changes) asks
    // NavigationNode.GetConnectedStars() ~260,000 times; every call enumerates the node's connections and allocates a new
    // list. The star topology does not change during BuildTerritory (it only rewrites claim flags), and its three call
    // sites only iterate the result, so the list is computed once per node for the duration of one BuildTerritory.
    [ThreadStatic] static int territoryDepth;
    [ThreadStatic] static Dictionary<NavigationNode, List<Star>> connStarsMemo;

    public static bool Pre_SpatialData_BuildTerritory(SpatialData self, Universe u)
    {
        if (Bypassed()) return false;
        if (connStarsMemo == null) connStarsMemo = new Dictionary<NavigationNode, List<Star>>();
        territoryDepth++;
        try { bypass = true; self.BuildTerritory(u); }
        finally { if (--territoryDepth == 0) connStarsMemo.Clear(); }
        return true;
    }

    public static bool Pre_NavigationNode_GetConnectedStars(NavigationNode self, out List<Star> result)
    {
        result = null;
        if (Bypassed() || territoryDepth == 0) return false;
        if (connStarsMemo.TryGetValue(self, out result))
        {
            if (Verify && Sample())
            {
                bypass = true; List<Star> o = self.GetConnectedStars();
                bool same = o.Count == result.Count;
                for (int i = 0; same && i < o.Count; i++) same = ReferenceEquals(o[i], result[i]);
                if (!same) { Mismatch("GetConnectedStars", o.Count, result.Count); connStarsMemo[self] = o; result = o; }
            }
            return true;
        }
        bypass = true;
        result = self.GetConnectedStars();
        connStarsMemo[self] = result;
        return true;
    }

    // ---- Timeline turn data (TimelineManager.PushTurn at every new turn, ~1 s on the main thread). Same code as the
    // original GetTurnData, except the compression: LZ4Codec.Encode32 instead of Encode32HC (high compression).
    // Both produce the same LZ4 block format that TimelineManager decodes with LZ4Codec.Decode32, so old and new saves
    // load the same way. Measured in game: HC took 474 ms of ~0.9 s; the data stored per turn becomes somewhat larger.
    // Timing of both phases is logged; the server lock is released in a finally block.
    public static bool Pre_TimelineManager_GetTurnData(TimelineManager self, out TurnData result)
    {
        result = null;
        if (Bypassed()) return false;
        bool flag = Client.Get().IsSinglePlayer();
        if (!flag) flag = Client.Get().IsHost();
        long f = System.Diagnostics.Stopwatch.Frequency;
        if (flag) Client.LockServer();
        try
        {
            TurnData turnData = new TurnData();
            Universe source = (!flag) ? Client.GetUniverse() : Server.GetUniverse();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ObjectBase.bSerializingGame = true;
            MemoryStream memoryStream = GameSerializer.ToStream(source);
            ObjectBase.bSerializingGame = false;
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            turnData.compressedData = LZ4s.LZ4Codec.Encode32(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
            turnData.uncompressedSize = (int)memoryStream.Length;
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            Log("TIMELINE turn data: serialize " + ((t1 - t0) * 1000.0 / f).ToString("F0") + "ms (" + (memoryStream.Length / 1048576.0).ToString("F1") + " MB), LZ4 fast " + ((t2 - t1) * 1000.0 / f).ToString("F0") + "ms (" + (turnData.compressedData.Length / 1048576.0).ToString("F1") + " MB)");
            result = turnData;
        }
        finally
        {
            ObjectBase.bSerializingGame = false;
            if (flag) Client.UnlockServer();
        }
        return true;
    }

    // mirrors Universe.GetStarSeparationLevels(a, b, civ: null) -> PathFinder.FindPath(a, b, ...) with StarGraph
    static int StarSeparation(List<Star> stars, Dictionary<Star, int> index, int[][] neigh, GameCore.Utils.Vec3[] pos, int start, int end)
    {
        var g = new StarSepGraph { stars = stars, index = index, neigh = neigh, pos = pos, end = end };
        AStarState s = AState();
        int last;
        bool found = FaithfulAStar(ref g, s, start, end, null, out last);   // FindPath(..., last: null)
        int len = FaithfulPathLength(ref g, s, last, start);
        // GetStarSeparationLevels: not found and the path does not end in the target's system -> -1
        if (!found && ((NavigationNode)stars[last]).Root != stars[end]) return -1;
        return len - 1;
    }

    // the original computation without touching Universe's cache (verification only)
    static int OriginalStarSeparation(Universe u, Star a, Star b)
    {
        List<AStar.ICell> outPath; float c; int it;
        if (!u.starPathFinder.FindPath(a, b, out outPath, out c, out it) && ((NavigationNode)outPath[outPath.Count - 1]).Root != b) return -1;
        if (outPath.Count > 0) return outPath.Count - 1;
        return -1;
    }
}
