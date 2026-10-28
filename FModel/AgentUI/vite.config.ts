import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The build is copied next to Flora.exe (AgentUI/) and served by WebView2 from https://flora-agent.local/.
// While working on the interface: npm run dev, then start Flora with FLORA_AGENT_UI_URL=http://localhost:5173
export default defineConfig({
  plugins: [react()],
  base: "./",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022",
    chunkSizeWarningLimit: 900,
  },
  server: { port: 5173, strictPort: true },
});
