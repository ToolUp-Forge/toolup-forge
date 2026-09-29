import { defineConfig, mergeConfig } from "vite";
import base from "./vite.config.mts";

// Phase 909 — the SDK shell's production bundle, for the perf-budget gate's
// bundle-size budget (`client.shellBundleKiB` in perf-budgets.json).
//
// The minimal sample's own entry (`index.html` -> `output/Client.js`) never
// reaches the shell: it mounts a plain Elmish program, so its bundle holds no
// part of `SDK.Client`, the remoting proxies or the generated client module.
// This build takes the shell module Fable emitted for this sample as the
// entry and keeps EVERY export of it (`preserveEntrySignatures: "strict"`),
// so the bundle is what the platform's client tier adds to a consumer that
// composes it — the figure that grows when a generated module grows.
//
//   dotnet fable -o output --noCache
//   npx vite build --config vite.shell.config.mts --outDir <dir> --emptyOutDir
//
// dev-scripts/perf-budget-gate.ps1 runs both, and ClientBench measures the
// directory.
export default defineConfig((env) =>
    mergeConfig(typeof base === "function" ? base(env) : base, {
        // The SDK's `./icons/*.svg?react` imports resolve to files under the
        // repository's `src/`, outside this sample, where no node_modules
        // sits above them; svgr's generated component imports `react` from
        // there. Resolve it (and its DOM twin) from this sample's install.
        resolve: { dedupe: ["react", "react-dom"] },
        build: {
            rolldownOptions: {
                input: "output/src/ToolUp.Platform.Client/Client/SDK.Client.js",
                preserveEntrySignatures: "strict",
            },
        },
    }),
);
