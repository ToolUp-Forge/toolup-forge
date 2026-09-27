// Phase 849 — ONE fresh-process boot of a transpiled client module.
//
// Spawned by ClientBench.fs (never run by `node --test`):
//
//   node --import ./register-loader.mjs client-bench-boot.mjs <module-url> <render|import>
//
// A module graph evaluates once per process, so a warm loop cannot see what
// boot costs; each sample is therefore its own process. This script imports
// NOTHING from the SDK itself — every module the target pulls in is
// evaluated for the first time inside the timed window.
//
//   render — time `import()` of the target to the first commit into the
//            #elmish-app placeholder (a MutationObserver on the host sees
//            React's commit). A boot that never commits is reported as NOT
//            rendered; the parent refuses it rather than reading a number.
//   import — time `import()` alone (the SDK shell's module graph, which
//            renders nothing without a composition).
//
// Both report how many remoting proxies were built inside the window. The
// counter is installed by ./client-bench-loader.mjs, which wraps the
// transpiled `Remoting.buildProxy`; its state is reported verbatim so a
// counter that could not be installed reads as UNOBSERVED, never as zero.
//
// The one line of output the parent reads is prefixed `CLIENTBENCH-BOOT `;
// anything the app itself logs is ignored.

import { register } from "node:module";
import { JSDOM } from "jsdom";

register("./client-bench-loader.mjs", import.meta.url);

const [target, mode = "render", placeholder = "elmish-app"] = process.argv.slice(2);

function installCanvasStub(window) {
    const noop = () => {};
    const matrix = () => ({
        a: 1, b: 0, c: 0, d: 1, e: 0, f: 0,
        inverse() { return this; },
        multiply() { return this; },
        translate() { return this; },
        scale() { return this; },
        transformPoint: (p) => ({ x: (p && p.x) || 0, y: (p && p.y) || 0 }),
    });
    const values = {
        measureText: (text) => {
            const width = String(text ?? "").length * 6;
            return {
                width,
                actualBoundingBoxLeft: 0,
                actualBoundingBoxRight: width,
                actualBoundingBoxAscent: 8,
                actualBoundingBoxDescent: 2,
                fontBoundingBoxAscent: 9,
                fontBoundingBoxDescent: 3,
                emHeightAscent: 9,
                emHeightDescent: 3,
            };
        },
        getImageData: (_x, _y, w, h) => ({
            data: new Uint8ClampedArray(Math.max(4, (w | 0) * (h | 0) * 4)),
            width: w | 0,
            height: h | 0,
        }),
        createImageData: (w, h) => ({ data: new Uint8ClampedArray(Math.max(4, (w | 0) * (h | 0) * 4)), width: w | 0, height: h | 0 }),
        createLinearGradient: () => ({ addColorStop: noop }),
        createRadialGradient: () => ({ addColorStop: noop }),
        createConicGradient: () => ({ addColorStop: noop }),
        createPattern: () => ({ setTransform: noop }),
        getTransform: matrix,
        isPointInPath: () => false,
        isPointInStroke: () => false,
        getLineDash: () => [],
        getContextAttributes: () => ({ alpha: true }),
    };
    const context = (canvas) =>
        new Proxy(
            { canvas, direction: "ltr", font: "10px sans-serif", globalAlpha: 1, lineWidth: 1 },
            {
                get: (target, prop) => (prop in target ? target[prop] : prop in values ? values[prop] : noop),
                set: (target, prop, value) => {
                    target[prop] = value;
                    return true;
                },
            },
        );
    const proto = window.HTMLCanvasElement.prototype;
    proto.getContext = function (type) {
        if (type !== "2d") return null;
        this.__benchContext ??= context(this);
        return this.__benchContext;
    };
    proto.toDataURL = () => "data:image/png;base64,";
    proto.toBlob = (callback) => callback(null);
    if (!globalThis.Path2D) {
        globalThis.Path2D = class Path2D {
            constructor() {
                return new Proxy(this, { get: (t, p) => (p in t ? t[p] : noop) });
            }
        };
    }
    if (!globalThis.DOMMatrix) {
        globalThis.DOMMatrix = function DOMMatrix() { return matrix(); };
    }
}

