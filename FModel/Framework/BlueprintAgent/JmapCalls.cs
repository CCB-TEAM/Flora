using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CUE4Parse.UE4.Objects.UObject;
using FModel.BlueprintGraph.Engine;
using FModel.BlueprintGraph.Signatures;
using static FModel.BlueprintGraph.Engine.EngineBPData;
using static FModel.BlueprintGraph.Engine.EngineEnums;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// Native calls written as bytecode objects, checked against the jmap: how many parameters, which are out/ref (they take variables), what the call returns.
/// <see cref="Template"/> writes the call with its parameters in order and the locals it needs; <see cref="Check"/> reads a statement back and lists what doesn't fit.
/// </summary>
public static class JmapCalls
{
    private static readonly string[] CallOps = ["EX_CallMath", "EX_FinalFunction", "EX_LocalFinalFunction", "EX_VirtualFunction", "EX_LocalVirtualFunction"];

    private static readonly string[] VariableOps = ["EX_LocalVariable", "EX_InstanceVariable", "EX_DefaultVariable", "EX_LocalOutVariable", "EX_ClassSparseDataVariable", "EX_Context", "EX_Context_FailSilent", "EX_StructMemberContext"];

    public static IEnumerable<AgentTool> Build() =>
    [
        new()
        {
            Name = "jmap_call_template",
            Description = "A fill-in bytecode statement for calling a native function, built from the jmap: the call with its parameters in the engine's order (placeholders say the type of each), " +
                          "wrapped in a Context when it is an instance function, assigned to a local when it returns a value, and the kismet_add_local declarations the out parameters and the result need. " +
                          "Replace every \"<...>\" placeholder with an expression object, then pass the statement to kismet_insert_after / kismet_replace; the edit tools check the call again against the jmap.",
            InputSchema = Object(("class", String("Class: full path (/Script/Engine.KismetSystemLibrary) or short name"), true), ("function", String("Function name, e.g. PrintString"), true),
                ("function_in", String("Name of the bytecode function the statement goes into (for the locals' owner), e.g. ExecuteUbergraph_BP_Hero"), false),
                ("target", String("For instance functions: 'self' calls it on this object without a Context (default: a Context with a target placeholder)"), false)),
            Run = (args, _) => Task.FromResult(Template(args.Str("class"), args.Str("function"), args.Str("function_in", false) ?? "<function>", args.Str("target", false))),
        },
        new()
        {
            Name = "jmap_check_call",
            Description = "Checks a native call statement (an object as passed to the kismet edit tools) against the jmap without changing anything: the function exists, the number of parameters, " +
                          "out/ref parameters are variables, obvious literal type mismatches, the result is used. Calls nested anywhere in the statement are checked.",
            InputSchema = Object(("statement", Any("Statement object (or an array of them)"), true)),
            Run = (args, _) =>
            {
                var problems = Check(args.Json("statement"));
                return Task.FromResult(problems.Count == 0
                    ? "No problems found (only calls whose function the jmap knows are checked)."
                    : string.Join("\n", problems.Select(o => "- " + o)));
            },
        },
    ];

    /// <summary>Finds a function by an "import:/Script/Engine.Library:Name" path or by owner class and name.</summary>
    public static FunctionData Find(string classOrNull, string name)
    {
        var jmaps = JmapAgentTools.Jmaps(true);
        if (classOrNull != null)
        {
            var owner = JmapAgentTools.FindObject(classOrNull);
            for (var current = owner; current != null; current = current.SuperStruct)
                if (current.GetFunction(name) is { } function)
                    return function;
            throw new AgentToolException($"{owner.Name} has no function '{name}' (jmap_class lists them).");
        }
        return jmaps.SelectMany(o => o.Functions).FirstOrDefault(o => o.Name == name);
    }

    private static string LocalType(GraphPinType type)
    {
        string scalar = type.PinCategory switch
        {
            PinType.Bool => "bool",
            PinType.Byte => "byte",
            PinType.Int => "int",
            PinType.Int64 => "int64",
            PinType.Float => "float",
            PinType.Double => "double",
            PinType.Name => "name",
            PinType.String => "string",
            PinType.Text => "text",
            PinType.Object or PinType.Interface => $"object:{type.PinSubCategoryObject}",
            PinType.Class => $"class:{type.PinSubCategoryObject}",
            PinType.SoftObject => $"softobject:{type.PinSubCategoryObject}",
            PinType.Enum => $"enum:{type.PinSubCategoryObject}",
            _ => null, // structs (Vector, Transform...), delegates: copy the type from an existing property of the package
        };
        if (scalar != null && type.ContainerType == EPinContainerType.Array)
            return "array:" + scalar;
        return type.ContainerType == EPinContainerType.None ? scalar : null;
    }

