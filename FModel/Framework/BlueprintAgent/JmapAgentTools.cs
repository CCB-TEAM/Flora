using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CUE4Parse.UE4.Objects.UObject;
using FModel.BlueprintGraph.Engine;
using FModel.BlueprintGraph.Signatures;
using FModel.Framework.Mappings;
using FModel.Services;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// Agent tools over the .jmap dumps (jmap_dumper): the game's native classes, structs, functions with their parameters, and enums.
/// Cooked packages don't carry native signatures, so this is where the model checks what a native call takes before writing one.
/// </summary>
public static class JmapAgentTools
{
    private static readonly Newtonsoft.Json.Linq.JObject ClassArg = String("Class or struct: full path (/Script/Engine.Actor) or short name (Actor)");

    public static IEnumerable<AgentTool> Build() =>
    [
        new()
        {
            Name = "jmap_info",
            Description = "Which .jmap dumps are loaded (the game's mappings and the signature jmap of the settings), who made them, for which engine, and what they contain.",
            InputSchema = Object(),
            Run = (_, _) =>
            {
                var jmaps = Jmaps(false);
                if (jmaps.Count == 0)
                    return Task.FromResult("No .jmap is loaded. Native signatures are then inferred from call sites; the user can load a jmap as mappings or set one in Settings (blueprint signature jmap).");
                return Task.FromResult(string.Join("\n", jmaps.Select(o =>
                    $"{o.SourcePath}: {o.Description}; {o.Objects.Count(c => c.Kind == "Class")} classes, {o.Objects.Count(c => c.Kind == "ScriptStruct")} structs, " +
                    $"{o.Functions.Count} functions, {o.Enums.Count} enums")));
            },
        },
        new()
        {
            Name = "jmap_search",
            Description = "Searches the jmap dumps by name: classes, structs, functions, enums and properties whose name contains the query. Returns paths for jmap_class / jmap_function / jmap_enum.",
            InputSchema = Object(("query", String("Part of a name, e.g. Damage, GetActorLocation, ECollisionChannel"), true),
                ("kind", Enum("Only this kind", "class", "struct", "function", "enum", "property"), false),
                ("limit", Integer("Maximum results (default 60)"), false)),
            Run = (args, _) =>
            {
                var query = args.Str("query");
                var kind = args.Str("kind", false);
                var limit = Math.Clamp(args.Int("limit", 60), 1, 300);
                bool Matches(string name) => name.Contains(query, StringComparison.OrdinalIgnoreCase);
                var hits = new List<string>();
                foreach (var jmap in Jmaps(true))
                {
                    if (kind is null or "class" or "struct")
                        hits.AddRange(jmap.Objects.Where(o => o.Kind is "Class" or "ScriptStruct" && (kind == null || (kind == "class") == (o.Kind == "Class")) && Matches(ShortName(o.Name)))
                            .Select(o => $"{(o.Kind == "Class" ? "class " : "struct")} {o.Name}"));
                    if (kind is null or "function")
                        hits.AddRange(jmap.Functions.Where(o => Matches(o.Name)).Select(Signature));
                    if (kind is null or "enum")
                        hits.AddRange(jmap.Enums.Where(o => Matches(ShortName(o.Name))).Select(o => $"enum   {o.Name} ({o.Elements.Count} values)"));
                    if (kind == "property")
                        hits.AddRange(jmap.Objects.SelectMany(o => o.Properties.Values).Where(o => Matches(o.Name)).Select(o => $"prop   {o.Owner}.{o.Name}: {GraphText.DescribeType(o.PinType)}"));
                }
                var distinct = hits.Distinct().OrderBy(o => o.Length).ToList();
                if (distinct.Count == 0)
                    return Task.FromResult("Nothing found.");
                return Task.FromResult(string.Join("\n", distinct.Take(limit)) + (distinct.Count > limit ? $"\n... {distinct.Count - limit} more; refine the query or pass kind" : ""));
            },
        },
        new()
        {
            Name = "jmap_class",
            Description = "A native class or struct from the jmap: parent chain, interfaces, properties (name: type, flags) and the signatures of its functions.",
            InputSchema = Object(("class", ClassArg, true), ("inherited", Boolean("Also list what parent classes declare (default false)"), false),
                ("filter", String("Only members whose name contains this"), false)),
            Run = (args, _) =>
            {
                var data = FindObject(args.Str("class"));
                var inherited = args.Bool("inherited", false);
                var filter = args.Str("filter", false);
                bool Keep(string name) => filter == null || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

                var sb = new StringBuilder();
                var chain = new List<ObjectData>();
                for (var current = data; current != null; current = current.SuperStruct)
                    chain.Add(current);
                sb.AppendLine($"{(data.Kind == "ScriptStruct" ? "struct" : "class")} {data.Name}" + (chain.Count > 1 ? " : " + string.Join(" : ", chain.Skip(1).Select(o => ShortName(o.Name))) : ""));
                if (data.Interfaces is { Length: > 0 } interfaces)
                    sb.AppendLine("implements " + string.Join(", ", interfaces.Select(o => o.Name)));

                foreach (var owner in inherited ? chain : [data])
                {
                    var properties = owner.Properties.Values.Where(o => Keep(o.Name)).ToList();
                    var functions = owner.Functions.Where(o => Keep(o.Name)).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    if (properties.Count == 0 && functions.Count == 0)
                        continue;
                    if (inherited)
                        sb.AppendLine().AppendLine($"-- declared in {owner.Name}");
                    foreach (var property in properties)
                        sb.AppendLine($"  {property.Name}: {GraphText.DescribeType(property.PinType)}{Flags(property.Flags, false)}");
                    foreach (var function in functions)
                        sb.AppendLine("  " + Signature(function));
                }
                return Task.FromResult(sb.ToString());
            },
        },
        new()
        {
            Name = "jmap_function",
            Description = "One native function from the jmap: flags and every parameter in order (type, in/out/return, by reference, const). " +
                          "Bytecode calls pass parameters in this order; out parameters and the return value are variables.",
            InputSchema = Object(("class", ClassArg, true), ("function", String("Function name, e.g. K2_SetActorLocation"), true)),
            Run = (args, _) =>
            {
                var name = args.Str("function");
                var owner = FindObject(args.Str("class"));
                FunctionData function = null;
                for (var current = owner; current != null && function == null; current = current.SuperStruct)
                    function = current.GetFunction(name);
                if (function == null)
                    throw new AgentToolException($"{owner.Name} has no function '{name}' (jmap_class lists them; it may be declared by an interface).");

                var sb = new StringBuilder(Signature(function)).AppendLine();
                sb.AppendLine("flags: " + string.Join(" ", System.Enum.GetValues<EFunctionFlags>().Where(o => o != 0 && function.Flags.HasFlag(o)).Select(o => o.ToString().Replace("FUNC_", ""))));
                var index = 0;
                foreach (var parameter in function.Params)
                    sb.AppendLine($"  [{index++}] {parameter.Name}: {GraphText.DescribeType(parameter.PinType)}{Flags(parameter.Flags, true)}");
                return Task.FromResult(sb.ToString());
            },
        },
        new()
        {
            Name = "jmap_enum",
            Description = "The values of a native enum from the jmap (name = number), e.g. for an enum literal or a switch.",
            InputSchema = Object(("enum", String("Enum path or short name, e.g. ECollisionChannel"), true)),
            Run = (args, _) =>
            {
                var name = args.Str("enum");
                var data = Jmaps(true).SelectMany(o => o.Enums)
                               .FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(ShortName(o.Name), name, StringComparison.OrdinalIgnoreCase))
                           ?? throw new AgentToolException($"No enum '{name}' in the jmap (jmap_search with kind enum).");
                return Task.FromResult($"{data.Name}\n" + string.Join("\n", data.Elements.OrderBy(o => o.Value).Select(o => $"  {o.Key} = {o.Value}")));
            },
        },
    ];

    internal static List<JmapData> Jmaps(bool required)
    {
        var jmaps = BlueprintGraphContextFactory.GetJmaps(ApplicationService.ApplicationView.CUE4Parse.JmapPath);
        if (required && jmaps.Count == 0)
            throw new AgentToolException("No .jmap is loaded (see jmap_info).");
        return jmaps;
    }

    internal static string ShortName(string path) => path[(Math.Max(path.LastIndexOf('.'), path.LastIndexOf(':')) + 1)..];

    internal static ObjectData FindObject(string name)
    {
        var objects = Jmaps(true).SelectMany(o => o.Objects).ToList();
        var exact = objects.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;
        // engine prefixes (A, U, F) are not part of the reflected name
        var trimmed = name.Length > 1 && name[0] is 'A' or 'U' or 'F' && char.IsUpper(name[1]) ? name[1..] : name;
        var candidates = objects.Where(o => o.Kind is "Class" or "ScriptStruct" &&
                                            (string.Equals(ShortName(o.Name), name, StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(ShortName(o.Name), trimmed, StringComparison.OrdinalIgnoreCase))).ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new AgentToolException($"No class or struct '{name}' in the jmap (use jmap_search)."),
            _ => throw new AgentToolException($"'{name}' is ambiguous: {string.Join(", ", candidates.Select(o => o.Name).Take(10))}."),
        };
    }

    internal static string Signature(FunctionData function)
    {
        var parameters = function.Params.Where(o => !o.Flags.HasFlag(EPropertyFlags.ReturnParm))
            .Select(o => $"{(o.Flags.HasFlag(EPropertyFlags.OutParm) && !o.Flags.HasFlag(EPropertyFlags.ReferenceParm) ? "out " : o.Flags.HasFlag(EPropertyFlags.ReferenceParm) ? "ref " : "")}" +
                         $"{GraphText.DescribeType(o.PinType)} {o.Name}");
        var returns = function.Params.FirstOrDefault(o => o.Flags.HasFlag(EPropertyFlags.ReturnParm));
        var prefix = (function.Flags.HasFlag(EFunctionFlags.FUNC_Static) ? "static " : "") + (function.IsPure ? "pure " : "");
        return $"{prefix}{(returns == null ? "void" : GraphText.DescribeType(returns.PinType))} {function.Outer?.Name ?? "?"}:{function.Name}({string.Join(", ", parameters)})";
    }

    private static string Flags(EPropertyFlags flags, bool parameter)
    {
        var names = new List<string>();
        if (parameter)
        {
            if (flags.HasFlag(EPropertyFlags.ReturnParm)) names.Add("return");
            else if (flags.HasFlag(EPropertyFlags.OutParm)) names.Add(flags.HasFlag(EPropertyFlags.ReferenceParm) ? "ref" : "out");
            if (flags.HasFlag(EPropertyFlags.ConstParm)) names.Add("const");
        }
        else
        {
            if (flags.HasFlag(EPropertyFlags.BlueprintVisible)) names.Add(flags.HasFlag(EPropertyFlags.BlueprintReadOnly) ? "bp-readonly" : "bp-readwrite");
            if (flags.HasFlag(EPropertyFlags.Edit)) names.Add("editable");
            if (flags.HasFlag(EPropertyFlags.Net)) names.Add("replicated");
            if (flags.HasFlag(EPropertyFlags.Config)) names.Add("config");
            if (flags.HasFlag(EPropertyFlags.Transient)) names.Add("transient");
        }
        return names.Count == 0 ? "" : $"  [{string.Join(", ", names)}]";
    }
}
