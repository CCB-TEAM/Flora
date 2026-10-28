using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FModel.BlueprintGraph.Nodes;
using FModel.Framework.BlueprintEditing;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// The tools the language model edits blueprints with. Graph tools (L1) go through the same planner as the old wire editing;
/// bytecode object tools (L2) build Kismet expressions from <see cref="KismetObjects"/> JSON. Every edit stays pending in the
/// viewer (Changes list, undo) until <c>save_blueprint</c>.
/// </summary>
public static class BlueprintAgentTools
{
    private const int MaxResult = 60_000;

    private static readonly JObject BlueprintArg = String("Open blueprint: package path or name (see list_blueprints). Optional when only one is open.");
    private static readonly JObject FunctionArg = String("Event or function name as list_functions shows it (events share the event graph).");
    private static readonly JObject BytecodeFunctionArg = String("Bytecode function name (e.g. ExecuteUbergraph_BP_Hero or a function name). An event name selects the event graph, like get_graph; 'stub:<event>' is the event's own stub function.");
    private static readonly JObject OffsetArg = Integer("In-memory bytecode offset of the original code, as get_graph / kismet_list_statements show it.");
    private static readonly JObject TokenArg = String("The EX_ token expected at that offset (e.g. EX_Let); guards against stale offsets.");

    public static IReadOnlyList<AgentTool> All { get; } = [AgentGuide.Tool, .. Build(), .. DataTableAgentTools.Build(), .. JmapAgentTools.Build(), .. JmapCalls.Build(), .. PackAgentTools.Build()];

    /// <summary>The tools offered now (bytecode object tools only when the setting allows them).</summary>
    public static IEnumerable<AgentTool> Available() =>
        All.Where(o => o.Access != AgentToolAccess.ObjectEdit || UserSettings.Default.AgentAllowBytecodeObjects);