    private static string Placeholder(PropertyData parameter) => $"<{GraphText.DescribeType(parameter.PinType)} {parameter.Name}>";

    private static JObject Variable(string name, string function) => new()
    {
        ["op"] = "EX_LocalVariable",
        ["Variable"] = new JObject { ["path"] = new JArray(name), ["owner"] = $"export:{function}" },
    };

    public static string Template(string className, string functionName, string functionIn, string target)
    {
        var function = Find(className, functionName);
        var owner = function.Outer?.Name ?? "?";
        var isStatic = function.Flags.HasFlag(EFunctionFlags.FUNC_Static);
        var parameters = function.Params.Where(o => !o.Flags.HasFlag(EPropertyFlags.ReturnParm)).ToList();
        var returns = function.Params.FirstOrDefault(o => o.Flags.HasFlag(EPropertyFlags.ReturnParm));

        var notes = new List<string>();
        var declarations = new List<string>();
        var arguments = new JArray();
        foreach (var parameter in parameters)
        {
            var isOut = parameter.Flags.HasFlag(EPropertyFlags.OutParm);
            if (!isOut)
            {
                arguments.Add(Placeholder(parameter));
                continue;
            }
            var local = $"{function.Name}_{parameter.Name}";
            arguments.Add(Variable(local, functionIn));
            notes.Add($"{parameter.Name} is an {(parameter.Flags.HasFlag(EPropertyFlags.ReferenceParm) ? "in/out (by reference)" : "out")} parameter: it takes a variable ({local}), not a value");
            declarations.Add(Declaration(local, parameter.PinType, functionIn, $"receives {parameter.Name} of {function.Name}"));
        }

        var call = new JObject();
        if (isStatic)
        {
            call["op"] = "EX_CallMath";
            call["StackNode"] = $"import:{owner}:{function.Name}";
        }
        else
        {
            call["op"] = "EX_FinalFunction";
            call["StackNode"] = $"import:{owner}:{function.Name}";
            notes.Add($"{function.Name} is an instance function. EX_FinalFunction calls the native function directly; for a function that can be overridden the blueprint compiler may emit EX_VirtualFunction {{\"VirtualFunctionName\": \"{function.Name}\"}} instead (match what the surrounding code does)");
        }
        call["Parameters"] = arguments;

        JToken expression = call;
        if (!isStatic && !string.Equals(target, "self", StringComparison.OrdinalIgnoreCase))
        {
            expression = new JObject
            {
                ["op"] = "EX_Context",
                ["ObjectExpression"] = "<object expression: the object to call it on>",
                ["RValuePointer"] = returns != null ? "<property reference of the result variable>" : null,
                ["ContextExpression"] = call,
                ["PropertyType"] = 0,
            };
            notes.Add("the target is a Context: ObjectExpression yields the object; if it is null at run time the call is skipped");
        }

        JToken statement = expression;
        if (returns != null)
        {
            var local = $"{function.Name}_Result";
            declarations.Add(Declaration(local, returns.PinType, functionIn, $"the result of {function.Name}"));
            var pointer = Variable(local, functionIn);
            statement = new JObject { ["op"] = "EX_Let", ["Value"] = pointer["Variable"]!.DeepClone(), ["Variable"] = pointer, ["Expression"] = expression };
            if (expression is JObject context && context["op"]?.ToString() == "EX_Context")
                context["RValuePointer"] = pointer["Variable"]!.DeepClone();
            notes.Add($"it returns {GraphText.DescribeType(returns.PinType)}: the statement assigns it to {local} (drop the EX_Let to ignore the result)");
        }
        if (function.IsPure)
            notes.Add("pure function: it has no execution side effects, so it is usually an expression inside another statement rather than a statement of its own");

        var sb = new StringBuilder();
        sb.AppendLine(JmapAgentTools.Signature(function));
        sb.AppendLine();
        sb.AppendLine("Statement (replace every \"<...>\" with an expression object):");
        sb.AppendLine(statement.ToString(Formatting.Indented));
        if (declarations.Count > 0)
        {
            sb.AppendLine().AppendLine("Declare first:");
            foreach (var declaration in declarations)
                sb.AppendLine("  " + declaration);
        }
        if (notes.Count > 0)
        {
            sb.AppendLine().AppendLine("Notes:");
            foreach (var note in notes)
                sb.AppendLine("  - " + note);
        }
        return sb.ToString().TrimEnd();
    }

    private static string Declaration(string name, GraphPinType type, string function, string description)
    {
        var text = LocalType(type);
        return text != null
            ? $"kismet_add_local function={function} name={name} type={text}  ({description})"
            : $"kismet_add_local function={function} name={name} like=<a property of type {GraphText.DescribeType(type)} that the package already has>  ({description})";
    }

