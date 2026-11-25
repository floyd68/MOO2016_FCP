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
    static readonly object logLock = new object();
    static StreamWriter log;
    static int mismatchCount;

    static MooFix()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Master of Orion");
            Directory.CreateDirectory(dir);
            Verify = File.Exists(Path.Combine(dir, "moofix_verify.txt"));
            log = new StreamWriter(Path.Combine(dir, "moofix_log.txt"), true);
            log.AutoFlush = true;
        }
        catch { }
        Log("MooFix loaded. verify=" + Verify);
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
    [ThreadStatic] static PriorityList<AStar.Node> fHeap;
    [ThreadStatic] static List<AStar.Node> fPool;
    [ThreadStatic] static Dictionary<AStar.ICell, AStar.Node> fNodes;
    [ThreadStatic] static HashSet<AStar.ICell> fOpen, fClosed;
    [ThreadStatic] static Dictionary<NavigationNode, NodeInfo> fInfo;
    [ThreadStatic] static List<NavigationNode> fNeigh;
    [ThreadStatic] static int fPoolIdx;

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

    static AStar.Node FNode(AStar.ICell cell)
    {
        AStar.Node v;
        if (fNodes.TryGetValue(cell, out v)) return v;
        if (fPoolIdx >= fPool.Count) fPool.Add(new AStar.Node());
        v = fPool[fPoolIdx++];
        v.Initialize(cell, null, 0f, 0f);
        fNodes.Add(cell, v);
        return v;
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
        if (fHeap == null)
        {
            fHeap = new PriorityList<AStar.Node>(); fPool = new List<AStar.Node>(); fNodes = new Dictionary<AStar.ICell, AStar.Node>();
            fOpen = new HashSet<AStar.ICell>(); fClosed = new HashSet<AStar.ICell>(); fInfo = new Dictionary<NavigationNode, NodeInfo>(); fNeigh = new List<NavigationNode>();
        }
        fPoolIdx = 0; fHeap.Clear(); fOpen.Clear(); fClosed.Clear(); fNodes.Clear(); fInfo.Clear();
        Civilization owner = gm.traveler;
        bool bCanNavAdv = owner.HasAchievement("techachievement_advanced_navigation");
        bool bOmni = owner.IsAIControlled() || gm.bConnectionOmniscient;
        AStar.ICell start = from, end = to;
        List<AStar.ICell> outPath = new List<AStar.ICell>();

        if (fPoolIdx >= fPool.Count) fPool.Add(new AStar.Node());
        AStar.Node node = fPool[fPoolIdx++];
        node.Initialize(start, null, gm.GetHeuristic(start, end), 0f);
        node.parent = from; // FindPath(..., last: from)
        fNodes.Add(start, node);
        fHeap.Push(node);
        AStar.ICell best = null;
        float bestH = float.MaxValue;
        while (fHeap.Count != 0)
        {
            AStar.ICell cell2 = null;
            AStar.Node node2 = null;
            while (fHeap.Count > 0)
            {
                node2 = fHeap.Pop();
                cell2 = node2.cell;
                fOpen.Remove(cell2);
                if (!fClosed.Contains(cell2)) break;
            }
            fClosed.Add(cell2);
            float h = gm.GetHeuristic(cell2, end);
            if (h < bestH) { bestH = h; best = cell2; }
            if (cell2.Equals(end))
            {
                AStar.Node node3 = node2;
                outPath.Add(node3.cell);
                while (node3.cell != start)
                {
                    node3 = FNode(node3.parent);
                    outPath.Add(node3.cell);
                }
                outPath.Reverse();
                return ToNav(outPath);
            }
            FNeighbors(cell2 as NavigationNode, gm.destination, node2.parent as NavigationNode, gm, owner, gm.bIgnoreBlocks, gm.bIgnoreAdvancedWarpgates, bCanNavAdv, bOmni, gm.bUseKnownData, fNeigh);
            // copy: FNeighbors list is reused
            for (int i = 0; i < fNeigh.Count; i++)
            {
                AStar.ICell nc = fNeigh[i];
                if (fClosed.Contains(nc)) continue;
                AStar.Node node4 = FNode(nc);
                float g = node2.g + 1f;
                if (!fOpen.Contains(nc) || g < node4.g)
                {
                    node4.parent = cell2;
                    node4.g = g;
                    node4.f = g + gm.GetHeuristic(nc, end);
                    fOpen.Add(nc);
                    fHeap.Push(node4);
                }
            }
        }
        AStar.ICell c4 = best;
        outPath.Add(c4);
        while (c4 != start)
        {
            c4 = FNode(c4).parent;
            outPath.Add(c4);
        }
        outPath.Reverse();
        return ToNav(outPath);
    }

    static List<NavigationNode> ToNav(List<AStar.ICell> l)
    {
        var r = new List<NavigationNode>(l.Count);
        for (int i = 0; i < l.Count; i++) r.Add((NavigationNode)l[i]);
        return r;
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

    public static bool Pre_Colony_GetAvailableBuildables(Colony self, bool ignoreQueue, out List<IBuildable> result)
    {
        result = null;
        if (Bypassed() || scopeDepth == 0) return false;
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

    public static bool Pre_UIManager_LateUpdate(UIManager self)
    {
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
                        bypass = true;
                        sp.OnMessage(new MessageRefreshPlanet(p.planetID, p.civ, t));
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
}
