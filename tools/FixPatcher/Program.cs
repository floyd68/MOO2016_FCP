using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// publicize <in> <out> <managedDir>
// patch <in> <MooFix.dll> <out> <managedDir>
var mode = args[0];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(args[args.Length - 1]);

if (mode == "publicize") {
  var a = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
  foreach (var t in a.MainModule.GetTypes()) {
    if (t.IsNested) t.IsNestedPublic = true; else t.IsPublic = true;
    foreach (var m in t.Methods) m.IsPublic = true;
    foreach (var f in t.Fields) f.IsPublic = true;
  }
  a.Write(args[2]); Console.WriteLine("publicized"); return;
}

var asm = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
var mod = asm.MainModule;
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2])));
var fix = ModuleDefinition.ReadModule(args[2], new ReaderParameters { AssemblyResolver = resolver });
var fixType = fix.Types.First(t => t.Name == "MooFix");

// 1) publicize exactly the members MooFix references
int pub = 0;
foreach (var mr in fix.GetMemberReferences()) {
  if (mr.DeclaringType?.Scope?.Name != "Assembly-CSharp") continue;
  var td = mod.GetType(mr.DeclaringType.FullName.Replace('/', '/'));
  if (td == null) { td = mod.GetTypes().FirstOrDefault(x => x.FullName == mr.DeclaringType.FullName); }
  if (td == null) { Console.WriteLine("WARN type not found " + mr.DeclaringType.FullName); continue; }
  if (mr is FieldReference fr) { var f = td.Fields.FirstOrDefault(x => x.Name == fr.Name); if (f != null && !f.IsPublic) { if (IsMono(td)) throw new Exception("refusing to publicize MonoBehaviour field " + f.FullName); f.IsPublic = true; pub++; Console.WriteLine("public field " + f.FullName); } }
  else if (mr is MethodReference mref) { var m = td.Methods.FirstOrDefault(x => x.Name == mref.Name && x.Parameters.Count == mref.Parameters.Count && x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(mref.Parameters.Select(p => p.ParameterType.FullName))); if (m != null && !m.IsPublic) { m.IsPublic = true; pub++; Console.WriteLine("public method " + m.FullName); } }
}
foreach (var tr in fix.GetTypeReferences()) {
  if (tr.Scope?.Name != "Assembly-CSharp") continue;
  var td = mod.GetTypes().FirstOrDefault(x => x.FullName == tr.FullName);
  if (td == null) continue;
  if (td.IsNested && !td.IsNestedPublic) { td.IsNestedPublic = true; pub++; Console.WriteLine("public type " + td.FullName); }
  else if (!td.IsNested && !td.IsPublic) { td.IsPublic = true; pub++; Console.WriteLine("public type " + td.FullName); }
}

