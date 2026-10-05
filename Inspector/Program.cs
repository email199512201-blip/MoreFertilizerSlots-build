using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 1) { Console.Error.WriteLine("usage: Inspector <assembly.dll>"); return; }
var asm = AssemblyDefinition.ReadAssembly(args[0]);

string Attr(CustomAttribute a)
{
    string args = string.Join(", ", a.ConstructorArguments.Select(x => x.Value?.ToString() ?? "null"));
    var props = string.Join(", ", a.Properties.Select(p => p.Name+"="+(p.Argument.Value?.ToString() ?? "null")));
    var fields = string.Join(", ", a.Fields.Select(p => p.Name+"="+(p.Argument.Value?.ToString() ?? "null")));
    return a.AttributeType.FullName+"("+args+")"+(props.Length>0?" props{"+props+"}":"")+(fields.Length>0?" fields{"+fields+"}":"");
}

IEnumerable<TypeDefinition> Flatten(TypeDefinition t)
{
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var x in Flatten(n)) yield return x;
}

foreach (var t in asm.MainModule.Types.SelectMany(Flatten))
{
    Console.WriteLine("\n=== TYPE " + t.FullName + " ===");
    foreach (var a in t.CustomAttributes) Console.WriteLine("TYPEATTR "+Attr(a));
    foreach (var fld in t.Fields) Console.WriteLine($"FIELD {fld.FieldType.FullName} {fld.Name}");
    foreach (var m in t.Methods)
    {
        Console.WriteLine($"\nMETHOD {m.ReturnType.FullName} {m.Name}({string.Join(", ",m.Parameters.Select(p=>p.ParameterType.FullName+" "+p.Name))})");
        foreach (var a in m.CustomAttributes) Console.WriteLine("METHODATTR "+Attr(a));
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) Console.WriteLine(i);
    }
}
