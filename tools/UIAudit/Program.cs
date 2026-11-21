using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Safe, read-only metadata audit. Never loads or executes Sea Power assemblies.
if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: UIAudit <path to Seapower-Scripts.dll>");
    return 2;
}
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
if (!pe.HasMetadata)
{
    Console.Error.WriteLine("Assembly has no CLI metadata.");
    return 3;
}
var md = pe.GetMetadataReader();
string[] terms = { "Window", "ViewModel", "EventLog", "Formation", "UnitStatus", "Reference", "Camera", "UI", "Show", "Open", "Hide", "Toggle", "Panel", "Command" };
int printedTypes = 0;
Console.WriteLine("Sea Power UI method index (read-only; no game code executed)");
Console.WriteLine("Assembly: " + args[0]);
foreach (var t in md.TypeDefinitions)
{
    var def = md.GetTypeDefinition(t);
    var ns = md.GetString(def.Namespace);
    var name = md.GetString(def.Name);
    if (!ns.StartsWith("SeapowerUI", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Window", StringComparison.OrdinalIgnoreCase)) continue;
    var methods = new List<string>();
    foreach (var handle in def.GetMethods())
    {
        var m = md.GetMethodDefinition(handle);
        var method = md.GetString(m.Name);
        if (terms.Any(t => method.Contains(t, StringComparison.OrdinalIgnoreCase)))
            methods.Add(method);
    }
    if (methods.Count == 0) continue;
    Console.WriteLine("TYPE " + ns + "." + name);
    foreach (string method in methods.Distinct().OrderBy(x => x)) Console.WriteLine("  " + method);
    printedTypes++;
}
Console.WriteLine("Type count: " + printedTypes);
return 0;