// 2) prefixes: (type, method, hook)
var hooks = new (string type, string method, string hook)[] {
  ("Backend.Core.Universe","DispatchAction","Pre_Universe_DispatchAction"),
  ("Backend.Civilizations.Civilization","HasBuiltStructure","Pre_Civilization_HasBuiltStructure"),
  ("Backend.Civilizations.Civilization","HasBuiltUpgrade","Pre_Civilization_HasBuiltUpgrade"),
  ("Backend.Civilizations.Civilization","HasBuildableInQueue","Pre_Civilization_HasBuildableInQueue"),
  ("Backend.Civilizations.MajorCivilization","GetEmpireSecurity","Pre_MajorCivilization_GetEmpireSecurity"),
  ("Backend.Civilizations.Civilization","CalculateResearchPoints","Pre_Civilization_CalculateResearchPoints"),
  ("Backend.Settlements.Colony","GetAvailableBuildables","Pre_Colony_GetAvailableBuildables"),
  ("SWFEmpireManagement","LoadColoniesData","Pre_SWFEmpireManagement_LoadColoniesData"),
  ("SWFEmpireManagement","UpdateSpecificColony","Pre_SWFEmpireManagement_UpdateSpecificColony"),
  ("SWFEmpireManagement","OnColonyClicked","Pre_SWFEmpireManagement_OnColonyClicked"),
  ("UIStateEmpireManagement","OnPresented","Pre_UIStateEmpireManagement_OnPresented"),
  ("SWFPlanet","Refresh","Pre_SWFPlanet_Refresh"),
  ("SWFEmpireTopBar","SendEmpireData","Pre_SWFEmpireTopBar_SendEmpireData"),
  ("Backend.Civilizations.MajorCivilization","UpdateNotifications","Pre_MajorCivilization_UpdateNotifications"),
  ("Backend.Settlements.Colony","GetStructuresMoraleBoost","Pre_Colony_GetStructuresMoraleBoost"),
  ("AI.Awareness","IsProductionMature","Pre_Awareness_IsProductionMature"),
  ("AI.Awareness","IsResearchMature","Pre_Awareness_IsResearchMature"),
  ("AI.Awareness","IsLifeMature","Pre_Awareness_IsLifeMature"),
  ("SWFPlanet","OnMessage","Pre_SWFPlanet_OnMessage"),
  ("SWFEmpireTopBar","OnMessage","Pre_SWFEmpireTopBar_OnMessage"),
  ("SWFEmpireBottomBar","OnMessage","Pre_SWFEmpireBottomBar_OnMessage"),
  ("SWFEmpireManagement","OnMessage","Pre_SWFEmpireManagement_OnMessage"),
  ("SWFMovie","OnRemoved","Pre_SWFMovie_OnRemoved"),
  ("SWFEmpireManagement","OnRemoved","Pre_SWFEmpireManagement_OnRemoved"),
  ("UIManager","LateUpdate","Pre_UIManager_LateUpdate"),
  ("SWFMovie","PlaySound","Pre_SWFMovie_PlaySound"),
  ("Backend.Galaxy.NavigationNode","ReportChange","Pre_NavigationNode_ReportChange"),
  ("Backend.Civilizations.Civilization","RefreshVisibleKnownNodes","Pre_Civilization_RefreshVisibleKnownNodes"),
  ("Backend.Civilizations.MajorCivilization","ScanNavigationNodesInRange","Pre_MajorCivilization_ScanNavigationNodesInRange"),
  ("Backend.Settlements.Colony","GetStructureSecurityBonus","Pre_Colony_GetStructureSecurityBonus"),
  ("Scaleform.Movie","DoHitTest","Pre_Movie_DoHitTest"),
  ("Scaleform.Movie","MovieInit","Pre_Movie_MovieInit"),
  ("Scaleform.Movie","Advance","Pre_Movie_Advance"),
  ("AssetManager","LoadExternalFileToMemory","Pre_AssetManager_LoadExternalFileToMemory"),
  ("Backend.Fleets.FleetHelper","GetETAToAll","Pre_FleetHelper_GetETAToAll"),
  ("GameStage","SetCurrent","Pre_GameStage_SetCurrent"),
  ("HerefordGame","Update","Pre_HerefordGame_Update"),
  ("HerefordGame","OnLoadingComplete","Pre_HerefordGame_OnLoadingComplete"),
  ("UIVersion","GetBuildString","Pre_UIVersion_GetBuildString"),
  ("SWFMovie","PumpDelayedCalls","Pre_SWFMovie_PumpDelayedCalls"),
  ("Backend.Civilizations.MajorCivilization","CheckDeficit","Pre_MajorCivilization_CheckDeficit"),
  ("Backend.Core.Universe","RunAI","Pre_Universe_RunAI"),
  ("Backend.Core.Universe","PlotPath","Pre_Universe_PlotPath"),
  // planet screen: tradeable resource scope caches, partial refresh for citizen moves
  ("Backend.Civilizations.Civilization","GetTradeableResourceCount","Pre_Civilization_GetTradeableResourceCount"),
  ("Backend.Civilizations.Civilization","GetRawTradeableResourceProduction","Pre_Civilization_GetRawTradeableResourceProduction"),
  ("Backend.Settlements.Colony","RunUpdateProductionWorkers","Pre_Colony_RunUpdateProductionWorkers"),
  ("Backend.Core.Universe","DispatchMessage","Pre_Universe_DispatchMessage"),
  // turn simulation: merged deficit advice checks (client), growth ETA scope cache, territory connected-stars memo,
  // timeline turn data timing   (Universe.AdvanceSimulation is already hooked below)
  ("Backend.Settlements.Colony","GetPopulationGrowthETA","Pre_Colony_GetPopulationGrowthETA"),
  ("SpatialData","BuildTerritory","Pre_SpatialData_BuildTerritory"),
  ("Backend.Galaxy.NavigationNode","GetConnectedStars","Pre_NavigationNode_GetConnectedStars"),
  ("TimelineManager","GetTurnData","Pre_TimelineManager_GetTurnData"),
  // empire security cached across colony OnTurns (generation number bumped on every relevant change)
  ("Backend.Civilizations.Civilization","ProcessSettlementsOnTurn","Pre_Civilization_ProcessSettlementsOnTurn"),
  ("Backend.Settlements.Settlement","AddStructure","Pre_Settlement_AddStructure"),
  ("Backend.Settlements.Settlement","DemolishStructure","Pre_Settlement_DemolishStructure"),
  ("Backend.Settlements.Settlement","DemolishStructure","Pre_Settlement_DemolishStructure2"),
  ("Backend.Settlements.Settlement","DemolishStructure","Pre_Settlement_DemolishStructureCore"),
  ("Backend.Settlements.Colony","DemolishStructure","Pre_Colony_DemolishStructureCore"),
  ("Backend.Settlements.NodeDevelopment","DemolishStructure","Pre_NodeDevelopment_DemolishStructureCore"),
  ("Backend.Settlements.Settlement","ChangeOwner","Pre_Settlement_ChangeOwner"),
  ("Backend.Settlements.Colony","ChangeOwner","Pre_Colony_ChangeOwner"),
  ("Backend.Civilizations.Civilization","OnColonyOwned","Pre_Civilization_OnColonyOwned"),
  ("Backend.Civilizations.MajorCivilization","OnColonyOwned","Pre_MajorCivilization_OnColonyOwned"),
  ("Backend.Civilizations.Civilization","OnColonyLost","Pre_Civilization_OnColonyLost"),
  ("Backend.Civilizations.MajorCivilization","OnColonyLost","Pre_MajorCivilization_OnColonyLost"),
  ("Backend.Civilizations.PirateCivilization","OnColonyLost","Pre_PirateCivilization_OnColonyLost"),
  ("Backend.Fleets.ColonyAttackResult","Bombard","Pre_ColonyAttackResult_Bombard"),
  // AI stage: closest empire location per target star, non-settlement pact neighbour set
  ("AI.Awareness","GetClosestLocationInEmpireTo","Pre_Awareness_GetClosestLocationInEmpireTo"),
  ("Backend.Civilizations.MajorCivilization","CheckIfPlanetWouldBeUnderNonSettlementPact","Pre_MajorCivilization_CheckIfPlanetWouldBeUnderNonSettlementPact"),
  // allocation-free versions of hot allocating methods
  ("Backend.Galaxy.Star","HasAllConnectionsDiscoveredBy","Pre_Star_HasAllConnectionsDiscoveredBy"),
  ("Backend.Settlements.ColonyStructureType","IsCompatible","Pre_ColonyStructureType_IsCompatible"),
  ("Backend.Civilizations.MajorCivilization","GetEmpireStructureSecurityBonus","Pre_MajorCivilization_GetEmpireStructureSecurityBonus"),
  ("Backend.Civilizations.Civilization","GetStarSystemLeaderBonusValue","Pre_Civilization_GetStarSystemLeaderBonusValue"),
  // per-settlement values copied into every observer's known data (memoized inside cache scopes)
  ("Backend.Settlements.Colony","GetCreditOutput","Pre_Colony_GetCreditOutput"),
  ("Backend.Settlements.Colony","GetUpkeepValue","Pre_Colony_GetUpkeepValue"),
  ("Backend.Settlements.Colony","GetSecurityRating","Pre_Colony_GetSecurityRating"),
  ("Backend.Settlements.Settlement","GetOrbitalDefenseRating","Pre_Settlement_GetOrbitalDefenseRating"),
  // node detection bookkeeping (scanner -> star systems it detects nodes in) for system-level scan skipping
  ("Backend.Galaxy.NavigationNode","DetectionBeganBy","Pre_NavigationNode_DetectionBeganBy"),
  ("Backend.Galaxy.NavigationNode","DetectionEndedBy","Pre_NavigationNode_DetectionEndedBy"),
  // AI: engage target index, travel region timing
  ("AI.T_Engage","BoostPriorityOfBlockingSettlements","Pre_T_Engage_BoostPriorityOfBlockingSettlements"),
  ("Backend.Galaxy.NavigationNode","CacheCanTravelRegions","Pre_NavigationNode_CacheCanTravelRegions"),
  // AI planet rating cache (AI stage) and the AI-internal state changes that invalidate it
  ("AI.Awareness","GetPlanetRating","Pre_Awareness_GetPlanetRating"),
  ("AI.Awareness","UpdateEmpire","Pre_Awareness_UpdateEmpire"),
  ("AI.Awareness","CalculateStarCentrality","Pre_Awareness_CalculateStarCentrality"),
  ("AI.SP_GeneralStrategy","RefreshIndependentCivilizations","Pre_SP_GeneralStrategy_RefreshIndependentCivilizations"),
  ("AI.SP_GeneralStrategy","RefreshTargetVictoryCondition","Pre_SP_GeneralStrategy_RefreshTargetVictoryCondition"),
  // load: star separation matrix with a faithful, faster A*
  ("SpatialData","BuildStarsTravelDistance","Pre_SpatialData_BuildStarsTravelDistance"),
  // scanner node detection: skip nodes that are clearly out of range (load, espionage, visibility updates)
  ("Backend.Civilizations.Civilization","ScanNavigationNodesInRange","Pre_Civilization_ScanNavigationNodesInRange"),
  // research screens: cache scope
  ("SWFTechTree","SendTechTree","Pre_SWFTechTree_SendTechTree"),
  ("SWFResearchManagement","SendResearchData","Pre_SWFResearchManagement_SendResearchData"),
  // obsolete blueprints: availability computed at most once per colony
  ("Backend.Settlements.Colony","UpdateObsoleteBlueprintsInProductionQueue","Pre_Colony_UpdateObsoleteBlueprintsInProductionQueue"),
  // turn results: merged notification refreshes, debounced radar outline
  ("VisualActionsSequencer","ProcessQueueableMessages","Pre_VisualActionsSequencer_ProcessQueueableMessages"),
  ("SWFEmpireBottomBar","RefreshNotifications","Pre_SWFEmpireBottomBar_RefreshNotifications"),
  ("RadarZone","RefreshAll","Pre_RadarZone_RefreshAll"),
  // overlapped turn simulation (experimental, off unless moofix_overlap.txt exists)
  ("Backend.Core.Universe","RunUniverseAction","Pre_Universe_RunUniverseAction"),
  ("Backend.Core.Client","AdvanceSimulation","Pre_Client_AdvanceSimulation"),
  ("Backend.Core.Universe","AdvanceSimulation","Pre_Universe_AdvanceSimulation"),
  ("CheckSum","CalculateCheckSum","Pre_CheckSum_CalculateCheckSum"),
  ("Backend.RandomEvents.RandomEvent","Create","Pre_RandomEvent_Create"),
};

