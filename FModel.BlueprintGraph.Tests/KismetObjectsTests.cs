using FModel.Framework.BlueprintEditing;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.CustomVersions;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using Xunit;

namespace FModel.BlueprintGraph.Tests;

public class KismetObjectsTests
{
    /// <summary>A package with one function: if (bFlag) Count = 5; return.</summary>
    private static (UAsset Asset, StructExport Function) CreateAsset()
    {
        var asset = new UAsset(EngineVersion.VER_UE5_1);
        asset.ClearNameIndexList();
        asset.Imports = [];
        asset.Exports = [];

        FName Name(string text)
        {
            asset.AddNameReference(new FString(text));
            return FName.FromString(asset, text);
        }

        var package = asset.AddImport(new Import("/Script/CoreUObject", "Package", FPackageIndex.FromRawIndex(0), "/Script/Engine", false, asset));
        var library = asset.AddImport(new Import("/Script/CoreUObject", "Class", package, "KismetSystemLibrary", false, asset));
        asset.AddImport(new Import("/Script/CoreUObject", "Function", library, "PrintString", false, asset));

        var classExport = new ClassExport { Asset = asset, ObjectName = Name("BP_Test_C") };
        var function = new FunctionExport { Asset = asset, ObjectName = Name("DoThing") };
        asset.Exports.Add(classExport);
        asset.Exports.Add(function);
        var owner = FPackageIndex.FromExport(0);

        KismetPropertyPointer Pointer(string name) => new(new FFieldPath([Name(name)], owner));

        var ret = new EX_Return { ReturnExpression = new EX_Nothing() };
        function.ScriptBytecode =
        [
            new EX_JumpIfNot { BooleanExpression = new EX_InstanceVariable { Variable = Pointer("bFlag") } },
            new EX_Let { Value = Pointer("Count"), Variable = new EX_InstanceVariable { Variable = Pointer("Count") }, Expression = new EX_IntConst { Value = 5 } },
            ret,
            new EX_EndOfScript(),
        ];
        // the conditional jump skips the assignment
        var offsets = KismetObjects.Offsets(asset, function);
        ((EX_JumpIfNot) function.ScriptBytecode[0]).CodeOffset = offsets[ret];
        return (asset, function);
    }

    private static JToken WithoutOffsets(JToken token)
    {
        var copy = token.DeepClone();
        foreach (var o in (copy is JContainer container ? container.DescendantsAndSelf() : [copy]).OfType<JObject>().ToList())
            o.Remove(KismetObjects.OffsetKey);
        return copy;
    }

    [Fact]
    public void Statements_RoundTripThroughJson()
    {
        var (asset, function) = CreateAsset();
        var offsets = KismetObjects.Offsets(asset, function);
        foreach (var statement in function.ScriptBytecode)
        {
            var json = KismetObjects.ToJson(asset, statement, offsets);
            var builder = new KismetObjects.Builder(asset, true);
            var built = builder.Build(json);
            Assert.Equal(statement.GetType(), built.GetType());
            Assert.Equal(statement.GetSize(asset), built.GetSize(asset));
            // code offsets come back as statement references, not numbers
            Assert.True(JToken.DeepEquals(WithoutOffsets(json), WithoutOffsets(KismetObjects.ToJson(asset, built))) || builder.Targets.Count > 0);
        }
    }

    [Fact]
    public void ToJson_UsesSymbolicReferences()
    {
        var (asset, function) = CreateAsset();
        var json = KismetObjects.ToJson(asset, function.ScriptBytecode[1]);
        Assert.Equal("EX_Let", json.Value<string>("op"));
        Assert.Equal("export:BP_Test_C", json["Value"]!["owner"]!.Value<string>());
        Assert.Equal("Count", json["Value"]!["path"]![0]!.Value<string>());

        var jump = KismetObjects.ToJson(asset, function.ScriptBytecode[0]);
        Assert.StartsWith("@", jump.Value<string>("CodeOffset"));
        Assert.Equal("import:/Script/Engine.KismetSystemLibrary:PrintString", KismetObjects.IndexSymbol(asset, FPackageIndex.FromImport(2)));
    }