    /// <summary>Problems of the native calls in a statement (or array of statements); empty when everything the jmap knows fits.</summary>
    public static List<string> Check(JToken statement)
    {
        var problems = new List<string>();
        try
        {
            if (JmapAgentTools.Jmaps(false).Count == 0)
                return problems;
        }
        catch (Exception)
        {
            return problems;
        }
        foreach (var token in (statement is JArray array ? array : [statement]).SelectMany(o => o.SelectTokens("$..*").Prepend(o)).OfType<JObject>().Distinct())
        {
            var op = token["op"]?.ToString();
            if (op == null || !CallOps.Contains(op))
                continue;
            var stackNode = token["StackNode"]?.ToString();
            FunctionData function = null;
            string name = stackNode;
            if (stackNode != null && stackNode.StartsWith("import:", StringComparison.Ordinal))
            {
                var path = stackNode["import:".Length..];
                var colon = path.LastIndexOf(':');
                if (colon <= 0)
                    continue;
                name = path[(colon + 1)..];
                try { function = Find(path[..colon], name); }
                catch (AgentToolException) { continue; } // not a native class of the jmap (a blueprint's own function)
            }
            else if (token["VirtualFunctionName"]?.ToString() is { Length: > 0 } virtualName)
            {
                name = virtualName;
                function = Find(null, virtualName); // by name only: the class isn't known, a unique match is enough
                if (function == null || JmapAgentTools.Jmaps(false).SelectMany(o => o.Functions).Count(o => o.Name == virtualName) != 1)
                    continue;
            }
            if (function == null)
                continue;

            var where = $"{op} {name}";
            var parameters = function.Params.Where(o => !o.Flags.HasFlag(EPropertyFlags.ReturnParm)).ToList();
            var given = token["Parameters"] as JArray ?? [];
            if (given.Count != parameters.Count)
            {
                problems.Add($"{where}: the jmap has {parameters.Count} parameter(s) ({string.Join(", ", parameters.Select(o => o.Name))}) but the statement passes {given.Count}. " +
                             $"Parameters go in that order; every one is passed, out parameters as variables. Signature: {JmapAgentTools.Signature(function)}");
                continue;
            }
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                if (given[i] is not JObject expression)
                {
                    problems.Add($"{where}: parameter {i} ({parameter.Name}) must be an expression object, found {given[i].Type}.");
                    continue;
                }
                var parameterOp = expression["op"]?.ToString() ?? "";
                if (parameter.Flags.HasFlag(EPropertyFlags.OutParm) && !VariableOps.Contains(parameterOp))
                    problems.Add($"{where}: parameter {i} ({parameter.Name}) is an {(parameter.Flags.HasFlag(EPropertyFlags.ReferenceParm) ? "in/out" : "out")} parameter and needs a variable (EX_LocalVariable, EX_InstanceVariable...), found {parameterOp}. Declare a local with kismet_add_local.");
                else if (LiteralMismatch(parameter.PinType, parameterOp) is { } mismatch)
                    problems.Add($"{where}: parameter {i} ({parameter.Name}) is {GraphText.DescribeType(parameter.PinType)} but the statement passes {parameterOp}; {mismatch}");
            }
        }
        return problems;
    }

    /// <summary>A literal of the wrong kind for a parameter, or null (variables and calls are not judged).</summary>
    private static string LiteralMismatch(GraphPinType type, string op)
    {
        if (type.ContainerType != EPinContainerType.None || !op.EndsWith("Const", StringComparison.Ordinal) && op is not ("EX_True" or "EX_False" or "EX_Self" or "EX_NoObject" or "EX_NoInterface"))
            return null;
        string[] expected = type.PinCategory switch
        {
            PinType.Bool => ["EX_True", "EX_False"],
            PinType.Int => ["EX_IntConst", "EX_IntZero", "EX_IntOne", "EX_Int64Const"],
            PinType.Int64 => ["EX_Int64Const", "EX_UInt64Const", "EX_IntConst", "EX_IntZero", "EX_IntOne"],
            PinType.Float or PinType.Double => ["EX_FloatConst", "EX_DoubleConst", "EX_IntConst", "EX_IntZero", "EX_IntOne"],
            PinType.Byte or PinType.Enum => ["EX_ByteConst", "EX_IntConstByte", "EX_IntConst"],
            PinType.Name => ["EX_NameConst"],
            PinType.String => ["EX_StringConst", "EX_UnicodeStringConst"],
            PinType.Text => ["EX_TextConst"],
            PinType.Object or PinType.Interface or PinType.Class => ["EX_ObjectConst", "EX_NoObject", "EX_NoInterface", "EX_Self", "EX_InterfaceContext"],
            _ => null,
        };
        return expected == null || expected.Contains(op) ? null : $"expected {string.Join(" / ", expected)}.";
    }
}
