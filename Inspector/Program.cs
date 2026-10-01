using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 1) { Console.Error.WriteLine("usage: Inspector Assembly-CSharp.dll"); return; }
var asm = AssemblyDefinition.ReadAssembly(args[0]);

void DumpType(string typeName, string[]? methodNames = null)
{
    var t = asm.MainModule.Types.SelectMany(Flatten).FirstOrDefault(x => x.Name == typeName || x.FullName == typeName);
    if (t == null) { Console.WriteLine("\nNOT FOUND " + typeName); return; }
    Console.WriteLine("\n=== " + t.FullName + " ===");
    Console.WriteLine("FIELDS:");
    foreach (var fld in t.Fields) Console.WriteLine($"  {fld.FieldType.FullName} {fld.Name}");
    Console.WriteLine("PROPERTIES:");
    foreach (var p in t.Properties) Console.WriteLine($"  {p.PropertyType.FullName} {p.Name}");
    Console.WriteLine("METHODS:");
    foreach (var m in t.Methods) Console.WriteLine($"  {m.ReturnType.FullName} {m.Name}({string.Join(", ",m.Parameters.Select(p=>p.ParameterType.FullName+" "+p.Name))})");
    if (methodNames == null) return;
    foreach (var mn in methodNames)
    {
        foreach (var m in t.Methods.Where(x=>x.Name==mn))
        {
            Console.WriteLine($"\n=== IL {t.Name}.{m.Name} ===");
            if (!m.HasBody) { Console.WriteLine("<no body>"); continue; }
            foreach (var i in m.Body.Instructions) Console.WriteLine(i);
        }
    }
}

IEnumerable<TypeDefinition> Flatten(TypeDefinition t)
{
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var x in Flatten(n)) yield return x;
}

DumpType("UIGardenBedWindow", new[]{
    "IsItemValid","AddFertilizerPerk","RemoveFertilizerPerk","TryApplyFertilizer","HasPerkForSlot","UpdatePerks","Redraw"
});
DumpType("GardenInteractionHandler", new[]{
    "TryApplyFertilizer","HasFreeFertilizerPerkSlot","TryAssignPerkSlotForNewestAddedPerk"
});
DumpType("CraftParamsData", new[]{
    "GetWgoPerksCraftMasteryBonusValue","GetWgoPerksCraftStartTicksBonusValue","GetWgoPerksCraftAddTotalProgressTicks"
});
DumpType("WgoData");
DumpType("PerkData");
