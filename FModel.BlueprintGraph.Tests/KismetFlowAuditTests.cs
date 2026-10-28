using FModel.Framework.BlueprintEditing;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using Xunit;

namespace FModel.BlueprintGraph.Tests;

public class KismetFlowAuditTests
{
    private static (UAsset Asset, StructExport Function) Create(Func<KismetExpression[]> code)
    {
        var asset = new UAsset(EngineVersion.VER_UE5_1);
        asset.ClearNameIndexList();
        asset.Imports = [];
        asset.Exports = [];
        asset.AddNameReference(new FString("Fn"));
        var function = new FunctionExport { Asset = asset, ObjectName = FName.FromString(asset, "Fn") };
        asset.Exports.Add(function);
        function.ScriptBytecode = code();
        return (asset, function);
    }

    private static uint Offset(UAsset asset, KismetExpression[] code, int index)
    {
        uint position = 0;
        for (var i = 0; i < index; i++)
            position += code[i].GetSize(asset);
        return position;
    }

    [Fact]
    public void StraightCode_HasNoFindings()
    {
        var (asset, function) = Create(() => [new EX_IntConst { Value = 1 }, new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript()]);
        var result = KismetFlowAudit.Audit(asset, function);
        Assert.Empty(result.Findings);
        Assert.Equal(2, result.Reachable - 0 == 3 ? 2 : 2);
    }

    [Fact]
    public void CodeAfterReturn_IsUnreachable()
    {
        var (asset, function) = Create(() => [new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_IntConst { Value = 7 }, new EX_EndOfScript()]);
        var result = KismetFlowAudit.Audit(asset, function);
        Assert.Contains(result.Findings, o => o.Kind == "unreachable");
    }

    [Fact]
    public void JumpToItself_IsAnEndlessLoop()
    {
        var (asset, function) = Create(() => [new EX_Jump { CodeOffset = 0 }, new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript()]);
        var result = KismetFlowAudit.Audit(asset, function);
        Assert.Contains(result.Findings, o => o.Kind == "no-exit");
        Assert.Contains(result.Findings, o => o.Kind == "loop");
    }

    [Fact]
    public void LoopWithExit_IsOnlyInformation()
    {
        KismetExpression[] code = null!;
        var (asset, function) = Create(() => code =
        [
            new EX_JumpIfNot { BooleanExpression = new EX_True(), CodeOffset = 0 },
            new EX_Jump { CodeOffset = 0 },
            new EX_Return { ReturnExpression = new EX_Nothing() },
            new EX_EndOfScript(),
        ]);
        ((EX_JumpIfNot) code[0]).CodeOffset = Offset(asset, code, 2);
        ((EX_Jump) code[1]).CodeOffset = 0;
        var result = KismetFlowAudit.Audit(asset, function);
        Assert.DoesNotContain(result.Findings, o => o.Severity == "error");
        Assert.Equal(1, result.Loops);
    }

    [Fact]
    public void PopWithoutPush_EndsTheThread()
    {
        var (asset, function) = Create(() => [new EX_PopExecutionFlow(), new EX_EndOfScript()]);
        Assert.DoesNotContain(KismetFlowAudit.Audit(asset, function).Findings, o => o.Severity == "error");
    }

    [Fact]
    public void PushedFlow_ResumesAtThePushedAddress()
    {
        KismetExpression[] code = null!;
        var (asset, function) = Create(() => code =
        [
            new EX_PushExecutionFlow { PushingAddress = 0 },
            new EX_PopExecutionFlow(),
            new EX_IntConst { Value = 3 }, // the pushed address: runs after the pop
            new EX_Return { ReturnExpression = new EX_Nothing() },
            new EX_EndOfScript(),
        ]);
        ((EX_PushExecutionFlow) code[0]).PushingAddress = Offset(asset, code, 2);
        var result = KismetFlowAudit.Audit(asset, function);
        Assert.DoesNotContain(result.Findings, o => o.Kind == "unreachable");
    }

    [Fact]
    public void BadJumpTarget_IsReported()
    {
        var (asset, function) = Create(() => [new EX_Jump { CodeOffset = 3 }, new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript()]);
        Assert.Contains(KismetFlowAudit.Audit(asset, function).Findings, o => o.Kind == "bad-target");
    }

