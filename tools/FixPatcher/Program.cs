using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// publicize <in> <out> <managedDir>
// patch <in> <MooFix.dll> <out> <managedDir>
var mode = args[0];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(args[^1]);

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
  ("Backend.Civilizations.MajorCivilization","CheckDeficit","Pre_MajorCivilization_CheckDeficit"),
  ("Backend.Core.Universe","RunAI","Pre_Universe_RunAI"),
  ("Backend.Core.Universe","PlotPath","Pre_Universe_PlotPath"),
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
