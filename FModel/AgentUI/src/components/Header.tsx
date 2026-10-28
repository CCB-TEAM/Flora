import { useApp } from "../App";
import { send } from "../bridge";
import { formatTokens } from "../state";
import { GearIcon, MenuIcon, PlusIcon } from "./Icons";

export function Header() {
  const { state, dispatch, t } = useApp();
  const { status, session } = state;
  const max = status.compactAt;
  const ratio = max > 0 ? Math.min(1, status.contextTokens / max) : 0;
  const level = ratio > 0.85 ? "high" : ratio > 0.6 ? "mid" : "low";

  return (
    <header className="header">
      <button className="icon-button" title={t.sessions} onClick={() => dispatch({ type: "sidebar", open: !state.sidebar })}>
        <MenuIcon />
      </button>
      <div className="header-title">
        <span className="title-text">{state.view === "settings" ? t.settings : session.title || t.untitled}</span>
        {status.model && state.view === "chat" && <span className="badge">{status.model}</span>}
      </div>
      {state.view === "chat" && (
        <button
          className={`context-meter ${level}`}
          disabled={status.running || session.transcript.length === 0}
          title={max > 0 ? t.contextTip(formatTokens(status.contextTokens), formatTokens(max)) : t.contextOff}
          onClick={() => send({ type: "compact" })}
        >
          <span className="meter">
            <span className="meter-fill" style={{ width: `${Math.round(ratio * 100)}%` }} />
          </span>
          <span className="meter-text">{formatTokens(status.contextTokens)}</span>
        </button>
      )}
      <button className="icon-button" title={t.newChat} disabled={status.running} onClick={() => send({ type: "newSession" })}>
        <PlusIcon />
      </button>
      <button
        className={`icon-button ${state.view === "settings" ? "active" : ""}`}
        title={t.settings}
        onClick={() => dispatch({ type: "view", view: state.view === "settings" ? "chat" : "settings" })}
      >
        <GearIcon />
      </button>
    </header>
  );
}
