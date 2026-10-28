using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework.BlueprintEditing;
using FModel.Framework.Docking;
using FModel.Services;
using FModel.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UAssetAPI.PropertyTypes.Structs;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>A DataTable package opened for row editing: the UAssetAPI package, the editor over its rows and the undo history.</summary>
public sealed class DataTableSession : IDisposable
{
    private readonly string _workDirectory;
    private readonly string _stagedPath;
    private readonly Stack<List<StructPropertyData>> _undo = new();
    private int _saves;

    public GameFile Entry { get; }
    public DataTableEditor Editor { get; }

    private DataTableSession(GameFile entry, DataTableEditor editor, string stagedPath, string workDirectory)
    {
        Entry = entry;
        Editor = editor;
        _stagedPath = stagedPath;
        _workDirectory = workDirectory;
    }

    /// <summary>Stages and loads the package with UAssetAPI (slow: also its dependencies).</summary>
    public static async Task<DataTableSession> OpenAsync(CUE4ParseViewModel owner, GameFile entry)
    {
        var (asset, staged, work) = await Task.Run(() =>
        {
            var loaded = owner.LoadBlueprintAssetForEditing(entry, out var stagedPath, out var workPath);
            return (loaded, stagedPath, workPath);
        });
        try
        {
            return new DataTableSession(entry, new DataTableEditor(asset), staged, work);
        }
        catch
        {
            CUE4ParseViewModel.DeleteBlueprintEditDirectory(work);
            throw;
        }
    }

    public void PushUndo(List<StructPropertyData> snapshot = null) => _undo.Push(snapshot ?? Editor.Snapshot());

    public bool Undo()
    {
        if (!_undo.TryPop(out var snapshot))
            return false;
        Editor.Restore(snapshot);
        return true;
    }

    public int UndoDepth => _undo.Count;

    /// <summary>
    /// Writes the package in a work folder and checks it: UAssetAPI reads it back with the same rows, CUE4Parse can read it.
    /// Returns the written .uasset path, or throws with what differs.
    /// </summary>
    public string WriteAndVerify(CUE4ParseViewModel owner)
    {
        var problems = Editor.Validate();
        if (problems.Count > 0)
            throw new AgentToolException("The table is not consistent: " + string.Join("; ", problems.Take(10)));

        var directory = Path.Combine(_workDirectory, "_save", (++_saves).ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(_stagedPath));
        try
        {
            Editor.Asset.Write(path);
        }
        catch (Exception e)
        {
            throw new AgentToolException($"[write] UAssetAPI could not write the table: {ExceptionText.Describe(e)}");
        }

        try
        {
            var reread = new DataTableEditor(CUE4ParseViewModel.ReadBlueprintAssetLike(Editor.Asset, path));
            if (reread.Rows.Count != Editor.Rows.Count)
                throw new AgentToolException($"[read back] the written table has {reread.Rows.Count} rows, expected {Editor.Rows.Count}");
            foreach (var row in Editor.Rows)
            {
                var other = reread.Find(row.Name.ToString());
                if (other == null)
                    throw new AgentToolException($"[read back] the row '{row.Name}' is missing from the written table");
                var before = Editor.ReadRow(row);
                var after = reread.ReadRow(other);
                if (!JToken.DeepEquals(before, after))
                {
                    var cells = new List<(string, string, string)>();
                    var detail = string.Join("; ", Differences(before, after).Take(5));
                    throw new AgentToolException($"[read back] the row '{row.Name}' reads back differently: {detail}");
                }
            }

            var package = owner.ReadLegacyPackageFromDisk(Entry, path);
            if (!package.GetExports().Any())
                throw new AgentToolException("[read back] CUE4Parse reads no exports from the written table");
        }
        catch (AgentToolException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new AgentToolException($"[read back] the written table can't be read: {ExceptionText.Describe(e)}");
        }
        return path;
    }

    private static IEnumerable<string> Differences(JToken before, JToken after, string path = "")
    {
        if (before is JObject b && after is JObject a)
        {
            foreach (var key in b.Properties().Select(o => o.Name).Union(a.Properties().Select(o => o.Name)))
                foreach (var difference in Differences(b[key] ?? JValue.CreateNull(), a[key] ?? JValue.CreateNull(), path.Length == 0 ? key : $"{path}.{key}"))
                    yield return difference;
        }
        else if (!JToken.DeepEquals(before, after))
        {
            yield return $"{path}: {before.ToString(Formatting.None)} -> {after.ToString(Formatting.None)}";
        }
    }

    public void Dispose() => CUE4ParseViewModel.DeleteBlueprintEditDirectory(_workDirectory);
}

