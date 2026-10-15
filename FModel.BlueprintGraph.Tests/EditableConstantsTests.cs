using CUE4Parse.UE4.Kismet;
using FModel.BlueprintGraph.Decompiler;
using Xunit;

namespace FModel.BlueprintGraph.Tests;

public class EditableConstantsTests
{
    [Fact]
    public void LiteralTokensAreEditable()
    {
        Assert.True(EditableConstants.TryGetKind(new EX_True(), out var kind));
        Assert.Equal(EditableConstantKind.Bool, kind);
        Assert.True(EditableConstants.TryGetKind(new EX_IntOne(), out kind));
        Assert.Equal(EditableConstantKind.Int, kind);
        Assert.False(EditableConstants.IsEditable(new EX_Nothing()));
    }

    [Theory]
    [InlineData(EditableConstantKind.Bool, "true", true)]
    [InlineData(EditableConstantKind.Bool, "yes", false)]
    [InlineData(EditableConstantKind.Int, " -12 ", true)]
    [InlineData(EditableConstantKind.Int, "1.5", false)]
    [InlineData(EditableConstantKind.Byte, "256", false)]
    [InlineData(EditableConstantKind.Float, "0.75", true)]
    [InlineData(EditableConstantKind.Float, "1e3", true)]
    [InlineData(EditableConstantKind.Name, "", false)]
    [InlineData(EditableConstantKind.String, "", true)]
    public void ValidatesText(EditableConstantKind kind, string text, bool valid) =>
        Assert.Equal(valid, EditableConstants.IsValid(kind, text));

    [Fact]
    public void FormatsInvariant()
    {
        Assert.Equal("false", EditableConstants.FormatValue(new EX_False()));
        Assert.Equal("1", EditableConstants.FormatValue(new EX_IntOne()));
        Assert.Equal("0", EditableConstants.FormatValue(new EX_IntZero()));
    }
}