int injected = 0;
foreach (var (typeName, methodName, hookName) in hooks) {
  var td = mod.GetTypes().FirstOrDefault(t => t.FullName == typeName);
  if (td == null) { Console.WriteLine("skip (not in module) " + typeName); continue; }
  var hook = fixType.Methods.Single(m => m.Name == hookName);
  var hp = hook.Parameters.ToList();
  int skip = 0;
  var cands = td.Methods.Where(m => m.Name == methodName && m.HasBody).ToList();
  MethodDefinition target = null;
  foreach (var m in cands) {
    var expected = new List<string>();
    if (!m.IsStatic) expected.Add(td.FullName);
    expected.AddRange(m.Parameters.Select(p => p.ParameterType.FullName));
    bool isVoid = m.ReturnType.MetadataType == MetadataType.Void;
    if (!isVoid) expected.Add(m.ReturnType.FullName + "&");
    if (expected.SequenceEqual(hp.Select(p => p.ParameterType.FullName))) { target = m; break; }
  }
  if (target == null) throw new Exception($"no overload of {typeName}.{methodName} matches {hookName}({string.Join(",", hp.Select(p=>p.ParameterType.FullName))})");
  if (hook.ReturnType.MetadataType != MetadataType.Boolean) throw new Exception("hook must return bool " + hookName);
  Inject(target, mod.ImportReference(hook));
  injected++;
  Console.WriteLine($"prefix {target.FullName} -> {hookName}");
}
Console.WriteLine($"publicized {pub}, injected {injected}");
asm.Write(args[3]);

