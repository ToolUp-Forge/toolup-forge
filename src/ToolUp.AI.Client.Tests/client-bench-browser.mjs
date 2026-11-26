// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
//
// ClientBench's browser leg: one host dispatch of the 200-row table, as Feliz and as a Fuaran-UI tree, and of each
// real module view that moved to a tree, as the Feliz page it replaced and as the shipped tree, timed in a
// headless browser with React's commit and a forced layout. ClientBench's `viewPerDispatchUs` times the view's
// construction under Node and cannot see commit or layout, which is where much of a tree's cost sits; this is the
// figure a tree surface is judged on. Recorded, never gated.
//
// What it does:
//   1. Bundles `output/ClientBenchTables.js` (the Fable output of ClientBenchTables.fs) for a page with Vite's
//      production build, using the Vite the shell sample installs (samples/MinimalClient), so React is its
//      production build, as a deployment's is.
//   2. Serves the bundle cross-origin isolated (so `performance.now()` resolves to 5 us) and drives the browser
//      over the DevTools protocol with `--js-flags=--expose-gc`.
//   3. Per profile (desktop; 6x CPU throttling, the study's low phone) and per arm (feliz, tree, the tree with its rows kept, the rows as a DataGrid with a button column,
//      the boxes with Markdown cells, then feliz again
//      as the control for drift across the run; then per real module view its Feliz page, its shipped tree and its
//      Feliz page again, each dispatch going through the module's own `update`): mounts the page, runs warm-ups,
//      then samples one dispatch each:
//      `gc()`, the next model rendered synchronously (`flushSync`), then `offsetHeight` forced. `render` is the
//      rebuild, React's render and commit; `layout` the forced layout after it; `total` both.
//   4. Refuses a reading in which a mount built under 100 elements, a dispatch changed no DOM, the page was not
//      cross-origin isolated or `gc` was not exposed: each would be a number about nothing.
//
// The browser: TOOLUP_BENCH_BROWSER if set (a Chromium-family executable), else Microsoft Edge where it installs,
// else Chrome. No browser is a recorded absence, not a failure. Prints one `CLIENTBENCH-BROWSER <json>` line.
//
// usage: node client-bench-browser.mjs [--samples <n>] [--warmups <n>] [--rows <n>]
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const arg = (flag, fallback) => {
    const i = process.argv.indexOf(flag);
    return i > 0 && i + 1 < process.argv.length ? Number(process.argv[i + 1]) : fallback;
};
const SAMPLES = arg("--samples", 30);
const WARMUPS = arg("--warmups", 15);
const ROWS = arg("--rows", 200);
const PROFILES = [
    { name: "desktop", cpu: 1 },
    { name: "low phone: 6x CPU", cpu: 6 },
];
// The real module views, each an arm pair and its drift control. A batch that moves more views adds its views here
// and their mounts in ClientBenchTables.mount (docs/platform/budgets.md says how).
const VIEWS = [
    { name: "team-manager", label: "the team manager, one of 200 teams renamed" },
    { name: "team-manager-reload", label: "the team manager, its 200 teams loaded again" },
    { name: "visibility", label: "the module-visibility editor, the rule kind flipped" },
    { name: "platform-teams", label: "Platform Management's teams tab, one of 200 teams renamed" },
    { name: "platform-teams-reload", label: "Platform Management's teams tab, its 200 teams loaded again" },
];
const ARMS = [
    "feliz", "tree", "tree-kept", "tree-grid", "tree-markdown", "feliz-control",
    ...VIEWS.flatMap((v) => [`${v.name}-feliz`, `${v.name}-tree`, `${v.name}-feliz-control`]),
];

const report = (value) => {
    console.log("CLIENTBENCH-BROWSER " + JSON.stringify(value));
};
const absent = (reason) => {
    report({ observed: false, reason });
    process.exit(0);
};

function findBrowser() {
    const named = process.env.TOOLUP_BENCH_BROWSER;
    if (named) return fs.existsSync(named) ? named : absent(`TOOLUP_BENCH_BROWSER names ${named}, which does not exist`);
    const candidates = [
        "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
        "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
        "/opt/microsoft/msedge/msedge",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        "C:/Program Files/Google/Chrome/Application/chrome.exe",
        "/usr/bin/google-chrome",
        "/usr/bin/chromium",
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    ];
    return candidates.find((c) => fs.existsSync(c)) ?? absent("no Edge or Chrome on this machine (set TOOLUP_BENCH_BROWSER)");
}

const browserPath = findBrowser();
const tables = path.join(here, "output", "ClientBenchTables.js");
if (!fs.existsSync(tables)) absent(`${tables} does not exist: transpile the harness first (dotnet fable -o output)`);
const viteEntry = path.resolve(here, "../../samples/MinimalClient/node_modules/vite/dist/node/index.js");
if (!fs.existsSync(viteEntry)) absent(`no Vite at ${viteEntry}: npm ci in samples/MinimalClient first`);

