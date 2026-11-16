import { defineConfig } from "vite";
// Resolves `@nuget/<package-id>/...` in index.css to the restored package, so
// the Fuaran-UI reference stylesheet is read from the renderer this client
// compiled against.
import nugetContent from "./vite-fuaran-content.mjs";
// The canonical styling block's two Vite plugins (client-styling/tailwind/):
// `fableTailwindGitignore()` lets Tailwind scan Fable's output/, where the
// SDK shell's classes live, and MUST precede `tailwindcss()`.
import tailwindcss from "@tailwindcss/vite";
import fableTailwindGitignore from "./vite-fable-tailwind.mjs";

export default defineConfig({
  plugins: [nugetContent(), fableTailwindGitignore(), tailwindcss()],
  server: {
    port: 9080,
    proxy: {
      "/api": "http://localhost:9000"
    }
  }
});
