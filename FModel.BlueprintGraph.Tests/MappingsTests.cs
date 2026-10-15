using System.IO.Compression;
using System.Text;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using FModel.Framework.Mappings;
using Xunit;

namespace FModel.BlueprintGraph.Tests;

public sealed class MappingsTests : IDisposable
{
    private const string Jmap = """
        {"objects":{
         "/Script/Engine.Actor":{"type":"Class","super_struct":"/Script/CoreUObject.Object","properties":[
           {"name":"Health","type":"FloatProperty","array_dim":1},
           {"name":"Slots","type":"IntProperty","array_dim":3},
           {"name":"OwnerClass","type":"ClassProperty","array_dim":1},
           {"name":"Tags","type":"ArrayProperty","array_dim":1,"inner":{"name":"Tags","type":"NameProperty"}},
           {"name":"Mode","type":"EnumProperty","array_dim":1,"enum":"/Script/Engine.EMode","container":{"name":"UnderlyingType","type":"ByteProperty"}},
           {"name":"Location","type":"StructProperty","array_dim":1,"struct":"/Script/CoreUObject.Vector"},
           {"name":"OnHit","type":"MulticastSparseDelegateProperty","array_dim":1},
           {"name":"Lookup","type":"MapProperty","array_dim":1,"key_prop":{"name":"k","type":"StrProperty"},"value_prop":{"name":"v","type":"SoftClassProperty"}},
           {"name":"Label","type":"FUtf8StrProperty","array_dim":1},
           {"name":"ByteMode","type":"ByteProperty","array_dim":1,"enum":"/Script/Engine.EMode"}
         ]},
         "/Script/Engine.EMode":{"type":"Enum","names":[["EMode::A",0],["EMode::B",5]]},
         "/Script/Engine.Actor:K2_DestroyActor":{"type":"Function","outer":"/Script/Engine.Actor","properties":[]}
        }}
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fmodel-bpg-tests-" + Guid.NewGuid().ToString("N"));

    public MappingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { /* best effort */ }
    }

    private string WriteJmap(bool gzip)
    {
        var path = Path.Combine(_directory, gzip ? "test.jmap.gz" : "test.jmap");
        if (!gzip)
        {
            File.WriteAllText(path, Jmap);
            return path;
        }

        using var file = File.Create(path);
        using var stream = new GZipStream(file, CompressionLevel.Fastest);
        stream.Write(Encoding.UTF8.GetBytes(Jmap));
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JmapProvider_ReadsClassesStructsAndEnums(bool gzip)
    {
        var path = WriteJmap(gzip);
        Assert.True(FileJmapTypeMappingsProvider.IsJmapFile(path));

        var mappings = new FileJmapTypeMappingsProvider(path).MappingsForGame!;
        var actor = mappings.Types["Actor"];

        Assert.Equal("Object", actor.SuperType);
        Assert.Equal(12, actor.PropertyCount); // Slots[3] expands to three schema entries
        Assert.Equal("Slots", actor.Properties[2].Name);
        Assert.Equal(1, actor.Properties[2].Index);
        Assert.Equal("EMode", actor.Properties[6].MappingType.EnumName);
        Assert.Equal("B", mappings.Enums["EMode"][5]);
        Assert.Equal("Utf8StrProperty", actor.Properties[10].MappingType.Type); // "FUtf8StrProperty" in the dump
        Assert.Equal("ByteProperty", actor.Properties[11].MappingType.Type);
        Assert.Equal("EMode", actor.Properties[11].MappingType.EnumName);
        Assert.False(mappings.Types.ContainsKey("K2_DestroyActor"));
    }

    [Fact]
    public void UsmapWriter_RoundTripsThroughCue4ParseAndUAssetApi()
    {
        var mappings = new FileJmapTypeMappingsProvider(WriteJmap(false)).MappingsForGame!;
        var usmapPath = Path.Combine(_directory, "roundtrip.usmap");
        UsmapWriter.Write(mappings, usmapPath);

        var back = new UsmapParser(usmapPath).Mappings!;
        var actor = back.Types["Actor"];
        Assert.Equal(mappings.Types.Count, back.Types.Count);
        Assert.Equal(12, actor.Properties.Count);
        Assert.Equal("ObjectProperty", actor.Properties[4].MappingType.Type); // ClassProperty
        Assert.Equal("NameProperty", actor.Properties[5].MappingType.InnerType!.Type);
        Assert.Equal("ByteProperty", actor.Properties[6].MappingType.InnerType!.Type);
        Assert.Equal("Vector", actor.Properties[7].MappingType.StructType);
        Assert.Equal("MulticastDelegateProperty", actor.Properties[8].MappingType.Type); // sparse delegate
        Assert.Equal("SoftObjectProperty", actor.Properties[9].MappingType.ValueType!.Type); // SoftClassProperty
        Assert.Equal("Utf8StrProperty", actor.Properties[10].MappingType.Type);
        // TEnumAsByte is written the Dumper-7 way so UAssetAPI shows enum names
        Assert.Equal("EnumProperty", actor.Properties[11].MappingType.Type);
        Assert.Equal("ByteProperty", actor.Properties[11].MappingType.InnerType!.Type);
        Assert.Equal("EMode", actor.Properties[11].MappingType.EnumName);
        Assert.Equal("B", back.Enums["EMode"][5]);

        var uassetApi = new UAssetAPI.Unversioned.Usmap(usmapPath);
        Assert.Equal(12, uassetApi.Schemas["Actor"].Properties.Count);
        Assert.Equal("B", uassetApi.EnumMap["EMode"].Values[5]);
    }

    [Fact]
    public void UsmapWriter_PreservesRealUsmapWhenAvailable()
    {
        // uses UAssetAPI's test assets when the repository layout provides them
        var sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../UAssetAPI-master/UAssetAPI.Tests/TestAssets/TestJson/MotorTown.usmap"));
        if (!File.Exists(sample))
            return;

        var original = new UsmapParser(sample).Mappings!;
        var path = Path.Combine(_directory, "motortown.usmap");
        UsmapWriter.Write(original, path);
        var back = new UsmapParser(path).Mappings!;

        Assert.Equal(original.Types.Count, back.Types.Count);
        Assert.Equal(original.Enums.Count, back.Enums.Count);
        foreach (var (name, type) in original.Types)
        {
            Assert.Equal(type.PropertyCount, back.Types[name].PropertyCount);
            Assert.Equal(type.Properties.Count, back.Types[name].Properties.Count);
        }
    }
}
