import { memo, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useApp } from "../App";
import { send } from "../bridge";
import { group } from "../state";
import type { Entry } from "../types";
import { Markdown } from "./Markdown";
import { CheckIcon, ChevronIcon, CopyIcon, CrossIcon, LayersIcon, ShieldIcon, SparkIcon, WrenchIcon } from "./Icons";

export function Transcript() {
  const { state } = useApp();
  const { transcript } = state.session;
  const scroller = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);

  // follow the stream while the view is at the bottom; leave it alone once the user scrolls up
  const onScroll = () => {
    const el = scroller.current!;
    pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
  };
  useLayoutEffect(() => {
    if (pinned.current && scroller.current) scroller.current.scrollTop = scroller.current.scrollHeight;
  });
  useEffect(() => {
    pinned.current = true;
  }, [state.session.id]);

  const blocks = group(transcript);
  const last = transcript[transcript.length - 1];
  const waiting = state.status.running && (!last || last.kind === "user" || (last.kind === "tool" && last.status !== "running"));

  return (
    <div className="transcript" ref={scroller} onScroll={onScroll}>
      <div className="transcript-inner">
        {transcript.length === 0 && <Welcome />}
        {blocks.map((block) =>
          block.kind === "tools" ? <ToolGroup key={block.entries[0].id} entries={block.entries} /> : <EntryView key={block.entry.id} entry={block.entry} />,
        )}
        {waiting && <Working />}
      </div>
    </div>
  );
}

function Welcome() {
  const { t, state } = useApp();
  return (
    <div className="welcome">
      <div className="welcome-mark">
        <SparkIcon size={22} />
      </div>
      <h1>{t.welcomeTitle}</h1>
      <p>{t.welcomeText}</p>
      {state.settings && <p className="muted small">{t.toolCount(state.settings.toolCount)}</p>}
      <div className="suggestions">
        {t.suggestions.map((suggestion) => (
          <button key={suggestion} className="suggestion" onClick={() => window.dispatchEvent(new CustomEvent("flora:suggest", { detail: suggestion }))}>
            {suggestion}
          </button>
        ))}
      </div>
    </div>
  );
}

function Working() {
  const { t } = useApp();
  return (
    <div className="working">
      <span className="dots">
        <i />
        <i />
        <i />
      </span>
      {t.working}
    </div>
  );
}

const EntryView = memo(function EntryView({ entry }: { entry: Entry }) {
  switch (entry.kind) {
    case "user":
      return (
        <div className="row user">
          <div className="bubble">{entry.text}</div>
        </div>
      );
    case "assistant":
      return <Assistant entry={entry} />;
    case "approval":
      return <Approval entry={entry} />;
    case "compaction":
      return <Compaction entry={entry} />;
    case "error":
      return <div className="notice error">{entry.text}</div>;
    default:
      return <div className="notice info">{entry.text}</div>;
  }
});

function Assistant({ entry }: { entry: Entry }) {
  const { t } = useApp();
  const streaming = entry.status === "streaming";
  const hasText = entry.text.trim().length > 0;
  if (!hasText && !entry.thinking && !streaming) return null;
  return (
    <div className="row assistant">
      {entry.thinking && <Thinking text={entry.thinking} live={streaming && !hasText} />}
      {hasText && <Markdown text={entry.text} />}
      {streaming && !hasText && !entry.thinking && <div className="muted small">{t.thinking}…</div>}
      {streaming && hasText && <span className="caret" />}
      {!streaming && hasText && <CopyButton text={entry.text} />}
    </div>
  );
}

function Thinking({ text, live }: { text: string; live: boolean }) {
  const { t } = useApp();
  const [open, setOpen] = useState(false);
  const expanded = open || live;
  return (
    <div className={`thinking ${expanded ? "open" : ""}`}>
      <button className="thinking-head" onClick={() => setOpen(!open)}>
        <ChevronIcon size={12} />
        {live ? `${t.thinking}…` : t.thought}
      </button>
      {expanded && <div className="thinking-body">{text}</div>}
    </div>
  );
}

function CopyButton({ text }: { text: string }) {
  const { t } = useApp();
  const [copied, setCopied] = useState(false);
  return (
    <button
      className="copy-button"
      title={t.copy}
      onClick={() => {
        send({ type: "copy", text });
        setCopied(true);
        setTimeout(() => setCopied(false), 1200);
      }}
    >
      {copied ? <CheckIcon size={13} /> : <CopyIcon size={13} />}
      {copied ? t.copied : t.copy}
    </button>
  );
}

