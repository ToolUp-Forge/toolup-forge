module MODULE_NAMESPACE_ROOT.Icons

open Fable.Core.JsInterop
open Fable.React
open ToolUp.Platform

// ─── Module icon ─────────────────────────────────────────────────
//
// `importDefault` resolves at the CONSUMER's bundle time via
// vite-plugin-svgr's `?react` query, against the `.svg` packed
// alongside this file under `fable/icons/`. The packaged layout
// contract declares that asset in `RequiredAssets`, so a pack that
// forgets it fails the build here rather than rendering a missing
// icon in someone else's app.

/// The module's sidebar icon. A FUNCTION rather than a value, on
/// purpose: `importDefault` is Fable-only, and this file is also
/// compiled into the .NET assembly. A module-level `let icon =
/// Icon.ofImport …` would run at type initialisation and throw the
/// moment any .NET consumer — including this repo's own conformance
/// test — touched the module. Deferring it costs nothing in the
/// browser, where it is called once during registration.
let moduleIcon () : ReactElement =
    Icon.ofImport (importDefault "./icons/module-icon.svg?react")