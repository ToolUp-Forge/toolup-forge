// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
//
// The path Fable spells for the on-demand import of the tree renderer resolves
// where a consumer restores this package from a feed.
//
// `importValueDynamic` of a member of this module compiles to
// `import("./TreeLoweringRuntime.js")`: Fable spells a dynamic import from the
// project's output extension, which is `.js` whenever the consumer passes
// `--outDir`. A project-reference build writes this module as
// `TreeLoweringRuntime.js`, so the path resolves and this file is never reached.
// A FEED consumer compiles the same source under `fable_modules/`, where Fable
// keeps the default `.fs.js` for package files. This file ships in the
// package's `fable/` payload beside the source and re-exports the compiled
// module, so the renderer chunk loads on demand from a feed as it does from a
// project reference.
export * from "./TreeLoweringRuntime.fs.js";
