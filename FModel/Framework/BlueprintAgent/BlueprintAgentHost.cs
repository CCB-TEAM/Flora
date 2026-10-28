using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using FModel.ViewModels;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// The blueprints open in viewers, as the agent tools see them. Every tool runs on the UI thread (the view models are bound to the UI).
/// </summary>
public static class BlueprintAgentHost
{
    private static readonly List<BlueprintViewerViewModel> Viewers = [];

    /// <summary>How the built-in agent asks the user (inline in its panel) while it runs a tool. MCP calls have none and are never asked.</summary>
    public static readonly System.Threading.AsyncLocal<Func<string, Task<bool>>> ConfirmOverride = new();

    /// <summary>
    /// Whether a tool call that writes files may go ahead. The built-in agent asks the user in its panel; a call that came in through MCP
    /// (a client the user connected with the bearer token) is allowed without asking.
    /// </summary>
    public static Task<bool> ConfirmWrite(string message) => ConfirmOverride.Value?.Invoke(message) ?? Task.FromResult(true);

    public static event Action Changed;

    public static void Register(BlueprintViewerViewModel viewer)
    {
        lock (Viewers)
        {
            if (Viewers.Contains(viewer))
                return;
            // a viewer of the same asset opened again replaces the old one
            Viewers.RemoveAll(o => string.Equals(o.Entry.Path, viewer.Entry.Path, StringComparison.OrdinalIgnoreCase));
            Viewers.Add(viewer);
        }
        Changed?.Invoke();
    }

    public static void Unregister(BlueprintViewerViewModel viewer)
    {
        lock (Viewers)
            Viewers.Remove(viewer);
        Changed?.Invoke();
    }

    public static IReadOnlyList<BlueprintViewerViewModel> Open
    {
        get
        {
            lock (Viewers)
                return Viewers.ToList();
        }
    }

    /// <summary>
    /// The open blueprint named by <paramref name="blueprint"/> (package path, file name or name without extension);
    /// when it is empty and exactly one blueprint is open, that one.
    /// </summary>
    public static BlueprintViewerViewModel Resolve(string blueprint)
    {
        var open = Open;
        if (string.IsNullOrWhiteSpace(blueprint))
        {
            return open.Count switch
            {
                1 => open[0],
                0 => throw new AgentToolException("No blueprint is open. Use find_assets and open_blueprint first."),
                _ => throw new AgentToolException("Several blueprints are open; pass 'blueprint' (see list_blueprints)."),
            };
        }

        var key = blueprint.Trim().Replace('\\', '/');
        var match = open.FirstOrDefault(o => string.Equals(o.Entry.Path, key, StringComparison.OrdinalIgnoreCase)) ??
                    open.FirstOrDefault(o => o.Entry.Path.EndsWith(key, StringComparison.OrdinalIgnoreCase) ||
                                             o.Entry.Path.EndsWith(key + ".uasset", StringComparison.OrdinalIgnoreCase)) ??
                    open.FirstOrDefault(o => string.Equals(o.Entry.NameWithoutExtension, key, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new AgentToolException($"'{blueprint}' is not open. Open blueprints: {string.Join(", ", open.Select(o => o.Entry.Path))}.");
    }

    /// <summary>Runs a tool body on the UI thread.</summary>
    public static Task<T> OnUiAsync<T>(Func<Task<T>> body)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("The application is shutting down.");
        return dispatcher.CheckAccess() ? body() : dispatcher.InvokeAsync(body).Task.Unwrap();
    }
}
