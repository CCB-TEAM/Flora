using System.Collections.Generic;

namespace FModel.BlueprintGraph.Engine;

public class EnumData
{
    public string Name;
    public Dictionary<string, long> Elements = [];

    public EnumData(string name)
    {
        Name = name;
    }
}