const report = (fields) => {
    process.stdout.write(`\nCLIENTBENCH-BOOT ${JSON.stringify(fields)}\n`, () => process.exit(0));
};

if (!target) {
    report({ error: "no target module URL was passed" });
} else {
    const dom = new JSDOM(
        `<!doctype html><html><body><div id="${placeholder}"></div></body></html>`,
        { pretendToBeVisual: true, url: "http://localhost/" },
    );

    // The browser globals the client tier reads off the ambient scope: every
    // window property Node lacks, plus the handful Node already defines
    // differently (navigator is getter-only on Node's global, hence
    // defineProperty throughout).
    const define = (name, value) =>
        Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });

    for (const name of Object.getOwnPropertyNames(dom.window)) {
        if (!(name in globalThis)) {
            try { define(name, dom.window[name]); } catch { /* non-configurable on Node: keep Node's */ }
        }
    }

    for (const name of ["window", "document", "navigator", "location", "history", "localStorage", "sessionStorage"]) {
        define(name, dom.window[name]);
    }

    // jsdom has no canvas: `getContext("2d")` returns null, and the sample's
    // AG Charts gallery throws on it, which unmounts the whole root (React 19
    // renders nothing rather than a partial tree). A drawing context that
    // accepts every call and draws nothing is the faithful stand-in for what
    // this measurement can claim anyway: boot's module evaluation, `init`,
    // `view` and React's commit transfer to a browser; the paint does not.
    installCanvasStub(dom.window);

    globalThis.__toolupClientBench = {
        proxiesBuilt: 0,
        proxiesDeferred: 0,
        proxiesResolved: 0,
        remotingLoaded: false,
        apiLoaded: false,
        counterInstalled: false,
        deferredCounterInstalled: false,
        resolvedCounterInstalled: false,
        counterProblem: null,
    };

    const host = dom.window.document.getElementById(placeholder);
    let renderedAt = null;
    let resolveRendered;
    const renderedSignal = new Promise((resolve) => { resolveRendered = resolve; });

    new dom.window.MutationObserver(() => {
        if (renderedAt === null && host.childElementCount > 0) {
            renderedAt = performance.now();
            resolveRendered();
        }
    }).observe(host, { childList: true, subtree: true });

    const t0 = performance.now();
    let error = null;

    try {
        await import(target);
    } catch (e) {
        error = String((e && e.stack) || e);
    }

    const importedAt = performance.now();

    if (mode === "render" && error === null) {
        await Promise.race([renderedSignal, new Promise((resolve) => setTimeout(resolve, 30000))]);
    }

    const bench = globalThis.__toolupClientBench;
    // Phase 853 — three counters (reflective builds, deferred proxies,
    // makeProxy resolutions); a loaded module whose counter is missing is
    // UNOBSERVED, never zero.
    const counterState = bench.counterProblem
        ? `UNOBSERVED: ${bench.counterProblem}`
        : bench.remotingLoaded && !(bench.counterInstalled && bench.deferredCounterInstalled)
            ? "UNOBSERVED: Remoting.js loaded but its counters were not installed"
            : bench.apiLoaded && !bench.resolvedCounterInstalled
                ? "UNOBSERVED: Api.js loaded but the resolution counter was not installed"
                : bench.remotingLoaded
                    ? "counted"
                    : "remoting module never loaded (no proxy can have been built)";

    report({
        mode,
        error,
        importMs: importedAt - t0,
        renderMs: mode === "render" && renderedAt !== null ? renderedAt - t0 : null,
        proxiesBuilt: bench.proxiesBuilt,
        proxiesDeferred: bench.proxiesDeferred,
        proxiesGenerated: bench.proxiesResolved - bench.proxiesDeferred,
        counterState,
    });
}
