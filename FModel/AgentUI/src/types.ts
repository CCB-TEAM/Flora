/** One line of the transcript (TranscriptEntry in AgentConversation.cs, camelCase). */
export interface Entry {
  id: string;
  kind: "user" | "assistant" | "tool" | "approval" | "compaction" | "info" | "error";
  text: string;
  thinking?: string;
  name?: string;
  input?: string;
  output?: string;
  /** tool: running / ok / error; approval: pending / allowed / denied; assistant, compaction: streaming / done. */
  status?: string;
  time?: string;
}

export interface SessionSummary {
  id: string;
  title: string;
  updated: string;
  model: string;
}

export interface Session {
  id: string;
  title: string;
  transcript: Entry[];
}

export interface Status {
  running: boolean;
  contextTokens: number;
  compactAt: number;
  compactions: number;
  model: string;
  title: string;
  id: string;
}

export interface Settings {
  provider: "Anthropic" | "OpenAICompatible";
  model: string;
  baseUrl: string;
  effort: string;
  hasKey: boolean;
  allowObjects: boolean;
  compactAt: number;
  mcpEnabled: boolean;
  mcpPort: number;
  mcpStatus: string;
  mcpCommand: string;
  toolCount: number;
}

/** Messages from Flora (AgentPanel.xaml.cs). */
export type HostMessage =
  | { type: "init"; lang: "en" | "zh"; settings: Settings; session: Session; status: Status; sessions: SessionSummary[] }
  | { type: "session"; session: Session; status: Status }
  | { type: "sessions"; sessions: SessionSummary[] }
  | { type: "settings"; settings: Settings }
  | { type: "status" } & Status
  | { type: "entry" } & Entry
  | { type: "patch"; id: string } & Partial<Entry>
  | { type: "delta"; id: string; field: "text" | "thinking"; text: string }
  | { type: "toast"; text: string; error?: boolean };

/** Messages to Flora. */
export type PanelMessage =
  | { type: "ready" }
  | { type: "send"; text: string }
  | { type: "stop" }
  | { type: "compact" }
  | { type: "approve"; id: string; allow: boolean }
  | { type: "newSession" }
  | { type: "openSession"; id: string }
  | { type: "deleteSession"; id: string }
  | { type: "saveSettings"; settings: Partial<Settings> }
  | { type: "setApiKey"; key: string }
  | { type: "copy"; text: string }
  | { type: "openPacker" };
