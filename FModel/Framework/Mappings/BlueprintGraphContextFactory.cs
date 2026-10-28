#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using CUE4Parse.FileProvider;
using FModel.BlueprintGraph;
using FModel.BlueprintGraph.Signatures;
using FModel.Settings;
using Serilog;

namespace FModel.Framework.Mappings;

/// <summary>
/// Builds the decompiler context from FModel's state: function signatures from the loaded .jmap mappings
/// and the optional "Blueprint Signature Jmap" setting, native properties/enums from the loaded mappings,
/// and call site inference for anything else. Parsed jmaps and macros are cached across viewer windows.
/// </summary>
public static class BlueprintGraphContextFactory
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, (DateTime Stamp, JmapData Data)> _jmaps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Dictionary<string, BPGraph>> _macros =
        new(() => BlueprintGraphOptions.LoadMacros(BlueprintGraphOptions.DefaultMacrosDirectory));

    public static BlueprintGraphContext Create(IFileProvider provider, string? mappingsJmapPath)
    {
        var providers = new List<ISignatureProvider?>
        {
            LoadJmap(mappingsJmapPath),
        };

        var settingsJmap = UserSettings.Default.BlueprintSignatureJmapPath;
        if (!string.Equals(settingsJmap, mappingsJmapPath, StringComparison.OrdinalIgnoreCase))
            providers.Add(LoadJmap(settingsJmap));

        if (provider.MappingsForGame is { } mappings)
            providers.Add(new MappingsSignatureProvider(mappings));

        var options = new BlueprintGraphOptions
        {
            ReorderEvents = UserSettings.Default.BlueprintReorderEvents,
            SelectedEventFirst = UserSettings.Default.BlueprintSelectedEventFirst,
            Macros = _macros.Value,
        };

        return new BlueprintGraphContext(new CompositeSignatureProvider(providers), options);
    }

    /// <summary>
    /// True when some jmap provides real function signatures (otherwise all native calls are inferred).
    /// </summary>
    public static bool HasSignatureJmap(string? mappingsJmapPath) =>
        IsFile(mappingsJmapPath) || IsFile(UserSettings.Default.BlueprintSignatureJmapPath);

    private static bool IsFile(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>The jmap dumps in use: the game's mappings (when they are a jmap) and the signature jmap of the settings.</summary>
    public static List<JmapData> GetJmaps(string? mappingsJmapPath)
    {
        var result = new List<JmapData>();
        if (LoadJmap(mappingsJmapPath) is { } mappings)
            result.Add(mappings);
        var settingsJmap = UserSettings.Default.BlueprintSignatureJmapPath;
        if (!string.Equals(settingsJmap, mappingsJmapPath, StringComparison.OrdinalIgnoreCase) && LoadJmap(settingsJmap) is { } signatures)
            result.Add(signatures);
        return result;
    }

    private static JmapData? LoadJmap(string? path)
    {
        if (!IsFile(path))
            return null;

        var stamp = File.GetLastWriteTimeUtc(path!);
        lock (_lock)
        {
            if (_jmaps.TryGetValue(path!, out var cached) && cached.Stamp == stamp)
                return cached.Data;

            try
            {
                var data = new JmapData(path!);
                _jmaps[path!] = (stamp, data);
                Log.Information("Loaded blueprint function signatures from {Path}", path);
                return data;
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to load blueprint function signatures from {Path}", path);
                return null;
            }
        }
    }
}