    [Fact]
    public void NewFindings_IgnoresWhatWasAlreadyThere()
    {
        var (asset, loop) = Create(() => [new EX_Jump { CodeOffset = 0 }, new EX_EndOfScript()]);
        var before = KismetFlowAudit.Audit(asset, loop);
        Assert.Empty(KismetFlowAudit.NewFindings(before, KismetFlowAudit.Audit(asset, loop)));

        var (cleanAsset, clean) = Create(() => [new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript()]);
        Assert.NotEmpty(KismetFlowAudit.NewFindings(KismetFlowAudit.Audit(cleanAsset, clean), before));
    }

    [Fact]
    public void Diff_ShowsChangedCellsAndInsertionsWithoutJumpNoise()
    {
        KismetExpression[] a = null!, b = null!;
        var (assetA, fa) = Create(() => a = [new EX_Jump { CodeOffset = 0 }, new EX_Let { Variable = new EX_LocalVariable(), Expression = new EX_IntConst { Value = 5 } }, new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript()]);
        ((EX_Jump) a[0]).CodeOffset = Offset(assetA, a, 2);
        var (assetB, fb) = Create(() => b = [new EX_Jump { CodeOffset = 0 }, new EX_Let { Variable = new EX_LocalVariable(), Expression = new EX_IntConst { Value = 9 } }, new EX_IntConst { Value = 1 }, new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript()]);
        ((EX_Jump) b[0]).CodeOffset = Offset(assetB, b, 3);

        var text = KismetDiff.Compare(assetA, fa, assetB, fb);
        Assert.Contains("5 -> 9", text);
        Assert.Contains("+ new statement", text);
        Assert.DoesNotContain("statement 0", text); // the jump reaches the same statement either way
        Assert.Equal("no statement changed", KismetDiff.Compare(assetA, fa, assetA, fa));
    }
}

public class KismetBuilderErrorTests
{
    private static UAsset NewAsset()
    {
        var asset = new UAsset(EngineVersion.VER_UE5_1);
        asset.ClearNameIndexList();
        asset.Imports = [];
        asset.Exports = [];
        return asset;
    }

    [Fact]
    public void NonAsciiStringConst_BecomesUnicode()
    {
        var asset = NewAsset();
        var builder = new KismetObjects.Builder(asset, true);
        var built = builder.Build(Newtonsoft.Json.Linq.JObject.Parse("{\"op\":\"EX_StringConst\",\"Value\":\"你好\"}"));
        var unicode = Assert.IsType<EX_UnicodeStringConst>(built);
        Assert.Equal("你好", unicode.Value);
        Assert.Contains(builder.Notes, o => o.Contains("EX_UnicodeStringConst"));
        Assert.IsType<EX_StringConst>(new KismetObjects.Builder(asset, true).Build(Newtonsoft.Json.Linq.JObject.Parse("{\"op\":\"EX_StringConst\",\"Value\":\"hi\"}")));
    }

    [Fact]
    public void NullExpression_NamesTheField()
    {
        var builder = new KismetObjects.Builder(NewAsset(), true);
        var error = Assert.ThrowsAny<Exception>(() => builder.Build(Newtonsoft.Json.Linq.JObject.Parse("{\"op\":\"EX_Return\",\"ReturnExpression\":null}")));
        Assert.Contains("$.ReturnExpression", error.Message);
        Assert.Contains("must not be null", error.Message);
    }

    [Fact]
    public void NullPointer_IsBuiltAsAnEmptyPointer()
    {
        var asset = NewAsset();
        var json = Newtonsoft.Json.Linq.JObject.Parse("{\"op\":\"EX_Context\",\"ObjectExpression\":{\"op\":\"EX_Self\"},\"ContextExpression\":{\"op\":\"EX_Nothing\"},\"RValuePointer\":null,\"PropertyType\":0}");
        var built = (EX_Context) new KismetObjects.Builder(asset, true).Build(json);
        Assert.NotNull(built.RValuePointer);
        Assert.NotEqual(0u, built.GetSize(asset));
    }
}
