using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FModel.Framework.Docking;
using FModel.Framework.Packing;
using FModel.Framework.Packing.Audio;
using FModel.Framework.Packing.Locres;
using FModel.Services;
using FModel.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// Agent tools for the pak packer (View → Pak Packer): files of the pak, .locres strings, texture and sound replacement, building.
/// They work on the project open in the packer panel, which the user sees change. Files are only written by pack_build / pack_save_project,
/// (the built-in agent asks the user first, MCP calls run directly).
/// </summary>
public static class PackAgentTools
{
    private static readonly JObject PakPathArg = String("Path inside the pak, as the game names the file (e.g. Game/Content/UI/T_Logo.uasset)");

    public static IEnumerable<AgentTool> Build() =>
    [
        #region the pak

        new()
        {
            Name = "pack_status",
            Description = "The pak packer project: settings (mount point, pak version, compression, last output) and its files (pak path, size, edited or not, where it came from).",
            InputSchema = Object(("filter", String("Only files whose path contains this"), false), ("limit", Integer("Files to list (default 200)"), false)),
            Run = (args, _) =>
            {
                var workspace = Workspace();
                var project = workspace.Project;
                var filter = args.Str("filter", false);
                var items = project.Items.Where(o => filter == null || o.PakPath.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                var limit = Math.Clamp(args.Int("limit", 200), 1, 2000);
                var sb = new StringBuilder();
                sb.AppendLine($"project {workspace.Title}{(project.FilePath == null ? " (not saved)" : $" ({project.FilePath})")}: {workspace.Summary}");
                sb.AppendLine($"mount point {project.Settings.MountPoint}, pak version {(UAssetAPI.PakVersion) project.Settings.PakVersion}, compress {project.Settings.Compress}" +
                              (string.IsNullOrEmpty(project.Settings.OutputPath) ? "" : $", last output {project.Settings.OutputPath}"));
                foreach (var item in items.OrderBy(o => o.PakPath, StringComparer.OrdinalIgnoreCase).Take(limit))
                    sb.AppendLine($"{item.PakPath}  {PackWorkspaceViewModel.FormatSize(item.Size)}{(item.Modified ? "  EDITED" : "")}  <- {item.Origin}");
                if (items.Count > limit)
                    sb.AppendLine($"... {items.Count - limit} more");
                return Task.FromResult(sb.ToString());
            },
        },
        new()
        {
            Name = "pack_add_game_files",
            Description = "Adds files of the loaded game to the pak unchanged (packages come with their .uexp/.ubulk). Use find_assets (extension '*' for any file type) to get paths.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("paths", Array("Game file paths", String("Path")), true), ("target_folder", String("Put the files in this pak folder instead of their own path (rarely wanted)"), false)),
            Run = async (args, _) =>
            {
                var workspace = Workspace();
                var source = workspace.GameSource() ?? throw new AgentToolException("No game is loaded.");
                var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
                var paths = args.Strings("paths").Select(o => o.Trim().Replace('\\', '/').TrimStart('/')).ToList();
                var missing = paths.Where(o => !provider.Files.ContainsKey(o)).ToList();
                if (missing.Count > 0)
                    throw new AgentToolException($"Not in the game: {string.Join(", ", missing.Take(10))} (use find_assets).");
                var added = await workspace.AddFromSourceAsync(source, paths, args.Str("target_folder", false) ?? "");
                return $"Added {added} file(s). {workspace.Summary}";
            },
        },
        new()
        {
            Name = "pack_add_local_files",
            Description = "Adds files or folders from disk to the pak (videos, images, configs, packages built elsewhere). Only use paths the user gave you. " +
                          "Folders keep their own name and inner structure under target_folder.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("paths", Array("Files or folders on disk", String("Path")), true),
                ("target_folder", String("Pak folder to put them in, e.g. Game/Content/Movies"), true)),
            Run = async (args, _) =>
            {
                var workspace = Workspace();
                var paths = args.Strings("paths").Select(o => o.Trim().Trim('"')).ToList();
                var missing = paths.Where(o => !File.Exists(o) && !Directory.Exists(o)).ToList();
                if (missing.Count > 0)
                    throw new AgentToolException($"Not found on disk: {string.Join(", ", missing)}.");
                var added = await workspace.AddDiskPathsAsync(paths, args.Str("target_folder"));
                return $"Added {added} file(s). {workspace.Summary}";
            },
        },
        new()
        {
            Name = "pack_remove",
            Description = "Removes files from the pak project (packages: all their parts by default).",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("paths", Array("Pak paths", String("Path")), true), ("package_parts", Boolean("Also remove the .uexp/.ubulk of packages (default true)"), false)),
            Run = (args, _) =>
            {
                var workspace = Workspace();
                var parts = args.Bool("package_parts", true);
                var items = args.Strings("paths").Select(o => Item(workspace, o))
                    .SelectMany(o => parts && PackPaths.IsPackagePart(o.PakPath) ? workspace.Project.PackageOf(o) : [o])
                    .Distinct().ToList();
                workspace.Remove(items);
                return Task.FromResult($"Removed {items.Count} file(s). {workspace.Summary}");
            },
        },
        new()
        {
            Name = "pack_rename",
            Description = "Moves a file to another path inside the pak. Packages must keep their name to be found by the game; renaming is for loose files.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("path", PakPathArg, true), ("new_path", String("New pak path"), true)),
            Run = (args, _) =>
            {
                var workspace = Workspace();
                var item = Item(workspace, args.Str("path"));
                if (workspace.Rename(item, args.Str("new_path")) is { } error)
                    throw new AgentToolException(error);
                return Task.FromResult($"Renamed to {item.PakPath}.");
            },
        },
        new()
        {
            Name = "pack_set_options",
            Description = "Changes the pak settings: mount point (usually ../../../), pak version (match the game's paks) and compression.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("mount_point", String("Mount point"), false),
                ("pak_version", Enum("Pak version", System.Enum.GetNames<UAssetAPI.PakVersion>()), false), ("compress", Boolean("Compress the files"), false)),
            Run = (args, _) =>
            {
                var settings = Workspace().Project.Settings;
                if (args.Str("mount_point", false) is { } mount)
                    settings.MountPoint = mount;
                if (args.Str("pak_version", false) is { } version)
                    settings.PakVersion = System.Enum.TryParse<UAssetAPI.PakVersion>(version, true, out var parsed) ? (int) parsed : throw new AgentToolException($"Unknown pak version {version}.");
                if (args["compress"] != null)
                    settings.Compress = args.Bool("compress", settings.Compress);
                DockPanels.ShowPackWorkspace().ReloadSettings();
                return Task.FromResult($"mount point {settings.MountPoint}, pak version {(UAssetAPI.PakVersion) settings.PakVersion}, compress {settings.Compress}");
            },
        },
        new()
        {
            Name = "pack_build",
            Description = "Writes the pak file and returns its manifest (every file with pak path, size, SHA-256, whether it was edited) and the pak's own SHA-256. Patch paks usually need a name ending in _P (e.g. MyMod_P.pak) to override the game's files. Only call it when the user asked for the pak.",
            Access = AgentToolAccess.Write,
            InputSchema = Object(("output", String("Output .pak path on disk (default: the project's last output)"), false)),
            Run = async (args, _) =>
            {
                var workspace = Workspace();
                if (workspace.Project.Items.Count == 0)
                    throw new AgentToolException("The pak has no files.");
                var output = args.Str("output", false) ?? workspace.Project.Settings.OutputPath;
                if (string.IsNullOrWhiteSpace(output))
                    throw new AgentToolException("Give an output path (ask the user where to write the pak).");
                output = Path.GetFullPath(output.Trim().Trim('"'));
                if (!output.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                    throw new AgentToolException("The output must be a .pak file.");
                var message = $"The agent wants to build the pak\n\n{output}{(File.Exists(output) ? "  (replaces the existing file)" : "")}\n\n{workspace.Summary}\n\nAllow?";
                if (!await BlueprintAgentHost.ConfirmWrite(message))
                    throw new AgentToolException("The user declined the build.");
                var previous = File.Exists(output) ? new FileInfo(output) : null;
                var previousHash = previous == null ? null : AgentFiles.Sha256(output);
                if (await workspace.BuildAsync(output) is { } error)
                    throw new AgentToolException("Build failed: " + error);
                var manifest = AgentFiles.PakManifest(workspace, output);
                manifest["status"] = workspace.Status;
                manifest["replacedExistingFile"] = previous != null;
                if (previous != null)
                    manifest["previous"] = new JObject { ["size"] = previous.Length, ["sha256"] = previousHash, ["identical"] = previousHash == manifest["pak"]?["sha256"]?.ToString() };
                return manifest.ToString(Formatting.Indented);
            },
        },
        new()
        {
            Name = "pack_manifest",
            Description = "The pak project as structured data: mount point, pak version, every file with pak path, size, SHA-256, whether it was edited and where it came from. With pak, also describes that built .pak file (size and SHA-256). Writes nothing.",
            InputSchema = Object(("pak", String("A built .pak on disk to include"), false)),
            Run = (args, _) =>
            {
                var pak = args.Str("pak", false)?.Trim().Trim('"');
                if (pak != null && !File.Exists(pak))
                    throw new AgentToolException($"Not found on disk: {pak}.");
                return Task.FromResult(AgentFiles.PakManifest(Workspace(), pak).ToString(Formatting.Indented));
            },
        },
        new()
        {
            Name = "pack_import_pak",
            Description = "Starts from an existing .pak: adds its files to the pak project so a patch can build on it. conflict says what happens to files the project already has: " +
                          "keep (default: the project's file stays, e.g. your edit) or replace (the pak's file wins). Returns which files were added, kept and replaced. The pak is only read.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("pak", String("Path of the .pak on disk"), true), ("conflict", Enum("Files the project already has", ["keep", "replace"]), false),
                ("filter", String("Only files whose path contains this"), false)),
            Run = async (args, _) =>
            {
                var workspace = Workspace();
                var file = Path.GetFullPath(args.Str("pak").Trim().Trim('"'));
                if (!File.Exists(file))
                    throw new AgentToolException($"Not found on disk: {file}.");
                var replace = string.Equals(args.Str("conflict", false), "replace", StringComparison.OrdinalIgnoreCase);
                var filter = args.Str("filter", false);

                var sourcesBefore = workspace.Sources.Count;
                await workspace.AddPakSourceAsync(file);
                if (workspace.Sources.Count == sourcesBefore)
                    throw new AgentToolException("The pak could not be opened (encrypted? the AES key may be missing). " + workspace.Status);
                var source = workspace.Sources[^1];

                var wanted = source.Files.Select(o => o.Path).Where(o => filter == null || o.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                var existing = wanted.Where(o => workspace.Project.Find(o) != null).ToList();
                var toAdd = replace ? wanted : wanted.Except(existing, StringComparer.OrdinalIgnoreCase).ToList();
                await workspace.AddFromSourceAsync(source, toAdd);
                return new JObject
                {
                    ["pak"] = AgentFiles.Disk(file),
                    ["filesInPak"] = source.Files.Count,
                    ["added"] = wanted.Count - existing.Count,
                    ["kept"] = replace ? 0 : existing.Count,
                    ["replaced"] = replace ? existing.Count : 0,
                    ["keptPaths"] = new JArray(replace ? [] : existing.Take(50)),
                    ["replacedPaths"] = new JArray(replace ? existing.Take(50) : []),
                    ["project"] = workspace.Summary,
                }.ToString(Formatting.Indented);
            },
        },
        new()
        {
            Name = "pack_save_project",
            Description = "Saves the packer project (.florapack) so the user can reopen and rebuild it.",
            Access = AgentToolAccess.Write,
            InputSchema = Object(("file", String("Project file path (.florapack); default: where it was opened from"), false)),
            Run = async (args, _) =>
            {
                var workspace = Workspace();
                var file = args.Str("file", false) ?? workspace.Project.FilePath ?? throw new AgentToolException("Give a project file path.");
                file = Path.GetFullPath(file.Trim().Trim('"'));
                if (!file.EndsWith(".florapack", StringComparison.OrdinalIgnoreCase))
                    file += ".florapack";
                if (!await BlueprintAgentHost.ConfirmWrite($"The agent wants to save the pak project to\n\n{file}\n\nAllow?"))
                    throw new AgentToolException("The user declined.");
                if (workspace.SaveProject(file) is { } error)
                    throw new AgentToolException(error);
                return workspace.Status;
            },
        },

        #endregion

        #region localization

        new()
        {
            Name = "locres_list",
            Description = "Lists the game's localization files (.locres), optionally for one culture (e.g. zh-Hans, en, ja), and marks those already in the pak.",
            InputSchema = Object(("culture", String("Culture folder name, e.g. zh-Hans"), false)),
            Run = (args, _) =>
            {
                var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
                var culture = args.Str("culture", false);
                var project = Workspace().Project;
                var files = provider.Files.Values.Where(o => o.Extension.Equals("locres", StringComparison.OrdinalIgnoreCase) &&
                                                             (culture == null || o.Path.Contains($"/{culture}/", StringComparison.OrdinalIgnoreCase)))
                    .Select(o => o.Path).OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
                var inPak = project.Items.Where(o => o.Extension == "locres" && !provider.Files.ContainsKey(o.PakPath)).Select(o => o.PakPath + "  (only in the pak)");
                var lines = files.Select(o => project.Find(o) is { Modified: true } ? o + "  (edited in the pak)" : project.Find(o) != null ? o + "  (in the pak)" : o).Concat(inPak).ToList();
                return Task.FromResult(lines.Count == 0 ? "No .locres files." : string.Join("\n", lines.Take(500)));
            },
        },
        new()
        {
            Name = "locres_search",
            Description = "Reads entries of a .locres (the pak's edited copy when there is one, else the game's): namespace, key and text, filtered and paged. " +
                          "Text is JSON-escaped. Use it to find the keys to change; for whole-file work page through with skip.",
            InputSchema = Object(("path", String("Path of the .locres (see locres_list)"), true),
                ("query", String("Only entries whose key, namespace or text contains this"), false),
                ("namespace", String("Only this namespace"), false),
                ("skip", Integer("Entries to skip"), false), ("take", Integer("Entries to return (default 200, max 1000)"), false)),
            Run = (args, _) =>
            {
                var (path, locres, edited) = ReadLocres(args.Str("path"));
                var query = args.Str("query", false);
                var ns = args.Str("namespace", false);
                var matches = locres.Entries.Where(o => (ns == null || o.Namespace == ns) &&
                                                        (query == null || o.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                                         o.Namespace.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                                         o.Text.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
                var skip = Math.Max(0, args.Int("skip", 0));
                var take = Math.Clamp(args.Int("take", 200), 1, 1000);
                var sb = new StringBuilder($"{path} ({locres.Version}, {locres.EntryCount} entries{(edited ? ", edited copy from the pak" : "")}): {matches.Count} match\n");
                sb.AppendLine("namespace\tkey\ttext");
                foreach (var entry in matches.Skip(skip).Take(take))
                    sb.AppendLine($"{entry.Namespace}\t{entry.Key}\t{JsonConvert.ToString(entry.Text)}");
                if (skip + take < matches.Count)
                    sb.AppendLine($"... more: skip={skip + take}");
                return Task.FromResult(sb.ToString());
            },
        },
        new()
        {
            Name = "locres_set",
            Description = "Changes the text of existing .locres entries (matched by namespace and key; new keys can't be added). " +
                          "The file is added to the pak from the game first when needed. Keep format tags and arguments ({0}, <RichText>, \\n) intact.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("path", String("Path of the .locres"), true),
                ("entries", Array("Changes", Object(("namespace", String("Namespace (may be empty)"), true), ("key", String("Key"), true), ("text", String("New text"), true))), true)),
            Run = async (args, _) =>
            {
                var workspace = Workspace();
                var path = NormalizePath(args.Str("path"));
                var item = workspace.Project.Find(path);
                if (item == null)
                {
                    var source = workspace.GameSource() ?? throw new AgentToolException("No game is loaded.");
                    if (await workspace.AddFromSourceAsync(source, [path]) == 0 || (item = workspace.Project.Find(path)) == null)
                        throw new AgentToolException($"{path} is neither in the pak nor in the game (see locres_list).");
                }

                var locres = LocresResourceCodec.Read(File.ReadAllBytes(item.DiskPath));
                // a key may appear twice in odd files: changes go to the first one
                var index = locres.Entries.Select((entry, i) => (entry, i)).GroupBy(o => (o.entry.Namespace, o.entry.Key)).ToDictionary(o => o.Key, o => o.First().i);
                var entries = locres.Entries.ToArray();
                var unknown = new List<string>();
                var changed = 0;
                foreach (var change in args["entries"] as JArray ?? [])
                {
                    var key = (change.Value<string>("namespace") ?? "", change.Value<string>("key") ?? "");
                    if (!index.TryGetValue(key, out var i))
                    {
                        unknown.Add($"{key.Item1}/{key.Item2}");
                        continue;
                    }
                    var text = change.Value<string>("text") ?? "";
                    if (entries[i].Text != text)
                    {
                        entries[i] = entries[i] with { Text = text };
                        changed++;
                    }
                }
                if (unknown.Count > 0)
                    throw new AgentToolException($"Unknown entries (nothing was changed): {string.Join(", ", unknown.Take(20))}. Find the keys with locres_search.");
                if (changed > 0)
                    workspace.ReplaceContent(item, LocresResourceCodec.Write(locres with { Entries = entries }), item.Origin);
                return $"Changed {changed} entr{(changed == 1 ? "y" : "ies")} of {path} in the pak.";
            },
        },

        #endregion

        #region textures and sounds

        new()
        {
            Name = "texture_inspect",
            Description = "Format, size and mip count of a texture asset (from the pak, or the game's copy added to the pak).",
            InputSchema = Object(("path", PakPathArg, true)),
            Run = async (args, _) =>
            {
                var item = await EnsureInPakAsync(args.Str("path"));
                if (!PackReplace.IsTexture(item))
                    throw new AgentToolException($"{item.PakPath} is not a Texture2D asset.");
                var info = await PackReplace.InspectTextureAsync(item);
                return $"{item.PakPath}: {info.Format} {info.Width}x{info.Height}, {info.MipCount} mip(s)";
            },
        },
        new()
        {
            Name = "texture_replace",
            Description = "Replaces a texture with an image file from disk (png, jpg, tga, dds...): it is resized to the texture's size and encoded to its format with mips. " +
                          "The texture is added from the game first when needed. Only use image paths the user gave you.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("path", PakPathArg, true), ("image", String("Image file on disk"), true)),
            Run = async (args, _) =>
            {
                var image = args.Str("image").Trim().Trim('"');
                if (!File.Exists(image))
                    throw new AgentToolException($"{image} does not exist.");
                var item = await EnsureInPakAsync(args.Str("path"));
                if (!PackReplace.IsTexture(item))
                    throw new AgentToolException($"{item.PakPath} is not a Texture2D asset.");
                var info = await PackReplace.ReplaceTextureAsync(Workspace(), item, image);
                return $"Replaced {item.PakPath} with {Path.GetFileName(image)} ({info.Format} {info.Width}x{info.Height}).";
            },
        },
        new()
        {
            Name = "sound_replace",
            Description = "Replaces a sound wave: either encodes an audio file (wav, mp3, ogg, flac...) to Bink Audio into it (needs binka_encode.exe in Flora's tools folder), " +
                          "or copies another sound asset (.uasset with its .uexp/.ubulk) over it. The sound is added from the game first when needed. Only use paths the user gave you.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("path", PakPathArg, true), ("audio_file", String("Audio file to encode"), false),
                ("sound_asset", String("Sound .uasset on disk to copy instead"), false), ("quality", Integer("Bink quality 0 (best) - 9 (smallest), default 4"), false)),
            Run = async (args, _) =>
            {
                var audio = args.Str("audio_file", false)?.Trim().Trim('"');
                var asset = args.Str("sound_asset", false)?.Trim().Trim('"');
                if ((audio == null) == (asset == null))
                    throw new AgentToolException("Give either audio_file or sound_asset.");
                var file = audio ?? asset!;
                if (!File.Exists(file))
                    throw new AgentToolException($"{file} does not exist.");
                var item = await EnsureInPakAsync(args.Str("path"));
                if (!PackReplace.IsSound(item))
                    throw new AgentToolException($"{item.PakPath} is not a sound wave asset.");
                if (asset != null)
                    return $"Copied {PackReplace.ReplaceSoundAsset(Workspace(), item, asset)} part(s) of {Path.GetFileName(asset)} over {item.PakPath}.";
                if (!BinkaEncoder.IsAvailable)
                    throw new AgentToolException($"{BinkaEncoder.ToolName} is not installed: the user has to put it into {BinkaEncoder.ToolsDirectory} (see the README).");
                var result = await PackReplace.EncodeSoundAsync(Workspace(), item, audio!, Math.Clamp(args.Int("quality", 4), 0, 9));
                return $"Encoded {Path.GetFileName(audio)} into {item.PakPath}: {result.Seconds:0.##} s, {result.Channels} ch, {result.SampleRate} Hz.";
            },
        },

        #endregion
    ];

    #region helpers

    /// <summary>The pak packer's workspace; opens the panel (the user sees the changes there).</summary>
    private static PackWorkspaceViewModel Workspace() => DockPanels.ShowPackWorkspace().ViewModel;

    private static string NormalizePath(string path) => PackPaths.Normalize(path.Trim().Replace('\\', '/'));

    private static PackItem Item(PackWorkspaceViewModel workspace, string path) =>
        workspace.Project.Find(NormalizePath(path)) ?? throw new AgentToolException($"{path} is not in the pak (see pack_status).");

    /// <summary>The pak's copy of a game file, added from the game when the pak doesn't have it yet.</summary>
    private static async Task<PackItem> EnsureInPakAsync(string path)
    {
        var workspace = Workspace();
        path = NormalizePath(path);
        if (workspace.Project.Find(path) is { } existing)
            return existing;
        var source = workspace.GameSource() ?? throw new AgentToolException("No game is loaded.");
        if (!ApplicationService.ApplicationView.CUE4Parse.Provider.Files.ContainsKey(path))
            throw new AgentToolException($"{path} is neither in the pak nor in the game (use find_assets).");
        await workspace.AddFromSourceAsync(source, [path]);
        return workspace.Project.Find(path) ?? throw new AgentToolException($"Could not add {path} from the game.");
    }

    /// <summary>A .locres as the pak would ship it: the edited copy when the pak has one, else the game's file.</summary>
    private static (string Path, LocresPreviewDto Locres, bool Edited) ReadLocres(string path)
    {
        path = NormalizePath(path);
        if (Workspace().Project.Find(path) is { } item)
            return (path, LocresResourceCodec.Read(File.ReadAllBytes(item.DiskPath)), item.Modified);
        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
        if (!provider.Files.TryGetValue(path, out var file))
            throw new AgentToolException($"{path} is neither in the pak nor in the game (see locres_list).");
        return (path, LocresResourceCodec.Read(file.Read()), false);
    }

    #endregion
}
