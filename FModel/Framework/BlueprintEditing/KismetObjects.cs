#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace FModel.Framework.BlueprintEditing;

/// <summary>
/// Kismet bytecode as nested JSON objects that an LLM can read and write: <c>{"op": "EX_Let", "Variable": {...}, "Expression": {...}}</c>.
/// <para>
/// Nothing in the JSON is an index or a byte offset the writer has to compute:
/// objects are <c>"import:/Script/Engine.KismetSystemLibrary:PrintString"</c> / <c>"export:ExecuteUbergraph_BP_Hero"</c>,
/// properties are <c>{"path": ["Health"], "owner": "export:BP_Hero_C"}</c>, names are plain strings, and code offsets
/// (jumps, execution flow pushes, latent resume points) are statement references: <c>"@120"</c> (a statement of the original
/// bytecode, by its offset) or <c>"#label"</c> (an inserted statement carrying <c>"label": "..."</c>).
/// Offsets derived from sizes (context and skip lengths, switch ends and cases) are left out and computed when the code is laid out.
/// </para>
/// Read output carries <c>"@": offset</c> on every expression (ignored on input) so nested expressions can be targeted.
/// </summary>
public static class KismetObjects
{
    public const string OpKey = "op";
    public const string OffsetKey = "@";
    public const string LabelKey = "label";

    private static readonly Dictionary<EExprToken, Type> TypesByToken = BuildTypes();

    private static Dictionary<EExprToken, Type> BuildTypes()
    {
        var types = new Dictionary<EExprToken, Type>();
        foreach (var type in typeof(KismetExpression).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.ContainsGenericParameters || !typeof(KismetExpression).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                continue;
            var token = ((KismetExpression) Activator.CreateInstance(type)!).Token;
            if (token == EExprToken.EX_Nothing && type != typeof(EX_Nothing))
                continue;
            // base classes may report a token of their own: the class named after the token wins
            if (type.Name == token.ToString())
                types[token] = type;
            else
                types.TryAdd(token, type);
        }
        return types;
    }

    public static IReadOnlyCollection<EExprToken> Tokens => TypesByToken.Keys;

    public static bool TryGetType(string op, out Type type)
    {
        type = null!;
        return Enum.TryParse<EExprToken>(op, out var token) && TypesByToken.TryGetValue(token, out type!);
    }

    #region members

    public enum MemberRole
    {
        Value,

        /// <summary>An absolute code offset, written as a statement reference.</summary>
        CodeTarget,

        /// <summary>Computed from sizes when the code is laid out; never shown or read.</summary>
        Derived,
    }

    public sealed record Member(string Name, Type Type, MemberRole Role, Func<object, object?> Get, Action<object, object?> Set);

    private static readonly Dictionary<Type, List<Member>> MemberCache = new();

