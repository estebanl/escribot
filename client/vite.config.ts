import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

/**
 * Where the .NET server listens. Under the Aspire AppHost this comes from the reference
 * to the "api" resource; on its own the dev server expects the API's launch profile port.
 */
const apiUrl =
  process.env.API_HTTP ?? process.env.services__api__http__0 ?? "http://localhost:5271";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) },
  },
  server: {
    port: process.env.PORT ? Number(process.env.PORT) : 5173,
    strictPort: Boolean(process.env.PORT),
    proxy: {
      // The Host header is passed on unchanged (changeOrigin: false), so the API sees the
      // page's own origin and its same-origin check for changing requests passes.
      "/api": { target: apiUrl, changeOrigin: false },
    },
  },
  build: {
    outDir: "dist",
    // The bundle includes Recharts, which is large on its own.
    chunkSizeWarningLimit: 1500,
  },
});