// ── 1. The bundle ──
const work = fs.mkdtempSync(path.join(os.tmpdir(), "toolup-bench-browser-"));
const entry = path.join(here, "output", "client-bench-browser-entry.js");
// The tables are laid out under the stylesheets a shell loads (the renderer's reference sheet and the shell's
// bridge), imported the way samples/MinimalClient's index.css imports them.
const bridge = path.resolve(here, "../../client-styling/fuaran-bridge.css").split(path.sep).join("/");
fs.writeFileSync(
    entry,
    [
        'import "@nuget/fuaran.ui.renderer/content/fuaran-reference.css";',
        `import ${JSON.stringify(bridge)};`,
        'import { mount } from "./ClientBenchTables.js";',
        "window.__bench = { mount };",
        "",
    ].join("\n"),
);
const { build } = await import(pathToFileURL(viteEntry).href);
const plugin = path.resolve(here, "../../client-styling/vite-fuaran-content.mjs");
const { default: nugetContent } = await import(pathToFileURL(plugin).href);
try {
    await build({
        root: here,
        configFile: false,
        mode: "production",
        logLevel: "error",
        plugins: [nugetContent()],
        resolve: { dedupe: ["react", "react-dom"] },
        build: { outDir: path.join(work, "dist"), emptyOutDir: true, manifest: true, rolldownOptions: { input: entry } },
    });
} catch (e) {
    absent(`the bench page did not bundle: ${String(e).slice(0, 400)}`);
} finally {
    fs.rmSync(entry, { force: true });
}
const manifest = JSON.parse(fs.readFileSync(path.join(work, "dist", ".vite", "manifest.json"), "utf8"));
const entryChunk = Object.values(manifest).find((m) => m.isEntry);
const entryFile = entryChunk.file;
const styles = (entryChunk.css ?? []).map((f) => `<link rel=stylesheet href="./${f}">`).join("");

