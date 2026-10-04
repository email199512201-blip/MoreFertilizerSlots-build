using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 1) { Console.Error.WriteLine("usage: Inspector <assembly.dll>"); return; }
var asm = AssemblyDefinition.ReadAssembly(args[0]);

IEnumerable<TypeDefinition> Flatten(TypeDefinition t)
{
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var x in Flatten(n)) yield return x;
}

foreach (var t in asm.MainModule.Types.SelectMany(Flatten))
{
    Console.WriteLine("\n=== TYPE " + t.FullName + " ===");
    foreach (var fld in t.Fields)
        Console.WriteLine($"FIELD {fld.FieldType.FullName} {fld.Name}");
    foreach (var p in t.Properties)
        Console.WriteLine($"PROP {p.PropertyType.FullName} {p.Name}");
    foreach (var m in t.Methods)
    {
        Console.WriteLine($"\nMETHOD {m.ReturnType.FullName} {m.Name}({string.Join(", ",m.Parameters.Select(p=>p.ParameterType.FullName+" "+p.Name))})");
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions)
            Console.WriteLine(i);
    }
}
