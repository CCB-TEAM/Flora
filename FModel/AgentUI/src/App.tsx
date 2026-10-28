import { createContext, useContext, useEffect, useReducer } from "react";
import { onHostMessage, send } from "./bridge";
import { strings, type Strings } from "./i18n";
import { initialState, reducer, type Action, type State } from "./state";
import { Header } from "./components/Header";
import { Sidebar } from "./components/Sidebar";
import { Transcript } from "./components/Transcript";
import { Composer } from "./components/Composer";
import { SettingsView } from "./components/SettingsView";
import { Toasts } from "./components/Toasts";

interface Context {
  state: State;
  dispatch: (action: Action) => void;
  t: Strings;
}

const AppContext = createContext<Context>(null!);
export const useApp = () => useContext(AppContext);

export function App() {
  const [state, dispatch] = useReducer(reducer, initialState);

  useEffect(() => {
    const off = onHostMessage((message) => dispatch({ type: "host", message }));
    send({ type: "ready" });
    return off;
  }, []);

  const t = strings[state.lang];
  useEffect(() => {
    document.documentElement.lang = state.lang === "zh" ? "zh-CN" : "en";
  }, [state.lang]);

  return (
    <AppContext.Provider value={{ state, dispatch, t }}>
      <div className="app">
        <Sidebar />
        <main className="main">
          <Header />
          {state.view === "settings" ? (
            <SettingsView />
          ) : (
            <>
              <Transcript />
              <Composer />
            </>
          )}
        </main>
        <Toasts />
      </div>
    </AppContext.Provider>
  );
}
