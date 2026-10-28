import { useApp } from "../App";
import { send } from "../bridge";
import { PlusIcon, TrashIcon } from "./Icons";

function relativeTime(iso: string, lang: "en" | "zh") {
  const date = new Date(iso);
  const minutes = Math.round((Date.now() - date.getTime()) / 60000);
  const format = new Intl.RelativeTimeFormat(lang === "zh" ? "zh-CN" : "en", { numeric: "auto" });
  if (minutes < 60) return format.format(-minutes, "minute");
  if (minutes < 60 * 24) return format.format(-Math.round(minutes / 60), "hour");
  if (minutes < 60 * 24 * 7) return format.format(-Math.round(minutes / 1440), "day");
  return date.toLocaleDateString(lang === "zh" ? "zh-CN" : "en");
}

/** The saved conversations; a drawer over the chat (the panel is usually narrow). */
export function Sidebar() {
  const { state, dispatch, t } = useApp();
  const running = state.status.running;

  return (
    <>
      <div className={`scrim ${state.sidebar ? "open" : ""}`} onClick={() => dispatch({ type: "sidebar", open: false })} />
      <aside className={`sidebar ${state.sidebar ? "open" : ""}`}>
        <button className="new-chat" disabled={running} onClick={() => send({ type: "newSession" })}>
          <PlusIcon /> {t.newChat}
        </button>
        <div className="sidebar-label">{t.sessions}</div>
        <nav className="session-list">
          {state.sessions.length === 0 && <div className="muted small pad">{t.noSessions}</div>}
          {state.sessions.map((session) => (
            <div
              key={session.id}
              className={`session ${session.id === state.session.id ? "current" : ""} ${running ? "locked" : ""}`}
              onClick={() => !running && session.id !== state.session.id && send({ type: "openSession", id: session.id })}
            >
              <div className="session-title">{session.title || t.untitled}</div>
              <div className="session-meta">
                {relativeTime(session.updated, state.lang)}
                {session.model && <> · {session.model}</>}
              </div>
              <button
                className="icon-button session-delete"
                title={t.delete}
                onClick={(event) => {
                  event.stopPropagation();
                  if (confirm(t.deleteConfirm)) send({ type: "deleteSession", id: session.id });
                }}
              >
                <TrashIcon size={14} />
              </button>
            </div>
          ))}
        </nav>
      </aside>
    </>
  );
}
