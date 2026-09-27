// Phase 849 — the proxy counter for ClientBench's boot child.
//
// Registered by client-bench-boot.mjs only; the ordinary test run never
// loads it. It rewrites two transpiled modules so that three exported
// functions are wrapped in counters:
//
//   * `Remoting.buildProxy` (Remoting.js) — a REFLECTIVE proxy build: the
//     `createTypeInfo` walk over the API record. Every reflective build
//     reaches that one function (the generic overload is `inline` and
//     compiles to a call of it), so the count is the number of proxies
//     built, not the number of call sites.
//   * Phase 853 — `Remoting.buildLazyProxy` (Remoting.js) — a proxy whose
//     reflective build is DEFERRED to its first call (a record with no
//     generated proxy).
//   * Phase 853 — `Api.resolveProxy` (Api.js) — every `Api.makeProxy`. A
//     resolution that did not defer was served by a GENERATED proxy, built
//     with no reflection; the boot child reports that difference.
//
// The rewrite keys on the transpiled SHAPE of those modules, which a change
// to the remoting client can move. It therefore never guesses: it requires
// exactly one exported function of each name, and when it finds any other
// number it records why, which the boot child reports as UNOBSERVED rather
// than as a count of zero.

const MODULES = [
    {
        path: /\/Client\/Remoting\/Remoting\.js$/,
        loaded: "remotingLoaded",
        counters: [
            { pattern: /export function (Remoting_buildProxy(?:_[A-Za-z0-9]+)?)\(/g, counter: "proxiesBuilt", installed: "counterInstalled", label: "Remoting_buildProxy" },
            { pattern: /export function (Remoting_buildLazyProxy(?:_[A-Za-z0-9]+)?)\(/g, counter: "proxiesDeferred", installed: "deferredCounterInstalled", label: "Remoting_buildLazyProxy" },
        ],
    },
    {
        path: /\/Client\/Api\.js$/,
        loaded: "apiLoaded",
        counters: [
            { pattern: /export function (Api_resolveProxy(?:_[A-Za-z0-9]+)?)\(/g, counter: "proxiesResolved", installed: "resolvedCounterInstalled", label: "Api_resolveProxy" },
        ],
    },
];

export async function load(url, context, nextLoad) {
    const result = await nextLoad(url, context);
    const path = url.split("?")[0].split("#")[0];
    const module = MODULES.find((m) => m.path.test(path));

    if (module === undefined || result.source == null) {
        return result;
    }

    let source = typeof result.source === "string" ? result.source : new TextDecoder().decode(result.source);

    let head =
        "globalThis.__toolupClientBench ??= { proxiesBuilt: 0, proxiesDeferred: 0, proxiesResolved: 0 };\n" +
        `globalThis.__toolupClientBench.${module.loaded} = true;\n`;

    let tail = "";

    for (const c of module.counters) {
        const names = [...source.matchAll(c.pattern)].map((m) => m[1]);

        if (names.length === 1) {
            const name = names[0];
            source = source.replace(`export function ${name}(`, `function __clientBench_${name}(`);
            tail +=
                `\nexport function ${name}(...args) {\n` +
                `    globalThis.__toolupClientBench.${c.counter}++;\n` +
                `    return __clientBench_${name}(...args);\n` +
                "}\n" +
                `globalThis.__toolupClientBench.${c.installed} = true;\n`;
        } else {
            const problem = `expected exactly one exported ${c.label} function in ${path}, found ${names.length}`;
            tail += `\nglobalThis.__toolupClientBench.counterProblem = ${JSON.stringify(problem)};\n`;
        }
    }

    return { ...result, source: head + source + tail, shortCircuit: true };
}
