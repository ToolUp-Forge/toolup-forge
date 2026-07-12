module MyTemplate.Server

open ToolUp.Platform
open ToolUp.Platform.Server

// ── Sample seed / fixture pack (Phase 447) ──────────────────────────
//
// A module contributes an `ISeedPack`; the SDK applies it once,
// idempotently, at end-of-compose — but ONLY when the composition opts
// in with `ServerApp.withSeedData EnabledSeedData` (below). Uncomment
// both this pack and the two `|>` lines in `main` to boot a fresh
// scaffold with demo data on first run. Packs MUST be deterministic
// (fixed ids/timestamps) so a re-apply after a `Version` bump is
// comparable; the loader guards each pack with an applied-marker blob
// keyed by `Name@Version`, so re-boot is a no-op and a version bump
// re-applies. `EnabledSeedData` refuses startup on a Team / multi-team
// production shape (demo data must not leak into a real tenant) — use
// `ForcedSeedData` to override deliberately.
//
// type DemoSeedPack() =
//     interface ISeedPack with
//         member _.Name = "demo"
//         member _.Version = "1"
//
//         member _.Apply(ctx: SeedContext) =
//             async {
//                 // Seed platform-scoped demo content. A pack has full
//                 // store handles (ctx.EntityStore / ctx.DataObjectStore /
//                 // ctx.BlobStorage) and may target any scope; ctx.ScopeId
//                 // is the SDK's suggested default (`_platform`).
//                 let! _ =
//                     ctx.BlobStorage.Upload(
//                         ctx.ScopeId,
//                         "demo/welcome.json",
//                         System.Text.Encoding.UTF8.GetBytes """{"welcome":true}"""
//                     )
//
//                 return {
//                     PackName = "demo"
//                     Version = "1"
//                     ItemsSeeded = 1
//                     Notes = [ "welcome.json" ]
//                 }
//             }

// Phase 11.G — env-driven composition shape. `ConsoleLogger.fromEnv`
// reads `TOOLUP_LOG_LEVEL` / `TOOLUP_TRACE_CATEGORIES`;
// `ServerConfig.fromEnv` honours the full `TOOLUP_*` env-var contract
// documented in toolup-forge/docs/platform/composition-roots.md. To
// switch from Anonymous to Individual / Team mode at runtime, set
// `TOOLUP_PLATFORM_MODE=individual` (etc.); the override record stays
// empty unless the deployment opts into the reference-app posture
// (`ServerConfigOverrides.referenceApp` — webhooks, audit, default
// hardening).
[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.empty

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    // Uncomment to seed demo data on first boot (see DemoSeedPack above):
    // |> ServerApp.withSeedData EnabledSeedData
    // |> ServerApp.withSeedPack (DemoSeedPack())
    |> ServerApp.run