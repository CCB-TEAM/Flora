using System.Collections.Generic;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.BlueprintGraph.Engine;

public class FunctionData
{
    public string Name;
    public List<PropertyData> Params = [];

    public EFunctionFlags Flags;
    public bool IsPure => Flags.HasFlag(EFunctionFlags.FUNC_BlueprintPure);

    public ObjectData Outer;

    /// <summary>
    /// Signature was guessed from a call site because no mappings knew the function.
    /// </summary>
    public bool IsInferred;
        
    public FunctionData(string name, EFunctionFlags flags)
    {
        Name = name;
        Flags = flags;
    }

    public override string ToString() => Name;
}