/// <summary>Agent tools for DataTable packages: read the rows, change them all-or-nothing, see the row-level diff, import and export, save into the pak.</summary>
public static class DataTableAgentTools
{
    private static readonly Dictionary<string, DataTableSession> Sessions = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JObject TableArg = String("Open DataTable: package path or name (see list_datatables). Optional when only one is open.");

    public static IEnumerable<AgentTool> Build() =>
    [
        new()
        {
            Name = "open_datatable",
            Description = "Opens a DataTable package (.uasset with a UDataTable export) for row editing and returns its row struct, row count and columns. Cooked DataTables are not blueprints: open_blueprint refuses them.",
            InputSchema = Object(("path", String("Package path from find_assets"), true)),
            Run = async (args, _) =>
            {
                var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
                var path = args.Str("path").Trim().Replace('\\', '/');
                if (!cue4Parse.Provider.Files.TryGetValue(path, out var entry) && !cue4Parse.Provider.Files.TryGetValue(path.TrimStart('/'), out entry) &&
                    !Blueprints.BlueprintNavigator.TryFindGameFile(cue4Parse.Provider, path, out entry))
                    throw new AgentToolException($"No package '{path}' in the loaded game (use find_assets).");
                if (Sessions.TryGetValue(entry.Path, out var existing))
                    return Describe(existing, "already open");

                DataTableSession session;
                try
                {
                    session = await DataTableSession.OpenAsync(cue4Parse, entry);
                }
                catch (InvalidOperationException e) when (e.Message.Contains("no DataTable export"))
                {
                    throw new AgentToolException($"{entry.Path} has no DataTable export" + (cue4Parse.IsBlueprintPackage(entry) ? " (it is a blueprint: use open_blueprint)." : "."));
                }
                Sessions[entry.Path] = session;
                return Describe(session, "opened");
            },
        },
        new()
        {
            Name = "list_datatables",
            Description = "The DataTables open for editing, with their pending changes (rows added, removed, changed).",
            InputSchema = Object(),
            Run = (_, _) => Task.FromResult(Sessions.Count == 0 ? "No DataTable is open (open_datatable)." : string.Join("\n", Sessions.Values.Select(o => Describe(o, null)))),
        },
        new()
        {
            Name = "close_datatable",
            Description = "Closes an open DataTable and drops its pending changes.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("table", TableArg, false)),
            Run = (args, _) =>
            {
                var session = Resolve(args);
                Sessions.Remove(session.Entry.Path);
                session.Dispose();
                return Task.FromResult($"Closed {session.Entry.Path}.");
            },
        },
        new()
        {
            Name = "dt_rows",
            Description = "Reads rows as plain JSON ({column: value}; nested structs are objects, arrays are arrays, object references are \"import:...\" strings, soft references \"/Game/x.y\"). " +
                          "Filter by row name, by column values, choose columns, page with skip/take. Always states the total, the number shown and the next skip.",
            InputSchema = Object(("table", TableArg, false), ("filter", String("Rows whose name contains this (case-insensitive)"), false),
                ("where", Any("Object {column: value}: rows whose column equals the value (strings compared as text, case-insensitive); a column path like Stats.Armor works"), false),
                ("columns", Array("Only these columns", String("Column")), false),
                ("skip", Integer("Rows to skip"), false), ("take", Integer("Rows to return (default 50, max 500)"), false)),
            Run = (args, _) =>
            {
                var session = Resolve(args);
                var editor = session.Editor;
                var filter = args.Str("filter", false);
                var where = args["where"] as JObject;
                var columns = args.Strings("columns").ToList();
                var matches = editor.Rows.Where(row =>
                {
                    if (filter != null && !row.Name.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (where == null)
                        return true;
                    var json = editor.ReadRow(row);
                    return where.Properties().All(o => json.SelectToken(o.Name.Replace("[", ".[").Replace(".[", "[")) is { } cell &&
                                                       string.Equals(Text(cell), Text(o.Value), StringComparison.OrdinalIgnoreCase));
                }).ToList();

                var skip = Math.Max(0, args.Int("skip", 0));
                var take = Math.Clamp(args.Int("take", 50), 1, 500);
                var shown = matches.Skip(skip).Take(take).ToList();
                var sb = new StringBuilder($"{session.Entry.Path}: {matches.Count} matching of {editor.Rows.Count} rows" +
                                           (shown.Count == 0 ? "" : $", showing {skip + 1}-{skip + shown.Count}") + (skip + shown.Count < matches.Count ? $" (next skip={skip + shown.Count})" : "") + "\n");
                foreach (var row in shown)
                {
                    var json = editor.ReadRow(row);
                    if (columns.Count > 0)
                        json = new JObject(columns.Select(c => new JProperty(c, json[c])));
                    sb.AppendLine($"{row.Name}  {json.ToString(Formatting.None)}");
                }
                return Task.FromResult(sb.ToString());
            },
        },
        new()
        {
            Name = "dt_get_row",
            Description = "One row, pretty-printed: every column with its type, and the value in the form dt_edit takes.",
            InputSchema = Object(("table", TableArg, false), ("row", String("Row name (case-insensitive)"), true)),
            Run = (args, _) =>
            {
                var editor = Resolve(args).Editor;
                var row = editor.Find(args.Str("row")) ?? throw new AgentToolException($"No row '{args.Str("row")}' (dt_rows with filter finds names).");
                var types = editor.Columns().ToDictionary(o => o.Name, o => o.Type);
                var json = editor.ReadRow(row);
                return Task.FromResult($"{row.Name}\n" + json.ToString(Formatting.Indented) + "\ncolumn types: " +
                                       string.Join(", ", json.Properties().Select(o => $"{o.Name}:{(types.TryGetValue(o.Name, out var type) ? type : "?")}")));
            },
        },
        new()
        {
            Name = "dt_edit",
            Description = "Changes rows. 'operations' is a list applied in order, all or nothing (any error leaves the table untouched and names the operation and JSON path): " +
                          "{op:'set', row, values:{column: value}} changes cells of a row; {op:'add', row, values?, copy_from?} adds a row that starts as a copy of copy_from (default the first row) with the given values; " +
                          "{op:'upsert', row, values, copy_from?} sets or adds; {op:'delete', row}; {op:'rename', row, new_name}. Row names are not case sensitive and must be unique. " +
                          "Values use dt_rows' form; only the given columns change. dry_run checks without changing anything. Returns the resulting row-level changes; undo with dt_undo.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("table", TableArg, false),
                ("operations", Array("Operations", Object(("op", Enum("Operation", "set", "add", "upsert", "delete", "rename"), true), ("row", String("Row name"), true),
                    ("values", Any("{column: value} (set, add, upsert)"), false), ("copy_from", String("add/upsert: row to copy the other columns from"), false),
                    ("new_name", String("rename: the new name"), false))), true),
                ("dry_run", Boolean("Only check the operations"), false)),
            Run = (args, _) =>
            {
                var session = Resolve(args);
                var operations = (args["operations"] as JArray ?? throw new AgentToolException("'operations' is required (an array)."))
                    .Select((o, i) => o is JObject json
                        ? new DataTableOperation { Op = json.Value<string>("op") ?? "", Row = json.Value<string>("row") ?? "", Values = json["values"] as JObject, CopyFrom = json.Value<string>("copy_from"), NewName = json.Value<string>("new_name") }
                        : throw new AgentToolException($"operations[{i}] is not an object"))
                    .ToList();
                if (operations.Count == 0)
                    throw new AgentToolException("No operations.");

                var snapshot = session.Editor.Snapshot();
                var errors = session.Editor.Apply(operations);
                if (errors.Count > 0)
                    throw new AgentToolException("Nothing was changed. " + string.Join("\n", errors));
                var problems = session.Editor.Validate();
                if (args.Bool("dry_run", false))
                {
                    session.Editor.Restore(snapshot);
                    return Task.FromResult($"Dry run: the {operations.Count} operation(s) are valid." + (problems.Count > 0 ? "\nThe table would have problems: " + string.Join("; ", problems) : ""));
                }

                session.PushUndo(snapshot);
                return Task.FromResult($"Applied {operations.Count} operation(s). " + DiffSummary(session, 30) + (problems.Count > 0 ? "\nWARNING, the table now has problems: " + string.Join("; ", problems) : ""));
            },
        },
        new()
        {
            Name = "dt_diff",
            Description = "Row-level difference between the table as opened and now: rows added, removed (a rename is both), and per changed row the cells with before and after.",
            InputSchema = Object(("table", TableArg, false), ("limit", Integer("Rows to list (default 50)"), false)),
            Run = (args, _) => Task.FromResult(DiffSummary(Resolve(args), Math.Clamp(args.Int("limit", 50), 1, 500))),
        },
        new()
        {
            Name = "dt_validate",
            Description = "Checks the table: duplicate row names (not case sensitive), empty names, rows whose columns or types differ from the first row's. Run it before dt_save.",
            InputSchema = Object(("table", TableArg, false)),
            Run = (args, _) =>
            {
                var problems = Resolve(args).Editor.Validate();
                return Task.FromResult(problems.Count == 0 ? "Valid: unique row names, every row has the same columns and types." : string.Join("\n", problems.Select(o => "- " + o)));
            },
        },
        new()
        {
            Name = "dt_undo",
            Description = "Undoes the last dt_edit (or dt_import).",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("table", TableArg, false)),
            Run = (args, _) =>
            {
                var session = Resolve(args);
                return Task.FromResult(session.Undo() ? $"Undone. {session.UndoDepth} more step(s) can be undone. " + DiffSummary(session, 10) : throw new AgentToolException("Nothing to undo."));
            },
        },
        new()
        {
            Name = "dt_revert",
            Description = "Puts one row (or every row, without 'row') back as it was when the table was opened. A renamed row is reverted by reverting both names.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("table", TableArg, false), ("row", String("Row name; omit for the whole table"), false)),
            Run = (args, _) =>
            {
                var session = Resolve(args);
                session.PushUndo();
                try
                {
                    var reverted = session.Editor.Revert(args.Str("row", false));
                    return Task.FromResult($"Reverted {reverted.Count} row(s). " + DiffSummary(session, 10));
                }
                catch (InvalidOperationException e)
                {
                    session.Undo();
                    throw new AgentToolException(e.Message);
                }
            },
        },
        new()
        {
            Name = "dt_export",
            Description = "Writes the table (as it is now) to a file: json ({struct, columns, rows}) or csv (first column Name; nested values as JSON text). Use it to see or share a large table without paging.",
            Access = AgentToolAccess.Read,
            InputSchema = Object(("table", TableArg, false), ("file", String("Output file path (.json or .csv)"), true), ("format", Enum("Format (default: by the file extension)", "json", "csv"), false)),
            Run = (args, _) =>
            {
                var session = Resolve(args);
                var file = Path.GetFullPath(args.Str("file").Trim().Trim('"'));
                var csv = (args.Str("format", false) ?? Path.GetExtension(file).TrimStart('.')).Equals("csv", StringComparison.OrdinalIgnoreCase);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, csv ? session.Editor.ExportCsv() : session.Editor.ExportJson().ToString(Formatting.Indented), new UTF8Encoding(false));
                return Task.FromResult($"Wrote {session.Editor.Rows.Count} rows to {file}\n{AgentFiles.Disk(file).ToString(Formatting.None)}");
            },
        },
        new()
        {
            Name = "dt_import",
            Description = "Reads rows from a json file (dt_export's format, or an array of {Name, column: value}) or a csv (first column the row name, header = column names). " +
                          "mode merge (default) adds new rows and sets the given columns of existing ones; replace also deletes the rows that are not in the file. All or nothing; undo with dt_undo.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("table", TableArg, false), ("file", String("Input file path"), true), ("mode", Enum("merge or replace", "merge", "replace"), false)),
            Run = (args, _) =>
            {
                var session = Resolve(args);
                var file = args.Str("file").Trim().Trim('"');
                if (!File.Exists(file))
                    throw new AgentToolException($"{file} does not exist.");
                List<DataTableOperation> operations;
                try
                {
                    operations = session.Editor.ParseImport(File.ReadAllText(file), Path.GetExtension(file).Equals(".csv", StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception e) when (e is FormatException or JsonException)
                {
                    throw new AgentToolException($"Could not read {file}: {e.Message}");
                }
                if (args.Str("mode", false) == "replace")
                {
                    var keep = operations.Select(o => o.Row).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    operations.AddRange(session.Editor.Rows.Select(o => o.Name.ToString()).Where(o => !keep.Contains(o)).Select(o => new DataTableOperation { Op = "delete", Row = o }));
                }

                var snapshot = session.Editor.Snapshot();
                var errors = session.Editor.Apply(operations);
                if (errors.Count > 0)
                    throw new AgentToolException("Nothing was changed. " + string.Join("\n", errors.Take(10)));
                session.PushUndo(snapshot);
                return Task.FromResult($"Imported {operations.Count} operation(s) from {Path.GetFileName(file)}. " + DiffSummary(session, 20));
            },
        },
        new()
        {
            Name = "dt_save",
            Description = "Validates the table, writes it and checks the written package (UAssetAPI reads it back with identical rows, CUE4Parse reads it), then puts it into the pak packer (create_pak, default) and/or writes the package files (export_asset). " +
                          "Returns the files with pak paths, sizes and SHA-256. Writes files: only call it when the user asked to save or build.",
            Access = AgentToolAccess.Write,
            InputSchema = Object(("table", TableArg, false), ("export_asset", Boolean("Write the .uasset/.uexp to the exports folder"), false), ("create_pak", Boolean("Add the package to the pak packer (default true)"), false)),
            Run = async (args, _) =>
            {
                var session = Resolve(args);
                var export = args.Bool("export_asset", false);
                var pak = args.Bool("create_pak", true);
                if (!export && !pak)
                    throw new AgentToolException("Choose export_asset and/or create_pak.");
                if (!session.Editor.IsModified)
                    throw new AgentToolException("There is nothing to save: the table is as it was opened.");
                if (!await BlueprintAgentHost.ConfirmWrite($"The agent wants to save the DataTable {session.Entry.Path}\n\n{DiffSummary(session, 8)}\n\nAllow?"))
                    throw new AgentToolException("The user declined the save.");

                var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
                await Task.Run(() => session.WriteAndVerify(cue4Parse));
                var result = new JObject { ["table"] = session.Entry.Path, ["rows"] = session.Editor.Rows.Count, ["changes"] = session.Editor.Diff().Count, ["verified"] = "written, read back by UAssetAPI with identical rows, read by CUE4Parse" };
                if (pak)
                {
                    if (!cue4Parse.AddEditedBlueprintToPack(session.Entry, session.Editor.Asset, out var added, out var error))
                        throw new AgentToolException("[pack] " + error);
                    result["pak"] = AgentFiles.PakItems(DockPanels.ShowPackWorkspace().ViewModel, AgentFiles.SplitPaths(added));
                }
                if (export)
                    result["exported"] = AgentFiles.PackageFiles(cue4Parse.WriteEditedBlueprint(session.Entry, session.Editor.Asset));
                return result.ToString(Formatting.Indented);
            },
        },
    ];

    private static string Text(JToken value) => value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None);

    private static DataTableSession Resolve(JObject args)
    {
        var table = args.Str("table", false);
        if (string.IsNullOrWhiteSpace(table))
        {
            return Sessions.Count switch
            {
                1 => Sessions.Values.First(),
                0 => throw new AgentToolException("No DataTable is open (open_datatable)."),
                _ => throw new AgentToolException("Several DataTables are open; pass 'table' (see list_datatables)."),
            };
        }
        var key = table.Trim().Replace('\\', '/');
        return Sessions.Values.FirstOrDefault(o => string.Equals(o.Entry.Path, key, StringComparison.OrdinalIgnoreCase)) ??
               Sessions.Values.FirstOrDefault(o => o.Entry.Path.EndsWith(key, StringComparison.OrdinalIgnoreCase) || o.Entry.Path.EndsWith(key + ".uasset", StringComparison.OrdinalIgnoreCase)) ??
               Sessions.Values.FirstOrDefault(o => string.Equals(o.Entry.NameWithoutExtension, key, StringComparison.OrdinalIgnoreCase)) ??
               throw new AgentToolException($"'{table}' is not open. Open tables: {string.Join(", ", Sessions.Values.Select(o => o.Entry.Path))}.");
    }

    private static string Describe(DataTableSession session, string state)
    {
        var editor = session.Editor;
        var changes = editor.Diff();
        var sb = new StringBuilder($"{session.Entry.Path}{(state == null ? "" : $" ({state})")}: {editor.Rows.Count} rows, row struct {editor.RowStruct}\n");
        sb.AppendLine("columns: " + string.Join(", ", editor.Columns().Select(o => $"{o.Name}:{o.Type}")));
        sb.Append(changes.Count == 0 ? "no changes" : $"changes: {changes.Count(o => o.Kind == "added")} added, {changes.Count(o => o.Kind == "removed")} removed, {changes.Count(o => o.Kind == "changed")} changed");
        return sb.ToString();
    }

    private static string DiffSummary(DataTableSession session, int limit)
    {
        var changes = session.Editor.Diff();
        if (changes.Count == 0)
            return "The table is as it was opened.";
        var sb = new StringBuilder($"{changes.Count} changed row(s) since opened ({changes.Count(o => o.Kind == "added")} added, {changes.Count(o => o.Kind == "removed")} removed, {changes.Count(o => o.Kind == "changed")} changed):\n");
        foreach (var change in changes.Take(limit))
        {
            sb.AppendLine($"  {change.Kind,-7} {change.Row}");
            foreach (var (path, before, after) in change.Cells.Take(8))
                sb.AppendLine($"            {path}: {before} -> {after}");
            if (change.Cells.Count > 8)
                sb.AppendLine($"            ... {change.Cells.Count - 8} more cell(s)");
        }
        if (changes.Count > limit)
            sb.AppendLine($"  ... {changes.Count - limit} more row(s)");
        return sb.ToString().TrimEnd();
    }
}
