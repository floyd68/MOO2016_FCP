using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// args: <input Assembly-CSharp.dll> <MooPerf.dll> <output dll> <managed dir>
var input = args[0]; var perfPath = args[1]; var output = args[2]; var managed = args[3];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
resolver.AddSearchDirectory(Path.GetDirectoryName(perfPath));
var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
var mod = asm.MainModule;
var perf = ModuleDefinition.ReadModule(perfPath);
var perfType = perf.Types.First(t => t.Name == "MooPerfLog");
var beginRef = mod.ImportReference(perfType.Methods.First(m => m.Name == "Begin"));
var endRef = mod.ImportReference(perfType.Methods.First(m => m.Name == "End"));
var endObjRef = mod.ImportReference(perfType.Methods.First(m => m.Name == "EndObj"));

var targets = new Dictionary<string, string[]> {
  ["SWFEmpireManagement"] = new[]{"OnMessage","Refresh","LoadEmpireData","LoadColoniesData","UpdateSpecificColony","OnBuyClicked","OnColonySelected","OnColonyClicked","OnShowTooltip","LoadInterstellarStockExchangeData","OnUpdate"},
  ["SWFEmpireTopBar"] = new[]{"OnMessage","SendEmpireData","OnUpdate"},
  ["SWFEmpireBottomBar"] = new[]{"OnMessage","RefreshNotifications","OnUpdate"},
  ["SWFPlanet"] = new[]{"OnMessage","Refresh","RefreshPlanet","FillColonyScreen","UpdateColonyProductionValues","FillProductionGrid","UpdateBuildablesETA","UpdateBuildablesCost","UpdateColonyBuildings","UpdateCurrentItemOnProduction","FillColonyBuildOptions","FillColonyFocusOptions","OnRemoveWorkerFrom","OnMoveWorkerTo","OnShowTooltip","UpdateLeader","FillColonyEvents","OnUpdate"},
  ["UIStatePlanet"] = new[]{"OnMessage","OnBackgroundMessage"},
  ["UIStateEmpireManagement"] = new[]{"OnMessage","OnPresented"},
  ["SWFMovie"] = new[]{"CallSWFMethod","OnUpdate"},
  ["UIManager"] = new[]{"LateUpdate","Process","DispatchMessage","ShowTooltip","HideTooltip"},

  ["HerefordGame"] = new[]{"OnMessage","DispatchMessage"},
  ["StageGalaxyView"] = new[]{"OnMessage"},
  ["VisualActionsSequencer"] = new[]{"OnMessage","RefreshStarSystemPlanet"},
  ["VisualGalaxyManager"] = new[]{"OnMessage"},
  ["UUIStateGalaxy"] = new[]{"OnMessage","OnLateMessage"},
  ["UIStateGalaxy"] = new[]{"OnMessage"},
  ["MessageSink"] = new[]{"Dispatch"},
  ["Backend.Core.Client"] = new[]{"Update","OnPacket","AdvanceSimulation","OnNewTurn","OnMessage"},
  ["NetworkLayer"] = new[]{"SendToHost","Update","DispatchClientPackets","DispatchServerPackets"},
  ["Backend.Core.Universe"] = new[]{"DispatchAction","AdvanceSimulation","ProcessMessages","RunAI"},
  ["Backend.Core.Server"] = new[]{"Update","OnPacket","RunAndBroadcast"},
  ["Backend.Settlements.Colony"] = new[]{"RunAction","RefreshGrid","BuyProduction","GetAvailableStructures","GetAvailableBuildables","UpdateWorkersProduction","OnTurn","Govern"},
  ["Backend.Civilizations.Civilization"] = new[]{"BalanceSystemFood","CheckDeficit","CalculateTotalIncome","OnTurn","OnPostTurn"},
  ["Backend.Civilizations.MajorCivilization"] = new[]{"CheckDeficit","UpdateNotifications","OnTurn","OnPostTurn"},
  ["TimelineManager"] = new[]{"PushTurn"},
  ["Scaleform.SFManager"] = new[]{"Advance","HandleMouseMoveEvent","HandleMouseEvent","HandleKeyEvent","HandleCharEvent","CheckResolutionChange","ReleaseMoviesMarkedForRelease"},
  ["Scaleform.Movie"] = new[]{"Advance","HandleMouseEvent","DoHitTest","HandleKeyEvent","HandleCharEvent","Invoke","SetFocus"},
  ["Scaleform.SFCamera"] = new[]{"_Update","_LateUpdate","ProcessInput","SWFFocus","OnPostRender","OnRenderObject","Update","LateUpdate","OnGUI"},
};
var unityMethods = new HashSet<string>{"Update","LateUpdate","FixedUpdate","OnGUI"};

