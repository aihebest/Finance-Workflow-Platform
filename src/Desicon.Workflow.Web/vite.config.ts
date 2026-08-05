import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // The API rejects unknown origins via CORS (allowed_cors_origins in
    // Terraform, empty in dev). Proxying in development keeps the browser
    // origin identical to the app's, so local work does not depend on a CORS
    // entry that production should not need either -- the SPA and the API sit
    // behind the same Front Door.
    proxy: {
      "/api": {
        target: process.env.VITE_DEV_API_PROXY ?? "http://localhost:5080",
        changeOrigin: true,
        secure: false,
      },
    },
  },
  build: {
    // docker/web.Dockerfile copies /app/dist into nginx. Named here so the
    // two cannot drift apart silently.
    outDir: "dist",
    sourcemap: true,
  },
});
