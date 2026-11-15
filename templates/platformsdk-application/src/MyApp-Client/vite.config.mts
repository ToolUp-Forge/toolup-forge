import { defineConfig } from "vite";
// Resolves `@nuget/<package-id>/...` in index.css to the restored package, so
// the Fuaran-UI reference stylesheet is read from the renderer this client
// compiled against.
import nugetContent from "./vite-fuaran-content.mjs";

export default defineConfig({
  plugins: [nugetContent()],
  server: {
    port: 9080,
    proxy: {
      "/api": "http://localhost:9000"
    }
  }
});
