using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length is not (4 or 5))
{
    Console.Error.WriteLine("Usage: <official OpenAI.dll> <replacement OpenAI.dll> <replacement dependency directory> <report.json> [official dependency directory]");
    return 2;
}

HashSet<string> Scan(string path, string dependencyDirectory)
{
    var dependencies = Path.GetFullPath(dependencyDirectory);
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

var released = Scan(args[0], args.Length == 5 ? args[4] : args[2]);
var replacement = Scan(args[1], args[2]);
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
