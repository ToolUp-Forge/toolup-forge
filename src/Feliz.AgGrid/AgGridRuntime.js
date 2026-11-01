// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
//
// Phase 1200 — the path Fable spells for the on-demand import resolves where a
// consumer restores this package from a feed.
//
// `importValueDynamic` of a member of this module compiles to
// `import("./AgGridRuntime.js")`: Fable 5 spells a dynamic import from the project's
// output extension, which is `.js` whenever the consumer passes `--outDir`. A
// project-reference build writes this module as `AgGridRuntime.js`, so the path
// resolves and this file is never reached. A FEED consumer compiles the same
// source under `fable_modules/`, where Fable keeps the default `.fs.js` for
// package files, so the spelled path named nothing and Vite refused the bundle
// (`Could not resolve './AgGridRuntime.js'`). This file ships in the package's
// `fable/` payload beside the source and re-exports the compiled module, so
// the chunk Phase 1168 designed loads on demand from a feed as it does from a
// project reference.
export * from "./AgGridRuntime.fs.js";
