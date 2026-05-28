import { defineConfig } from "vite";

export default defineConfig({
  server: {
    port: 9080,
    proxy: {
      "/api": "http://localhost:9000"
    }
  }
});
