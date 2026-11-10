using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FModel.Services;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>Tools added for working on a game whose paks change while Flora is open.</summary>
public static class ClaudeAgentTools
{
    public static IEnumerable<AgentTool> Build() =>
    [
        new()
        {
            Name = "reload_game",
            Description = "Mounts the game's pak/utoc files again from disk. Use it after the game's pak was rebuilt or replaced while Flora was open (reads then fail with 'Invalid uasset magic'); " +
                          "find_assets, open_blueprint and open_datatable already do it by themselves when they notice the files changed. " +
                          "Closes every open blueprint and DataTable (their data is stale); blueprints or tables with pending edits stop the reload unless discard=true, which drops those edits. Writes nothing.",
            InputSchema = Object(("discard", Boolean("Also close blueprints and tables that have pending edits, dropping the edits"), false)),
            Run = async (args, _) =>
            {
                var reason = GameReload.StaleReason();
                var result = await GameReload.ReloadAsync(args.Bool("discard", false));
                return (reason == null ? "The files had not changed on disk; reloaded anyway. " : $"Changed on disk: {reason}. ") + result;
            },
        },
        new()
        {
            Name = "search_pseudocode",
            Description = "Searches the decompiled pseudo C++ of a blueprint for a text (a variable, function, string or constant) and returns each matching line with a few lines around it, tagged with the function it is in. " +
                          "Much smaller than reading the whole class with get_pseudocode.",
            InputSchema = Object(("blueprint", String("Open blueprint: package path or name (see list_blueprints). Optional when only one is open."), false),
                ("term", String("Text to find (case-insensitive), or a regular expression when regex=true"), true),
                ("context", Integer("Lines before and after each match (default 3, at most 20)"), false),
                ("max_matches", Integer("Most matching lines to show (default 30, at most 200)"), false),
                ("regex", Boolean("Treat term as a regular expression"), false)),
            Run = async (args, _) =>
            {
                var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
                var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
                var text = await Task.Run(() => cue4Parse.DecompileBlueprintPseudo(viewer.Entry)) ?? throw new AgentToolException("The package has no class to decompile.");
                var term = args.Str("term");
                string found;
                try
                {
                    found = PseudoText.Search(text, term, Math.Clamp(args.Int("context", 3), 0, 20), Math.Clamp(args.Int("max_matches", 30), 1, 200), args.Bool("regex", false));
                }
                catch (ArgumentException e)
                {
                    throw new AgentToolException("Invalid regular expression: " + e.Message);
                }
                return found ?? $"'{term}' does not appear in the pseudo code of {viewer.Entry.Path}.";
            },
        },
    ];
}