    /// <summary>Runs a tool by name on the UI thread; failures come back as text marked as an error.</summary>
    public static async Task<(string Text, bool IsError)> RunAsync(string name, JObject args, CancellationToken cancellation)
    {
        var tool = Available().FirstOrDefault(o => o.Name == name);
        if (tool == null)
        {
            var hidden = All.Any(o => o.Name == name);
            return (hidden ? $"'{name}' is disabled: bytecode object editing is turned off in the agent settings." : $"Unknown tool '{name}'.", true);
        }
        try
        {
            var text = tool.OffUiThread ? await tool.Run(args ?? new JObject(), cancellation) : await BlueprintAgentHost.OnUiAsync(() => tool.Run(args ?? new JObject(), cancellation));
            return (Truncate(text), false);
        }
        catch (AgentToolException e)
        {
            return (e.Message, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Agent tool {Tool} failed", name);
            return ($"{e.GetType().Name}: {e.Message}", true);
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxResult ? text : text[..MaxResult] + $"\n... (truncated, {text.Length - MaxResult} more characters; narrow the request)";

    private static List<AgentTool> Build() =>
    [
        #region assets and blueprints

        new()
        {
            Name = "find_assets",
            Description = "Searches the loaded game's files by path. Every space-separated term must appear in the path (case-insensitive). By default only packages (.uasset); " +
                          "pass extension (locres, umap, ini, bnk...) or '*' for other files. Results are in a stable order (shortest path first) and paged: the header gives the total and the skip for the next page. " +
                          "exact matches a whole path or file name (BP_Hero or BP_Hero.uasset); path_prefix limits to a folder; export_file writes the full result list to a file instead of paging. Returns paths for open_blueprint and the pack tools.",
            InputSchema = Object(("query", String("Path terms, e.g. 'Hero BP_'. May be empty with exact or path_prefix"), false),
                ("extension", String("File extension without the dot, or * for any (default uasset)"), false),
                ("exact", String("A full path or file name to match exactly (ignoring case and the extension if omitted)"), false),
                ("path_prefix", String("Only paths starting with this folder, e.g. Game/Content/Characters"), false),
                ("skip", Integer("Results to skip (the next page: use 'next skip' from the last result)"), false),
                ("limit", Integer("Page size (default 40, at most 500)"), false),
                ("export_file", Boolean("Write all matches to a text file and return its path, size and SHA-256"), false)),
            Run = (args, _) =>
            {
                var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
                var terms = (args.Str("query", false) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var extension = (args.Str("extension", false) ?? "uasset").Trim().TrimStart('.');
                var exact = args.Str("exact", false)?.Trim().Replace('\\', '/').TrimStart('/');
                var prefix = args.Str("path_prefix", false)?.Trim().Replace('\\', '/').TrimStart('/');
                var skip = Math.Max(0, args.Int("skip", 0));
                var limit = Math.Clamp(args.Int("limit", 40), 1, 500);
                if (terms.Length == 0 && exact == null && prefix == null)
                    throw new AgentToolException("Give a query, exact or path_prefix.");

                bool IsExact(string path)
                {
                    if (exact == null)
                        return true;
                    var name = path[(path.LastIndexOf('/') + 1)..];
                    return path.Equals(exact, StringComparison.OrdinalIgnoreCase) || name.Equals(exact, StringComparison.OrdinalIgnoreCase) ||
                           System.IO.Path.GetFileNameWithoutExtension(name).Equals(exact, StringComparison.OrdinalIgnoreCase) ||
                           System.IO.Path.ChangeExtension(path, null).Equals(exact, StringComparison.OrdinalIgnoreCase);
                }

                var all = provider.Files.Values
                    .Where(o => (extension == "*" || o.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)) && terms.All(t => o.Path.Contains(t, StringComparison.OrdinalIgnoreCase)) &&
                                (prefix == null || o.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && IsExact(o.Path))
                    .Select(o => o.Path).Distinct().OrderBy(o => o.Length).ThenBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
                if (all.Count == 0)
                    return Task.FromResult("No file matches (total 0).");

                if (args.Bool("export_file", false))
                {
                    var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FModel", "Agent", "Exports");
                    System.IO.Directory.CreateDirectory(directory);
                    var file = System.IO.Path.Combine(directory, $"find_assets_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    System.IO.File.WriteAllLines(file, all);
                    return Task.FromResult($"Total {all.Count} matches written to the file:\n{AgentFiles.Disk(file).ToString(Newtonsoft.Json.Formatting.None)}");
                }

                var page = all.Skip(skip).Take(limit).ToList();
                if (page.Count == 0)
                    return Task.FromResult($"Total {all.Count} matches; skip {skip} is past the end.");
                var end = skip + page.Count;
                return Task.FromResult($"Total {all.Count} matches, showing {skip + 1}-{end}" + (end < all.Count ? $" (next skip {end})" : " (last page)") + ":\n" + string.Join("\n", page));
            },
        },
        new()
        {
            Name = "open_blueprint",
            Description = "Opens a blueprint package in Flora's viewer (the user sees it) and waits until it is loaded. Accepts a package path from find_assets or an object path.",
            InputSchema = Object(("path", String("Package path (Game/.../BP_Hero.uasset) or object path (/Game/.../BP_Hero.BP_Hero_C)"), true)),
            Run = async (args, cancellation) =>
            {
                var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
                var path = args.Str("path").Trim().Replace('\\', '/');
                if (!cue4Parse.Provider.Files.TryGetValue(path, out var entry) &&
                    !cue4Parse.Provider.Files.TryGetValue(path.TrimStart('/'), out entry) &&
                    !Blueprints.BlueprintNavigator.TryFindGameFile(cue4Parse.Provider, path, out entry))
                    throw new AgentToolException($"No package '{path}' in the loaded game (use find_assets).");
                if (!cue4Parse.IsBlueprintPackage(entry))
                {
                    var isTable = false;
                    try { isTable = cue4Parse.Provider.LoadPackage(entry).GetExports().Any(o => o.GetType().Name == "UDataTable"); }
                    catch (Exception) { /* not readable: it is not a blueprint either way */ }
                    throw new AgentToolException(isTable
                        ? $"{entry.Path} is a DataTable, not a blueprint: use open_datatable, then dt_rows / dt_edit / dt_save."
                        : $"{entry.Path} is not a blueprint.");
                }

                cue4Parse.OpenBlueprintViewer(entry);
                for (var waited = 0; waited < 180_000; waited += 250)
                {
                    if (BlueprintAgentHost.Open.FirstOrDefault(o => o.Entry.Path == entry.Path) is { } viewer)
                        return $"Opened {entry.Path}: {viewer.Functions.Count(o => o.Kind == EBlueprintFunctionKind.Event)} event(s), " +
                               $"{viewer.Functions.Count(o => o.Kind == EBlueprintFunctionKind.Function)} function(s). Next: list_functions.";
                    await Task.Delay(250, cancellation);
                }
                throw new AgentToolException("The viewer did not finish loading in time; check the viewer tab.");
            },
        },
        new()
        {
            Name = "list_blueprints",
            Description = "Lists the blueprints open in viewers, with their pending edit counts.",
            InputSchema = Object(),
            Run = (_, _) =>
            {
                var open = BlueprintAgentHost.Open;
                if (open.Count == 0)
                    return Task.FromResult("No blueprint is open.");
                return Task.FromResult(string.Join("\n", open.Select(o => $"{o.Entry.Path}  ({(o.Editor.Session == null ? "not loaded for editing" : o.Editor.PendingText)})")));
            },
        },
        new()
        {
            Name = "list_functions",
            Description = "Lists the events and functions of a blueprint. All events live in one event graph (the ubergraph); get_graph on any event shows it.",
            InputSchema = Object(("blueprint", BlueprintArg, false)),
            Run = (args, _) =>
            {
                var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
                var sb = new StringBuilder();
                foreach (var item in viewer.Functions)
                    sb.AppendLine(item.Kind == EBlueprintFunctionKind.Event ? $"event    {item.DisplayName}" : $"function {item.DisplayName}");
                return Task.FromResult(sb.ToString());
            },
        },
        new()
        {
            Name = "get_graph",
            Description = "The decompiled node graph of an event or function as text. All events of a blueprint share one big graph (the ubergraph, thousands of nodes): " +
                          "for an event this returns only the nodes reachable from the event's start (scope=event, the default), in execution order with their exec step; " +
                          "scope=all returns the whole graph, scope=around with 'node' returns the neighbourhood of one node (radius exec steps, direction down/up/both). 'depth' limits the exec steps. " +
                          "Nodes are N<i>, pins N<i>.i<n> (inputs) / N<i>.o<n> (outputs). " +
                          "Pin flags: value:<kind> = the literal can be changed with set_pin_value; arg = the input can be rewired or given a literal; " +
                          "var = the output is kept in a variable other inputs can read; entry = an event's start; const/arg/var show the bytecode token@offset behind the pin. " +
                          "Large graphs are paged; filter by node ids ('N3,N7') or a search term.",
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", FunctionArg, true),
                ("scope", Enum("Which nodes: event (reachable from the event start; default for events), all, around (needs node)", "event", "all", "around"), false),
                ("node", String("Node id for scope=around, e.g. N120"), false), ("radius", Integer("Exec steps around the node (default 3)"), false),
                ("direction", Enum("Follow execution wires down (what runs after), up (what runs before) or both (default down)", "down", "up", "both"), false),
                ("depth", Integer("At most this many exec steps from the start"), false),
                ("filter", String("Node ids ('N3,N10') or a term matched against node names, types, pin names and values"), false),
                ("skip", Integer("Nodes to skip (paging)"), false), ("take", Integer("Nodes to return (default 80)"), false)),
            Run = async (args, _) =>
            {
                var (viewer, item, graph) = await GraphOfAsync(args);
                var selection = SelectNodes(args, item, graph);
                return GraphText.Describe(viewer, graph, item.DisplayName, args.Str("filter", false), Math.Max(0, args.Int("skip", 0)), Math.Clamp(args.Int("take", 80), 1, 400), selection);
            },
        },
        new()
        {
            Name = "search_nodes",
            Description = "Finds nodes in every event and function of a blueprint by node name, pin name, pin type or pin value.",
            InputSchema = Object(("blueprint", BlueprintArg, false), ("term", String("Text to look for"), true)),
            Run = async (args, cancellation) =>
            {
                var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
                var hits = await viewer.SearchAllAsync(args.Str("term"), null, cancellation);
                if (hits.Count == 0)
                    return "Nothing found.";
                var sb = new StringBuilder();
                foreach (var hit in hits.Take(200))
                {
                    var graph = await viewer.GetGraphAsync(hit.Item);
                    sb.AppendLine($"{hit.Item.DisplayName}: {GraphText.NodeId(graph.Graph, hit.Node)} \"{hit.Node.Name}\" - {hit.Match}");
                }
                return sb.ToString();
            },
        },
        new()
        {
            Name = "get_pseudocode",
            Description = "The blueprint class as decompiled pseudo C++ (variables, functions, control flow). A statement or function the decompiler fails on is marked in place " +
                          "('DECOMPILE FAILED in <function>: <exception> @ <frame>' next to its [stmt:<offset> token:<token>]) and everything else is still returned. " +
                          "Use list=true to see every function with its size and failures, then 'function' to read one; without it the whole class is paged by characters.",
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", String("Only this function (name as in the pseudo code)"), false),
                ("list", Boolean("List the functions with character ranges and failure counts instead of the code"), false),
                ("start", Integer("First character (default 0)"), false), ("length", Integer("Characters (default 40000)"), false)),
            Run = async (args, _) =>
            {
                var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
                var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
                var text = await Task.Run(() => cue4Parse.DecompileBlueprintPseudo(viewer.Entry)) ?? throw new AgentToolException("The package has no class to decompile.");
                var (failedStatements, failedClasses) = PseudoText.FailureCounts(text);
                var failures = failedStatements + failedClasses == 0 ? "" : $"// {failedStatements} statement(s) and {failedClasses} class(es) could not be decompiled (marked below)\n";

                if (args.Bool("list", false))
                {
                    var functions = PseudoText.Functions(text);
                    return failures + $"{functions.Count} function(s), {text.Length} characters:\n" +
                           string.Join("\n", functions.Select(o => $"{o.Name}  chars {o.Start}-{o.End} ({o.Length}){(o.FailedStatements > 0 ? $"  {o.FailedStatements} FAILED statement(s)" : "")}"));
                }
                if (args.Str("function", false) is { } name)
                    return failures + (PseudoText.Extract(text, name) ?? throw new AgentToolException($"No function '{name}' in the pseudo code (use list)."));

                var start = Math.Clamp(args.Int("start", 0), 0, text.Length);
                var length = Math.Clamp(args.Int("length", 40_000), 1, 55_000);
                var page = text.Substring(start, Math.Min(length, text.Length - start));
                return (start == 0 ? failures : "") + (start + page.Length < text.Length ? page + $"\n... ({text.Length - start - page.Length} more characters: start={start + page.Length})" : page);
            },
        },

        #endregion

        #region class defaults

        new()
        {
            Name = "list_class_defaults",
            Description = "Lists the class default (CDO) properties: name, type, current value, and whether this class overrides it.",
            InputSchema = Object(("blueprint", BlueprintArg, false), ("filter", String("Only properties whose name or type contains this"), false),
                ("overridden_only", Boolean("Only properties this class sets"), false)),
            Run = async (args, _) =>
            {
                var viewer = await LoadedAsync(args);
                if (viewer.Editor.Session.Cdo == null)
                    return "The blueprint has no class default object.";
                var filter = args.Str("filter", false);
                var overriddenOnly = args.Bool("overridden_only", false);
                var sb = new StringBuilder($"{viewer.Editor.CdoTitle}\n");
                foreach (var item in viewer.Editor.CdoProperties.Where(o => (!overriddenOnly || o.IsOverridden) &&
                                                                            (string.IsNullOrEmpty(filter) || o.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                                                             o.TypeText.Contains(filter, StringComparison.OrdinalIgnoreCase))))
                    sb.AppendLine($"{item.DisplayName} : {item.TypeText} = {item.Preview}{(item.IsOverridden ? "" : "  (inherited)")}  [{(item.IsSimple ? "simple" : "json")}]");
                return sb.ToString();
            },
        },
        new()
        {
            Name = "get_class_default",
            Description = "The full value of one class default property: plain text for simple types, UAssetAPI property JSON for the others (edit that JSON and pass it to set_class_default).",
            InputSchema = Object(("blueprint", BlueprintArg, false), ("property", String("Property name as list_class_defaults shows it"), true)),
            Run = async (args, _) =>
            {
                var viewer = await LoadedAsync(args);
                var item = CdoItem(viewer, args.Str("property"));
                return item.IsOverridden ? viewer.Editor.GetEditText(item) : $"{item.DisplayName} is inherited ({item.Preview}); set_class_default overrides it.";
            },
        },
        new()
        {
            Name = "set_class_default",
            Description = "Sets a class default property (overriding it in this class if it is inherited). Simple types take text (true, 12, 1.5, a name, an enum value); others take the JSON get_class_default returns.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("property", String("Property name"), true), ("value", Any("Text for simple types, JSON (object or string) for the others"), true)),
            Run = async (args, _) =>
            {
                var viewer = await LoadedAsync(args);
                var item = CdoItem(viewer, args.Str("property"));
                if (!item.IsOverridden && viewer.Editor.AddCdoOverride(item) is { } addError)
                    throw new AgentToolException(addError);
                if (viewer.Editor.ApplyCdoValue(item, args.Str("value")) is { } error)
                    throw new AgentToolException(error);
                return $"{item.DisplayName} = {item.Preview} (pending).";
            },
        },
        new()
        {
            Name = "remove_class_default",
            Description = "Removes this class's override of a class default property, so the parent's value applies again.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("property", String("Property name"), true)),
            Run = async (args, _) =>
            {
                var viewer = await LoadedAsync(args);
                var item = CdoItem(viewer, args.Str("property"));
                if (!item.IsOverridden)
                    throw new AgentToolException($"{item.DisplayName} is not overridden by this class.");
                viewer.Editor.RemoveCdoOverride(item);
                return $"{item.DisplayName} now inherits {item.Preview} (pending).";
            },
        },

        #endregion

        #region graph edits

        new()
        {
            Name = "set_pin_value",
            Description = "Changes the literal value of an input pin (a pin with a value:<kind> flag in get_graph). Enum pins take the enumerator name.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", FunctionArg, true), ("pin", String("Pin id, e.g. N4.i2"), true), ("value", String("New value"), true)),
            Run = async (args, _) =>
            {
                var (viewer, item, graph) = await EditableGraphAsync(args);
                var pin = GraphText.FindPin(graph.Graph, args.Str("pin"));
                var text = viewer.ParsePinValue(pin, args.Str("value"));
                if (await viewer.Editor.ApplyPinEditAsync(pin, graph.FunctionName, text, item) is { } error)
                    throw new AgentToolException(error);
                return $"{GraphText.PinId(graph.Graph, pin)} = {pin.Value} (pending).";
            },
        },
        new()
        {
            Name = "connect_pins",
            Description = "Connects two pins in either order: an exec output to an exec input (execution continues at the target node), " +
                          "an event's exec output to a node (the event starts there), or a data output with the 'var' flag to a data input with the 'arg' flag (the input reads that variable). " +
                          "Only changes the compiled code can express without recompiling are accepted; the error says why otherwise.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", FunctionArg, true), ("from", String("Pin id"), true), ("to", String("Pin id"), true)),
            Run = async (args, _) =>
            {
                var (viewer, item, graph) = await EditableGraphAsync(args);
                var from = GraphText.FindPin(graph.Graph, args.Str("from"));
                var to = GraphText.FindPin(graph.Graph, args.Str("to"));
                if (await viewer.Editor.ConnectAsync(from, to, graph.FunctionName, item, graph.Code, graph.Graph.Nodes) is { } error)
                    throw new AgentToolException(error);
                return $"Connected {GraphText.PinId(graph.Graph, from)} and {GraphText.PinId(graph.Graph, to)} (pending).";
            },
        },
        new()
        {
            Name = "break_pin",
            Description = "Breaks a pin's link: an exec output ends execution after its node (an event's exec output makes the event do nothing); " +
                          "a data input gets a literal default instead (then set_pin_value can change it). An exec input or data output with a single link breaks that link.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", FunctionArg, true), ("pin", String("Pin id"), true)),
            Run = async (args, _) =>
            {
                var (viewer, item, graph) = await EditableGraphAsync(args);
                var pin = GraphText.FindPin(graph.Graph, args.Str("pin"));
                if (WirePlanner.IsExec(pin) && pin.IsInput && pin.LinkedTo.Count == 1)
                    pin = pin.LinkedTo[0];
                else if (!WirePlanner.IsExec(pin) && pin.IsOutput && pin.LinkedTo.Count == 1)
                    pin = pin.LinkedTo[0];
                if (await viewer.Editor.BreakAsync(pin, graph.FunctionName, item, graph.Code, graph.Graph.Nodes) is { } error)
                    throw new AgentToolException(error);
                return $"Broke {GraphText.PinId(graph.Graph, pin)} (pending).";
            },
        },

        #endregion

        #region pending edits

        new()
        {
            Name = "list_pending_edits",
            Description = "Lists the pending (unsaved) edits of a blueprint: class defaults count, pin values, wires and bytecode objects.",
            InputSchema = Object(("blueprint", BlueprintArg, false)),
            Run = async (args, _) =>
            {
                var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
                if (viewer.Editor.Session == null)
                    return "No edits (the blueprint was not loaded for editing yet).";
                var sb = new StringBuilder(viewer.Editor.PendingText).AppendLine();
                foreach (var entry in viewer.Editor.PinEdits.ToList())
                {
                    var graph = entry.Pin != null && entry.Source is BlueprintFunctionItem item ? (await viewer.GetGraphAsync(item)).Graph : null;
                    var pin = graph != null ? GraphText.PinId(graph, entry.Pin) + " " : "";
                    sb.AppendLine($"- {entry.Where}: {pin}{entry.Summary}");
                }
                return sb.ToString();
            },
        },
        new()
        {
            Name = "revert_edit",
            Description = "Reverts one pending edit: a pin value or wire (function + pin), a bytecode object edit (bytecode function + offset + kind) or a declared local (bytecode function + local).",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", String("Event/function (pin edits) or bytecode function (object edits, locals)"), true),
                ("pin", String("Pin id of a pin or wire edit"), false), ("offset", Integer("Offset of an object edit"), false),
                ("kind", Enum("Object edit kind", "Replace", "InsertAfter", "Remove"), false), ("local", String("Name of a declared local"), false)),
            Run = async (args, _) =>
            {
                if (args.Str("local", false) is { } local)
                {
                    var localViewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
                    var localFunction = await BytecodeFunctionNameAsync(localViewer, args.Str("function"));
                    return localViewer.Editor.RevertLocal(localFunction, local)
                        ? $"Dropped the local {local} of {localFunction}. Edits that use it now fail validation; revert them too."
                        : throw new AgentToolException($"{localFunction} has no pending local {local}.");
                }
                if (args.Str("pin", false) is { } pinId)
                {
                    var (viewer, _, graph) = await GraphOfAsync(args);
                    var pin = GraphText.FindPin(graph.Graph, pinId);
                    if (viewer.Editor.FindPinEdit(pin) == null && viewer.Editor.FindWire(pin) == null)
                        throw new AgentToolException($"{pinId} has no pending edit.");
                    viewer.Editor.RevertPin(pin);
                    return $"Reverted {pinId}.";
                }
                var objectViewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
                var function = await BytecodeFunctionNameAsync(objectViewer, args.Str("function"));
                var kind = System.Enum.TryParse<ObjectEditKind>(args.Str("kind", false) ?? "Replace", true, out var parsed) ? parsed : ObjectEditKind.Replace;
                return objectViewer.Editor.RevertObjectEdit(function, kind, args.UInt("offset"))
                    ? $"Reverted the {kind} at {function}@{args.UInt("offset")}."
                    : throw new AgentToolException("No pending object edit there (see list_pending_edits).");
            },
        },
        new()
        {
            Name = "undo",
            Description = "Undoes the last pending edit of pins, wires or bytecode objects (class default changes are not in the undo history).",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false)),
            Run = (args, _) =>
            {
                var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
                if (!viewer.Editor.CanUndo)
                    throw new AgentToolException("Nothing to undo.");
                viewer.Editor.Undo();
                return Task.FromResult("Undone. " + viewer.Editor.PendingText);
            },
        },
        new()
        {
            Name = "discard_edits",
            Description = "Drops every pending edit of the blueprint. Graphs are decompiled again: node and pin ids from earlier get_graph calls become invalid.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false)),
            Run = async (args, _) =>
            {
                var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
                await viewer.DiscardAllAsync();
                return "All pending edits were discarded.";
            },
        },
        new()
        {
            Name = "validate_edits",
            Description = "Builds the edited package in memory and checks it without writing anything: offsets and sizes are laid out again, " +
                          "CUE4Parse reads the bytecode back, and every edited function is decompiled again. Optionally returns the decompiled edited graph of one function so you can check the result. " +
                          "With nothing pending it checks the baseline instead: whether every graph of the original decompiles (run it before the first edit). " +
                          "A failure names the step (declare locals, bytecode, write, read back, sizes, decompile), the function and the top of the exception stack.",
            InputSchema = Object(("blueprint", BlueprintArg, false), ("show_function", String("Bytecode function whose edited graph to return"), false)),
            Run = async (args, _) =>
            {
                var viewer = await LoadedAsync(args);
                if (!viewer.Editor.IsDirty)
                    return await BaselineAsync(viewer);
                var result = await viewer.Editor.ValidateAsync();
                if (!result.Success)
                    throw new AgentToolException("Invalid: " + result.Message);
                var sb = new StringBuilder("Valid: the pending edits build, read back and decompile (this checks structure, not what the game does). ").AppendLine(viewer.Editor.PendingText);
                foreach (var note in viewer.Editor.Session.LastNotes)
                    sb.AppendLine("note: " + note);
                var behavior = viewer.Editor.Session.LastBehavior;
                sb.AppendLine(behavior.Count == 0
                    ? "Behavior check (edited functions against the original): the edits add no unreachable code, endless loops or flow stack problems. Use diff_edits to see exactly which statements changed."
                    : "Behavior check (edited functions against the original) found changes to look at:\n" + string.Join("\n", behavior.Select(o => "- " + o)));
                if (args.Str("show_function", false) is { } show)
                {
                    var name = await BytecodeFunctionNameAsync(viewer, show);
                    if (viewer.VerifiedGraphs.TryGetValue(name, out var graph))
                        sb.AppendLine().Append(GraphText.Describe(viewer, new BlueprintGraphResult { Graph = graph, FunctionName = name, Result = new BlueprintGraph.Decompiler.DecompilationResult() },
                            name + " (edited, read-only)", null, 0, 400));
                    else
                        sb.AppendLine($"{name} has no edits, so it was not decompiled again.");
                }
                return sb.ToString();
            },
        },
        new()
        {
            Name = "audit_flow",
            Description = "Control-flow check of one bytecode function (events: pass the event name): statements nothing reaches, loops that can never end, execution flow stack that grows or underflows, paths that never reach a return, and where the loops are. " +
                          "This is a behavior check, separate from validate_edits' format check: a function can be well-formed and still never end. Works on the original or the pending state (after validate_edits).",
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", String("Bytecode function or event name"), true),
                ("edited", Boolean("Audit the last built (edited) version instead of the original"), false)),
            Run = async (args, _) =>
            {
                var (viewer, asset, function) = await BytecodeOfAsync(args);
                if (args.Bool("edited", false))
                {
                    var built = viewer.Editor.Session.LastEdited ?? throw new AgentToolException("Nothing was built yet: run validate_edits first.");
                    asset = built;
                    function = KismetConstantPatcher.FindFunction(built, function.ObjectName.ToString()) ?? throw new AgentToolException("The function is not in the built package.");
                }
                return KismetFlowAudit.Format(KismetFlowAudit.Audit(asset, function), function.ObjectName.ToString());
            },
        },
        new()
        {
            Name = "diff_edits",
            Description = "Semantic diff of one function between the original and the last built edit (run validate_edits first): statements added, removed or changed, as cells (\"expr.args[1].value: 5 -> 7\"). " +
                          "Code offsets compare by the statement they reach, so inserting a statement doesn't make every jump look changed; references and layout noise are ignored.",
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", String("Bytecode function or event name"), true)),
            Run = async (args, _) =>
            {
                var (viewer, asset, function) = await BytecodeOfAsync(args);
                var built = viewer.Editor.Session.LastEdited ?? throw new AgentToolException("Nothing was built yet: run validate_edits first.");
                var edited = KismetConstantPatcher.FindFunction(built, function.ObjectName.ToString()) ?? throw new AgentToolException("The function is not in the built package.");
                return function.ObjectName + ": " + KismetDiff.Compare(asset, function, built, edited);
            },
        },
        new()
        {
            Name = "save_blueprint",
            Description = "Validates and writes the edited blueprint: as a package file (export_asset) and/or into the pak packer (create_pak). Writes files: only call it when the user asked to save or build. Returns the files written with their pak paths, sizes and SHA-256.",
            Access = AgentToolAccess.Write,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("export_asset", Boolean("Write the edited .uasset/.uexp"), false),
                ("create_pak", Boolean("Add the edited package to the pak packer"), false)),
            Run = async (args, _) =>
            {
                var viewer = await LoadedAsync(args);
                var export = args.Bool("export_asset", false);
                var pak = args.Bool("create_pak", true);
                if (!export && !pak)
                    throw new AgentToolException("Choose export_asset and/or create_pak.");
                if (!viewer.Editor.IsDirty)
                    throw new AgentToolException("There is nothing to save.");
                var message = $"The agent wants to save {viewer.Entry.Path}\n\n{viewer.Editor.PendingText}\n\n" +
                              (export ? "- write the edited package file\n" : "") + (pak ? "- add it to the pak packer\n" : "") + "\nAllow?";
                if (!await BlueprintAgentHost.ConfirmWrite(message))
                    throw new AgentToolException("The user declined the save.");
                var result = await viewer.Editor.SaveAsync(export, pak);
                if (!result.Success)
                    throw new AgentToolException("Save failed: " + result.Message);
                var saved = new Newtonsoft.Json.Linq.JObject { ["status"] = viewer.Editor.Status, ["editedFunctions"] = new Newtonsoft.Json.Linq.JArray(viewer.Editor.Session.EditedFunctions) };
                if (result.AssetPath != null && System.IO.File.Exists(result.AssetPath))
                    saved["files"] = AgentFiles.PackageFiles(result.AssetPath);
                if (result.PakPath != null)
                    saved["pak"] = AgentFiles.PakItems(Docking.DockPanels.ShowPackWorkspace().ViewModel, AgentFiles.SplitPaths(result.PakPath));
                return saved.ToString(Newtonsoft.Json.Formatting.Indented);
            },
        },

        #endregion

        #region bytecode objects

        new()
        {
            Name = "kismet_list_statements",
            Description = "Lists the top-level bytecode statements of a function from UAssetAPI: offset, token and a compact JSON preview. " +
                          "Offsets are those of the original code (the same as get_graph's stmt@ values). Pending object edits are marked.",
            Access = AgentToolAccess.ObjectEdit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", BytecodeFunctionArg, true),
                ("from_offset", Integer("Start at this offset (default 0)"), false), ("count", Integer("Statements (default 120)"), false)),
            Run = async (args, _) =>
            {
                var (viewer, asset, function) = await BytecodeOfAsync(args);
                var offsets = KismetObjects.Offsets(asset, function);
                var from = (uint) Math.Max(0, args.Int("from_offset", 0));
                var count = Math.Clamp(args.Int("count", 120), 1, 600);
                var pending = viewer.Editor.ObjectEntries.Where(o => o.FunctionName == function.ObjectName.ToString()).ToList();
                var sb = new StringBuilder($"{function.ObjectName}: {function.ScriptBytecode.Length} statements.{StubHint(asset, function)}\n");
                var locals = viewer.Editor.Session.Locals.Where(o => o.FunctionName == function.ObjectName.ToString()).ToList();
                if (locals.Count > 0)
                    sb.AppendLine("pending locals: " + string.Join(", ", locals.Select(o => o.ToString())));
                var shown = 0;
                foreach (var statement in function.ScriptBytecode)
                {
                    var offset = offsets[statement];
                    if (offset < from)
                        continue;
                    if (shown++ == count)
                    {
                        sb.AppendLine($"... more: from_offset={offset}");
                        break;
                    }
                    var preview = KismetObjects.ToJson(asset, statement).ToString(Formatting.None);
                    if (preview.Length > 220)
                        preview = preview[..217] + "...";
                    var marks = string.Concat(pending.Where(o => o.Edit.Offset == offset).Select(o => $"  [pending {o.Edit.Kind}]"));
                    sb.AppendLine($"@{offset} {statement.Token}  {preview}{marks}");
                }
                return sb.ToString();
            },
        },
        new()
        {
            Name = "kismet_get_expression",
            Description = "Returns the expression (statement or nested expression) at an offset as KismetObjects JSON: {\"op\": token, fields...}; every object carries \"@\": its offset. " +
                          "Modify this JSON and pass it to kismet_replace.",
            Access = AgentToolAccess.ObjectEdit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", BytecodeFunctionArg, true), ("offset", OffsetArg, true),
                ("token", String("Token, when several expressions start at the offset"), false)),
            Run = async (args, _) =>
            {
                var (_, asset, function) = await BytecodeOfAsync(args);
                var layout = KismetCodeLayout.Bind(asset, function);
                var token = args.Str("token", false);
                var expression = token != null && System.Enum.TryParse<EExprToken>(token, out var parsed)
                    ? layout.FindOriginal(args.UInt("offset"), parsed)
                    : layout.FindOriginal(args.UInt("offset"));
                if (expression == null)
                    throw new AgentToolException($"No expression starts at {args.UInt("offset")} in {function.ObjectName}." + StubHint(asset, function));
                return KismetObjects.ToJson(asset, expression, KismetObjects.Offsets(asset, function)).ToString(Formatting.Indented);
            },
        },
        new()
        {
            Name = "kismet_schema",
            Description = "The JSON fields of Kismet expression ops, generated from UAssetAPI. Without 'ops' lists every op name.",
            Access = AgentToolAccess.ObjectEdit,
            InputSchema = Object(("ops", Array("Op names, e.g. [\"EX_CallMath\", \"EX_Let\"]", String("Op")), false)),
            Run = (args, _) =>
            {
                var ops = args.Strings("ops").ToList();
                if (ops.Count == 0)
                    return Task.FromResult(string.Join(", ", KismetObjects.Tokens.Select(o => o.ToString()).OrderBy(o => o, StringComparer.Ordinal)));
                var result = new JArray();
                foreach (var op in ops)
                    result.Add(KismetObjects.TryGetType(op, out var type) ? KismetObjects.Describe(type) : new JObject { ["op"] = op, ["error"] = "unknown op" });
                return Task.FromResult(result.ToString(Formatting.Indented));
            },
        },
        new()
        {
            Name = "kismet_find_symbols",
            Description = "Finds references to use in KismetObjects JSON: imports (\"import:...\"), exports (\"export:...\") and properties (functions' locals and parameters, class variables) whose name contains the query.",
            Access = AgentToolAccess.ObjectEdit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("query", String("Part of a name, e.g. PrintString or Health"), true)),
            Run = async (args, _) =>
            {
                var viewer = await LoadedAsync(args);
                var asset = viewer.Editor.Session.Working;
                var query = args.Str("query");
                var sb = new StringBuilder();
                for (var i = 0; i < asset.Imports.Count; i++)
                {
                    if (asset.Imports[i].ObjectName.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"\"{KismetObjects.IndexSymbol(asset, UAssetAPI.UnrealTypes.FPackageIndex.FromImport(i))}\"  ({asset.Imports[i].ClassName})");
                }
                for (var i = 0; i < asset.Exports.Count; i++)
                {
                    var export = asset.Exports[i];
                    var exportSymbol = KismetObjects.IndexSymbol(asset, UAssetAPI.UnrealTypes.FPackageIndex.FromExport(i));
                    if (export.ObjectName.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"\"{exportSymbol}\"  ({export.GetExportClassType()})");
                    if (export is StructExport { LoadedProperties: { } properties })
                        foreach (var property in properties.Where(o => o.Name.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)))
                            sb.AppendLine($"{{\"path\":[\"{property.Name}\"],\"owner\":\"{exportSymbol}\"}}  ({property.SerializedType} in {export.ObjectName})");
                }
                return sb.Length == 0 ? "Nothing found. A missing import can still be written: \"import:/Script/Module.Class:Function\" adds it." : sb.ToString();
            },
        },
        new()
        {
            Name = "kismet_replace",
            Description = "Replaces the expression or statement at an offset with a KismetObjects JSON object (pending until saved). " +
                          "References are symbolic (import:/export:/property paths/names); code offsets are \"@<original statement offset>\"; size-derived offsets are computed. Validate afterwards.",
            Access = AgentToolAccess.ObjectEdit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", BytecodeFunctionArg, true), ("offset", OffsetArg, true), ("token", TokenArg, true),
                ("object", Any("The new expression: a KismetObjects JSON object (or JSON text)"), true), ("description", String("What the change does, for the user"), true)),
            Run = (args, _) => RecordObjectAsync(args, ObjectEditKind.Replace, args.Json("object")),
        },
        new()
        {
            Name = "kismet_insert_after",
            Description = "Inserts statements right after the statement at an offset (jumps that pointed at the next statement still do). " +
                          "A statement may carry \"label\": \"name\" so jumps of the same insert can target it as \"#name\".",
            Access = AgentToolAccess.ObjectEdit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", BytecodeFunctionArg, true), ("offset", OffsetArg, true), ("token", TokenArg, true),
                ("statements", Any("Array of KismetObjects statement objects (or JSON text)"), true), ("description", String("What the change does, for the user"), true)),
            Run = (args, _) => RecordObjectAsync(args, ObjectEditKind.InsertAfter, args.Json("statements")),
        },
        new()
        {
            Name = "kismet_remove_statement",
            Description = "Turns the statement at an offset into EX_Nothing (a no-op), keeping every offset that points at it valid.",
            Access = AgentToolAccess.ObjectEdit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", BytecodeFunctionArg, true), ("offset", OffsetArg, true), ("token", TokenArg, true),
                ("description", String("What the change does, for the user"), true)),
            Run = (args, _) => RecordObjectAsync(args, ObjectEditKind.Remove, null),
        },
        new()
        {
            Name = "kismet_add_local",
            Description = "Declares a new local variable of a bytecode function (for the event graph: of its persistent frame), e.g. to receive an out parameter or the result of a call. " +
                          "Give a type (int, int64, float, double, bool, byte, name, string, text, object:<class path>, class:<meta class path>, softobject:<class path>, enum:<enum path>, array:<type>) " +
                          "or 'like': an existing property of this package to copy the type from (needed for structs). Flags and size are copied from a property of the same type when the package has one. " +
                          "Then reference it as {\"path\": [name], \"owner\": \"export:<function>\"}. Experimental: the engine lays out the frame when it loads the class; test in game.",
            Access = AgentToolAccess.ObjectEdit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("function", BytecodeFunctionArg, true), ("name", String("Variable name (letters, digits, _), unique in the function"), true),
                ("type", String("Type, e.g. array:int, object:/Script/Engine.Actor, int"), false),
                ("like", Any("Property to copy the type from: {\"path\": [\"Name\"], \"owner\": \"export:...\"}"), false),
                ("description", String("What the variable is for, for the user"), true)),
            Run = async (args, _) =>
            {
                var (viewer, _, function) = await BytecodeOfAsync(args);
                var like = args["like"] is { Type: not JTokenType.Null } ? args.Json("like") : null;
                var type = args.Str("type", false);
                if ((like == null) == string.IsNullOrWhiteSpace(type))
                    throw new AgentToolException("Give either type or like.");
                var declaration = new LocalDeclaration
                {
                    FunctionName = function.ObjectName.ToString(), Name = args.Str("name").Trim(), Type = type?.Trim() ?? "", Like = like,
                    Description = args.Str("description", false) ?? "",
                };
                var source = viewer.Functions.FirstOrDefault(o => o.Function.Name == declaration.FunctionName) ??
                             (KismetCodeLayout.IsUbergraph(function) ? viewer.Functions.FirstOrDefault(o => o.Kind == EBlueprintFunctionKind.Event) : null);
                var notes = new List<string>();
                if (await viewer.Editor.DeclareLocalAsync(declaration, source, notes) is { } error)
                    throw new AgentToolException(error);
                var text = $"Declared {declaration} (pending). Reference it as {{\"path\": [\"{declaration.Name}\"], \"owner\": \"export:{declaration.FunctionName}\"}}.";
                return notes.Count == 0 ? text : text + "\nNotes: " + string.Join("; ", notes.Distinct());
            },
        },

        #endregion
    ];

    #region helpers

    private static async Task<(BlueprintViewerViewModel Viewer, BlueprintFunctionItem Item, BlueprintGraphResult Graph)> GraphOfAsync(JObject args)
    {
        var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
        var name = args.Str("function");
        var item = viewer.FindFunctionItem(name) ??
                   viewer.Functions.FirstOrDefault(o => string.Equals(o.DisplayName, name, StringComparison.OrdinalIgnoreCase)) ??
                   (name.StartsWith("ExecuteUbergraph", StringComparison.Ordinal) ? viewer.Functions.FirstOrDefault(o => o.Kind == EBlueprintFunctionKind.Event) : null) ??
                   throw new AgentToolException($"No event or function '{name}' (see list_functions).");
        var graph = await viewer.GetGraphAsync(item);
        return (viewer, item, graph);
    }

    /// <summary>The nodes get_graph shows: the event's reachable part by default, the whole graph for a function or scope=all.</summary>
    private static GraphText.Selection SelectNodes(JObject args, BlueprintFunctionItem item, BlueprintGraphResult graph)
    {
        var scope = args.Str("scope", false) ?? (item.Kind == EBlueprintFunctionKind.Event ? "event" : "all");
        var depth = args["depth"] != null ? Math.Max(0, args.Int("depth", int.MaxValue)) : int.MaxValue;
        switch (scope)
        {
            case "all":
                return null;
            case "around":
            {
                var reference = args.Str("node", false) ?? throw new AgentToolException("scope=around needs 'node' (e.g. N120).");
                if (reference.Length < 2 || reference[0] is not ('N' or 'n') || !int.TryParse(reference[1..], out var index) || index < 0 || index >= graph.Graph.Nodes.Count)
                    throw new AgentToolException($"'{reference}' is not a node of this graph.");
                var direction = args.Str("direction", false) ?? "down";
                var radius = args["radius"] != null ? Math.Max(0, args.Int("radius", 3)) : Math.Min(depth, 3);
                return GraphText.Reach(graph.Graph, graph.Graph.Nodes[index], radius, direction is "down" or "both", direction is "up" or "both", $"around {reference} ({direction}, radius {radius})");
            }
            default:
            {
                if (item.Kind != EBlueprintFunctionKind.Event)
                    return null; // a function's graph is its own
                var start = GraphText.FindEventStart(graph.Graph, item.Function.Name, item.DisplayName)
                            ?? throw new AgentToolException($"The start node of event '{item.DisplayName}' is not in the graph; use scope=all and filter.");
                return GraphText.Reach(graph.Graph, start, depth, true, args.Str("direction", false) == "both", $"reachable from the start of event {item.DisplayName} ({GraphText.NodeId(graph.Graph, start)})");
            }
        }
    }

    private static async Task<(BlueprintViewerViewModel Viewer, BlueprintFunctionItem Item, BlueprintGraphResult Graph)> EditableGraphAsync(JObject args)
    {
        var result = await GraphOfAsync(args);
        if (!result.Viewer.Editor.CanEdit)
            throw new AgentToolException("This viewer can't edit (no game provider).");
        if (!result.Graph.IsBytecodeComplete)
            throw new AgentToolException("CUE4Parse could not read this function's bytecode to the end; it can't be edited.");
        return result;
    }

    private static async Task<BlueprintViewerViewModel> LoadedAsync(JObject args)
    {
        var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
        if (!await viewer.Editor.EnsureLoadedAsync())
            throw new AgentToolException(viewer.Editor.Status);
        return viewer;
    }

    private static CdoPropertyItem CdoItem(BlueprintViewerViewModel viewer, string name)
    {
        if (viewer.Editor.Session.Cdo == null)
            throw new AgentToolException("The blueprint has no class default object.");
        return viewer.Editor.CdoProperties.FirstOrDefault(o => string.Equals(o.DisplayName, name, StringComparison.OrdinalIgnoreCase)) ??
               viewer.Editor.CdoProperties.FirstOrDefault(o => string.Equals(o.Property.Name, name, StringComparison.OrdinalIgnoreCase)) ??
               throw new AgentToolException($"No class default property '{name}' (see list_class_defaults).");
    }

    /// <summary>The UAssetAPI function a name means: a bytecode function, or the graph function of an event or function.</summary>
    private static async Task<string> BytecodeFunctionNameAsync(BlueprintViewerViewModel viewer, string name)
    {
        if (!await viewer.Editor.EnsureLoadedAsync())
            throw new AgentToolException(viewer.Editor.Status);
        // an event name means its graph (the ubergraph), like get_graph; its stub function is reached as "stub:<name>"
        if (viewer.FindFunctionItem(name) is { Kind: EBlueprintFunctionKind.Event } eventItem)
            return (await viewer.GetGraphAsync(eventItem)).FunctionName;
        if (name.StartsWith("stub:", StringComparison.Ordinal))
            name = name[5..];
        if (KismetConstantPatcher.FindFunction(viewer.Editor.Session.Working, name) != null)
            return name;
        if (viewer.FindFunctionItem(name) is { } item)
            return (await viewer.GetGraphAsync(item)).FunctionName;
        throw new AgentToolException($"No bytecode function '{name}' (see list_functions; events use the event graph).");
    }

    /// <summary>When the function is an event stub, where its code really is; otherwise empty.</summary>
    private static string StubHint(UAssetAPI.UAsset asset, StructExport function)
    {
        foreach (var ubergraph in asset.Exports.OfType<StructExport>().Where(o => o.ScriptBytecode != null && KismetCodeLayout.IsUbergraph(o)))
            foreach (var (caller, entry) in KismetCodeLayout.FindUbergraphEntryPoints(asset, ubergraph))
                if (ReferenceEquals(caller, function))
                    return $" {function.ObjectName} is the stub of an event: it only calls {ubergraph.ObjectName} with entry point @{entry.Value}. " +
                           $"The event's nodes and the stmt@ offsets get_graph shows are in {ubergraph.ObjectName}; pass the event name or that function name.";
        return "";
    }

    /// <summary>With nothing pending: does every graph of the original decompile? The baseline to compare a failed edit with.</summary>
    private static async Task<string> BaselineAsync(BlueprintViewerViewModel viewer)
    {
        var sb = new StringBuilder("Nothing is pending: the package is unchanged, so no edit was built. Baseline check of the original:\n");
        var seen = new HashSet<string>();
        int ok = 0, bad = 0;
        foreach (var item in viewer.Functions)
        {
            BlueprintGraphResult graph;
            try
            {
                graph = await viewer.GetGraphAsync(item);
            }
            catch (Exception e)
            {
                bad++;
                sb.AppendLine($"- {item.DisplayName}: decompiling threw {ExceptionText.Describe(e, 3)}");
                continue;
            }
            if (!seen.Add(graph.FunctionName))
                continue;
            var critical = graph.Result.Problems.Count(o => o.IsCritical);
            if (critical == 0 && graph.IsBytecodeComplete)
            {
                ok++;
                continue;
            }
            bad++;
            sb.AppendLine($"- {graph.FunctionName}: {(graph.IsBytecodeComplete ? "" : "bytecode incomplete; ")}{critical} critical problem(s)" +
                          (critical > 0 ? ": " + graph.Result.Problems.First(o => o.IsCritical).Message : ""));
        }
        return sb.Append($"{ok} graph(s) decompile cleanly, {bad} with problems. Edits to functions with problems can't be verified by decompiling.").ToString();
    }

    private static async Task<(BlueprintViewerViewModel Viewer, UAssetAPI.UAsset Asset, StructExport Function)> BytecodeOfAsync(JObject args)
    {
        var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
        var name = await BytecodeFunctionNameAsync(viewer, args.Str("function"));
        var asset = viewer.Editor.Session.Working;
        return (viewer, asset, KismetConstantPatcher.FindFunction(asset, name)!);
    }

    private static async Task<string> RecordObjectAsync(JObject args, ObjectEditKind kind, JToken payload)
    {
        var (viewer, _, function) = await BytecodeOfAsync(args);
        var functionName = function.ObjectName.ToString();
        var edit = new ObjectEdit
        {
            FunctionName = functionName, Kind = kind, Offset = args.UInt("offset"), Token = args.Str("token"),
            Payload = payload, Description = args.Str("description", false) ?? "",
        };

        // the function list entry the Changes list jumps to
        var source = viewer.Functions.FirstOrDefault(o => o.Function.Name == functionName) ??
                     (KismetCodeLayout.IsUbergraph(function) ? viewer.Functions.FirstOrDefault(o => o.Kind == EBlueprintFunctionKind.Event) : null);
        var notes = new List<string>();
        if (await viewer.Editor.ApplyObjectEditAsync(edit, source, notes) is { } error)
            throw new AgentToolException(error + (error.StartsWith("No ", StringComparison.Ordinal) ? StubHint(viewer.Editor.Session.Working, function) : ""));
        var text = $"Recorded {kind} at {functionName}@{edit.Offset} (pending). Run validate_edits to check it.";
        if (notes.Count > 0)
            text += "\nApplying it " + string.Join("; ", notes.Distinct()) + ".";
        // native calls against the jmap: warnings, because the jmap may not match the game's engine exactly
        var callProblems = payload == null ? [] : JmapCalls.Check(payload);
        if (callProblems.Count > 0)
            text += "\nWARNING native call check (jmap_check_call / jmap_call_template):\n" + string.Join("\n", callProblems.Select(o => "- " + o));
        return text;
    }

    #endregion
}
