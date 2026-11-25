using System.Reflection;
using System.Runtime.CompilerServices;
var dirs = new[]{ args[1], args[2] };
AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
  var n = new AssemblyName(e.Name).Name + ".dll";
  foreach (var d in dirs) { var p = Path.Combine(d, n); if (File.Exists(p)) return Assembly.LoadFrom(p); }
  return null; };
var asm = Assembly.LoadFrom(args[0]);
if (args.Length > 3) {
  var fx = Assembly.LoadFrom(args[3]);
  foreach (var mm in fx.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.DeclaredOnly|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))) {
    if (mm.ContainsGenericParameters || mm.IsAbstract) continue;
    try { RuntimeHelpers.PrepareMethod(mm.MethodHandle); } catch (Exception e) { Console.WriteLine("MOOFIX " + mm.DeclaringType.Name + "." + mm.Name + ": " + e.GetType().Name + " " + e.Message); }
  }
  Console.WriteLine("moofix jit done");
}
Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
int ok=0, invalid=0, other=0;
foreach (var t in types) foreach (var m in t.GetMethods(BindingFlags.DeclaredOnly|BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)) {
  if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType.ContainsGenericParameters) continue;
  MethodBody b; try { b = m.GetMethodBody(); } catch { continue; } if (b == null) continue;
  bool wrapped = b.LocalVariables.Count > 0 && b.ExceptionHandlingClauses.Any(c => c.Flags == ExceptionHandlingClauseOptions.Finally);
  if (!wrapped && Environment.GetEnvironmentVariable("ALLM") == null) continue;
  try { RuntimeHelpers.PrepareMethod(m.MethodHandle); ok++; }
  catch (InvalidProgramException e) { invalid++; Console.WriteLine($"INVALID {t.FullName}.{m.Name}: {e.Message}"); }
  catch (Exception) { other++; }
}
Console.WriteLine($"jitted ok={ok} invalid={invalid} unresolvable(other)={other}");
