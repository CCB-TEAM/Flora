using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FModel.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// A Model Context Protocol server (Streamable HTTP, JSON responses) on localhost that exposes <see cref="BlueprintAgentTools"/>
/// to external clients such as Claude Code: <c>claude mcp add --transport http flora http://localhost:27121/mcp --header "Authorization: Bearer ..."</c>.
/// Requests need the bearer token from the settings; browsers are kept out by the Origin check.
/// </summary>
public sealed class McpServer
{
    private static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18", "2025-03-26"];

    public static McpServer Instance { get; } = new();

    private HttpListener _listener;
    private CancellationTokenSource _stop;

    public bool IsRunning => _listener?.IsListening == true;
    public string Status { get; private set; } = "stopped";
    public event Action StatusChanged;

    public string Url => $"http://localhost:{UserSettings.Default.McpServerPort}/mcp";

    public static string EnsureToken()
    {
        if (string.IsNullOrEmpty(UserSettings.Default.McpServerToken))
            UserSettings.Default.McpServerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        return UserSettings.Default.McpServerToken;
    }

    /// <summary>Starts or stops the server to match the settings.</summary>
    public void ApplySettings()
    {
        Stop();
        if (UserSettings.Default.McpServerEnabled)
            Start();
    }

    private void Start()
    {
        EnsureToken();
        try
        {
            var port = UserSettings.Default.McpServerPort;
            var note = "";
            try
            {
                // clients use either name; 127.0.0.1 may need a URL reservation (netsh http add urlacl) without admin rights
                _listener = Listen($"http://localhost:{port}/", $"http://127.0.0.1:{port}/");
            }
            catch (HttpListenerException)
            {
                _listener = Listen($"http://localhost:{port}/");
                note = " (localhost only: 127.0.0.1 needs a URL reservation)";
            }
            _stop = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_listener, _stop.Token));
            SetStatus($"listening on {Url}{note}");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "MCP server failed to start");
            _listener = null;
            SetStatus($"failed to start: {e.Message}");
        }
    }

    private static HttpListener Listen(params string[] prefixes)
    {
        var listener = new HttpListener();
        foreach (var prefix in prefixes)
            listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
            return listener;
        }
        catch
        {
            listener.Close();
            throw;
        }
    }

    public void Stop()
    {
        _stop?.Cancel();
        try { _listener?.Close(); }
        catch (ObjectDisposedException) { }
        _listener = null;
        SetStatus("stopped");
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    private static async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (cancellation.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context, cancellation), cancellation);
        }
    }

    private static async Task HandleAsync(HttpListenerContext context, CancellationToken cancellation)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (request.Url?.AbsolutePath.TrimEnd('/') != "/mcp")
            {
                await WriteAsync(response, 404, null);
                return;
            }
            // DNS rebinding: a page in a browser always sends its origin
            if (request.Headers["Origin"] is { Length: > 0 } origin &&
                !(Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && originUri.IsLoopback))
            {
                await WriteAsync(response, 403, null);
                return;
            }
            if (request.Headers["Authorization"] != $"Bearer {UserSettings.Default.McpServerToken}")
            {
                await WriteAsync(response, 401, Error(null, -32001, "Missing or wrong bearer token (see Flora's agent panel)."));
                return;
            }
            if (request.HttpMethod == "DELETE")
            {
                await WriteAsync(response, 200, null);
                return;
            }
            if (request.HttpMethod != "POST")
            {
                // no server-initiated stream
                response.AddHeader("Allow", "POST, DELETE");
                await WriteAsync(response, 405, null);
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync(cancellation);

            JToken message;
            try { message = JToken.Parse(body); }
            catch (JsonException e)
            {
                await WriteAsync(response, 400, Error(null, -32700, "Parse error: " + e.Message));
                return;
            }

            if (message is JArray batch)
            {
                var replies = new JArray();
                foreach (var item in batch.OfType<JObject>())
                    if (await DispatchAsync(item, cancellation) is { } reply)
                        replies.Add(reply);
                await WriteAsync(response, replies.Count == 0 ? 202 : 200, replies.Count == 0 ? null : replies);
                return;
            }
            var single = message is JObject obj ? await DispatchAsync(obj, cancellation) : Error(null, -32600, "Invalid request");
            if (single?["result"]?["protocolVersion"] != null)
                response.AddHeader("Mcp-Session-Id", Guid.NewGuid().ToString("N"));
            await WriteAsync(response, single == null ? 202 : 200, single);
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "MCP request failed");
            try { await WriteAsync(response, 500, Error(null, -32603, e.Message)); }
            catch (Exception) { /* the client went away */ }
        }
    }

    /// <summary>Handles one JSON-RPC message; returns the response, or null for notifications.</summary>
    private static async Task<JObject> DispatchAsync(JObject message, CancellationToken cancellation)
    {
        var id = message["id"];
        var method = message.Value<string>("method");
        if (id == null || method == null)
            return null; // a notification or a response to something we never asked

        var parameters = message["params"] as JObject ?? new JObject();
        switch (method)
        {
            case "initialize":
            {
                var requested = parameters.Value<string>("protocolVersion");
                return Result(id, new JObject
                {
                    ["protocolVersion"] = SupportedVersions.Contains(requested) ? requested : SupportedVersions[0],
                    ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false }, ["resources"] = new JObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JObject { ["name"] = "flora", ["title"] = "Flora blueprint tools", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString() },
                    ["instructions"] = "Edits Unreal Engine blueprints open in Flora. Before the first edit call read_guide (topic skill; kismet-objects before any kismet_* edit); " +
                                       "the same documents are resources flora://guide/<topic>. Workflow: find_assets -> open_blueprint -> list_functions -> get_graph -> " +
                                       "validate_edits on the unchanged blueprint (baseline) -> one edit -> validate_edits -> next edit. Edits stay pending in Flora's viewer until save_blueprint, " +
                                       "and writes the package or the pak without asking. Key rules: prefer graph edits over kismet_*; every property a bytecode object references must be declared " +
                                       "(kismet_add_local for new locals, out parameters need real storage, never EX_Nothing); event names select the event graph (ExecuteUbergraph_*); " +
                                       "check native signatures with jmap_function (jmap_call_template writes a fill-in call); DataTables are edited with open_datatable/dt_* tools, not the blueprint tools; validate_edits reports format and behavior (audit_flow, diff_edits); 'validated' means it builds and decompiles, not that it works in game; ask the user when a request has two readings.",
                });
            }
            case "ping":
                return Result(id, new JObject());
            case "resources/list":
                return Result(id, new JObject
                {
                    ["resources"] = new JArray(AgentGuide.Topics.Select(o => new JObject
                    {
                        ["uri"] = $"flora://guide/{o}",
                        ["name"] = o == "skill" ? "flora-blueprint-bytecode" : o,
                        ["title"] = o == "skill" ? "Flora blueprint editing guide" : $"Flora guide: {o}",
                        ["mimeType"] = "text/markdown",
                    })),
                });
            case "resources/templates/list":
                return Result(id, new JObject { ["resourceTemplates"] = new JArray() });
            case "resources/read":
            {
                var uri = parameters.Value<string>("uri") ?? "";
                var topic = uri.StartsWith("flora://guide/", StringComparison.Ordinal) ? uri["flora://guide/".Length..] : "";
                var text = AgentGuide.Read(topic);
                return text.Length == 0
                    ? Error(id, -32002, $"Resource not found: {uri}")
                    : Result(id, new JObject { ["contents"] = new JArray(new JObject { ["uri"] = uri, ["mimeType"] = "text/markdown", ["text"] = text }) });
            }
            case "tools/list":
                return Result(id, new JObject
                {
                    ["tools"] = new JArray(BlueprintAgentTools.Available().Select(o => new JObject
                    {
                        ["name"] = o.Name,
                        ["description"] = o.Description,
                        ["inputSchema"] = o.InputSchema,
                        ["annotations"] = new JObject
                        {
                            ["readOnlyHint"] = o.Access == AgentToolAccess.Read,
                            ["destructiveHint"] = o.Access == AgentToolAccess.Write,
                            ["openWorldHint"] = false,
                        },
                    })),
                });
            case "tools/call":
            {
                var name = parameters.Value<string>("name");
                var arguments = parameters["arguments"] as JObject ?? new JObject();
                var (text, isError) = await BlueprintAgentTools.RunAsync(name, arguments, cancellation);
                return Result(id, new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                });
            }
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static JObject Result(JToken id, JObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JObject Error(JToken id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } };

    private static async Task WriteAsync(HttpListenerResponse response, int status, JToken body)
    {
        response.StatusCode = status;
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        response.Close();
    }
}
