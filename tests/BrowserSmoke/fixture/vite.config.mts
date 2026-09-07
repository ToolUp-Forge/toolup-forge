import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import svgr from "vite-plugin-svgr";

// The harness serves the BUILT bundle from its own HttpListener host —
// there is no dev server in this project's life, so no port is
// declared and none is claimed. `npm run build` is the only script.
export default defineConfig(() => ({
    plugins: [
        // svgr must run BEFORE react so the SDK client tier's
        // `./icons/foo.svg?react` imports resolve to React components
        // rather than data: URLs.
        svgr({ svgrOptions: { dimensions: false } }),
        react(),
    ],
    build: {
        // A smoke fixture is not shipped; readable output is worth more
        // than bytes when a scenario fails in CI and the trace is all
        // anyone has.
        minify: false,
        sourcemap: true,
    },
}));
