import { useEffect } from "react";
import { useApp } from "../App";

export function Toasts() {
  const { state, dispatch } = useApp();

  useEffect(() => {
    if (state.toasts.length === 0) return;
    const first = state.toasts[0];
    const timer = setTimeout(() => dispatch({ type: "dismiss", id: first.id }), first.error ? 6000 : 3000);
    return () => clearTimeout(timer);
  }, [state.toasts, dispatch]);

  return (
    <div className="toasts">
      {state.toasts.map((toast) => (
        <div key={toast.id} className={`toast ${toast.error ? "error" : ""}`} onClick={() => dispatch({ type: "dismiss", id: toast.id })}>
          {toast.text}
        </div>
      ))}
    </div>
  );
}