// ── 2. The page and the server ──
const page = `<!doctype html><meta charset=utf-8><title>bench</title>${styles}<div id=host></div><script type=module>
import "./${entryFile}";
const median = (a) => [...a].sort((x, y) => x - y)[Math.floor(a.length / 2)];
const run = (kind, samples, warmups, rows) => {
    const host = document.getElementById("host");
    const container = document.createElement("div");
    host.replaceChildren(container);
    const t0 = performance.now();
    const handle = window.__bench.mount(kind.endsWith("-control") ? kind.slice(0, -"-control".length) : kind, rows, container);
    void container.offsetHeight;
    const mountMs = performance.now() - t0;
    const elements = container.querySelectorAll("*").length;
    let mutations = 0;
    const observer = new MutationObserver((records) => { mutations += records.length; });
    observer.observe(container, { subtree: true, childList: true, characterData: true, attributes: true });
    for (let i = 0; i < warmups; i++) { handle.dispatch(); void container.offsetHeight; }
    const render = [], layout = [], total = [], changed = [];
    for (let i = 0; i < samples; i++) {
        globalThis.gc();
        observer.takeRecords(); mutations = 0;
        const a = performance.now();
        handle.dispatch();
        const b = performance.now();
        void container.offsetHeight;
        const c = performance.now();
        changed.push(mutations + observer.takeRecords().length);
        render.push(b - a); layout.push(c - b); total.push(c - a);
    }
    observer.disconnect();
    handle.unmount();
    return { arm: kind, mountMs, elements, render, layout, total, domChangesMin: Math.min(...changed), domChangesMedian: median(changed) };
};
window.__run = (samples, warmups, rows) => ({
    isolated: self.crossOriginIsolated,
    gc: typeof globalThis.gc === "function",
    arms: ${JSON.stringify(ARMS)}.map((k) => run(k, samples, warmups, rows)),
});
window.__ready = true;
</script>`;
const server = http.createServer((req, res) => {
    const url = req.url.split("?")[0];
    const isolate = { "cross-origin-opener-policy": "same-origin", "cross-origin-embedder-policy": "require-corp" };
    if (url === "/") {
        res.writeHead(200, { "content-type": "text/html", ...isolate });
        return res.end(page);
    }
    const file = path.join(work, "dist", path.normalize(url).replace(/^([\\/])+/, ""));
    if (!file.startsWith(path.join(work, "dist")) || !fs.existsSync(file)) {
        res.writeHead(404);
        return res.end();
    }
    res.writeHead(200, { "content-type": file.endsWith(".js") ? "text/javascript" : file.endsWith(".css") ? "text/css" : "application/octet-stream", ...isolate });
    res.end(fs.readFileSync(file));
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const port = server.address().port;

// ── 3. The browser ──
const profileDir = fs.mkdtempSync(path.join(os.tmpdir(), "toolup-bench-browser-profile-"));
const browser = spawn(
    browserPath,
    ["--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profileDir}`, "--no-first-run", "--disable-extensions", "--js-flags=--expose-gc", "about:blank"],
    { stdio: "ignore" },
);
const finish = (code) => {
    try { browser.kill(); } catch {}
    server.close();
    setTimeout(() => {
        for (const d of [work, profileDir]) try { fs.rmSync(d, { recursive: true, force: true }); } catch {}
        process.exit(code);
    }, 500);
};
const portFile = path.join(profileDir, "DevToolsActivePort");
for (let i = 0; i < 300 && !fs.existsSync(portFile); i++) await new Promise((r) => setTimeout(r, 100));
if (!fs.existsSync(portFile)) {
    report({ observed: false, reason: `${browserPath} did not open a DevTools port in 30 s` });
    finish(0);
}
const debugPort = fs.readFileSync(portFile, "utf8").split("\n")[0].trim();
const target = await (await fetch(`http://127.0.0.1:${debugPort}/json/new?about:blank`, { method: "PUT" })).json();
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0;
const pending = new Map();
ws.onmessage = (m) => {
    const d = JSON.parse(m.data);
    if (d.id && pending.has(d.id)) {
        pending.get(d.id)(d);
        pending.delete(d.id);
    }
};
const cdp = (method, params = {}) =>
    new Promise((r) => {
        const i = ++id;
        pending.set(i, r);
        ws.send(JSON.stringify({ id: i, method, params }));
    });
const evaluate = async (expression) => {
    const v = await cdp("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
    if (v.result?.exceptionDetails) throw new Error(JSON.stringify(v.result.exceptionDetails).slice(0, 600));
    return v.result?.result?.value;
};
const version = (await (await fetch(`http://127.0.0.1:${debugPort}/json/version`)).json()).Browser;

const round = (x) => Math.round(x * 1000) / 1000;
const stat = (values) => {
    const s = [...values].sort((a, b) => a - b);
    return { median: round(s[Math.floor(s.length / 2)]), p90: round(s[Math.min(s.length - 1, Math.floor(s.length * 0.9))]), min: round(s[0]), n: s.length };
};

const profiles = [];
const refusals = [];
try {
    for (const p of PROFILES) {
        await cdp("Page.navigate", { url: `http://127.0.0.1:${port}/` });
        for (let i = 0; i < 300 && !(await evaluate("window.__ready === true")); i++) await new Promise((r) => setTimeout(r, 100));
        await cdp("Emulation.setCPUThrottlingRate", { rate: p.cpu });
        const raw = await evaluate(`JSON.stringify(window.__run(${SAMPLES}, ${WARMUPS}, ${ROWS}))`);
        await cdp("Emulation.setCPUThrottlingRate", { rate: 1 });
        const r = JSON.parse(raw);
        if (!r.isolated) refusals.push(`${p.name}: the page is not cross-origin isolated, so the clock is coarse`);
        if (!r.gc) refusals.push(`${p.name}: gc is not exposed`);
        const arms = r.arms.map((a) => {
            if (a.elements < 100) refusals.push(`${p.name} / ${a.arm}: the mount built ${a.elements} elements`);
            if (a.domChangesMin < 1) refusals.push(`${p.name} / ${a.arm}: a dispatch changed no DOM`);
            return { arm: a.arm, elements: a.elements, mountMs: round(a.mountMs), domChangesPerDispatch: a.domChangesMedian, totalMs: stat(a.total), renderMs: stat(a.render), layoutMs: stat(a.layout) };
        });
        profiles.push({ profile: p.name, cpuThrottleRate: p.cpu, arms });
    }
} catch (e) {
    report({ observed: false, reason: `the page failed: ${String(e).slice(0, 600)}`, browser: version });
    finish(0);
}

const medianOf = (prof, arm) => profiles.find((p) => p.profile === prof).arms.find((a) => a.arm === arm).totalMs.median;
const viewSummary = (prof) =>
    VIEWS.map((v) => `${v.label}: Feliz ${medianOf(prof, `${v.name}-feliz`)} ms, tree ${medianOf(prof, `${v.name}-tree`)} ms, Feliz again ${medianOf(prof, `${v.name}-feliz-control`)} ms`).join(", ");
const summary = PROFILES.map((p) => `${p.name}: feliz ${medianOf(p.name, "feliz")} ms, tree ${medianOf(p.name, "tree")} ms, tree with kept rows ${medianOf(p.name, "tree-kept")} ms, DataGrid ${medianOf(p.name, "tree-grid")} ms, boxes with Markdown ${medianOf(p.name, "tree-markdown")} ms, feliz again ${medianOf(p.name, "feliz-control")} ms | ${viewSummary(p.name)}`).join("; ");
report({
    observed: refusals.length === 0,
    reason: refusals.length ? refusals.join("; ") : undefined,
    summary: `median dispatch with commit and layout, ${ROWS} rows, ${SAMPLES} samples after ${WARMUPS} warm-ups — ${summary}`,
    method: "client-bench-browser.mjs: Vite production bundle of ClientBenchTables, cross-origin isolated page, headless browser over CDP, gc() before each sample, flushSync render then forced layout; a module view's dispatch goes through its own update, effects dropped, and its tree is mounted by the component its deferred surface loads",
    views: VIEWS,
    browser: version,
    machine: { cpu: os.cpus()[0]?.model, logicalCores: os.cpus().length, os: `${os.platform()} ${os.release()}`, node: process.version },
    rows: ROWS,
    samples: SAMPLES,
    warmups: WARMUPS,
    profiles,
});
finish(0);
