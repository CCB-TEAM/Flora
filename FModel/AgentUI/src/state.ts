import type { Entry, HostMessage, Session, SessionSummary, Settings, Status } from "./types";

export interface Toast {
  id: number;
  text: string;
  error?: boolean;
}

export interface State {
  ready: boolean;
  lang: "en" | "zh";
  settings: Settings | null;
  sessions: SessionSummary[];
  session: Session;
  status: Status;
  view: "chat" | "settings";
  sidebar: boolean;
  toasts: Toast[];
}

export const initialState: State = {
  ready: false,
  lang: navigator.language.startsWith("zh") ? "zh" : "en",
  settings: null,
  sessions: [],
  session: { id: "", title: "", transcript: [] },
  status: { running: false, contextTokens: 0, compactAt: 0, compactions: 0, model: "", title: "", id: "" },
  view: "chat",
  sidebar: false,
  toasts: [],
};

export type Action =
  | { type: "host"; message: HostMessage }
  | { type: "view"; view: State["view"] }
  | { type: "sidebar"; open: boolean }
  | { type: "toast"; text: string; error?: boolean }
  | { type: "dismiss"; id: number };

let toastId = 0;

function updateEntry(transcript: Entry[], id: string, update: (entry: Entry) => Entry): Entry[] {
  const index = transcript.findIndex((o) => o.id === id);
  if (index < 0) return transcript;
  const next = transcript.slice();
  next[index] = update(transcript[index]);
  return next;
}

function withoutType<T extends { type: string }>(message: T): Omit<T, "type"> {
  const { type: _type, ...rest } = message;
  return rest;
}

export function reducer(state: State, action: Action): State {
  switch (action.type) {
    case "view":
      return { ...state, view: action.view, sidebar: false };
    case "sidebar":
      return { ...state, sidebar: action.open };
    case "toast":
      return { ...state, toasts: [...state.toasts, { id: ++toastId, text: action.text, error: action.error }] };
    case "dismiss":
      return { ...state, toasts: state.toasts.filter((o) => o.id !== action.id) };
    case "host":
      return host(state, action.message);
  }
}

function host(state: State, message: HostMessage): State {
  switch (message.type) {
    case "init":
      return { ...state, ready: true, lang: message.lang, settings: message.settings, session: message.session, status: message.status, sessions: message.sessions };
    case "session":
      return { ...state, session: message.session, status: message.status, view: "chat", sidebar: false };
    case "sessions":
      return { ...state, sessions: message.sessions };
    case "settings":
      return { ...state, settings: message.settings };
    case "status": {
      const status = withoutType(message) as Status;
      return { ...state, status, session: { ...state.session, title: status.title || state.session.title } };
    }
    case "entry": {
      const entry = withoutType(message) as Entry;
      return { ...state, session: { ...state.session, transcript: [...state.session.transcript, entry] } };
    }
    case "patch": {
      const { type: _type, id, ...fields } = message;
      return { ...state, session: { ...state.session, transcript: updateEntry(state.session.transcript, id, (o) => ({ ...o, ...fields })) } };
    }
    case "delta":
      return {
        ...state,
        session: {
          ...state.session,
          transcript: updateEntry(state.session.transcript, message.id, (o) => ({ ...o, [message.field]: (o[message.field] ?? "") + message.text })),
        },
      };
    case "toast":
      return { ...state, toasts: [...state.toasts, { id: ++toastId, text: message.text, error: message.error }] };
  }
}

/** Consecutive tool calls are shown as one group. */
export type Block = { kind: "entry"; entry: Entry } | { kind: "tools"; entries: Entry[] };

export function group(transcript: Entry[]): Block[] {
  const blocks: Block[] = [];
  for (const entry of transcript) {
    const last = blocks[blocks.length - 1];
    if (entry.kind === "tool") {
      if (last?.kind === "tools") last.entries.push(entry);
      else blocks.push({ kind: "tools", entries: [entry] });
    } else {
      blocks.push({ kind: "entry", entry });
    }
  }
  return blocks;
}

export function formatTokens(tokens: number): string {
  if (tokens >= 1000_000) return (tokens / 1000_000).toFixed(1) + "M";
  if (tokens >= 1000) return (tokens / 1000).toFixed(tokens >= 100_000 ? 0 : 1) + "k";
  return String(tokens);
}
