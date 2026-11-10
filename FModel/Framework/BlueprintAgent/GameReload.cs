using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Encryption.Aes;
using FModel.Services;
using FModel.Settings;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// Notices that the game's pak/utoc files changed on disk after Flora mounted them (a pak rebuilt or replaced while Flora runs) and mounts them again.
/// Without this Flora keeps reading the old pak index against the new file and fails with "Invalid uasset magic" until it is restarted.
/// </summary>
public static class GameReload
{
    // files written after this moment are newer than what Flora mounted (the process start at first; the last reload afterwards)
    private static DateTime _loadedAtUtc = SafeProcessStartUtc();

    private static DateTime SafeProcessStartUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch (Exception) { return DateTime.UtcNow; }
    }

    /// <summary>Why the mounted containers are out of date, or null when they match the disk.</summary>
    public static string StaleReason()
    {
        try
        {
            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
            if (provider == null)
                return null;
            var readers = provider.MountedVfs.Concat(provider.UnloadedVfs).Where(o => !string.IsNullOrEmpty(o.Path) && Path.IsPathRooted(o.Path)).ToList();
            if (readers.Count == 0)
                return null;

            var known = new HashSet<string>(readers.Select(o => Path.GetFullPath(o.Path)), StringComparer.OrdinalIgnoreCase);
            foreach (var reader in readers)
            {
                var file = new FileInfo(reader.Path);
                if (!file.Exists)
                    return $"{file.Name} was removed";
                if (reader is AbstractAesVfsReader aes && aes.Length > 0 && aes.Length != file.Length)
                    return $"{file.Name} changed size ({aes.Length} -> {file.Length} bytes)";
                if (file.LastWriteTimeUtc > _loadedAtUtc.AddSeconds(1))
                    return $"{file.Name} was written at {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}, after Flora mounted it";
            }

            foreach (var directory in readers.Select(o => Path.GetDirectoryName(Path.GetFullPath(o.Path))).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var container in Directory.EnumerateFiles(directory, "*.pak").Concat(Directory.EnumerateFiles(directory, "*.utoc")))
                    if (!known.Contains(Path.GetFullPath(container)))
                        return $"{Path.GetFileName(container)} is new";
            }
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not check whether the game files changed");
        }
        return null;
    }

    private static List<string> PendingEdits()
    {
        var pending = new List<string>();
        foreach (var viewer in BlueprintAgentHost.Open)
            if (viewer.Editor.HasUnsavedChanges)
                pending.Add(viewer.Entry.Path);
        foreach (var table in DataTableAgentTools.OpenSessions)
            if (table.Editor.IsModified)
                pending.Add(table.Entry.Path);
        return pending;
    }

    /// <summary>
    /// Called before the tools that look things up in the game. Reloads when the files changed and nothing has pending edits;
    /// returns a note for the tool result (null when nothing happened).
    /// </summary>
    public static async Task<string> EnsureFreshAsync()
    {
        var reason = StaleReason();
        if (reason == null)
            return null;
        var pending = PendingEdits();
        if (pending.Count > 0)
            return $"WARNING: the game files changed on disk ({reason}) but Flora did not reload because these have pending edits: {string.Join(", ", pending)}. " +
                   "Save or revert them, or call reload_game with discard=true; until then reads of changed files may fail.\n\n";
        var result = await ReloadAsync(false);
        return $"NOTE: the game files changed on disk ({reason}); Flora reloaded them. {result}\n\n";
    }

    /// <summary>Closes every open blueprint and DataTable and mounts the game's containers again from disk.</summary>
    public static async Task<string> ReloadAsync(bool discard)
    {
        var pending = PendingEdits();
        if (pending.Count > 0 && !discard)
            throw new AgentToolException($"These have pending edits, so nothing was reloaded: {string.Join(", ", pending)}. Save or revert them, or pass discard=true to drop the edits.");

        var closed = 0;
        foreach (var viewer in BlueprintAgentHost.Open.ToList())
        {
            if (viewer.Editor.HasUnsavedChanges)
                await viewer.DiscardAllAsync();
            // same id the viewer was shown with (DockPanels.ShowBlueprintViewer); closing unregisters it and deletes its edit session
            Docking.DockWorkspace.Close($"Blueprint:{viewer.Title}");
            closed++;
        }
        var tables = DataTableAgentTools.OpenSessions.Count;
        DataTableAgentTools.CloseAllSessions();

        var application = ApplicationService.ApplicationView;
        var cue4Parse = application.CUE4Parse;
        var before = cue4Parse.Provider.Files.Count; // read before ClearProvider empties it
        cue4Parse.ClearProvider();
        await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            cue4Parse.Provider.ClearAllVfs();
            cue4Parse.Provider.Initialize();
            var keys = application.AesManager.AesKeys.Select(x =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var k = x.Key.Trim();
                if (k.Length != 66) k = Constants.ZERO_64_CHAR;
                return new KeyValuePair<FGuid, FAesKey>(x.Guid, new FAesKey(k));
            }).ToList();
            cue4Parse.LoadVfs(keys);
        });
        _loadedAtUtc = DateTime.UtcNow;
        var provider = cue4Parse.Provider;
        return $"Mounted {provider.MountedVfs.Count}/{provider.MountedVfs.Count + provider.UnloadedVfs.Count} containers, {provider.Files.Count} files " +
               $"(was {before}); closed {closed} blueprint(s) and {tables} DataTable(s).";
    }
}
