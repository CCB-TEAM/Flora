import { useEffect, useState } from "react";
import { useApp } from "../App";
import { send } from "../bridge";
import type { Settings } from "../types";
import { BackIcon } from "./Icons";

const modelSuggestions: Record<Settings["provider"], string[]> = {
  Anthropic: ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1"],
  OpenAICompatible: ["deepseek-chat", "deepseek-reasoner", "qwen-plus", "qwen-max", "gpt-5"],
};

const urlHints: Record<Settings["provider"], string> = {
  Anthropic: "https://api.anthropic.com",
  OpenAICompatible: "https://api.deepseek.com/v1",
};

export function SettingsView() {
  const { state, dispatch, t } = useApp();
  const settings = state.settings;
  const [draft, setDraft] = useState<Settings | null>(settings);
  const [key, setKey] = useState("");
  const [savedAt, setSavedAt] = useState(0);

  useEffect(() => setDraft(settings), [settings]);
  if (!draft || !settings) return null;

  const set = <K extends keyof Settings>(name: K, value: Settings[K]) => setDraft({ ...draft, [name]: value });
  const dirty = (["provider", "model", "baseUrl", "effort", "allowObjects", "compactAt", "mcpEnabled", "mcpPort"] as const).some((k) => draft[k] !== settings[k]);

  const save = () => {
    send({
      type: "saveSettings",
      settings: {
        provider: draft.provider, model: draft.model, baseUrl: draft.baseUrl, effort: draft.effort,
        allowObjects: draft.allowObjects, compactAt: draft.compactAt, mcpEnabled: draft.mcpEnabled, mcpPort: draft.mcpPort,
      },
    });
    setSavedAt(Date.now());
  };

  return (
    <div className="settings">
      <div className="settings-inner">
        <button className="link-button" onClick={() => dispatch({ type: "view", view: "chat" })}>
          <BackIcon size={14} /> {t.back}
        </button>

        <section>
          <h2>{t.model}</h2>
          <label className="field">
            <span>{t.provider}</span>
            <select value={draft.provider} onChange={(e) => set("provider", e.target.value as Settings["provider"])}>
              <option value="Anthropic">{t.providerAnthropic}</option>
              <option value="OpenAICompatible">{t.providerOpenAI}</option>
            </select>
          </label>
          <label className="field">
            <span>{t.model}</span>
            <input list="models" value={draft.model} onChange={(e) => set("model", e.target.value)} spellCheck={false} />
            <datalist id="models">
              {modelSuggestions[draft.provider].map((o) => (
                <option key={o} value={o} />
              ))}
            </datalist>
          </label>
          <label className="field">
            <span>{t.baseUrl}</span>
            <input value={draft.baseUrl} placeholder={`${t.baseUrlHint} (${urlHints[draft.provider]})`} onChange={(e) => set("baseUrl", e.target.value)} spellCheck={false} />
          </label>
          {draft.provider === "Anthropic" && (
            <label className="field">
              <span>{t.effort}</span>
              <select value={draft.effort} onChange={(e) => set("effort", e.target.value)}>
                {["low", "medium", "high", "xhigh", "max"].map((o) => (
                  <option key={o} value={o}>
                    {o}
                  </option>
                ))}
              </select>
              <small>{t.effortHint}</small>
            </label>
          )}
          <div className="field">
            <span>{t.apiKey}</span>
            <div className="inline">
              <input type="password" value={key} placeholder={settings.hasKey ? "••••••••••••" : "sk-…"} onChange={(e) => setKey(e.target.value)} autoComplete="off" />
              <button
                disabled={!key}
                onClick={() => {
                  send({ type: "setApiKey", key });
                  setKey("");
                }}
              >
                {t.saveKey}
              </button>
              {settings.hasKey && <button onClick={() => send({ type: "setApiKey", key: "" })}>{t.clearKey}</button>}
            </div>
            <small className={settings.hasKey ? "ok" : "warn"}>{settings.hasKey ? t.apiKeySaved : t.apiKeyMissing}</small>
          </div>
        </section>

        <section>
          <h2>{t.tools}</h2>
          <label className="toggle">
            <input type="checkbox" checked={draft.allowObjects} onChange={(e) => set("allowObjects", e.target.checked)} />
            <span>
              {t.allowObjects}
              <small>{t.allowObjectsHint}</small>
            </span>
          </label>
          <small className="muted">{t.toolCount(settings.toolCount)}</small>
        </section>

        <section>
          <h2>{t.contextSection}</h2>
          <label className="field">
            <span>{t.compactAt}</span>
            <div className="inline">
              <input type="number" min={0} step={10000} value={draft.compactAt} onChange={(e) => set("compactAt", Math.max(0, Number(e.target.value) || 0))} onBlur={() => draft.compactAt > 0 && draft.compactAt < 20000 && set("compactAt", 20000)} />
              {[50000, 100000, 200000].map((o) => (
                <button key={o} className={draft.compactAt === o ? "selected" : ""} onClick={() => set("compactAt", o)}>
                  {o / 1000}k
                </button>
              ))}
            </div>
            <small>{t.compactAtHint}</small>
          </label>
        </section>

        <section>
          <h2>{t.mcp}</h2>
          <label className="toggle">
            <input type="checkbox" checked={draft.mcpEnabled} onChange={(e) => set("mcpEnabled", e.target.checked)} />
            <span>{t.mcpEnable}</span>
          </label>
          <label className="field">
            <span>{t.port}</span>
            <input type="number" min={1024} max={65535} value={draft.mcpPort} onChange={(e) => set("mcpPort", Number(e.target.value) || settings.mcpPort)} />
          </label>
          <div className="mcp-status">
            <span className={`dot ${settings.mcpStatus.startsWith("listening") ? "on" : ""}`} />
            {settings.mcpStatus}
          </div>
          <div className="inline">
            <code className="command-line">{settings.mcpCommand}</code>
            <button onClick={() => send({ type: "copy", text: settings.mcpCommand })}>{t.copyCommand}</button>
          </div>
        </section>
      </div>
      <div className="settings-footer">
        {savedAt > 0 && !dirty && <span className="ok small">{t.saved}</span>}
        <button className="primary" disabled={!dirty} onClick={save}>
          {t.save}
        </button>
      </div>
    </div>
  );
}
