using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FModel.BlueprintGraph.Signatures;

namespace FModel.BlueprintGraph;

public class BlueprintGraphOptions
{
    /// <summary>Order ubergraph events by their entry point instead of declaration order.</summary>
    public bool ReorderEvents = true;

    /// <summary>Decompile the selected event before the other ubergraph events.</summary>
    public bool SelectedEventFirst;

    /// <summary>Use MSAGL layered layout instead of the built-in one.</summary>
    public bool UseMsagl = true;

    /// <summary>Collapse inlined engine macros (loops, DoOnce...) back to macro nodes.</summary>
    public bool ParseMacros = true;

    public IReadOnlyDictionary<string, BPGraph> Macros = new Dictionary<string, BPGraph>();

    // debug switches kept from the original viewer
    public static bool DrawDebugGraph;
    public static bool ExperimentalExecStraightening;

    public static string DefaultMacrosDirectory => Path.Combine(AppContext.BaseDirectory, "Macros");

    public static Dictionary<string, BPGraph> LoadMacros(string directory)
    {
        Dictionary<string, BPGraph> macros = [];
        if (!Directory.Exists(directory))
            return macros;

        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(o => o, StringComparer.Ordinal))
        {
            macros[Path.GetFileNameWithoutExtension(path)] = GraphJson.FromJson(File.ReadAllText(path));
        }
        return macros;
    }
}

/// <summary>
/// Everything the decompiler needs besides the package itself (replaces the viewer's per-game profile).
/// </summary>
public class BlueprintGraphContext
{
    public ISignatureProvider Signatures;
    public BlueprintGraphOptions Options;

    public BlueprintGraphContext(ISignatureProvider signatures, BlueprintGraphOptions? options = null)
    {
        Signatures = signatures;
        Options = options ?? new BlueprintGraphOptions();
    }
}
