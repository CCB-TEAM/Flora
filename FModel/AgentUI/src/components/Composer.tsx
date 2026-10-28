import { useEffect, useRef, useState } from "react";
import { useApp } from "../App";
import { send } from "../bridge";
import { SendIcon, StopIcon } from "./Icons";

type Command = "compact" | "new" | "settings" | "packer";
const commands: Command[] = ["compact", "new", "settings", "packer"];

export function Composer() {
  const { state, dispatch, t } = useApp();
  const [text, setText] = useState("");
  const [selected, setSelected] = useState(0);
  const area = useRef<HTMLTextAreaElement>(null);
  const running = state.status.running;
  const noKey = state.settings && state.settings.provider === "Anthropic" && !state.settings.hasKey;

  // suggestions from the welcome screen fill the box
  useEffect(() => {
    const onSuggest = (event: Event) => {
      setText((event as CustomEvent<string>).detail);
      area.current?.focus();
    };
    window.addEventListener("flora:suggest", onSuggest);
    return () => window.removeEventListener("flora:suggest", onSuggest);
  }, []);

  // grow with the text, up to a third of the panel
  useEffect(() => {
    const el = area.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = Math.min(el.scrollHeight, window.innerHeight / 3) + "px";
  }, [text]);

  const slash = text.startsWith("/") && !text.includes(" ") && !text.includes("\n");
  const matching = slash ? commands.filter((o) => o.startsWith(text.slice(1).toLowerCase())) : [];

  const run = (command: Command) => {
    setText("");
    if (command === "compact") send({ type: "compact" });
    else if (command === "new") send({ type: "newSession" });
    else if (command === "settings") dispatch({ type: "view", view: "settings" });
    else send({ type: "openPacker" });
  };

  const submit = () => {
    if (matching.length > 0) return run(matching[Math.min(selected, matching.length - 1)]);
    const value = text.trim();
    if (!value || running) return;
    send({ type: "send", text: value });
    setText("");
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (matching.length > 0 && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
      event.preventDefault();
      setSelected((s) => (s + (event.key === "ArrowDown" ? 1 : matching.length - 1)) % matching.length);
      return;
    }
    if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      submit();
    }
    if (event.key === "Escape" && running) send({ type: "stop" });
  };

  return (
    <div className="composer">
      {matching.length > 0 && (
        <div className="commands">
          {matching.map((command, index) => (
            <button key={command} className={`command ${index === selected % matching.length ? "selected" : ""}`} onMouseDown={(e) => (e.preventDefault(), run(command))}>
              <span className="command-name">/{command}</span>
              <span className="muted">{t.commands[command]}</span>
            </button>
          ))}
        </div>
      )}
      {noKey && (
        <button className="notice warn clickable" onClick={() => dispatch({ type: "view", view: "settings" })}>
          {t.apiKeyMissing} → {t.settings}
        </button>
      )}
      <div className={`composer-box ${running ? "running" : ""}`}>
        <textarea
          ref={area}
          rows={1}
          value={text}
          placeholder={t.placeholder}
          onChange={(event) => {
            setText(event.target.value);
            setSelected(0);
          }}
          onKeyDown={onKeyDown}
          autoFocus
        />
        {running ? (
          <button className="send stop" title={t.stop} onClick={() => send({ type: "stop" })}>
            <StopIcon />
          </button>
        ) : (
          <button className="send" title={t.send} disabled={!text.trim()} onClick={submit}>
            <SendIcon />
          </button>
        )}
      </div>
    </div>
  );
}
