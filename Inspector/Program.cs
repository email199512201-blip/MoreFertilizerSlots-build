using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 1) { Console.Error.WriteLine("usage: Inspector Assembly-CSharp.dll"); return; }
var asm = AssemblyDefinition.ReadAssembly(args[0]);
foreach (var typeName in new[]{"UIGardenBedWindow","UIGardenBedSlot","GardenInteractionHandler"})
{
    var t = asm.MainModule.Types.FirstOrDefault(x => x.Name == typeName);
    if (t == null) { Console.WriteLine("NOT FOUND "+typeName); continue; }
    Console.WriteLine("\n=== "+t.FullName+" ===");
    Console.WriteLine("FIELDS:");
    foreach (var f in t.Fields) Console.WriteLine($"  {f.FieldType.FullName} {f.Name}");
    Console.WriteLine("PROPERTIES:");
    foreach (var p in t.Properties) Console.WriteLine($"  {p.PropertyType.FullName} {p.Name}");
    Console.WriteLine("METHODS:");
    foreach (var m in t.Methods) Console.WriteLine($"  {m.ReturnType.FullName} {m.Name}({string.Join(", ",m.Parameters.Select(p=>p.ParameterType.FullName+" "+p.Name))})");
}
var win=asm.MainModule.Types.FirstOrDefault(x=>x.Name=="UIGardenBedWindow");
if(win!=null)
{
    foreach(var mn in new[]{"UpdatePerks","DrawFertilizerSlot","DrawSeedSlot","UpdateSeedItemCell"})
    {
        var m=win.Methods.FirstOrDefault(x=>x.Name==mn);
        if(m?.HasBody!=true) continue;
        Console.WriteLine($"\n=== IL {m.Name} ===");
        foreach(var i in m.Body.Instructions) Console.WriteLine(i);
    }
}
