// Node ESM loader hook for the AI.Client.Tests Mocha runner.
//
// The Fable-transpiled AI.Client surface imports SVG icon files
// (e.g. `./icons/ai-assistant.svg?react`) and CSS via
// `importSideEffects`. Vite handles both via `vite-plugin-svgr` +
// its CSS pipeline in the browser dev path; Node has no native
// handler for either extension.
//
// `resolve` short-circuits asset specifiers to a custom `asset-stub:`
// URL; `load` provides the actual no-op source for any URL whose
// pathname carries an asset extension. The query/hash on `?react`
// SVG specifiers means a plain `endsWith` check on the URL string
// would miss; we match on the resolved pathname after stripping the
// query.
//
// The test code never executes view-rendering paths, so the no-op
// stand-ins are never actually called — they just satisfy the ESM
// loader at module-load time.

const ASSET_EXTENSIONS = [".svg", ".css", ".png", ".jpg", ".jpeg", ".gif", ".woff", ".woff2"];

const NOOP_SOURCE =
    "const Noop = () => null; export default Noop; export const ReactComponent = Noop;";

const stripQuery = (s) => {
    const queryIdx = s.indexOf("?");
    const hashIdx = s.indexOf("#");
    const cutAt = [queryIdx, hashIdx].filter((i) => i >= 0).reduce((a, b) => Math.min(a, b), s.length);
    return s.slice(0, cutAt);
};

const isAsset = (specifierOrUrl) => {
    const path = stripQuery(specifierOrUrl);
    return ASSET_EXTENSIONS.some((ext) => path.endsWith(ext));
};

export async function resolve(specifier, context, nextResolve) {
    if (isAsset(specifier)) {
        return {
            shortCircuit: true,
            url: `asset-stub:${specifier}`,
            format: "module",
        };
    }
    return nextResolve(specifier, context);
}

export async function load(url, context, nextLoad) {
    if (url.startsWith("asset-stub:") || isAsset(url)) {
        return {
            shortCircuit: true,
            format: "module",
            source: NOOP_SOURCE,
        };
    }
    return nextLoad(url, context);
}
