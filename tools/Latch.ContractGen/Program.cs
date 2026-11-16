// Generates web/src/contract.gen.ts from the C# contract (Latch.Core.Contract).
// Usage: dotnet run --project tools/Latch.ContractGen [-- --check]
// With --check it fails when the generated file is stale instead of writing it.
using System.Reflection;
using System.Text;
using System.Text.Json;
using Latch.Core.Contract;

var check = args.Contains("--check");
var root = FindRepoRoot();
var target = Path.Combine(root, "web", "src", "contract.gen.ts");
var output = Generate();

if (check)
{
    var existing = File.Exists(target) ? File.ReadAllText(target).ReplaceLineEndings("\n") : "";
    if (existing != output)
    {
        Console.Error.WriteLine("web/src/contract.gen.ts is stale. Run: dotnet run --project tools/Latch.ContractGen");
        return 1;
    }
    Console.WriteLine("contract.gen.ts is current.");
    return 0;
}

File.WriteAllText(target, output);
Console.WriteLine("Wrote " + target);
return 0;

static string Generate()
{
    var asm = typeof(ILatchApi).Assembly;
    var types = asm.GetTypes()
        .Where(t => t.Namespace == "Latch.Core.Contract" && t.IsPublic && t != typeof(ContractJson) && !t.IsAbstract | t.IsInterface | t.IsEnum)
        .Where(t => t != typeof(ContractJson) && t != typeof(ServerEvents) && !t.IsInterface && !t.IsGenericTypeDefinition)
        .OrderBy(t => t.IsEnum ? 0 : 1).ThenBy(t => t.MetadataToken)
        .ToList();
    var nullability = new NullabilityInfoContext();
    var sb = new StringBuilder();
    sb.Append("// Generated from src/Latch.Core/Contract/Contract.cs by tools/Latch.ContractGen. Do not edit.\n\n");

    foreach (var t in types.Where(t => t.IsEnum))
    {
        var values = Enum.GetNames(t).Select(n => $"\"{Camel(n)}\"");
        sb.Append($"export type {t.Name} = {string.Join(" | ", values)};\n");
    }
    sb.Append('\n');

    foreach (var t in types.Where(t => !t.IsEnum))
    {
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .OrderBy(p => p.MetadataToken);
        sb.Append($"export interface {t.Name} {{\n");
        foreach (var p in props)
        {
            var info = nullability.Create(p);
            sb.Append($"  {Camel(p.Name)}: {Ts(p.PropertyType, info)};\n");
        }
        sb.Append("}\n\n");
    }

    sb.Append("export interface LatchApi {\n");
    foreach (var m in typeof(ILatchApi).GetMethods().OrderBy(m => m.MetadataToken))
    {
        var arg = m.GetParameters()[0].ParameterType;
        var ret = m.ReturnType.GetGenericArguments()[0];
        var retInfo = nullability.Create(m.ReturnParameter);
        var retTs = Ts(ret, retInfo.GenericTypeArguments.Length > 0 ? retInfo.GenericTypeArguments[0] : retInfo);
        var param = arg == typeof(Empty) ? "" : $"args: {arg.Name}";
        sb.Append($"  {Camel(m.Name)}({param}): Promise<{retTs}>;\n");
    }
    sb.Append("}\n\n");

    sb.Append("export type LatchMethod = keyof LatchApi;\n\n");
    sb.Append("export interface ServerEventMap {\n");
    sb.Append($"  {ServerEvents.State}: AppSnapshot;\n");
    sb.Append($"  {ServerEvents.Prompt}: PromptView | null;\n");
    sb.Append("}\n");
    return sb.ToString();
}

static string Ts(Type type, NullabilityInfo? info)
{
    var underlying = Nullable.GetUnderlyingType(type);
    if (underlying is not null) return Ts(underlying, null) + " | null";
    var core = TsCore(type, info);
    return info?.ReadState == NullabilityState.Nullable && !type.IsValueType ? core + " | null" : core;
}

static string TsCore(Type type, NullabilityInfo? info)
{
    if (type == typeof(string) || type == typeof(DateTimeOffset)) return "string";
    if (type == typeof(bool)) return "boolean";
    if (type == typeof(int) || type == typeof(long) || type == typeof(double)) return "number";
    if (type == typeof(Empty)) return "Empty";
    if (type.IsGenericType && type.GetGenericTypeDefinition() is var g && (g == typeof(IReadOnlyList<>) || g == typeof(List<>)))
    {
        var element = Ts(type.GetGenericArguments()[0], info?.GenericTypeArguments.FirstOrDefault() ?? info?.ElementType);
        return element.Contains(' ') ? $"({element})[]" : element + "[]";
    }
    if (type.Namespace == "Latch.Core.Contract") return type.Name;
    throw new NotSupportedException("No TypeScript mapping for " + type);
}

static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Latch.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repo.");
}