void Inject(MethodDefinition m, MethodReference hook) {
  var body = m.Body; body.SimplifyMacros();
  var il = body.GetILProcessor();
  var first = body.Instructions[0];
  bool isVoid = m.ReturnType.MetadataType == MetadataType.Void;
  VariableDefinition res = null;
  if (!isVoid) { res = new VariableDefinition(m.ReturnType); body.Variables.Add(res); }
  var seq = new List<Instruction>();
  int argc = m.Parameters.Count + (m.IsStatic ? 0 : 1);
  for (int i = 0; i < argc; i++) seq.Add(Instruction.Create(OpCodes.Ldarg, i < 1 && !m.IsStatic ? m.Body.ThisParameter : m.Parameters[m.IsStatic ? i : i - 1]));
  if (!isVoid) seq.Add(Instruction.Create(OpCodes.Ldloca, res));
  seq.Add(Instruction.Create(OpCodes.Call, hook));
  seq.Add(Instruction.Create(OpCodes.Brfalse, first));
  if (!isVoid) seq.Add(Instruction.Create(OpCodes.Ldloc, res));
  seq.Add(Instruction.Create(OpCodes.Ret));
  foreach (var ins in seq) il.InsertBefore(first, ins);
  body.InitLocals = true;
  body.OptimizeMacros();
}

static bool IsMono(TypeDefinition t) {
  try { var b = t.BaseType; int g = 0; while (b != null && g++ < 30) { if (b.FullName == "UnityEngine.MonoBehaviour") return true; var r = b.Resolve(); if (r == null) return false; b = r.BaseType; } } catch {}
  return false;
}