bool IsMono(TypeDefinition t) {
  try { var b = t.BaseType; int guard = 0;
    while (b != null && guard++ < 30) { if (b.FullName == "UnityEngine.MonoBehaviour") return true; var r = b.Resolve(); if (r == null) return false; b = r.BaseType; } } catch {}
  return false;
}

int count = 0;
foreach (var t in mod.GetTypes()) {
  targets.TryGetValue(t.FullName, out var names);
  bool mono = IsMono(t);
  foreach (var m in t.Methods.ToList()) {
    if (!m.HasBody || m.IsConstructor || m.HasGenericParameters || m.IsAbstract) continue;
    bool want = (names != null && names.Contains(m.Name)) || (mono && unityMethods.Contains(m.Name) && m.Parameters.Count == 0);
    // broad profiling of turn processing / AI: non-trivial methods only
    if (!want && m.Body.Instructions.Count > 30 && !m.Name.StartsWith("<") && !t.Name.StartsWith("<") &&
        (t.Namespace == "AI" || t.Namespace == "Backend.Diplomacy" || t.Namespace == "Backend.Research" || t.Namespace == "Backend.Espionage" || t.Namespace == "Backend.Leaders" ||
         new[]{"Colony","Settlement","Civilization","MajorCivilization","Universe","Planet","Star","Fleet","ProductionGrid","KnownNodeData","KnownColonyData","KnownPlanetData","KnownSettlementData","NodeDevelopment","CelestialBody","NavigationNode","TimelineManager","CheckSum"}.Contains(t.Name)))
      want = true;
    if (!want) continue;
    Wrap(m, $"{t.Name}.{m.Name}({m.Parameters.Count})", t.FullName == "Scaleform.Movie" && !m.IsStatic);
    count++;
  }
}
Console.WriteLine($"wrapped {count} methods");
asm.Write(output);

void Wrap(MethodDefinition m, string label, bool withSelf = false) {
  var body = m.Body; body.SimplifyMacros();
  var il = body.GetILProcessor();
  var tLocal = new VariableDefinition(mod.TypeSystem.Int64); body.Variables.Add(tLocal);
  bool isVoid = m.ReturnType.MetadataType == MetadataType.Void;
  VariableDefinition rLocal = null;
  if (!isVoid) { rLocal = new VariableDefinition(m.ReturnType); body.Variables.Add(rLocal); }
  var first = body.Instructions[0];
  // epilogue
  var finStart = Instruction.Create(OpCodes.Ldloc, tLocal);
  var endLoad = isVoid ? Instruction.Create(OpCodes.Ret) : Instruction.Create(OpCodes.Ldloc, rLocal);
  // replace rets
  foreach (var ins in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList()) {
    if (isVoid) { ins.OpCode = OpCodes.Leave; ins.Operand = endLoad; }
    else { ins.OpCode = OpCodes.Stloc; ins.Operand = rLocal; il.InsertAfter(ins, Instruction.Create(OpCodes.Leave, endLoad)); }
  }
  il.Append(finStart);
  il.Append(Instruction.Create(OpCodes.Ldstr, label));
  if (withSelf) { il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Call, endObjRef)); }
  else il.Append(Instruction.Create(OpCodes.Call, endRef));
  il.Append(Instruction.Create(OpCodes.Endfinally));
  il.Append(endLoad);
  if (!isVoid) il.Append(Instruction.Create(OpCodes.Ret));
  // prologue
  il.InsertBefore(first, Instruction.Create(OpCodes.Call, beginRef));
  il.InsertBefore(first, Instruction.Create(OpCodes.Stloc, tLocal));
  body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
    TryStart = first, TryEnd = finStart, HandlerStart = finStart, HandlerEnd = endLoad });
  body.InitLocals = true;
  body.OptimizeMacros();
}
