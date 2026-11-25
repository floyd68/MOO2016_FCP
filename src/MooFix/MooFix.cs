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
        if (valCache != null) valCache.Clear();
    }

    // ---- Per-settlement values that KnownColonyData.Refresh / KnownSettlementData.Refresh copy into every observer's
    // known data (credit output, upkeep, security rating, orbital defense rating): inside a cache scope (no game state
    // changes) the same settlement is evaluated once per observing civilization. Memoized per settlement and scope.
    sealed class SetVals { public byte has; public float credit, creditNoTrade, upkeep, sec, orb; }
    [ThreadStatic] static Dictionary<Settlement, SetVals> valCache;
    [ThreadStatic] static long valHits, valMisses;

    static SetVals Vals(Settlement s)
    {
        if (valCache == null) valCache = new Dictionary<Settlement, SetVals>();
        SetVals v;
        if (!valCache.TryGetValue(s, out v)) { v = new SetVals(); valCache.Add(s, v); }
        return v;
    }

    // bit: 1 credit, 2 creditNoTrade, 4 upkeep, 8 security, 16 orbital defense
    static bool MemoFloat(Settlement s, byte bit, out float result)
    {
        result = 0f;
        SetVals v = Vals(s);
        if ((v.has & bit) == 0) return false;
        switch (bit) { case 1: result = v.credit; break; case 2: result = v.creditNoTrade; break; case 4: result = v.upkeep; break; case 8: result = v.sec; break; default: result = v.orb; break; }
        valHits++;
        return true;
    }
    static void StoreFloat(Settlement s, byte bit, float value)
    {
        SetVals v = Vals(s);
        switch (bit) { case 1: v.credit = value; break; case 2: v.creditNoTrade = value; break; case 4: v.upkeep = value; break; case 8: v.sec = value; break; default: v.orb = value; break; }
        v.has |= bit;
        valMisses++;
    }

    public static bool Pre_Colony_GetCreditOutput(Colony self, bool bIgnoreTradeGoods, out float result)
    {
        result = 0f;
        if (Bypassed() || scopeDepth == 0) return false;
        try
        {
            byte bit = bIgnoreTradeGoods ? (byte)2 : (byte)1;
            if (MemoFloat(self, bit, out result))
            {
                if (Verify && Sample()) { bypass = true; float o = self.GetCreditOutput(bIgnoreTradeGoods); if (o != result) Mismatch("GetCreditOutput", o, result); result = o; }
                return true;
            }
            bypass = true;
            result = self.GetCreditOutput(bIgnoreTradeGoods);
            StoreFloat(self, bit, result);
            return true;
        }
        catch (Exception e) { LogEx("GetCreditOutput", e); return false; }
    }

    public static bool Pre_Colony_GetUpkeepValue(Colony self, out float result)
    {
        result = 0f;
        if (Bypassed() || scopeDepth == 0) return false;
        try
        {
            if (MemoFloat(self, 4, out result))
            {
                if (Verify && Sample()) { bypass = true; float o = self.GetUpkeepValue(); if (o != result) Mismatch("GetUpkeepValue", o, result); result = o; }
                return true;
            }
            bypass = true;
            result = self.GetUpkeepValue();
            StoreFloat(self, 4, result);
            return true;
        }
        catch (Exception e) { LogEx("GetUpkeepValue", e); return false; }
    }

    public static bool Pre_Colony_GetSecurityRating(Colony self, out float result)
    {
        result = 0f;
        if (Bypassed() || scopeDepth == 0) return false;
        try
        {
            if (MemoFloat(self, 8, out result))
            {
                if (Verify && Sample()) { bypass = true; float o = self.GetSecurityRating(); if (o != result) Mismatch("GetSecurityRating", o, result); result = o; }
                return true;
            }
            bypass = true;
            result = self.GetSecurityRating();
            StoreFloat(self, 8, result);
            return true;
        }
        catch (Exception e) { LogEx("GetSecurityRating", e); return false; }
    }

    public static bool Pre_Settlement_GetOrbitalDefenseRating(Settlement self, out float result)
    {
        result = 0f;
        if (Bypassed() || scopeDepth == 0) return false;
        try
        {
            if (MemoFloat(self, 16, out result))
            {
                if (Verify && Sample()) { bypass = true; float o = self.GetOrbitalDefenseRating(); if (o != result) Mismatch("GetOrbitalDefenseRating", o, result); result = o; }
                return true;
            }
            bypass = true;
            result = self.GetOrbitalDefenseRating();
            StoreFloat(self, 16, result);
            return true;
        }
        catch (Exception e) { LogEx("GetOrbitalDefenseRating", e); return false; }
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
        if (pathCache != null) pathCache.Clear(); ClearPathState();
        ClearRatingCache();
        dispatchDepth++;
        try { bypass = true; self.DispatchAction(action); }
        finally
        {
            dispatchDepth--;
            if (pathCache != null) pathCache.Clear(); ClearPathState();
            ClearRatingCache();
            if (scopeDepth > 0) ClearCaches();
        }
        return true;
    }

    // ---- AI planet rating: Awareness.GetPlanetRating(planet) is evaluated for every known planet twice per
    // T_ColonizeWithinVicinity.HandleColonyShips (GetPlanetsToColonize, then Average) and again by the strategy code.
    // Same validity rules as the path cache (AI stage only, cleared before/after every action, never filled while an
    // action runs). It also depends on AI-internal state that changes without actions: Awareness.empire (UpdateEmpire),
    // star occurrences (CalculateStarCentrality), blackboard victory condition / independent allies
    // (SP_GeneralStrategy.RefreshTargetVictoryCondition / RefreshIndependentCivilizations) -> those clear the cache too.
    // The colony ship blueprint (AI blueprint updates) is part of the key.
    struct RatingKey : IEquatable<RatingKey>
    {
        public object aw, planet, bp; public bool shared;
        public bool Equals(RatingKey o) { return ReferenceEquals(aw, o.aw) && ReferenceEquals(planet, o.planet) && ReferenceEquals(bp, o.bp) && shared == o.shared; }
        public override bool Equals(object o) { return o is RatingKey && Equals((RatingKey)o); }
        public override int GetHashCode()
        {
            unchecked { return (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(planet) * 397 ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(aw)) * 31 ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bp) ^ (shared ? 1 : 0); }
        }
    }
    [ThreadStatic] static Dictionary<RatingKey, float> ratingCache;
    [ThreadStatic] static long ratingHits, ratingMisses, ratingMismatch;
    static void ClearRatingCache()
    {
        if (ratingCache != null && ratingCache.Count > 0) ratingCache.Clear();
        if (closestCache != null && closestCache.Count > 0) closestCache.Clear();
        if (pactCache != null && pactCache.Count > 0) pactCache.Clear();
    }

    // ---- Awareness.GetClosestLocationInEmpireTo(target): loops over every star of the AI's empire with
    // GetStarSeparationLevels for each planet rating. Depends only on the target's star, the empire set (rebuilt in
    // UpdateEmpire, which clears this cache) and game state; same validity rules as the planet ratings.
    struct ClosestKey : IEquatable<ClosestKey>
    {
        public object aw, star;
        public bool Equals(ClosestKey o) { return ReferenceEquals(aw, o.aw) && ReferenceEquals(star, o.star); }
        public override bool Equals(object o) { return o is ClosestKey && Equals((ClosestKey)o); }
        public override int GetHashCode() { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(aw) * 397 ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(star); }
    }
    [ThreadStatic] static Dictionary<ClosestKey, NavigationNode> closestCache;

    public static bool Pre_Awareness_GetClosestLocationInEmpireTo(AI.Awareness self, NavigationNode target, out NavigationNode result)
    {
        result = null;
        if (Bypassed()) return false;
        if (aiDepth == 0 || dispatchDepth > 0 || target == null) return false;
        try
        {
            Star st = target.GetStar();
            if (st == null) return false;
            if (closestCache == null) closestCache = new Dictionary<ClosestKey, NavigationNode>();
            var k = new ClosestKey { aw = self, star = st };
            if (closestCache.TryGetValue(k, out result))
            {
                if (Verify && Sample()) { bypass = true; NavigationNode o = self.GetClosestLocationInEmpireTo(target); if (!ReferenceEquals(o, result)) Mismatch("GetClosestLocationInEmpireTo", o == null ? "null" : o.Name, result == null ? "null" : result.Name); result = o; }
                return true;
            }
            bypass = true;
            result = self.GetClosestLocationInEmpireTo(target);
            closestCache[k] = result;
            return true;
        }
        catch (Exception e) { LogEx("GetClosestLocationInEmpireTo", e); return false; }
    }

    // ---- MajorCivilization.CheckIfPlanetWouldBeUnderNonSettlementPact: the last test builds, for every planet asked,
    // the set "undominated neighbours of the stars dominated by this civilization" from all colonies (LINQ, O(colonies)).
    // That set does not depend on the planet: built once per civilization during the AI stage (same validity rules
    // as the planet ratings); the other tests run unchanged.
    [ThreadStatic] static Dictionary<MajorCivilization, HashSet<Star>> pactCache;

    static HashSet<Star> PactNeighbours(MajorCivilization self)
    {
        if (pactCache == null) pactCache = new Dictionary<MajorCivilization, HashSet<Star>>();
        HashSet<Star> set;
        if (pactCache.TryGetValue(self, out set)) return set;
        var dominated = new HashSet<Star>();
        foreach (Colony c in self.colonies)
        {
            Star m = c.Placement.GetStar();
            if (m.GetDominantCivilization() != null && m.GetDominantCivilization().ID == self.ID) dominated.Add(m);
        }
        set = new HashSet<Star>();
        foreach (Star m in dominated)
            foreach (Star n in m.GetNeighbouringStars())
                if (n.GetDominantCivilization() == null) set.Add(n);
        pactCache[self] = set;
        return set;
    }

    public static bool Pre_MajorCivilization_CheckIfPlanetWouldBeUnderNonSettlementPact(MajorCivilization self, Planet planet, out bool result)
    {
        result = false;
        if (Bypassed()) return false;
        if (aiDepth == 0 || dispatchDepth > 0 || planet == null) return false;
        try
        {
            bool r;
            if (!planet.IsColonizable(self)) r = false;
            else
            {
                r = false;
                Star star = planet.GetStar();
                if (self.claimedStars.Contains(star)) r = true;
                else
                {
                    Civilization d = star.GetDominantCivilization();
                    if (d != null && d.ID == self.ID) r = true;
                }
                if (self.HaSettlementInStarWarpgates(planet.GetStar())) r = true;
                if (star != null && PactNeighbours(self).Contains(star)) r = true;
            }
            result = r;
            if (Verify && Sample()) { bypass = true; bool o = self.CheckIfPlanetWouldBeUnderNonSettlementPact(planet); if (o != result) Mismatch("CheckIfPlanetWouldBeUnderNonSettlementPact", o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("CheckIfPlanetWouldBeUnderNonSettlementPact", e); return false; }
    }

    public static bool Pre_Awareness_GetPlanetRating(AI.Awareness self, Planet p, bool considerSharedSystem, out float result)
    {
        result = 0f;
        if (Bypassed()) return false;
        if (aiDepth == 0 || dispatchDepth > 0 || p == null) return false;
        try
        {
            if (ratingCache == null) ratingCache = new Dictionary<RatingKey, float>();
            var k = new RatingKey { aw = self, planet = p, bp = self.GetColonyShipBlueprint(), shared = considerSharedSystem };
            if (ratingCache.TryGetValue(k, out result))
            {
                ratingHits++;
                if (Verify && Sample())
                {
                    bypass = true; float o = self.GetPlanetRating(p, considerSharedSystem);
                    if (!(o == result || (float.IsNaN(o) && float.IsNaN(result)))) { ratingMismatch++; Mismatch("GetPlanetRating", o, result); }
                    result = o;
                }
                return true;
            }
            bypass = true;
            result = self.GetPlanetRating(p, considerSharedSystem);
            ratingCache[k] = result;
            ratingMisses++;
            return true;
        }
        catch (Exception e) { LogEx("GetPlanetRating", e); return false; }
    }

    // ---- T_Engage.BoostPriorityOfBlockingSettlements: for every engage target it plots a path and, for every node of
    // that path, scanned the whole engage target list again (engageTargets.Where(m => m.node == n)) = O(T^2 * L).
    // Same code with a node -> targets index built once (targets in list order; the loop only changes priority values,
    // never the list or the nodes; NavigationNode has no == overload, so the original comparison is by reference).
    public static bool Pre_T_Engage_BoostPriorityOfBlockingSettlements(AI.T_Engage self)
    {
        if (Bypassed()) return false;
        Civilization myCiv = self.myCiv;
        AI.Blackboard bb = self.bb;
        NavigationNode colonizedPlanet = myCiv.GetMainColony().ColonizedPlanet;
        List<EngageTarget> targets = bb.engageTargets;
        var byNode = new Dictionary<NavigationNode, List<EngageTarget>>();
        for (int i = 0; i < targets.Count; i++)
        {
            EngageTarget t = targets[i];
            if (t.node == null) continue;            // never equal to a path node
            List<EngageTarget> l;
            if (!byNode.TryGetValue(t.node, out l)) { l = new List<EngageTarget>(); byNode.Add(t.node, l); }
            l.Add(t);
        }
        foreach (EngageTarget engageTarget in targets)
        {
            if (engageTarget.type != EngageType.ColonyDestroy && engageTarget.type != EngageType.ColonyInvadeOrDestroy && engageTarget.type != EngageType.ColonyInvadeOnly && engageTarget.type != EngageType.Blockade)
                continue;
            List<NavigationNode> list = myCiv.universe.PlotPath(colonizedPlanet, engageTarget.node, myCiv, true, true, false);
            foreach (NavigationNode n in list)
            {
                List<EngageTarget> atNode;
                if (n == null || !byNode.TryGetValue(n, out atNode)) continue;
                foreach (EngageTarget item2 in atNode)
                {
                    Settlement settlementFrom = n.GetSettlementFrom(item2.civ);
                    if (settlementFrom != null && settlementFrom.HaveBlockingStructure() && settlementFrom.Placement.Blocker != null && settlementFrom.Placement.Blocker != myCiv)
                    {
                        if (item2.priority < engageTarget.priority) item2.priority = engageTarget.priority;
                        item2.priorityBias++;
                    }
                }
            }
        }
        bb.engageTargets = self.GetSortedEngageTargets(bb.engageTargets);
        return true;
    }

    // ---- AI travel regions (NavigationNode.CacheCanTravelRegions, at the start of every AI pass): timing only. The
    // profiling build showed 2.2 s, but these are recursive methods and the profiler wraps every recursive call.
    [ThreadStatic] static long regionTicks;
    [ThreadStatic] static int regionCalls;

    public static bool Pre_NavigationNode_CacheCanTravelRegions(Civilization civ)
    {
        if (Bypassed()) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { bypass = true; NavigationNode.CacheCanTravelRegions(civ); }
        finally { regionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; regionCalls++; }
        return true;
    }

    // AI-internal state changes: drop the ratings before and after
    public static bool Pre_Awareness_UpdateEmpire(AI.Awareness self)
    {
        if (Bypassed()) return false;
        ClearRatingCache();
        try { bypass = true; self.UpdateEmpire(); } finally { ClearRatingCache(); }
        return true;
    }

    public static bool Pre_Awareness_CalculateStarCentrality(AI.Awareness self)
    {
        if (Bypassed()) return false;
        ClearRatingCache();
        try { bypass = true; self.CalculateStarCentrality(); } finally { ClearRatingCache(); }
        return true;
    }

    public static bool Pre_SP_GeneralStrategy_RefreshIndependentCivilizations(AI.SP_GeneralStrategy self)
    {
        if (Bypassed()) return false;
        ClearRatingCache();
        try { bypass = true; self.RefreshIndependentCivilizations(); } finally { ClearRatingCache(); }
        return true;
    }

    public static bool Pre_SP_GeneralStrategy_RefreshTargetVictoryCondition(AI.SP_GeneralStrategy self)
    {
        if (Bypassed()) return false;
        ClearRatingCache();
        try { bypass = true; self.RefreshTargetVictoryCondition(); } finally { ClearRatingCache(); }
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
                if (pathCache != null) pathCache.Clear(); ClearPathState();
                ClearRatingCache();
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double aiMs = (System.Diagnostics.Stopwatch.GetTimestamp() - aiStartTicks) * f;
                if (aiMs > 50 || pathHits + pathMisses > 0)
                    Log("AI RunAI " + aiMs.ToString("F0") + "ms | verified fast paths=" + fastCount + " mismatches=" + fastMismatch + " | PlotPath hits=" + pathHits + " computed=" + pathMisses + " (" + (pathMissTicks * f).ToString("F0") + "ms) of which unreachable=" + pathFailCount + " (" + (pathFailTicks * f).ToString("F0") + "ms) | planet ratings hits=" + ratingHits + " computed=" + ratingMisses + (Verify ? " mismatches=" + ratingMismatch : "") + " | travel regions " + regionCalls + "x " + (regionTicks * f).ToString("F0") + "ms");
                pathHits = 0; pathMisses = 0; pathMissTicks = 0; pathFailCount = 0; pathFailTicks = 0; ratingHits = 0; ratingMisses = 0; regionTicks = 0; regionCalls = 0;
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
    static void ClearPathState() { if (fInfo != null) { fInfo.Clear(); fCells.Clear(); fIdx.Clear(); } }

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

    // Search state per cell number. The game's PriorityList<AStar.Node> is a binary heap of Node references compared
    // with Node.CompareTo (f only) through Comparer<Node>.Default; a node can be in the heap several times and its f
    // is changed while it is in the heap. This heap stores cell numbers, compares the cells' current f values with the
    // same comparison and performs exactly the same push / pop / swap sequence, so the pop order is identical -
    // without object comparers, interface calls and Node objects.
    sealed class AStarState
    {
        public int[] heap = new int[64];
        public int heapCount;
        public readonly List<int> neigh = new List<int>();
        public float[] f = new float[0], g = new float[0];
        public int[] par = new int[0];                                   // parent cell, -1 = none, -2 = start parent
        public int[] nodeMark = new int[0], openMark = new int[0], closedMark = new int[0];
        public int mark;

        public void Ensure(int n)
        {
            if (n <= f.Length) return;
            int size = Math.Max(n, Math.Max(64, f.Length * 2));
            Array.Resize(ref f, size); Array.Resize(ref g, size); Array.Resize(ref par, size);
            Array.Resize(ref nodeMark, size); Array.Resize(ref openMark, size); Array.Resize(ref closedMark, size);
        }

        public void Begin()
        {
            if (++mark == int.MaxValue)
            {
                Array.Clear(nodeMark, 0, nodeMark.Length); Array.Clear(openMark, 0, openMark.Length); Array.Clear(closedMark, 0, closedMark.Length);
                mark = 1;
            }
            heapCount = 0;
        }

        // PathFinder.GetNode: the node of a cell, initialized on first use in this search
        public void Node(int i)
        {
            Ensure(i + 1);
            if (nodeMark[i] != mark) { f[i] = 0f; g[i] = 0f; par[i] = -1; nodeMark[i] = mark; }
        }

        // Node.CompareTo: 1 if f > other, -1 if f < other, else 0
        int Cmp(int i, int j)
        {
            float a = f[heap[i]], b = f[heap[j]];
            if (a > b) return 1;
            if (a < b) return -1;
            return 0;
        }

        public void Push(int cell)                                       // PriorityList.Push
        {
            if (heapCount == heap.Length) Array.Resize(ref heap, heap.Length * 2);
            int num = heapCount;
            heap[heapCount++] = cell;
            while (num != 0)
            {
                int num2 = (num - 1) / 2;
                if (Cmp(num, num2) < 0) { int t = heap[num]; heap[num] = heap[num2]; heap[num2] = t; num = num2; continue; }
                break;
            }
        }

        public int Pop()                                                 // PriorityList.Pop
        {
            int result = heap[0];
            int num = 0;
            heap[0] = heap[heapCount - 1];
            heapCount--;
            while (true)
            {
                int num2 = num, num3 = 2 * num + 1, num4 = 2 * num + 2;
                if (heapCount > num3 && Cmp(num, num3) > 0) num = num3;
                if (heapCount > num4 && Cmp(num, num4) > 0) num = num4;
                if (num == num2) break;
                int t = heap[num]; heap[num] = heap[num2]; heap[num2] = t;
            }
            return result;
        }
    }
    [ThreadStatic] static AStarState astar;
    static AStarState AState() { if (astar == null) astar = new AStarState(); return astar; }

    // returns true when 'end' was reached; 'last' = end, or (not reached) the cell with the smallest heuristic
    static bool FaithfulAStar<G>(ref G g, AStarState s, int start, int end, AStar.ICell startParent, out int last) where G : struct, IFaithfulGraph
    {
        s.Begin();
        s.Node(start);
        s.f[start] = g.Heuristic(start); s.g[start] = 0f;
        s.par[start] = -2;                                               // FindPath(..., last)
        s.Push(start);
        int best = -1;
        float bestH = float.MaxValue;
        List<int> nb = s.neigh;
        while (s.heapCount != 0)
        {
            int c2 = -1;
            while (s.heapCount > 0)
            {
                c2 = s.Pop();
                s.openMark[c2] = 0;
                if (s.closedMark[c2] != s.mark) break;
            }
            s.closedMark[c2] = s.mark;
            float h = g.Heuristic(c2);
            if (h < bestH) { bestH = h; best = c2; }
            if (c2 == end) { last = c2; return true; }
            nb.Clear();
            int pc = s.par[c2];
            g.Neighbors(c2, pc == -2 ? startParent : pc < 0 ? null : g.Cell(pc), nb);
            float cost = s.g[c2] + 1f;
            for (int t = 0; t < nb.Count; t++)
            {
                int k = nb[t];
                s.Ensure(k + 1);
                if (s.closedMark[k] == s.mark) continue;
                s.Node(k);
                if (s.openMark[k] != s.mark || cost < s.g[k])
                {
                    s.par[k] = c2;
                    s.g[k] = cost;
                    s.f[k] = cost + g.Heuristic(k);
                    s.openMark[k] = s.mark;
                    s.Push(k);
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
        for (int c = last; c != start; c = s.par[c]) len++;
        return len;
    }

    static List<NavigationNode> FaithfulPath<G>(ref G g, AStarState s, int last, int start) where G : struct, IFaithfulGraph
    {
        var path = new List<NavigationNode>();
        for (int c = last; ; c = s.par[c])
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
        // node info (jump gate, star) and cell numbering are kept for the whole AI stage: they only change through
        // actions or turn simulation, and are dropped together with the path cache (ClearPathState)
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

    // ---- Empire security across cache scopes during colony processing (turn simulation).
    // Every colony's OnTurn ends with Placement.ReportChange(), a cache scope of its own, so GetEmpireSecurity (an O(colonies)
    // walk over all structures) was recomputed ~1,000 times per turn on each side. Inside
    // Civilization.ProcessSettlementsOnTurn (the colony/development OnTurn loop) the value is cached per civilization
    // together with a generation number. The generation is increased before and after every change of its inputs that
    // can happen there: structures added/demolished (Settlement.AddStructure, Settlement.DemolishStructure overloads,
    // bombardment), colonies gained/lost (Civilization.OnColonyOwned/OnColonyLost incl. overrides), owner changes
    // (Settlement/Colony.ChangeOwner). Leaders, technologies, espionage missions and difficulty change outside this loop.
    // Verification mode compares samples with a fresh computation.
    [ThreadStatic] static int settleDepth;
    [ThreadStatic] static long secGen;
    [ThreadStatic] static Dictionary<MajorCivilization, SecEntry> secGenCache;
    [ThreadStatic] static long secGenHits, secGenMisses;
    class SecEntry { public long gen; public float value; }

    public static bool Pre_Civilization_ProcessSettlementsOnTurn(Civilization self)
    {
        if (Bypassed()) return false;
        settleDepth++; secGen++;
        try { bypass = true; self.ProcessSettlementsOnTurn(); }
        finally
        {
            secGen++;
            if (--settleDepth == 0 && secGenCache != null) secGenCache.Clear();
        }
        return true;
    }

    [ThreadStatic] static int inAddStructure, inDemolish, inChangeOwner, inColOwned, inColLost, inBombard;

    public static bool Pre_Settlement_AddStructure(Settlement self, ColonyStructureType structure)
    {
        if (Bypassed()) return false;
        if (settleDepth == 0) return false;
        if (inAddStructure > 0) { secGen++; return false; }
        inAddStructure++; secGen++;
        try { bypass = true; self.AddStructure(structure); } finally { inAddStructure--; secGen++; }
        return true;
    }

    public static bool Pre_Settlement_DemolishStructure(Settlement self, string structureKey, Settlement.LostReason reason, Backend.RandomEvents.RandomEventBase randomEvent)
    {
        if (Bypassed()) return false;
        if (settleDepth == 0) return false;
        if (inDemolish > 0) { secGen++; return false; }
        inDemolish++; secGen++;
        try { bypass = true; self.DemolishStructure(structureKey, reason, randomEvent); } finally { inDemolish--; secGen++; }
        return true;
    }

    public static bool Pre_Settlement_DemolishStructure2(Settlement self, string structureKey, Settlement.LostReason reason, Civilization remover, List<Fleet> attackers)
    {
        if (Bypassed()) return false;
        if (settleDepth == 0) return false;
        if (inDemolish > 0) { secGen++; return false; }
        inDemolish++; secGen++;
        try { bypass = true; self.DemolishStructure(structureKey, reason, remover, attackers); } finally { inDemolish--; secGen++; }
        return true;
    }

    // protected virtual DemolishStructure(5 args) and its overrides: bump only (cannot be re-called from here)
    public static bool Pre_Settlement_DemolishStructureCore(Settlement self, string structureKey, Settlement.LostReason reason, Civilization remover, List<Fleet> attackers, Backend.RandomEvents.RandomEventBase randomEvent)
    { if (settleDepth > 0) secGen++; return false; }
    public static bool Pre_Colony_DemolishStructureCore(Colony self, string structureKey, Settlement.LostReason reason, Civilization remover, List<Fleet> attackers, Backend.RandomEvents.RandomEventBase randomEvent)
    { if (settleDepth > 0) secGen++; return false; }
    public static bool Pre_NodeDevelopment_DemolishStructureCore(NodeDevelopment self, string structureKey, Settlement.LostReason reason, Civilization remover, List<Fleet> attackers, Backend.RandomEvents.RandomEventBase randomEvent)
    { if (settleDepth > 0) secGen++; return false; }

    // virtual ChangeOwner: the outermost hook wraps the virtual call; inner hooks (base called from the override) only bump
    static bool WrapChangeOwner(Settlement self, Civilization newOwner, Settlement.LostReason reason, List<Fleet> invasorFleets)
    {
        if (Bypassed()) return false;
        if (settleDepth == 0) return false;
        if (inChangeOwner > 0) { secGen++; return false; }
        inChangeOwner++; secGen++;
        try { bypass = true; self.ChangeOwner(newOwner, reason, invasorFleets); } finally { inChangeOwner--; secGen++; }
        return true;
    }
    public static bool Pre_Settlement_ChangeOwner(Settlement self, Civilization newOwner, Settlement.LostReason reason, List<Fleet> invasorFleets)
    { return WrapChangeOwner(self, newOwner, reason, invasorFleets); }
    public static bool Pre_Colony_ChangeOwner(Colony self, Civilization newOwner, Settlement.LostReason reason, List<Fleet> invasorFleets)
    { return WrapChangeOwner(self, newOwner, reason, invasorFleets); }

    static bool WrapColonyOwned(Civilization self, Colony col)
    {
        if (Bypassed()) return false;
        if (settleDepth == 0) return false;
        if (inColOwned > 0) { secGen++; return false; }
        inColOwned++; secGen++;
        try { bypass = true; self.OnColonyOwned(col); } finally { inColOwned--; secGen++; }
        return true;
    }
    public static bool Pre_Civilization_OnColonyOwned(Civilization self, Colony col) { return WrapColonyOwned(self, col); }
    public static bool Pre_MajorCivilization_OnColonyOwned(MajorCivilization self, Colony col) { return WrapColonyOwned(self, col); }

    static bool WrapColonyLost(Civilization self, Colony col, Settlement.LostReason reason, List<Fleet> attackerFleets)
    {
        if (Bypassed()) return false;
        if (settleDepth == 0) return false;
        if (inColLost > 0) { secGen++; return false; }
        inColLost++; secGen++;
        try { bypass = true; self.OnColonyLost(col, reason, attackerFleets); } finally { inColLost--; secGen++; }
        return true;
    }
    public static bool Pre_Civilization_OnColonyLost(Civilization self, Colony col, Settlement.LostReason reason, List<Fleet> attackerFleets) { return WrapColonyLost(self, col, reason, attackerFleets); }
    public static bool Pre_MajorCivilization_OnColonyLost(MajorCivilization self, Colony col, Settlement.LostReason reason, List<Fleet> attackerFleets) { return WrapColonyLost(self, col, reason, attackerFleets); }
    public static bool Pre_PirateCivilization_OnColonyLost(PirateCivilization self, Colony col, Settlement.LostReason reason, List<Fleet> attackerFleets) { return WrapColonyLost(self, col, reason, attackerFleets); }

    public static bool Pre_ColonyAttackResult_Bombard(ColonyAttackResult self, List<ColonyAttackType> sequence, out bool result)
    {
        result = false;
        if (Bypassed()) return false;
        if (settleDepth == 0) return false;
        if (inBombard > 0) { secGen++; return false; }
        inBombard++; secGen++;
        try { bypass = true; result = self.Bombard(sequence); } finally { inBombard--; secGen++; }
        return true;
    }

    public static bool Pre_MajorCivilization_GetEmpireSecurity(MajorCivilization self, out float result)
    {
        result = 0f;
        if (Bypassed()) return false;
        if (settleDepth > 0)
        {
            try
            {
                if (secGenCache == null) secGenCache = new Dictionary<MajorCivilization, SecEntry>();
                SecEntry e;
                if (secGenCache.TryGetValue(self, out e) && e.gen == secGen)
                {
                    secGenHits++;
                    result = e.value;
                    if (Verify && Sample()) { bypass = true; float o = self.GetEmpireSecurity(); if (o != result) { Mismatch("GetEmpireSecurity (colony processing)", o, result); e.value = o; } result = o; }
                    return true;
                }
                bypass = true;
                result = self.GetEmpireSecurity();
                if (e == null) { e = new SecEntry(); secGenCache[self] = e; }
                e.gen = secGen; e.value = result;
                secGenMisses++;
                return true;
            }
            catch (Exception ex) { LogEx("GetEmpireSecurity (colony processing)", ex); return false; }
        }
        if (scopeDepth == 0) return false;
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
        if (Bypassed() || resource == null) return false;
        if (scopeDepth == 0)
        {
            try
            {
                result = FastRawTrade(self, resource);
                if (Verify && Sample()) { bypass = true; int o = self.GetRawTradeableResourceProduction(resource); if (o != result) Mismatch("FastRawTrade", o, result); result = o; }
                return true;
            }
            catch (Exception e) { LogEx("GetRawTradeableResourceProduction", e); return false; }
        }
        try
        {
            if (tradeRawCache == null) tradeRawCache = new Dictionary<TradeKey, int>();
            var k = new TradeKey { civ = self, res = resource };
            if (tradeRawCache.TryGetValue(k, out result))
            {
                if (Verify && Sample()) { bypass = true; int o = self.GetRawTradeableResourceProduction(resource); if (o != result) Mismatch("GetRawTradeableResourceProduction", o, result); result = o; }
                return true;
            }
            result = FastRawTrade(self, resource);
            if (Verify && Sample()) { bypass = true; int o = self.GetRawTradeableResourceProduction(resource); if (o != result) Mismatch("FastRawTrade", o, result); result = o; }
            tradeRawCache[k] = result;
            return true;
        }
        catch (Exception e) { LogEx("GetRawTradeableResourceProduction", e); return false; }
    }

    // Same sum as Civilization.GetRawTradeableResourceProduction without building Planet.TradeableResources (a new
    // filtered Dictionary on every access, twice per colony: ~165 KB per call, ~200 MB per turn simulation).
    // TradeableResources = planetaryResources where key.isTradeable, so ContainsKey/indexer == TryGetValue + isTradeable.
    static int FastRawTrade(Civilization self, PlanetaryResourceType resource)
    {
        int num = 0, v;
        bool tradeable = resource.isTradeable;
        List<Colony> cols = self.Colonies;
        for (int i = 0; i < cols.Count; i++)
        {
            Colony colony = cols[i];
            Planet pl = colony.ColonizedPlanet;
            if (pl.planetaryResources.TryGetValue(resource, out v) && tradeable && !colony.IsBlockaded) num += v;
        }
        List<NodeDevelopment> devs = self.developments;
        for (int i = 0; i < devs.Count; i++)
        {
            NodeDevelopment d = devs[i];
            Planet pl = d.Placement as Planet;
            if (pl == null || !pl.planetaryResources.TryGetValue(resource, out v) || !tradeable || d.IsBlockaded) continue;
            bool extract = false;
            foreach (ColonyStructureType t in d.structures) if (t.canExtractResources) { extract = true; break; }
            if (extract) num += v;
        }
        return num;
    }

    // ---- Allocation-free versions of methods that allocate on every call (LINQ, iterators, closures, temporary
    // collections) and are called thousands of times per turn. Same results (same iteration order where it matters);
    // verification mode compares samples with the original.
    // Star.HasAllConnectionsDiscoveredBy: original enumerates GetInterstellarConnections() (iterators + a HashSet per
    // node, ~10 KB per call, 12,000 calls per turn simulation). Only "all discovered?" is asked, so the order of the
    // checks and the de-duplication do not matter.
    public static bool Pre_Star_HasAllConnectionsDiscoveredBy(Star self, Civilization civ, out bool result)
    {
        result = false;
        if (Bypassed()) return false;
        try
        {
            result = AllDiscovered(self, civ);
            if (Verify && Sample()) { bypass = true; bool o = self.HasAllConnectionsDiscoveredBy(civ); if (o != result) Mismatch("HasAllConnectionsDiscoveredBy", o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("HasAllConnectionsDiscoveredBy", e); return false; }
    }

    static bool AllDiscovered(Star self, Civilization civ)
    {
        List<NavigationNode> kids = self.children;
        for (int i = 0; i < kids.Count; i++)
        {
            NavigationNode child = kids[i];
            if (child is Warpgate && !child.WasDiscovered(civ)) return false;
        }
        return InterstellarDiscovered(self, civ);
    }

    // every star yielded by n.GetInterstellarConnections() was discovered by civ
    static bool InterstellarDiscovered(NavigationNode n, Civilization civ)
    {
        List<NavigationNode> conns = n.connections;
        if (n is Star)
        {   // Star override: connected stars, then the interstellar connections of every child
            for (int i = 0; i < conns.Count; i++)
            {
                Star st = conns[i] as Star;
                if (st != null && !st.WasDiscovered(civ)) return false;
            }
            List<NavigationNode> kids = n.children;
            for (int i = 0; i < kids.Count; i++) if (!InterstellarDiscovered(kids[i], civ)) return false;
            return true;
        }
        Star source = n.GetStar();
        if (source == null) return true;
        for (int i = 0; i < conns.Count; i++)
        {
            Star target = conns[i].GetStar();
            if (target != null && source.ID != target.ID && !target.WasDiscovered(civ)) return false;
        }
        return true;
    }

    // ColonyStructureType.IsCompatible: the original allocates a closure on every call (and LINQ for colonies in
    // system / planetary resources). 130,000 calls per turn simulation via Colony.IBuildableRequirementsMet.
    public static bool Pre_ColonyStructureType_IsCompatible(ColonyStructureType self, NavigationNode p, Civilization civ, out bool result)
    {
        result = false;
        if (Bypassed()) return false;
        try
        {
            result = FastIsCompatible(self, p, civ);
            if (Verify && Sample()) { bypass = true; bool o = self.IsCompatible(p, civ); if (o != result) Mismatch("IsCompatible " + self.key, o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("IsCompatible", e); return false; }
    }

    static bool FastIsCompatible(ColonyStructureType self, NavigationNode p, Civilization civ)
    {
        if (self.requiresColonyInSystem)
        {   // p.GetStar().GetColonies(civ).Count() == 0 (all planets are visited, like Count())
            List<Planet> planets = p.GetStar().GetPlanets();
            int count = 0;
            for (int i = 0; i < planets.Count; i++)
            {
                Colony c = planets[i].Colony;
                if (c != null && c.Owner != null && c.Owner.ID == civ.ID) count++;
            }
            if (count == 0) return false;
        }
        if (self.requiresWarpGate && !(p is Warpgate)) return false;
        if (!self.requiresWarpGate && p is Warpgate) return false;
        Planet pl = p as Planet;
        if (pl != null)
        {
            if (!(self.compatibleBiomes.Length == 0 || System.Linq.Enumerable.Contains(self.compatibleBiomes, pl.biome))) return false;
            if (!(self.compatibleRichness.Length == 0 || System.Linq.Enumerable.Contains(self.compatibleRichness, pl.MineralRichness))) return false;
            if (!(self.compatiblePlanetSize.Length == 0 || System.Linq.Enumerable.Contains(self.compatiblePlanetSize, pl.Size))) return false;
            if (self.compatiblePlanetaryResource.Length == 0) return true;
            PlanetaryResourceType[] req = self.compatiblePlanetaryResource;
            for (int i = 0; i < req.Length; i++)
            {
                PlanetaryResourceType r = req[i];
                foreach (PlanetaryResourceType pr in pl.planetaryResources.Keys) if (pr == r) return true;
            }
            return false;
        }
        return p is Warpgate;
    }

    // MajorCivilization.GetEmpireStructureSecurityBonus: same additions in the same order, without the temporary list
    public static bool Pre_MajorCivilization_GetEmpireStructureSecurityBonus(MajorCivilization self, out float result)
    {
        result = 0f;
        if (Bypassed()) return false;
        try
        {
            float num = 0f;
            List<Colony> cols = self.colonies;
            for (int i = 0; i < cols.Count; i++)
                foreach (ColonyStructureType m in cols[i].structures) if (m.empireSecurityBonus != 0f) num += m.empireSecurityBonus;
            result = num;
            if (Verify && Sample()) { bypass = true; float o = self.GetEmpireStructureSecurityBonus(); if (o != result) Mismatch("GetEmpireStructureSecurityBonus", o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("GetEmpireStructureSecurityBonus", e); return false; }
    }

    // Civilization.GetStarSystemLeaderBonusValue: star.GetColonies(this) without LINQ (same planets, same order)
    public static bool Pre_Civilization_GetStarSystemLeaderBonusValue(Civilization self, Star star, string bonusKey, float defaultValue, out float result)
    {
        result = 0f;
        if (Bypassed()) return false;
        try
        {
            float num = defaultValue;
            List<Planet> planets = star.GetPlanets();
            for (int i = 0; i < planets.Count; i++)
            {
                Colony colony = planets[i].Colony;
                if (colony == null || colony.Owner == null || colony.Owner.ID != self.ID) continue;
                if (colony.IsInRevolt || colony.IsBlockaded) continue;
                var assignedLeader = colony.GetAssignedLeader();
                if (assignedLeader != null)
                {
                    float v = assignedLeader.GetApplicableBonusValue(bonusKey, defaultValue);
                    if (v > num) num = v;
                }
            }
            result = num;
            if (Verify && Sample()) { bypass = true; float o = self.GetStarSystemLeaderBonusValue(star, bonusKey, defaultValue); if (o != result) Mismatch("GetStarSystemLeaderBonusValue", o, result); result = o; }
            return true;
        }
        catch (Exception e) { LogEx("GetStarSystemLeaderBonusValue", e); return false; }
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

    // ---- Empire Management list state across a visit to a planet screen (convenience, not a performance fix).
    // Opening a planet from the list closes the Empire Management screen (UIStatePlanet clears the UI stack), so the
    // list comes back sorted by colony, scrolled to the top, first colony selected. When a colony is clicked, the sort
    // (published by the patched FillList as widgetContainer.name = "mfsort|<filter>|<direction>"), the selected colony
    // and the scroll position are read from the movie; when the screen is opened again, after its colony list has been
    // filled, they are re-applied through the screen's own paths: header button clicks (same as the player clicking
    // the header), OnColonySelected of the colony's row, then the scroll position.
    sealed class EmState { public Colony colony; public double scroll; public string filter, dir; }
    static EmState emSaved;
    static SWFEmpireManagement emSavedFrom, emTarget;
    static int emFrames;

    static Scaleform.Value Mem(Scaleform.Value v, string name)
    {
        if (v == null) return null;
        Scaleform.Value r = v.GetMember(name);
        if (r == null || r.IsUndefined() || r.IsNull()) return null;
        return r;
    }

    // numeric AS3 values arrive as VT_Int, VT_UInt or VT_Number; each must be read with its own getter
    static bool Num(Scaleform.Value v, out double d)
    {
        d = 0;
        if (v == null) return false;
        if (v.IsInt()) { d = (int)v; return true; }
        if (v.IsUInt()) { d = (uint)v; return true; }
        if (v.IsNumber()) { d = (double)v; return true; }
        return false;
    }

    static Scaleform.Value EmColonyWindow(SWFEmpireManagement em) { return Mem(Mem(em.GetRoot(), "centeredContent"), "coloniesWindow"); }

    static Scaleform.Value EmScrollPane(Scaleform.Value cw)
    {
        double nd;
        if (!Num(Mem(cw, "numChildren"), out nd)) return null;
        for (int i = (int)nd - 1; i >= 0; i--)
        {
            Scaleform.Value c = cw.Invoke("getChildAt", i);
            if (c == null || c.IsUndefined() || c.IsNull()) continue;
            double pv;
            if (Num(Mem(c, "verticalScrollPosition"), out pv) && Mem(c, "content") != null) return c;
        }
        return null;
    }

    static bool EmReadSort(Scaleform.Value sp, out string filter, out string dir)
    {
        filter = null; dir = null;
        Scaleform.Value nm = Mem(Mem(sp, "content"), "name");
        if (nm == null || !nm.IsString()) return false;
        string[] parts = ((string)nm).Split('|');
        if (parts.Length != 3 || parts[0] != "mfsort") return false;
        filter = parts[1]; dir = parts[2];
        return true;
    }

    static string EmHeaderButton(string filter)
    {
        switch (filter)
        {
            case "colony": return "colonyFilter";
            case "population": return "populationFilter";
            case "strike": return "strikeFilter";
            case "credits": return "creditstFilter";
            case "marines": return "marinesstFilter";
            case "security": return "securityFilter";
            case "morale": return "moraleFilter";
            case "pollution": return "pollutionFilter";
            case "food": return "foodFilter";
            case "research": return "researchFilter";
            case "production": return "productionValueFilter";
            case "production queue": return "productionFilter";
        }
        return null;
    }

    // UIStateEmpireManagement.OnPresented: the screen became the top screen (opened, or back from the planet screen,
    // which is pushed on top of it) and has just refilled its colony list (scroll position reset to the top).
    public static bool Pre_UIStateEmpireManagement_OnPresented(UIStateEmpireManagement self, UIState from)
    {
        if (Bypassed()) return false;
        bypass = true; self.OnPresented(from);
        try
        {
            if (emSaved != null)
            {
                emTarget = self.GetSWF<SWFEmpireManagement>();
                emFrames = 0;
                Log("EMLIST presented (from " + (from == null ? "-" : from.GetType().Name) + "): restore pending, movie " + (emTarget == null ? "not found" : "found"));
            }
        }
        catch (Exception e) { LogEx("empire list presented", e); }
        return true;
    }

    public static bool Pre_SWFEmpireManagement_OnColonyClicked(SWFEmpireManagement self, int indexInColonyList)
    {
        if (Bypassed()) return false;
        try
        {
            Civilization civ = Client.GetUniverse().GetLocalPlayerCivilization();
            if (civ != null && indexInColonyList >= 0 && indexInColonyList < civ.Colonies.Count)
            {
                var st = new EmState { colony = civ.Colonies[indexInColonyList] };
                Scaleform.Value sp = EmScrollPane(EmColonyWindow(self));
                if (sp != null)
                {
                    Num(Mem(sp, "verticalScrollPosition"), out st.scroll);
                    EmReadSort(sp, out st.filter, out st.dir);
                }
                emSaved = st; emSavedFrom = self; emTarget = null;
                Log("EMLIST saved: colony " + st.colony.ColonizedPlanet.Name + ", scroll " + st.scroll.ToString("F0") + ", sort " + (st.filter ?? "?") + "/" + (st.dir ?? "?") + (sp == null ? " (scroll pane not found)" : ""));
            }
        }
        catch (Exception e) { LogEx("empire list save", e); }
        return false;                                                    // the original opens the planet
    }

    static void EmRestorePoll()
    {
        SWFEmpireManagement em = emTarget;
        if (em == null) return;
        if (!em.IsReady() || em.HasDelayedMethodCalls())
        {   // the colony list is filled by a queued call when the movie was not loaded yet
            if (++emFrames > 600) emTarget = null;
            return;
        }
        emTarget = null;
        EmState st = emSaved;
        emSaved = null; emSavedFrom = null;
        if (st == null) return;
        Scaleform.Value cw = EmColonyWindow(em);
        Scaleform.Value sp = EmScrollPane(cw);
        if (cw == null || sp == null) { Log("EMLIST restore: " + (cw == null ? "colony window" : "scroll pane") + " not found"); return; }
        string f, d;
        string btnName = st.filter != null && st.dir != null ? EmHeaderButton(st.filter) : null;
        if (btnName != null)
        {   // header clicks: another column sorts "down", clicking the sorted column again toggles the direction
            Scaleform.Value btn = Mem(Mem(cw, "headers"), btnName);
            for (int t = 0; btn != null && t < 3; t++)
            {
                if (EmReadSort(sp, out f, out d) && f == st.filter && d == st.dir) break;
                btn.Invoke("dispatchEvent", em.CreateObject("flash.events.MouseEvent", "click"));
            }
        }
        Civilization civ = Client.GetUniverse().GetLocalPlayerCivilization();
        int idx = civ != null && st.colony != null ? civ.Colonies.IndexOf(st.colony) : -1;
        if (idx >= 0)
        {
            Scaleform.Value content = Mem(sp, "content");
            double cd;
            int cnt = Num(Mem(content, "numChildren"), out cd) ? (int)cd : 0;
            for (int i = 0; i < cnt; i++)
            {
                Scaleform.Value w = content.Invoke("getChildAt", i);
                double ci;
                if (!Num(Mem(w, "colonyIndex"), out ci) || (int)ci != idx) continue;
                w.Invoke("OnColonySelected", em.CreateObject("flash.events.MouseEvent", "click"));
                break;
            }
        }
        sp.SetMember("verticalScrollPosition", st.scroll);
        double now; bool hasNow = Num(Mem(sp, "verticalScrollPosition"), out now);
        string nf, nd; EmReadSort(sp, out nf, out nd);
        Log("EMLIST restored after " + emFrames + " frames: colony index " + idx + ", scroll " + st.scroll.ToString("F0") + " -> " + (hasNow ? now.ToString("F0") : "?") + ", sort " + (nf ?? "?") + "/" + (nd ?? "?"));
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
                double tf = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                string scan = scanSkipped + scanEvaluated > 100000
                    ? " | node scans skipped " + scanSkipped + " / evaluated " + scanEvaluated + ", systems skipped " + scanSystemsSkipped + ", scan total " + (scanTotalTicks * tf).ToString("F0") + "ms of which original evaluation " + (scanEvalTicks * tf).ToString("F0") + "ms"
                    : "";
                int gcNow = -1; long heap = 0;
                try { gcNow = GC.CollectionCount(0); heap = GC.GetTotalMemory(false); } catch { }
                string gcs = gcNow >= 0 ? " | GC " + (gcNow - lastGcCount) + "x since last frame, heap " + (heap >> 20) + " MB" : "";
                Log("FRAME stall " + gap.ToString("F0") + "ms (server " + (st < 0 ? "-" : ((ServerState)st).ToString()) + ")" + gcs + scan);
                if (scan.Length > 0) { scanSkipped = 0; scanEvaluated = 0; scanTotalTicks = 0; scanEvalTicks = 0; scanSystemsSkipped = 0; }
            }
        }
        if (st != lastServerState)
        {
            if (lastServerState >= 0 || st >= 0) Log("SERVER state " + (st < 0 ? "-" : ((ServerState)st).ToString()));
            lastServerState = st;
        }
        lastLateUpdate = now;
        try { lastGcCount = GC.CollectionCount(0); } catch { }
    }
    static int lastGcCount;

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
        try { EmRestorePoll(); } catch (Exception e) { emTarget = null; LogEx("empire list restore", e); }
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
        if (self.IsServer)
        {   // the server simulation runs inside Universe.RunAI: like an action, it must not use or fill the AI-stage
            // caches (paths, path node info, planet ratings)
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (pathCache != null) pathCache.Clear(); ClearPathState(); ClearRatingCache();
            dispatchDepth++;
            try { bypass = true; self.AdvanceSimulation(); }
            finally { dispatchDepth--; if (pathCache != null) pathCache.Clear(); ClearPathState(); ClearRatingCache(); }
            if (Overlap) Log("OVERLAP server simulation " + ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + "ms (turn " + self.turn + ")");
            LogSecurityStats("server");
            return true;
        }
        if (!self.IsServer)
        {   // client simulation: deficit advice checks are merged (see Pre_MajorCivilization_CheckDeficit)
            clientSimDepth++;
            try { bypass = true; self.AdvanceSimulation(); }
            finally { if (--clientSimDepth == 0) { FlushDeficitChecks(); LogSecurityStats("client"); } }
            return true;
        }
        return false;
    }

    static void LogSecurityStats(string side)
    {
        if (secGenHits + secGenMisses == 0) return;
        Log("SECURITY " + side + " simulation: empire security during colony processing reused " + secGenHits + ", computed " + secGenMisses + " | settlement values in scopes reused " + valHits + ", computed " + valMisses);
        secGenHits = 0; secGenMisses = 0; valHits = 0; valMisses = 0;
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
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { ScanNodes(self, scanner); }
        finally { scanTotalTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        return true;
    }

    static long scanTotalTicks, scanEvalTicks;   // statistics only (log)

    static void ScanNodes(Civilization self, IPositionScanner scanner)
    {
        Universe u = self.universe;
        if (!u.isCreated) return;
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
            // whole system clearly out of range and nothing in it detected by this scanner: the star and its children
            // are no-ops (see SystemFarAndClean); interstellar end points belong to other systems and are still evaluated
            bool skipSystem = range >= 0f && SystemFarAndClean(self, exploredStar, scanner, isValidScanner, range, sp);
            if (!skipSystem) ScanOne(self, scanner, isValidScanner, exploredStar, range, sp, true);
            // original: foreach child in Childs (= children list); foreach ep in GetAllInterstelarEndPoints()
            // (= connections.Where(m => m.GetStar() != child.GetStar())). Same lists, same order, without the
            // per-child LINQ enumerator allocations (1,800 scanners x every discovered child).
            List<NavigationNode> kids = exploredStar.children;
            for (int ci = 0; ci < kids.Count; ci++)
            {
                NavigationNode child = kids[ci];
                if (!child.WasDiscovered(self)) continue;
                if (!skipSystem) ScanOne(self, scanner, isValidScanner, child, range, sp, child is Star);
                else scanSkipped++;
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
    }

    static long scanSkipped, scanEvaluated, scanSystemsSkipped;
    static bool scanSetupErrorLogged;

    // ---- Which scanner currently detects how many nodes of which star system. Node detection state
    // (NavigationNode.scannersByCiv) is not saved and only changes in DetectionBeganBy / DetectionEndedBy, so wrapping
    // those two methods gives an exact count. Keys are compared by reference (client and server objects share IDs).
    sealed class RefEq : IEqualityComparer<object>
    {
        public static readonly RefEq I = new RefEq();
        public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
    }
    static readonly object detLock = new object();
    static readonly Dictionary<object, Dictionary<object, int>> detByScanner = new Dictionary<object, Dictionary<object, int>>(RefEq.I);

    static bool DetectionWrap(NavigationNode self, IPositionScanner scanner, bool began)
    {
        if (Bypassed()) return false;
        if (scanner == null) return false;                    // original logs an error and returns
        bool before = self.IsAlreadyDetectedBy(scanner);
        try
        {
            bypass = true;
            if (began) self.DetectionBeganBy(scanner); else self.DetectionEndedBy(scanner);
        }
        finally
        {
            bool after = self.IsAlreadyDetectedBy(scanner);
            if (before != after)
            {
                object sys = (object)self.GetStar() ?? self;
                lock (detLock)
                {
                    Dictionary<object, int> m;
                    if (!detByScanner.TryGetValue(scanner, out m)) { m = new Dictionary<object, int>(RefEq.I); detByScanner.Add(scanner, m); }
                    int c; m.TryGetValue(sys, out c);
                    c += after ? 1 : -1;
                    if (c <= 0) m.Remove(sys); else m[sys] = c;
                    if (m.Count == 0) detByScanner.Remove(scanner);
                }
            }
        }
        return true;
    }
    public static bool Pre_NavigationNode_DetectionBeganBy(NavigationNode self, IPositionScanner scanner) { return DetectionWrap(self, scanner, true); }
    public static bool Pre_NavigationNode_DetectionEndedBy(NavigationNode self, IPositionScanner scanner) { return DetectionWrap(self, scanner, false); }

    static bool ScannerDetectsInSystem(IPositionScanner scanner, Star star)
    {
        lock (detLock)
        {
            Dictionary<object, int> m;
            return detByScanner.TryGetValue(scanner, out m) && m.ContainsKey(star);
        }
    }

    // radius of a star system = max distance of its direct children from the star (cached; recomputed when the number
    // of children changes)
    sealed class SysBound { public int count; public float r; }
    [ThreadStatic] static Dictionary<object, SysBound> sysBounds;

    static float SystemRadius(Star star)
    {
        if (sysBounds == null) sysBounds = new Dictionary<object, SysBound>(RefEq.I);
        List<NavigationNode> kids = star.children;
        SysBound b;
        if (!sysBounds.TryGetValue(star, out b) || b.count != kids.Count)
        {
            if (b == null) { b = new SysBound(); sysBounds[star] = b; }
            float r = 0f;
            GameCore.Utils.Vec3 c = star.Position;
            for (int i = 0; i < kids.Count; i++) { float d = (kids[i].Position - c).Length(); if (d > r) r = d; }
            b.count = kids.Count; b.r = r;
        }
        return b.r;
    }

    // true when the star and every direct child are farther than the scanner can detect (with margins: triangle
    // inequality + the same float margin as ScanOne) and the scanner detects nothing in the system: then each of them
    // would be skipped by ScanOne as well.
    static bool SystemFarAndClean(Civilization self, Star star, IPositionScanner scanner, bool valid, float range, GameCore.Utils.Vec3 sp)
    {
        try
        {
            float thr = Math.Max(range, (range + star.GetDetectionDistance()) * star.GetDetectionRangeMultiplier());
            float dist = (sp - star.Position).Length();
            float r = SystemRadius(star);
            if (dist - r <= thr * 1.0001f + 0.05f) return false;
            if (ScannerDetectsInSystem(scanner, star)) return false;
            if (Verify && Sample())
            {
                bool bad = star.IsAlreadyDetectedBy(scanner) || (valid && self.IsCelestialBodyVisibleBy(star, scanner));
                List<NavigationNode> kids = star.children;
                for (int i = 0; i < kids.Count && !bad; i++)
                {
                    NavigationNode k = kids[i];
                    if (k.IsAlreadyDetectedBy(scanner)) bad = true;
                    else if (valid && k is CelestialBody && k.WasDiscovered(self) && self.IsCelestialBodyVisibleBy(k as CelestialBody, scanner)) bad = true;
                }
                if (bad) { Mismatch("ScanNavigationNodesInRange system skip", true, false); return false; }
            }
            scanSystemsSkipped++;
            return true;
        }
        catch { return false; }
    }

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
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { self.EvaluateScanDetection(scanner, valid, node); }
        finally { scanEvalTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
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
