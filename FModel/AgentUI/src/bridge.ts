import type { HostMessage, PanelMessage } from "./types";

/**
 * The channel to Flora: WebView2's postMessage. Outside Flora (npm run dev in a browser) a small demo host answers instead,
 * so the interface can be worked on without the app.
 */
interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
}

const webview: WebView | undefined = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview;

type Listener = (message: HostMessage) => void;
const listeners = new Set<Listener>();

export function onHostMessage(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function dispatch(message: HostMessage) {
  for (const listener of listeners) listener(message);
}

if (webview) webview.addEventListener("message", (event) => dispatch(event.data as HostMessage));

export function send(message: PanelMessage) {
  if (webview) webview.postMessage(message);
  else demoHost(message);
}

export const isDemo = !webview;

// ---- demo host (browser only) ----

function demoHost(message: PanelMessage) {
  const later = (m: HostMessage, ms = 0) => setTimeout(() => dispatch(m), ms);
  const status = { running: false, contextTokens: 18400, compactAt: 100000, compactions: 0, model: "claude-opus-5-5", title: "Demo", id: "demo" };
  switch (message.type) {
    case "ready":
      later({
        type: "init",
        lang: navigator.language.startsWith("zh") ? "zh" : "en",
        settings: {
          provider: "Anthropic", model: "claude-opus-5-5", baseUrl: "", effort: "high", hasKey: true, allowObjects: false, compactAt: 100000,
          mcpEnabled: false, mcpPort: 27121, mcpStatus: "stopped", mcpCommand: "claude mcp add --transport http flora http://localhost:27121/mcp", toolCount: 46,
        },
        session: { id: "demo", title: "Demo", transcript: [] },
        status,
        sessions: [{ id: "demo", title: "Demo conversation", updated: new Date().toISOString(), model: "claude-opus-5-5" }],
      });
      break;
    case "send": {
      const id = Math.random().toString(36).slice(2);
      later({ type: "entry", id: "u" + id, kind: "user", text: message.text });
      later({ type: "status", ...status, running: true });
      later({ type: "entry", id: "a" + id, kind: "assistant", text: "", status: "streaming" }, 100);
      const reply = "I'll look at the graph first.\n\n```json\n{ \"op\": \"EX_Let\" }\n```";
      [...reply].forEach((c, i) => later({ type: "delta", id: "a" + id, field: "text", text: c }, 150 + i * 15));
      later({ type: "patch", id: "a" + id, status: "done" }, 200 + reply.length * 15);
      later({ type: "entry", id: "t" + id, kind: "tool", text: "", name: "get_graph", input: '{\n  "function": "ReceiveBeginPlay"\n}', status: "running" }, 250 + reply.length * 15);
      later({ type: "patch", id: "t" + id, status: "ok", output: "graph of ReceiveBeginPlay: 12 nodes\nN0 K2Node_Event ..." }, 900 + reply.length * 15);
      later({ type: "entry", id: "p" + id, kind: "approval", text: "The agent wants to build the pak\n\nD:\\Mods\\Demo_P.pak\n\nAllow?", name: "pack_build", status: "pending" }, 1000 + reply.length * 15);
      later({ type: "status", ...status, contextTokens: 64000, running: false }, 1100 + reply.length * 15);
      break;
    }
    case "approve":
      later({ type: "patch", id: message.id, status: message.allow ? "allowed" : "denied" });
      break;
    case "newSession":
      later({ type: "session", session: { id: "new", title: "", transcript: [] }, status: { ...status, contextTokens: 0 } });
      break;
    default:
      break;
  }
}
