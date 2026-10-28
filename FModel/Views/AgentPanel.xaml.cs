using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FModel.Framework.BlueprintAgent;
using FModel.Framework.Docking;
using FModel.Localization;
using FModel.Settings;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace FModel.Views;

/// <summary>
/// The built-in agent. The interface is a web app (FModel/AgentUI, built with Vite) shown in WebView2; this class is its host:
/// it runs the agent session and passes messages both ways (see AgentUI/src/bridge.ts for the protocol).
/// </summary>
public partial class AgentPanel : IDockPanel
{
    private const string Host = "flora-agent.local";
    private static readonly string Origin = $"https://{Host}";

    private AgentSession _session = new();
    private CancellationTokenSource _cancellation;
    private bool _initialized;

    public AgentPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        McpServer.Instance.StatusChanged += OnMcpStatusChanged;
        Attach(_session);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // a dock panel is loaded again every time it's moved
        if (_initialized)
            return;
        _initialized = true;

        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "WebView2 is not available");
            ShowFallback("The agent panel needs the Microsoft Edge WebView2 runtime: https://developer.microsoft.com/microsoft-edge/webview2/\n\n" + ex.Message);
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached || Environment.GetEnvironmentVariable("FLORA_AGENT_UI_DEVTOOLS") == "1";
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = core.Settings.AreDevToolsEnabled;
        core.WebMessageReceived += OnWebMessage;
        // links in replies open in the browser, never inside the panel
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternal(args.Uri);
        };
        core.NavigationStarting += (_, args) =>
        {
            if (!IsOwnPage(args.Uri))
            {
                args.Cancel = true;
                OpenExternal(args.Uri);
            }
        };

        // a Vite dev server (npm run dev) can replace the built files while working on the interface
        var devServer = Environment.GetEnvironmentVariable("FLORA_AGENT_UI_URL");
        if (!string.IsNullOrWhiteSpace(devServer))
        {
            core.Navigate(devServer);
            return;
        }

        var folder = Path.Combine(AppContext.BaseDirectory, "AgentUI");
        if (!File.Exists(Path.Combine(folder, "index.html")))
        {
            ShowFallback($"The agent interface was not built ({folder} is missing). Build FModel/AgentUI with npm run build.");
            return;
        }
        core.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate($"{Origin}/index.html");
    }

    private bool IsOwnPage(string uri) =>
        uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
        Environment.GetEnvironmentVariable("FLORA_AGENT_UI_URL") is { Length: > 0 } dev && uri.StartsWith(dev, StringComparison.OrdinalIgnoreCase);

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
    }

    private void ShowFallback(string text)
    {
        Web.Visibility = Visibility.Collapsed;
        Fallback.Text = text;
        Fallback.Visibility = Visibility.Visible;
    }

    #region session

    private void Attach(AgentSession session)
    {
        session.Changed += (kind, payload) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(session, _session))
                return;
            payload = (JObject) payload.DeepClone();
            payload["type"] = kind;
            Post(payload);
            if (kind == "status" && !session.IsRunning)
                PostSessions();
        });
    }

    private void Switch(AgentSession session)
    {
        _session = session;
        Attach(session);
        Post(new JObject { ["type"] = "session", ["session"] = SessionJson(session), ["status"] = session.Status() });
        PostSessions();
    }

    private static JObject SessionJson(AgentSession session) => new()
    {
        ["id"] = session.Conversation.Id,
        ["title"] = session.Conversation.Title,
        ["transcript"] = JArray.FromObject(session.Conversation.Transcript, AgentJson.Serializer),
    };

    #endregion

    #region messages

    private void Post(JObject message)
    {
        try
        {
            Web.CoreWebView2?.PostWebMessageAsJson(message.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not post to the agent panel");
        }
    }

    private void PostSessions() =>
        Post(new JObject { ["type"] = "sessions", ["sessions"] = JArray.FromObject(AgentStore.List(), AgentJson.Serializer) });

    private void PostSettings() => Post(new JObject { ["type"] = "settings", ["settings"] = SettingsJson() });

    private void OnMcpStatusChanged() => Dispatcher.BeginInvoke(PostSettings);

    private static JObject SettingsJson()
    {
        var settings = UserSettings.Default;
        return new JObject
        {
            ["provider"] = settings.AgentProvider.ToString(),
            ["model"] = settings.AgentModel,
            ["baseUrl"] = settings.AgentBaseUrl,
            ["effort"] = settings.AgentEffort,
            ["hasKey"] = !string.IsNullOrEmpty(settings.AgentApiKeyProtected),
            ["allowObjects"] = settings.AgentAllowBytecodeObjects,
            ["compactAt"] = settings.AgentCompactAtTokens,
            ["mcpEnabled"] = settings.McpServerEnabled,
            ["mcpPort"] = settings.McpServerPort,
            ["mcpStatus"] = McpServer.Instance.Status,
            ["mcpCommand"] = $"claude mcp add --transport http flora {McpServer.Instance.Url} --header \"Authorization: Bearer {McpServer.EnsureToken()}\"",
            ["toolCount"] = BlueprintAgentTools.Available().Count(),
        };
    }

    private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsOwnPage(e.Source))
            return;
        JObject message;
        try
        {
            message = JObject.Parse(e.WebMessageAsJson);
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            await HandleAsync(message);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Agent panel message {Type} failed", message.Value<string>("type"));
            Post(new JObject { ["type"] = "toast", ["text"] = ex.Message, ["error"] = true });
        }
    }

    private async Task HandleAsync(JObject message)
    {
        switch (message.Value<string>("type"))
        {
            case "ready":
                Post(new JObject
                {
                    ["type"] = "init",
                    ["lang"] = LocalizationManager.Instance.CurrentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en",
                    ["settings"] = SettingsJson(),
                    ["session"] = SessionJson(_session),
                    ["status"] = _session.Status(),
                    ["sessions"] = JArray.FromObject(AgentStore.List(), AgentJson.Serializer),
                });
                break;

            case "send":
                if (_session.IsRunning || message.Value<string>("text") is not { Length: > 0 } text)
                    break;
                _cancellation = new CancellationTokenSource();
                await _session.SendAsync(text.Trim(), _cancellation.Token);
                break;

            case "compact":
                if (_session.IsRunning)
                    break;
                _cancellation = new CancellationTokenSource();
                await _session.CompactNowAsync(_cancellation.Token);
                break;

            case "stop":
                _cancellation?.Cancel();
                break;

            case "approve":
                _session.ResolveApproval(message.Value<string>("id"), message.Value<bool>("allow"));
                break;

            case "newSession":
                if (!_session.IsRunning)
                    Switch(new AgentSession());
                break;

            case "openSession":
                if (!_session.IsRunning && AgentStore.Load(message.Value<string>("id")) is { } conversation)
                    Switch(new AgentSession(conversation));
                break;

            case "deleteSession":
            {
                var id = message.Value<string>("id");
                AgentStore.Delete(id);
                if (_session.Conversation.Id == id && !_session.IsRunning)
                    Switch(new AgentSession());
                else
                    PostSessions();
                break;
            }

            case "saveSettings":
                ApplySettings(message["settings"] as JObject ?? new JObject());
                PostSettings();
                break;

            case "setApiKey":
                UserSettings.Default.AgentApiKey = message.Value<string>("key") ?? "";
                UserSettings.Save();
                PostSettings();
                break;

            case "copy":
                Clipboard.SetText(message.Value<string>("text") ?? "");
                break;

            case "openPacker":
                DockPanels.ShowPackWorkspace();
                break;
        }
    }

    private static void ApplySettings(JObject values)
    {
        var settings = UserSettings.Default;
        if (values.Value<string>("provider") is { } provider && Enum.TryParse<EAgentProvider>(provider, out var parsed))
            settings.AgentProvider = parsed;
        if (values.Value<string>("model") is { } model)
            settings.AgentModel = model.Trim();
        if (values.Value<string>("baseUrl") is { } baseUrl)
            settings.AgentBaseUrl = baseUrl.Trim();
        if (values.Value<string>("effort") is { } effort)
            settings.AgentEffort = effort;
        if (values["allowObjects"] != null)
            settings.AgentAllowBytecodeObjects = values.Value<bool>("allowObjects");
        if (values["compactAt"] != null)
            settings.AgentCompactAtTokens = values.Value<int>("compactAt");

        var mcpChanged = false;
        if (values["mcpEnabled"] != null && values.Value<bool>("mcpEnabled") != settings.McpServerEnabled)
        {
            settings.McpServerEnabled = values.Value<bool>("mcpEnabled");
            mcpChanged = true;
        }
        if (values["mcpPort"] != null && values.Value<int>("mcpPort") is var port and > 0 and < 65536 && port != settings.McpServerPort)
        {
            settings.McpServerPort = port;
            mcpChanged = true;
        }
        if (mcpChanged)
            McpServer.Instance.ApplySettings();
        UserSettings.Save();
    }

    #endregion

    public void OnPanelClosed()
    {
        _cancellation?.Cancel();
        McpServer.Instance.StatusChanged -= OnMcpStatusChanged;
        AgentStore.Save(_session.Conversation);
    }
}