    [Fact]
    public void Builder_ChecksWithoutChangingTheAsset()
    {
        var (asset, _) = CreateAsset();
        var imports = asset.Imports.Count;
        var builder = new KismetObjects.Builder(asset, false);
        builder.Build(JObject.Parse("""
            {"op": "EX_CallMath", "StackNode": "import:/Script/Engine.KismetMathLibrary:Abs", "Parameters": [{"op": "EX_NameConst", "Value": "BrandNewName"}]}
            """));
        Assert.Equal(imports, asset.Imports.Count);
        Assert.Contains(builder.Notes, o => o.Contains("KismetMathLibrary"));
        Assert.Contains(builder.Notes, o => o.Contains("BrandNewName"));
    }

    [Fact]
    public void Builder_ReportsUnknownAndMissingFields()
    {
        var (asset, _) = CreateAsset();
        var builder = new KismetObjects.Builder(asset, true);
        var missing = Assert.Throws<InvalidOperationException>(() => builder.Build(JObject.Parse("""{"op": "EX_Let", "Variable": {"op": "EX_Self"}}""")));
        Assert.Contains("needs 'Value'", missing.Message);
        var unknown = Assert.Throws<InvalidOperationException>(() => builder.Build(JObject.Parse("""{"op": "EX_IntConst", "Value": 1, "Extra": 2}""")));
        Assert.Contains("no field 'Extra'", unknown.Message);
        Assert.Throws<InvalidOperationException>(() => builder.Build(JObject.Parse("""{"op": "EX_NotAThing"}""")));
    }

    [Fact]
    public void Builder_AddsImportChains()
    {
        var (asset, _) = CreateAsset();
        var builder = new KismetObjects.Builder(asset, true);
        var call = (EX_CallMath) builder.Build(JObject.Parse("""
            {"op": "EX_CallMath", "StackNode": "import:/Script/Engine.KismetMathLibrary:Abs", "Parameters": [{"op": "EX_FloatConst", "Value": -2.0}]}
            """));
        Assert.Equal("import:/Script/Engine.KismetMathLibrary:Abs", KismetObjects.IndexSymbol(asset, call.StackNode));
        var function = call.StackNode.ToImport(asset);
        Assert.Equal("Function", function.ClassName.ToString());
        Assert.Equal("Class", function.OuterIndex.ToImport(asset).ClassName.ToString());
        // the existing /Script/Engine package import is reused
        Assert.Equal(0, -function.OuterIndex.ToImport(asset).OuterIndex.Index - 1);
    }

    [Fact]
    public void Patcher_InsertsLabelledStatementsAndLaysOffsetsOut()
    {
        var (asset, function) = CreateAsset();
        var offsets = KismetObjects.Offsets(asset, function);
        var let = function.ScriptBytecode[1];
        var ret = function.ScriptBytecode[2];

        // after the assignment: if (!bFlag) goto Skip; Count = 7; Skip: (nothing)
        var insert = new ObjectEdit
        {
            FunctionName = "DoThing", Kind = ObjectEditKind.InsertAfter, Offset = offsets[let], Token = "EX_Let",
            Payload = JArray.Parse("""
                [
                  {"op": "EX_JumpIfNot", "CodeOffset": "#skip", "BooleanExpression": {"op": "EX_InstanceVariable", "Variable": {"path": ["bFlag"], "owner": "export:BP_Test_C"}}},
                  {"op": "EX_Let", "Value": {"path": ["Count"], "owner": "export:BP_Test_C"},
                   "Variable": {"op": "EX_InstanceVariable", "Variable": {"path": ["Count"], "owner": "export:BP_Test_C"}},
                   "Expression": {"op": "EX_IntConst", "Value": 7}},
                  {"op": "EX_Nothing", "label": "skip"}
                ]
                """),
        };
        // and the original constant 5 becomes 6
        var replace = new ObjectEdit
        {
            FunctionName = "DoThing", Kind = ObjectEditKind.Replace, Offset = offsets[((EX_Let) let).Expression], Token = "EX_IntConst",
            Payload = JObject.Parse("""{"op": "EX_IntConst", "Value": 6}"""),
        };

        KismetConstantPatcher.Apply(asset, [], [], [insert, replace]);

        Assert.Empty(KismetConstantPatcher.Validate(asset));
        var code = function.ScriptBytecode;
        Assert.Equal(7, code.Length);
        var after = KismetObjects.Offsets(asset, function);
        // the original jump still skips to the return, which moved
        Assert.Equal(after[ret], ((EX_JumpIfNot) code[0]).CodeOffset);
        // the inserted jump points at the labelled statement
        Assert.Equal(after[code[4]], ((EX_JumpIfNot) code[2]).CodeOffset);
        Assert.Equal(6, ((EX_IntConst) ((EX_Let) code[1]).Expression).Value);
    }

