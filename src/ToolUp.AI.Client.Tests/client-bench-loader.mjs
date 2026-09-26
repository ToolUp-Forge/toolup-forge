// Phase 849 — the proxy counter for ClientBench's boot child.
//
// Registered by client-bench-boot.mjs only; the ordinary test run never
// loads it. It rewrites exactly one transpiled module — the remoting
// client's `Remoting.js` — so that its exported `Remoting.buildProxy` is
// wrapped in a counter. Every `Api.makeProxy` / `Remoting.buildProxy<'T>`
// call site reaches that one function (the generic overload is `inline`
// and compiles to a call of it), so the count is the number of proxies
// built, not the number of call sites.
//
// The rewrite keys on the transpiled SHAPE of that module, which a change
// to the remoting client can move. It therefore never guesses: it requires
// exactly one exported `Remoting_buildProxy*` function, and when it finds
// any other number it records why, which the boot child reports as
// UNOBSERVED rather than as a count of zero.

const REMOTING_MODULE = /\/Client\/Remoting\/Remoting\.js$/;
const BUILD_PROXY = /export function (Remoting_buildProxy(?:_[A-Za-z0-9]+)?)\(/g;

export async function load(url, context, nextLoad) {
    const result = await nextLoad(url, context);
    const path = url.split("?")[0].split("#")[0];

    if (!REMOTING_MODULE.test(path) || result.source == null) {
        return result;
    }

    let source = typeof result.source === "string" ? result.source : new TextDecoder().decode(result.source);
    const names = [...source.matchAll(BUILD_PROXY)].map((m) => m[1]);

    const head =
        "globalThis.__toolupClientBench ??= { proxiesBuilt: 0 };\n" +
        "globalThis.__toolupClientBench.remotingLoaded = true;\n";

    let tail;

    if (names.length === 1) {
        const name = names[0];
        source = source.replace(`export function ${name}(`, `function __clientBench_${name}(`);
        tail =
            `\nexport function ${name}(...args) {\n` +
            "    globalThis.__toolupClientBench.proxiesBuilt++;\n" +
            `    return __clientBench_${name}(...args);\n` +
            "}\n" +
            "globalThis.__toolupClientBench.counterInstalled = true;\n";
    } else {
        const problem = `expected exactly one exported Remoting_buildProxy function in ${path}, found ${names.length}`;
        tail = `\nglobalThis.__toolupClientBench.counterProblem = ${JSON.stringify(problem)};\n`;
    }

    return { ...result, source: head + source + tail, shortCircuit: true };
}
