import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    host: "0.0.0.0",
    port: 8080,
    proxy: {
      "/api": {
        target: "https://cashlane-fkcnb7fgbjdkaxgx.southindia-01.azurewebsites.net",
        changeOrigin: true
      }
    }
  },
  preview: {
    host: "0.0.0.0",
    port: 8080
  }
});