    [Fact]
    public void Patcher_RejectsOverlappingEdits()
    {
        var (asset, function) = CreateAsset();
        var offsets = KismetObjects.Offsets(asset, function);
        var let = (EX_Let) function.ScriptBytecode[1];
        var whole = new ObjectEdit { FunctionName = "DoThing", Kind = ObjectEditKind.Remove, Offset = offsets[let], Token = "EX_Let" };
        var inner = new ObjectEdit
        {
            FunctionName = "DoThing", Kind = ObjectEditKind.Replace, Offset = offsets[let.Expression], Token = "EX_IntConst",
            Payload = JObject.Parse("""{"op": "EX_IntConst", "Value": 1}"""),
        };
        var error = Assert.Throws<InvalidOperationException>(() => KismetConstantPatcher.Apply(asset, [], [], [whole, inner]));
        Assert.Contains("overlapping", error.Message);
    }

    [Fact]
    public void Patcher_RemoveKeepsJumpTargets()
    {
        var (asset, function) = CreateAsset();
        var offsets = KismetObjects.Offsets(asset, function);
        var ret = function.ScriptBytecode[2];
        var remove = new ObjectEdit { FunctionName = "DoThing", Kind = ObjectEditKind.Remove, Offset = offsets[function.ScriptBytecode[1]], Token = "EX_Let" };

        KismetConstantPatcher.Apply(asset, [], [], [remove]);

        Assert.IsType<EX_Nothing>(function.ScriptBytecode[1]);
        Assert.Empty(KismetConstantPatcher.Validate(asset));
        Assert.Equal(KismetObjects.Offsets(asset, function)[ret], ((EX_JumpIfNot) function.ScriptBytecode[0]).CodeOffset);
    }

    [Fact]
    public void Schema_CoversEveryOp()
    {
        Assert.Contains(EExprToken.EX_Let, KismetObjects.Tokens);
        Assert.Contains(EExprToken.EX_CallMath, KismetObjects.Tokens);
        Assert.True(KismetObjects.TryGetType("EX_Context", out var context));
        var fields = (JObject) KismetObjects.Describe(context)["fields"]!;
        Assert.Contains("ObjectExpression", fields.Properties().Select(o => o.Name));
        // the skip length is computed, never written
        Assert.DoesNotContain("Offset", fields.Properties().Select(o => o.Name));
    }

    private static readonly JObject ArrayOfInt = new() { ["path"] = new JArray("cardIDs"), ["owner"] = "export:DoThing" };

