import fs from "node:fs";
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
// The two plugins the SDK client bundle's sources need: `svgr` turns the
// shell's `./icons/*.svg?react` imports into React components and MUST
// precede `react()`.
import react from "@vitejs/plugin-react";
import svgr from "vite-plugin-svgr";

// The sample's claimed ports, read from the claim itself (../ports.json):
// the first of each band, the server's and this dev server's.
const ports = JSON.parse(fs.readFileSync(new URL("../ports.json", import.meta.url), "utf8"));
const serverPort = ports.server[0];
const clientPort = ports.client[0];

// The development identity. It is on only when the server's development
// identity profile is selected, by the same variable the server reads, so
// one switch turns on both halves. The server declares that profile in
// Debug builds only; in a Release build selecting it refuses startup.
// With it on, the bundle runs on the team surface, sends the stable header
// identity the server's profile makes a platform admin, and lists the
// debug-only HelloWorld module; with it off nothing is defined and
// Client.fs's overrides stand.
const devIdentity = process.env.TOOLUP_PROFILE === "helloworld-dev-identity";

const devDefines = devIdentity
    ? {
          __TOOLUP_PLATFORM_SURFACES__: JSON.stringify("team"),
          __TOOLUP_DEV_DEFAULT_USER_ID__: JSON.stringify("dev-admin"),
          __TOOLUP_SHOW_DEBUG_MODULES__: JSON.stringify("true"),
      }
    : {};

export default defineConfig({
    plugins: [
        nugetContent(),
        fableTailwindGitignore(),
        tailwindcss(),
        svgr({ svgrOptions: { dimensions: false } }),
        react(),
    ],
    define: devDefines,
    // The SDK's `./icons/*.svg?react` imports resolve to files under the
    // repository's `src/`, outside this sample, where no node_modules sits
    // above them; svgr's generated component imports `react` from there.
    // Resolve it (and its DOM twin) from this client's install.
    resolve: { dedupe: ["react", "react-dom"] },
    server: {
        port: clientPort,
        strictPort: true,
        proxy: {
            "/api": `http://localhost:${serverPort}`,
        },
    },
});