/** A run of tool calls, collapsed to one line once it is done. */
function ToolGroup({ entries }: { entries: Entry[] }) {
  const { t } = useApp();
  const running = entries.some((o) => o.status === "running");
  const failed = entries.filter((o) => o.status === "error").length;
  const [open, setOpen] = useState<boolean | null>(null);
  const expanded = open ?? (running || entries.length <= 2);
  return (
    <div className="tool-group">
      <button className="tool-group-head" onClick={() => setOpen(!expanded)}>
        <span className={`chevron ${expanded ? "down" : ""}`}>
          <ChevronIcon size={12} />
        </span>
        <WrenchIcon size={13} />
        <span>{t.toolCalls(entries.length)}</span>
        {!expanded && <span className="tool-names">{entries.map((o) => o.name).join(" · ")}</span>}
        {running && <span className="spinner" />}
        {failed > 0 && <span className="pill error">{failed} ✕</span>}
      </button>
      {expanded && (
        <div className="tool-list">
          {entries.map((entry) => (
            <ToolCard key={entry.id} entry={entry} />
          ))}
        </div>
      )}
    </div>
  );
}

const ToolCard = memo(function ToolCard({ entry }: { entry: Entry }) {
  const { t } = useApp();
  const [open, setOpen] = useState(false);
  return (
    <div className={`tool ${entry.status}`}>
      <button className="tool-head" onClick={() => setOpen(!open)}>
        <span className="tool-status">
          {entry.status === "running" ? <span className="spinner" /> : entry.status === "error" ? <CrossIcon size={12} /> : <CheckIcon size={12} />}
        </span>
        <span className="tool-name">{entry.name}</span>
        <span className="tool-args">{summarize(entry.input)}</span>
      </button>
      {open && (
        <div className="tool-body">
          <div className="tool-label">{t.input}</div>
          <pre>{entry.input}</pre>
          {entry.output !== undefined && (
            <>
              <div className="tool-label">{t.output}</div>
              <pre className={entry.status === "error" ? "error" : ""}>{entry.output}</pre>
            </>
          )}
        </div>
      )}
    </div>
  );
});

/** The arguments that say what a call is about, in one line. */
function summarize(input?: string): string {
  if (!input) return "";
  try {
    const args = JSON.parse(input) as Record<string, unknown>;
    const keys = ["function", "pin", "from", "to", "property", "path", "query", "term", "class", "offset", "blueprint"];
    const parts = keys.filter((k) => args[k] !== undefined && args[k] !== "").map((k) => `${k}=${String(args[k])}`);
    const text = (parts.length ? parts : Object.entries(args).map(([k, v]) => `${k}=${typeof v === "object" ? "…" : String(v)}`)).join("  ");
    return text.length > 120 ? text.slice(0, 117) + "…" : text;
  } catch {
    return "";
  }
}

function Approval({ entry }: { entry: Entry }) {
  const { t } = useApp();
  const pending = entry.status === "pending";
  return (
    <div className={`approval ${entry.status}`}>
      <div className="approval-head">
        <ShieldIcon size={15} />
        <span>{t.approvalTitle}</span>
        <span className="tool-name">{entry.name}</span>
      </div>
      <div className="approval-text">{entry.text}</div>
      {pending ? (
        <div className="approval-actions">
          <button className="primary" onClick={() => send({ type: "approve", id: entry.id, allow: true })}>
            {t.allow}
          </button>
          <button onClick={() => send({ type: "approve", id: entry.id, allow: false })}>{t.deny}</button>
        </div>
      ) : (
        <div className={`approval-result ${entry.status}`}>{entry.status === "allowed" ? t.allowed : t.denied}</div>
      )}
    </div>
  );
}

function Compaction({ entry }: { entry: Entry }) {
  const { t } = useApp();
  const [open, setOpen] = useState(false);
  const streaming = entry.status === "streaming";
  return (
    <div className="compaction">
      <div className="compaction-line">
        <span />
        <button onClick={() => setOpen(!open)}>
          <LayersIcon size={13} />
          {streaming ? t.compacting : t.compacted}
          {entry.output && <span className="muted"> · {entry.output}</span>}
        </button>
        <span />
      </div>
      {(open || streaming) && entry.text && (
        <div className="compaction-summary">
          <div className="tool-label">{t.summary}</div>
          <Markdown text={entry.text} />
        </div>
      )}
    </div>
  );
}
