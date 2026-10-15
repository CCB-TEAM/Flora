using System;
using System.IO;

namespace CUE4Parse.MappingsProvider.Jmap;

public sealed class FileJmapTypeMappingsProvider : UsmapTypeMappingsProvider
{
    private readonly StringComparer? _stringComparer;
    public readonly string FilePath;
    public readonly string FileName;

    public FileJmapTypeMappingsProvider(string path, StringComparer? comparer = null)
    {
        _stringComparer = comparer;
        FilePath = path;
        FileName = Path.GetFileName(path);
        Load(path, _stringComparer);
    }

    public static bool IsJmapFile(string path)
    {
        return path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase);
    }

    public override void Load(string path, StringComparer? comparer = null)
    {
        MappingsForGame = new JmapParser(path, comparer).Mappings;
    }

    public override void Load(byte[] bytes, StringComparer? comparer = null)
    {
        MappingsForGame = new JmapParser(bytes, comparer).Mappings;
    }

    public override void Reload()
    {
        Load(FilePath, _stringComparer);
    }
}