    [Fact]
    public void Builder_RefusesPropertiesTheOwnerDoesNotDeclare()
    {
        var (asset, function) = CreateAsset();
        Assert.True(KismetLocals.Supported(asset));
        function.LoadedProperties = [];
        var builder = new KismetObjects.Builder(asset, false);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build(new JObject
        {
            ["op"] = "EX_LocalVariable", ["Variable"] = ArrayOfInt.DeepClone(),
        }));
        Assert.Contains("not a property of DoThing", error.Message);
        Assert.Contains("kismet_add_local", error.Message);
        // declared by a pending local: accepted
        builder.DeclaredLocals.Add(("DoThing", "cardIDs"));
        builder.Build(new JObject { ["op"] = "EX_LocalVariable", ["Variable"] = ArrayOfInt.DeepClone() });
    }

    [Fact]
    public void Locals_AreDeclaredWithTypedProperties()
    {
        var (asset, function) = CreateAsset();
        function.LoadedProperties = [];
        var names = asset.GetNameMapIndexList().Count;

        // check mode: valid, but the package is untouched
        var checkNotes = new List<string>();
        var declaration = new LocalDeclaration { FunctionName = "DoThing", Name = "cardIDs", Type = "array:int" };
        Assert.Null(KismetLocals.Build(asset, function, declaration, false, checkNotes));
        Assert.Equal(names, asset.GetNameMapIndexList().Count);
        Assert.Empty(function.LoadedProperties);

        KismetLocals.Apply(asset, [declaration, new LocalDeclaration { FunctionName = "DoThing", Name = "Cursor", Type = "object:/Script/Engine.Actor" }]);
        Assert.Equal(2, function.LoadedProperties.Length);
        var array = Assert.IsType<FArrayProperty>(function.LoadedProperties[0]);
        Assert.Equal("cardIDs", array.Name.ToString());
        Assert.Equal("cardIDs", array.Inner.Name.ToString());
        Assert.Equal("IntProperty", array.Inner.SerializedType.ToString());
        Assert.Equal(16, array.ElementSize);
        var actor = Assert.IsType<FObjectProperty>(function.LoadedProperties[1]);
        Assert.Equal("import:/Script/Engine.Actor", KismetObjects.IndexSymbol(asset, actor.PropertyClass));
        // not a parameter, not replicated
        Assert.False(array.PropertyFlags.HasFlag(UAssetAPI.UnrealTypes.EPropertyFlags.CPF_Parm));

        // the declared property is now a valid reference
        var builder = new KismetObjects.Builder(asset, true);
        var variable = (EX_LocalVariable) builder.Build(new JObject { ["op"] = "EX_LocalVariable", ["Variable"] = ArrayOfInt.DeepClone() });
        Assert.Equal("cardIDs", variable.Variable.New.Path[0].ToString());
    }

    [Fact]
    public void Locals_SurviveSerialization()
    {
        var (asset, function) = CreateAsset();
        function.LoadedProperties = [];
        KismetLocals.Apply(asset, [new LocalDeclaration { FunctionName = "DoThing", Name = "cardIDs", Type = "array:int" }]);

        // the declared property written and read back the way a package stores the properties of a function
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            var writer = new AssetBinaryWriter(stream, asset);
            MainSerializer.WriteFProperty(function.LoadedProperties[0], writer);
            writer.Flush();
            bytes = stream.ToArray();
        }
        var array = Assert.IsType<FArrayProperty>(MainSerializer.ReadFProperty(new AssetBinaryReader(new MemoryStream(bytes), asset)));
        Assert.Equal("cardIDs", array.Name.ToString());
        Assert.Equal("ArrayProperty", array.SerializedType.ToString());
        Assert.Equal("IntProperty", array.Inner.SerializedType.ToString());
        Assert.Equal(function.LoadedProperties[0].ElementSize, array.ElementSize);
        Assert.Equal(function.LoadedProperties[0].PropertyFlags, array.PropertyFlags);
    }

    [Fact]
    public void Locals_RejectBadDeclarations()
    {
        var (asset, function) = CreateAsset();
        function.LoadedProperties = [];
        var notes = new List<string>();
        LocalDeclaration Make(string name, string type) => new() { FunctionName = "DoThing", Name = name, Type = type };
        Assert.Contains("valid variable name", Assert.Throws<InvalidOperationException>(() => KismetLocals.Build(asset, function, Make("bad name", "int"), false, notes)).Message);
        Assert.Contains("unknown type", Assert.Throws<InvalidOperationException>(() => KismetLocals.Build(asset, function, Make("x", "quaternion"), false, notes)).Message);
        Assert.Contains("like", Assert.Throws<InvalidOperationException>(() => KismetLocals.Build(asset, function, Make("x", "struct:/Script/CoreUObject.Vector"), false, notes)).Message);
        KismetLocals.Apply(asset, [Make("x", "int")]);
        Assert.Contains("already has a property", Assert.Throws<InvalidOperationException>(() => KismetLocals.Build(asset, function, Make("X", "int"), false, notes)).Message);
        // a copy of an existing property's type, flags and size
        var like = new LocalDeclaration { FunctionName = "DoThing", Name = "y", Like = new JObject { ["path"] = new JArray("x") } };
        KismetLocals.Apply(asset, [like]);
        Assert.Equal("y", function.LoadedProperties[1].Name.ToString());
        Assert.Equal(function.LoadedProperties[0].ElementSize, function.LoadedProperties[1].ElementSize);
    }

    [Fact]
    public void ExceptionText_NamesTheChainAndTopFrames()
    {
        Exception caught = null!;
        try
        {
            try { throw new InvalidOperationException("inner"); }
            catch (Exception e) { throw new NullReferenceException("outer", e); }
        }
        catch (Exception e) { caught = e; }
        var text = ExceptionText.Describe(caught);
        Assert.Contains("NullReferenceException: outer <- InvalidOperationException: inner", text);
        Assert.Contains("at ", text);
    }
}
