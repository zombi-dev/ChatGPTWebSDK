using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: <official OpenAI.dll> <replacement OpenAI.dll> <dependency directory> <report.json>");
    return 2;
}

var dependencies = Path.GetFullPath(args[2]);
HashSet<string> Scan(string path)
{
    var context = new AssemblyLoadContext(Guid.NewGuid().ToString(), isCollectible: true);
    context.Resolving += (_, name) =>
    {
        var file = Path.Combine(dependencies, name.Name + ".dll");
        return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
    };
    var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
    var result = new HashSet<string>();
    string TypeName(Type type) => type.IsByRef ? TypeName(type.GetElementType()!) + "&" :
        type.IsArray ? TypeName(type.GetElementType()!) + "[]" :
        type.IsGenericType ? type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">" :
        type.FullName ?? type.ToString();
    string Parameters(MethodBase method) => string.Join(",", method.GetParameters().Select(parameter =>
        TypeName(parameter.ParameterType) + " " + parameter.Name + (parameter.IsOptional ? "=optional" : "")));
    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    foreach (var type in assembly.GetExportedTypes())
    {
        var name = TypeName(type);
        result.Add("type " + name);
        foreach (var method in type.GetMethods(flags).Where(m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
            result.Add(name + "." + method.Name + "(" + Parameters(method) + "):" + TypeName(method.ReturnType));
        foreach (var constructor in type.GetConstructors(flags).Where(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly))
            result.Add(name + ".ctor(" + Parameters(constructor) + ")");
        foreach (var field in type.GetFields(flags).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
            result.Add(name + "." + field.Name + ":" + TypeName(field.FieldType));
    }
    context.Unload();
    return result;
}

var released = Scan(args[0]);
var replacement = Scan(args[1]);
var missing = released.Except(replacement).Order().ToArray();
File.WriteAllText(args[3], JsonSerializer.Serialize(new
{
    releasedMembers = released.Count,
    replacementMembers = replacement.Count,
    missing
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Official public/protected signatures: {released.Count}; replacement: {replacement.Count}; missing: {missing.Length}");
foreach (var signature in missing.Take(20)) Console.WriteLine(signature);
return missing.Length == 0 ? 0 : 1;