    /// <summary>The serialized members of an expression class (or helper struct) in declaration order.</summary>
    public static IReadOnlyList<Member> MembersOf(Type type)
    {
        lock (MemberCache)
        {
            if (MemberCache.TryGetValue(type, out var cached))
                return cached;

            var members = new List<Member>();
            var chain = new List<Type>();
            for (var t = type; t != null && t != typeof(object) && t != typeof(KismetExpression); t = t.BaseType)
                chain.Insert(0, t);

            foreach (var t in chain)
            {
                foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(o => o.MetadataToken))
                    members.Add(new Member(field.Name, field.FieldType, RoleOf(type, field.Name), field.GetValue, field.SetValue));
                // KismetExpression<T>.Value
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KismetExpression<>) &&
                    t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is { CanWrite: true } value)
                    members.Add(new Member(value.Name, value.PropertyType, RoleOf(type, value.Name), value.GetValue, value.SetValue));
            }

            MemberCache[type] = members;
            return members;
        }
    }

    private static MemberRole RoleOf(Type owner, string name)
    {
        if (owner == typeof(EX_Jump) && name == nameof(EX_Jump.CodeOffset) ||
            owner == typeof(EX_JumpIfNot) && name == nameof(EX_JumpIfNot.CodeOffset) ||
            owner == typeof(EX_PushExecutionFlow) && name == nameof(EX_PushExecutionFlow.PushingAddress) ||
            owner == typeof(EX_SkipOffsetConst) && name == "Value")
            return MemberRole.CodeTarget;
        if (typeof(EX_Context).IsAssignableFrom(owner) && name == nameof(EX_Context.Offset) ||
            owner == typeof(EX_Skip) && name == nameof(EX_Skip.CodeOffset) ||
            owner == typeof(EX_SwitchValue) && name == nameof(EX_SwitchValue.EndGotoOffset) ||
            owner == typeof(FKismetSwitchCase) && name == nameof(FKismetSwitchCase.NextOffset))
            return MemberRole.Derived;
        return MemberRole.Value;
    }

    /// <summary>A short description of the JSON shape of one expression, generated from the UAssetAPI class.</summary>
    public static JObject Describe(Type type)
    {
        var fields = new JObject();
        foreach (var member in MembersOf(type))
        {
            if (member.Role == MemberRole.Derived)
                continue;
            fields[member.Name] = member.Role == MemberRole.CodeTarget ? "statement reference (\"@<original offset>\" or \"#<label>\")" : DescribeType(member.Type);
        }
        return new JObject { [OpKey] = ((KismetExpression) Activator.CreateInstance(type)!).Token.ToString(), ["fields"] = fields };
    }

    private static string DescribeType(Type type)
    {
        if (type == typeof(KismetExpression)) return "expression object";
        if (type == typeof(KismetExpression[])) return "array of expression objects";
        if (type == typeof(FPackageIndex)) return "object reference (\"import:<path>\", \"export:<name>\" or null)";
        if (type == typeof(KismetPropertyPointer)) return "property reference ({\"path\":[\"Name\"],\"owner\":\"export:...\"} or {\"object\":\"import:...\"})";
        if (type == typeof(FName)) return "name (string)";
        if (type == typeof(FKismetSwitchCase[])) return "array of {\"CaseIndexValueTerm\": expression, \"CaseTerm\": expression}";
        if (type == typeof(FScriptText)) return "text object {TextLiteralType, LocalizedSource, LocalizedKey, LocalizedNamespace, InvariantLiteralString, LiteralString, StringTableAsset, StringTableId, StringTableKey}";
        if (type.IsEnum) return "one of " + string.Join(", ", Enum.GetNames(type));
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "bool";
        if (type.IsPrimitive) return type.Name.ToLowerInvariant();
        return type.Name + " (JSON as UAssetAPI serializes it)";
    }

    #endregion

    #region reading code (UAssetAPI -> JSON)

    /// <summary>The in-memory offset of every expression of the function.</summary>
    public static Dictionary<KismetExpression, uint> Offsets(UAsset asset, StructExport function)
    {
        var offsets = new Dictionary<KismetExpression, uint>(ReferenceEqualityComparer.Instance);
        uint position = 0;
        foreach (var statement in function.ScriptBytecode)
            statement.Visit(asset, ref position, (expression, offset) => offsets.TryAdd(expression, offset));
        return offsets;
    }

    public static JObject ToJson(UAsset asset, KismetExpression expression, IReadOnlyDictionary<KismetExpression, uint>? offsets = null)
    {
        var json = new JObject { [OpKey] = expression.Token.ToString() };
        if (offsets != null && offsets.TryGetValue(expression, out var offset))
            json[OffsetKey] = offset;
        foreach (var member in MembersOf(expression.GetType()))
        {
            if (member.Role == MemberRole.Derived)
                continue;
            var value = member.Get(expression);
            json[member.Name] = member.Role == MemberRole.CodeTarget
                ? $"@{Convert.ToUInt32(value, CultureInfo.InvariantCulture)}"
                : WriteValue(asset, member.Type, value, offsets);
        }
        return json;
    }

    private static JToken WriteValue(UAsset asset, Type type, object? value, IReadOnlyDictionary<KismetExpression, uint>? offsets)
    {
        switch (value)
        {
            case null:
                return JValue.CreateNull();
            case KismetExpression expression:
                return ToJson(asset, expression, offsets);
            case KismetExpression[] expressions:
                return new JArray(expressions.Select(o => o == null ? JValue.CreateNull() : (JToken) ToJson(asset, o, offsets)));
            case FPackageIndex index:
                return IndexSymbol(asset, index) is { } symbol ? new JValue(symbol) : JValue.CreateNull();
            case KismetPropertyPointer pointer:
                return PointerJson(asset, pointer);
            case FName name:
                return new JValue(name.ToString());
            case FKismetSwitchCase[] cases:
                return new JArray(cases.Select(o => new JObject
                {
                    [nameof(FKismetSwitchCase.CaseIndexValueTerm)] = WriteValue(asset, typeof(KismetExpression), o.CaseIndexValueTerm, offsets),
                    [nameof(FKismetSwitchCase.CaseTerm)] = WriteValue(asset, typeof(KismetExpression), o.CaseTerm, offsets),
                }));
            case FScriptText text:
            {
                var json = new JObject();
                foreach (var member in MembersOf(typeof(FScriptText)))
                    json[member.Name] = WriteValue(asset, member.Type, member.Get(text), offsets);
                return json;
            }
            case Enum e:
                return new JValue(e.ToString());
            case string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double:
                return new JValue(value);
            default:
                return JToken.Parse(asset.SerializeJsonObject(value));
        }
    }

    /// <summary><c>import:/Script/Engine.KismetSystemLibrary:PrintString</c>, <c>export:Name</c> or null.</summary>
    public static string? IndexSymbol(UAsset asset, FPackageIndex? index)
    {
        if (index == null || index.IsNull())
            return null;
        if (index.IsExport())
        {
            var export = index.ToExport(asset);
            var name = export.ObjectName.ToString();
            var duplicates = asset.Exports.Count(o => o.ObjectName.ToString() == name);
            return duplicates > 1 ? $"export:{name}#{index.Index}" : $"export:{name}";
        }
        return "import:" + ImportPath(asset, index);
    }

    public static string ImportPath(UAsset asset, FPackageIndex index)
    {
        var names = new List<string>();
        for (var current = index; current != null && current.IsImport(); current = current.ToImport(asset).OuterIndex)
            names.Insert(0, current.ToImport(asset).ObjectName.ToString());
        if (names.Count == 1)
            return names[0];
        var path = names[0] + "." + names[1];
        return names.Count > 2 ? path + ":" + string.Join(".", names.Skip(2)) : path;
    }

    private static JToken PointerJson(UAsset asset, KismetPropertyPointer pointer)
    {
        if (pointer.New is { Path.Length: > 0 } path)
            return new JObject { ["path"] = new JArray(path.Path.Select(o => o.ToString())), ["owner"] = IndexSymbol(asset, path.ResolvedOwner) };
        if (pointer.Old != null && !pointer.Old.IsNull())
            return new JObject { ["object"] = IndexSymbol(asset, pointer.Old) };
        return JValue.CreateNull();
    }

    #endregion

    #region writing code (JSON -> UAssetAPI)

    /// <summary>A code offset of a built expression that still has to point at a statement.</summary>
    public sealed record PendingTarget(string Reference, Action<uint> Write, string Description);

    /// <summary>
    /// Builds UAssetAPI expressions from JSON objects. In check mode nothing is added to the asset (names, imports):
    /// the JSON is only validated and what would be added is listed in <see cref="Notes"/>.
    /// </summary>
    public sealed class Builder
    {
        public UAsset Asset { get; }
        public bool Create { get; }
        public List<PendingTarget> Targets { get; } = [];
        public List<string> Notes { get; } = [];

        /// <summary>Statements read with a <c>"label"</c>, by label.</summary>
        public Dictionary<string, KismetExpression> Labels { get; } = new(StringComparer.Ordinal);

        /// <summary>Locals declared by pending edits (not in the package yet in check mode): (owner export name, property name).</summary>
        public HashSet<(string Owner, string Name)> DeclaredLocals { get; } = [];

        /// <summary>Resolves an object reference ("import:...", "export:..."); null in check mode for imports that don't exist yet.</summary>
        public FPackageIndex? Reference(string symbol, string path) => ResolveIndex(new JValue(symbol), path);

        private readonly Lazy<(long Context, long Skip)> _bias;

        public Builder(UAsset asset, bool create)
        {
            Asset = asset;
            Create = create;
            _bias = new Lazy<(long, long)>(MeasureBias);
        }

        public KismetExpression Build(JToken token, string path = "$")
        {
            if (token is not JObject json)
                throw Error(path, "expected an expression object with an \"op\"");
            var op = json.Value<string>(OpKey);
            if (op == null || !TryGetType(op, out var type))
                throw Error(path, $"unknown op '{op}' (use a token name such as EX_Let, EX_CallMath, EX_IntConst)");

            var expression = (KismetExpression) Activator.CreateInstance(type)!;
            var known = new HashSet<string>(StringComparer.Ordinal) { OpKey, OffsetKey, LabelKey };
            foreach (var member in MembersOf(type))
            {
                known.Add(member.Name);
                if (member.Role == MemberRole.Derived)
                    continue;
                var memberPath = $"{path}.{member.Name}";
                if (!json.TryGetValue(member.Name, out var value))
                {
                    if (expression is EX_StructConst && member.Name == nameof(EX_StructConst.StructSize))
                        continue; // filled in below
                    throw Error(memberPath, $"{op} needs '{member.Name}' ({DescribeType(member.Type)})");
                }

                if (member.Role == MemberRole.CodeTarget)
                {
                    var reference = value.Type == JTokenType.String ? value.Value<string>()! : "";
                    if (!reference.StartsWith('@') && !reference.StartsWith('#'))
                        throw Error(memberPath, "a code offset is written as a statement reference: \"@<offset of an original statement>\" or \"#<label>\"");
                    var target = expression;
                    Targets.Add(new PendingTarget(reference, o => member.Set(target, ConvertOffset(member.Type, o)), $"{op}.{member.Name} at {memberPath}"));
                    member.Set(expression, ConvertOffset(member.Type, 0));
                    continue;
                }
                member.Set(expression, ReadValue(member.Type, value, memberPath));
            }

            foreach (var property in json.Properties())
            {
                if (!known.Contains(property.Name))
                    throw Error($"{path}.{property.Name}", $"{op} has no field '{property.Name}'; its fields are {string.Join(", ", MembersOf(type).Where(o => o.Role != MemberRole.Derived).Select(o => o.Name))}");
            }

            // EX_StringConst is 8-bit text: a string with other characters would be written with the wrong size, so use the UTF-16 token
            if (expression is EX_StringConst narrow && narrow.Value != null && narrow.Value.Any(c => c > 0x7F))
            {
                expression = new EX_UnicodeStringConst { Value = narrow.Value };
                Notes.Add($"{path}: the text has non-ASCII characters, so EX_StringConst was replaced by EX_UnicodeStringConst");
            }

            if (json.Value<string>(LabelKey) is { Length: > 0 } label && !Labels.TryAdd(label, expression))
                throw Error(path, $"the label '{label}' is used twice");

            if (Create)
                FillDerived(expression, json, path);
            return expression;
        }

        private static object ConvertOffset(Type type, uint value) => type == typeof(int) ? checked((int) value) : value;

        private object? ReadValue(Type type, JToken value, string path)
        {
            if (value.Type == JTokenType.Null)
            {
                // a null expression or switch list would fail while the code is laid out, far from the cause: name the field now
                if (type == typeof(KismetExpression) || type == typeof(KismetExpression[]) || type == typeof(FKismetSwitchCase[]))
                    throw Error(path, $"must not be null: expected {DescribeType(type)}" + (type == typeof(KismetExpression) ? " (use {\"op\": \"EX_Nothing\"} for \"no value\")" : ""));
                // "no property" (the RValuePointer of a call without a return value): a pointer with an empty path, not a missing one
                if (type == typeof(KismetPropertyPointer))
                    return Create ? NullPointer() : null;
                return null;
            }

            if (type == typeof(KismetExpression))
                return Build(value, path);
            if (type == typeof(KismetExpression[]))
                return value is JArray array
                    ? array.Select((o, i) => Build(o, $"{path}[{i}]")).ToArray()
                    : throw Error(path, "expected an array of expression objects");
            if (type == typeof(FPackageIndex))
                return ResolveIndex(value, path);
            if (type == typeof(KismetPropertyPointer))
                return ResolvePointer(value, path);
            if (type == typeof(FName))
                return value.Type == JTokenType.String ? Name(value.Value<string>()!) : throw Error(path, "expected a name string");
            if (type == typeof(FKismetSwitchCase[]))
            {
                if (value is not JArray cases)
                    throw Error(path, "expected an array of switch cases");
                return cases.Select((o, i) => new FKismetSwitchCase
                {
                    CaseIndexValueTerm = Build(o[nameof(FKismetSwitchCase.CaseIndexValueTerm)] ?? JValue.CreateNull(), $"{path}[{i}].CaseIndexValueTerm"),
                    CaseTerm = Build(o[nameof(FKismetSwitchCase.CaseTerm)] ?? JValue.CreateNull(), $"{path}[{i}].CaseTerm"),
                }).ToArray();
            }
            if (type == typeof(FScriptText))
            {
                if (value is not JObject obj)
                    throw Error(path, "expected a text object");
                var text = new FScriptText();
                foreach (var member in MembersOf(typeof(FScriptText)))
                {
                    if (obj.TryGetValue(member.Name, out var field))
                        member.Set(text, ReadValue(member.Type, field, $"{path}.{member.Name}"));
                }
                return text;
            }
            if (type.IsEnum)
            {
                return value.Type == JTokenType.String && Enum.TryParse(type, value.Value<string>(), true, out var parsed)
                    ? parsed
                    : value.Type == JTokenType.Integer ? Enum.ToObject(type, value.Value<long>()) : throw Error(path, $"expected {DescribeType(type)}");
            }
            if (type == typeof(string) || type.IsPrimitive)
            {
                try { return value.ToObject(type); }
                catch (Exception e) { throw Error(path, $"expected {DescribeType(type)}: {e.Message}"); }
            }

            try
            {
                var method = typeof(UAsset).GetMethod(nameof(UAsset.DeserializeJsonObject))!.MakeGenericMethod(type);
                return method.Invoke(Asset, [value.ToString(Formatting.None)]);
            }
            catch (Exception e)
            {
                throw Error(path, $"can't read a {type.Name}: {(e.InnerException ?? e).Message}");
            }
        }

        private KismetPropertyPointer NullPointer() =>
            Asset.GetCustomVersion<UAssetAPI.CustomVersions.FReleaseObjectVersion>() >= UAssetAPI.CustomVersions.FReleaseObjectVersion.FFieldPathOwnerSerialization
                ? new KismetPropertyPointer(new FFieldPath([], FPackageIndex.FromRawIndex(0)))
                : new KismetPropertyPointer(FPackageIndex.FromRawIndex(0));

        private FName? Name(string text)
        {
            if (!Create)
            {
                if (!Asset.ContainsNameReference(new FString(text)))
                    Notes.Add($"adds '{text}' to the name map (only a name: it declares no variable)");
                return null;
            }
            Asset.AddNameReference(new FString(text));
            return FName.FromString(Asset, text);
        }

        #region references

        private FPackageIndex? ResolveIndex(JToken value, string path)
        {
            string? symbol;
            string? className = null, classPackage = null;
            if (value is JObject obj)
            {
                symbol = obj.Value<string>("import") is { } import ? "import:" + import : null;
                className = obj.Value<string>("className");
                classPackage = obj.Value<string>("classPackage");
            }
            else
            {
                symbol = value.Type == JTokenType.String ? value.Value<string>() : null;
            }

            if (symbol == null)
                throw Error(path, "expected an object reference: \"import:<path>\", \"export:<name>\", null, or {\"import\": path, \"className\": ..., \"classPackage\": ...}");
            if (symbol.StartsWith("export:", StringComparison.Ordinal))
                return ResolveExport(symbol["export:".Length..], path);
            if (symbol.StartsWith("import:", StringComparison.Ordinal))
                return ResolveImport(symbol["import:".Length..], className, classPackage, path);
            throw Error(path, $"'{symbol}' is not an object reference (\"import:...\" or \"export:...\")");
        }

        private FPackageIndex ResolveExport(string name, string path)
        {
            var hash = name.LastIndexOf('#');
            if (hash > 0 && int.TryParse(name[(hash + 1)..], out var raw))
            {
                if (raw <= 0 || raw > Asset.Exports.Count)
                    throw Error(path, $"there is no export #{raw}");
                return FPackageIndex.FromRawIndex(raw);
            }
            var index = Asset.Exports.FindIndex(o => o.ObjectName.ToString() == name);
            if (index < 0)
                throw Error(path, $"the package has no export named '{name}'");
            return FPackageIndex.FromExport(index);
        }

        /// <summary>Splits <c>/Script/Engine.KismetSystemLibrary:PrintString</c> into the package and the names below it.</summary>
        public static List<string> SplitImportPath(string path)
        {
            var dot = path.IndexOf('.', path.LastIndexOf('/') + 1);
            if (dot < 0)
                return [path];
            var names = new List<string> { path[..dot] };
            names.AddRange(path[(dot + 1)..].Split(':', '.').Where(o => o.Length > 0));
            return names;
        }

        private FPackageIndex? ResolveImport(string importPath, string? leafClassName, string? leafClassPackage, string path)
        {
            var names = SplitImportPath(importPath);
            FPackageIndex? outer = null;
            for (var level = 0; level < names.Count; level++)
            {
                var name = names[level];
                var found = Asset.Imports.FindIndex(o => o.ObjectName.ToString() == name &&
                                                         (outer == null ? o.OuterIndex == null || o.OuterIndex.IsNull() : o.OuterIndex != null && o.OuterIndex.Index == outer.Index));
                if (found >= 0)
                {
                    outer = FPackageIndex.FromImport(found);
                    continue;
                }

                var isLeaf = level == names.Count - 1;
                var (classPackage, className) = isLeaf && leafClassName != null
                    ? (leafClassPackage ?? "/Script/CoreUObject", leafClassName)
                    : InferImportClass(names, level);
                var description = $"import {string.Join("/", names.Take(level + 1))} ({classPackage}.{className})";
                if (!Create)
                {
                    Notes.Add("adds the " + description);
                    return null; // the rest is checked when the edit is applied
                }
                foreach (var text in new[] { classPackage, className, name })
                    Asset.AddNameReference(new FString(text));
                outer = Asset.AddImport(new Import(classPackage, className, outer ?? FPackageIndex.FromRawIndex(0), name, false, Asset));
                Notes.Add("added the " + description);
            }
            return outer;
        }

        private static (string Package, string Class) InferImportClass(IReadOnlyList<string> names, int level)
        {
            if (level == 0)
                return ("/Script/CoreUObject", "Package");
            if (level == 1)
            {
                if (names[0].StartsWith("/Script/", StringComparison.Ordinal))
                    return ("/Script/CoreUObject", "Class");
                if (names[1].EndsWith("_C", StringComparison.Ordinal))
                    return ("/Script/Engine", "BlueprintGeneratedClass");
                return ("/Script/CoreUObject", "Class");
            }
            return ("/Script/CoreUObject", "Function");
        }

        private KismetPropertyPointer? ResolvePointer(JToken value, string path)
        {
            if (value is not JObject obj)
                throw Error(path, "expected a property reference: {\"path\": [\"Name\"], \"owner\": \"export:...\"} or {\"object\": \"import:...\"}");
            if (obj["object"] is { } old)
                return new KismetPropertyPointer(ResolveIndex(old, $"{path}.object"));
            if (obj["path"] is not JArray names || names.Count == 0)
                throw Error(path, "a property reference needs a non-empty \"path\" (innermost name first)");
            var owner = obj["owner"] is { Type: not JTokenType.Null } ownerToken ? ResolveIndex(ownerToken, $"{path}.owner") : null;
            CheckDeclared(names.Select(o => o.ToString()).ToList(), owner, path);
            var fnames = names.Select(o => Name(o.Value<string>() ?? throw Error(path, "property path entries are strings"))).ToArray();
            if (!Create)
                return null;
            return new KismetPropertyPointer(new FFieldPath(fnames!, owner ?? FPackageIndex.FromRawIndex(0)));
        }

        /// <summary>
        /// A property of this package must be declared by its owner (a function's locals and parameters, a class's variables):
        /// the engine resolves the pointer when it loads the code, and a name alone makes it null.
        /// </summary>
        private void CheckDeclared(IReadOnlyList<string> names, FPackageIndex? owner, string path)
        {
            if (owner == null || !owner.IsExport() || names.Count != 1 || !KismetLocals.Supported(Asset))
                return; // imports and nested paths can't be checked here
            if (owner.ToExport(Asset) is not StructExport { LoadedProperties: { } properties } export)
                return;
            var name = names[0];
            var ownerName = export.ObjectName.ToString();
            if (properties.Any(o => o.Name.ToString() == name) || DeclaredLocals.Contains((ownerName, name)))
                return;
            var known = properties.Select(o => o.Name.ToString()).Take(40).ToList();
            throw Error(path, $"'{name}' is not a property of {ownerName}, so the engine couldn't resolve it. " +
                              (known.Count == 0 ? $"{ownerName} declares no properties. " : $"Its properties: {string.Join(", ", known)}. ") +
                              "Declare a new local first with kismet_add_local.");
        }

        #endregion

        #region derived values

        private void FillDerived(KismetExpression expression, JObject json, string path)
        {
            switch (expression)
            {
                case EX_Context context when context.ContextExpression != null:
                    context.Offset = checked((uint) (context.ContextExpression.GetSize(Asset) + _bias.Value.Context));
                    break;
                case EX_Skip skip when skip.SkipExpression != null:
                    skip.CodeOffset = checked((uint) (skip.SkipExpression.GetSize(Asset) + _bias.Value.Skip));
                    break;
                case EX_StructConst structConst when !json.ContainsKey(nameof(EX_StructConst.StructSize)):
                    structConst.StructSize = FindStructSize(structConst.Struct)
                                             ?? throw Error(path, "StructSize is needed: no other constant of this struct in the package tells its size");
                    break;
            }
        }

        private int? FindStructSize(FPackageIndex? structIndex)
        {
            if (structIndex == null)
                return null;
            foreach (var function in Asset.Exports.OfType<StructExport>().Where(o => o.ScriptBytecode != null))
            {
                int? found = null;
                uint position = 0;
                foreach (var statement in function.ScriptBytecode)
                    statement.Visit(Asset, ref position, (e, _) =>
                    {
                        if (found == null && e is EX_StructConst { Struct: { } other } constant && other.Index == structIndex.Index)
                            found = constant.StructSize;
                    });
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// The difference between the skip lengths the cooked code stores and the size UAssetAPI computes for the skipped expression,
        /// measured on the package's own contexts (0 when it has none).
        /// </summary>
        private (long Context, long Skip) MeasureBias()
        {
            var contexts = new List<long>();
            var skips = new List<long>();
            foreach (var function in Asset.Exports.OfType<StructExport>().Where(o => o.ScriptBytecode != null))
            {
                uint position = 0;
                foreach (var statement in function.ScriptBytecode)
                    statement.Visit(Asset, ref position, (e, _) =>
                    {
                        if (e is EX_Context { ContextExpression: { } c } context)
                            contexts.Add(context.Offset - (long) c.GetSize(Asset));
                        else if (e is EX_Skip { SkipExpression: { } s } skip)
                            skips.Add(skip.CodeOffset - (long) s.GetSize(Asset));
                    });
            }
            static long MostCommon(List<long> values) => values.Count == 0 ? 0 : values.GroupBy(o => o).OrderByDescending(o => o.Count()).First().Key;
            return (MostCommon(contexts), MostCommon(skips));
        }

        #endregion

        private static InvalidOperationException Error(string path, string message) => new($"{path}: {message}");
    }

    #endregion
}
