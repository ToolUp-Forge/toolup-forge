import { defineConfig } from "vite";

export default defineConfig({
  server: {
    port: 8080,
    proxy: {
      "/api": "http://localhost:5000"
    }
  }
});
