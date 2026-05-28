module ToolUp.Platform.Tests.InProcess.CsrfPrefetchAnonymousGateTests

open System.IO
open Expecto

// ─── CSRF prefetch must be skipped in anonymous-only client deployments ──
//
// `SDK.Client.installRequestSeam` originally called `CsrfClient.prefetch ()`
// unconditionally as part of every composed app's boot. In an anonymous-
// only deployment (`Surfaces = [ SurfaceProfile.anonymous ]`) the server
// doesn't mount the `/api/csrf-token` route (no sessions → no per-session
// CSRF token), so the prefetch surfaces as a `GET /api/csrf-token → 404`
// in DevTools plus a request-guard "boot prefetch normally wins" warn-once
// log on every boot. The 404 is benign — the cache stays empty, the
// no-token branch of the request guard is the correct shape for state-
// changing anonymous requests — but the call is dead code on that path
// and the console noise drowns out real defects in anonymous-only
// apps.
//
// Phase 66 Stream B.8 — the gate now reads `ClientConfig.requiresAnyAuth`
// and skips prefetch when no authenticated surface is declared; mixed-mode
// deployments with an authenticated surface alongside the anonymous one
// still prefetch (the authenticated path needs the token). CSRF-enabled
// apps with an authenticated surface continue to prefetch on
// boot. The request-guard install above the prefetch stays
// shape-agnostic — it's the correct shape for any anonymous request that
// happens to be state-changing.
//
// The check is textual rather than behavioural because `SDK.Client.fs`
// lives in the Fable-only client tier (`open Fable.Core.JsInterop`,
// React DOM bindings, Elmish HMR) and cannot be compiled by the .NET
// Expecto runner. See the sibling `WithRequestHeadersPassthroughTests`
// for the established precedent. If Fable-runnable test infrastructure
// lands in the future, this test should be promoted to a behavioural
// assertion against a `prefetch` call-counter; the textual form pins
// the exact gate shape today.

[<Tests>]
let tests =
    testList "CSRF prefetch — anonymous-only gate" [

        test "installRequestSeam gates CsrfClient.prefetch on ClientConfig.requiresAnyAuth" {
            let testFile = System.Reflection.Assembly.GetExecutingAssembly().Location

            let repoRoot =
                let assemblyDir = Path.GetDirectoryName testFile
                // …/src/ToolUp.Platform.Tests/bin/Debug/net10.0 → …/
                Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

            let sdkClientPath =
                Path.Combine(repoRoot, "src", "ToolUp.Platform.Client", "Client", "SDK.Client.fs")

            Expect.isTrue (File.Exists sdkClientPath) (sprintf "expected SDK.Client.fs at %s" sdkClientPath)

            // Normalise CRLF → LF so the substring pattern below
            // (authored with `\n` line breaks) matches regardless of
            // the source file's line-ending convention.
            let contents = (File.ReadAllText sdkClientPath).Replace("\r\n", "\n")

            // The canonical gate — pinned exactly as it appears in source.
            // A future edit that removes the `requiresAnyAuth` gate (or
            // re-introduces an unconditional `CsrfClient.prefetch ()` above
            // the gate) will fail this assertion immediately.
            let canonicalGate =
                "if ClientConfig.requiresAnyAuth config then\n            CsrfClient.prefetch ()"

            Expect.stringContains
                contents
                canonicalGate
                "installRequestSeam must gate CsrfClient.prefetch on ClientConfig.requiresAnyAuth. \
                 Anonymous-only deployments have no sessions and the server does not mount /api/csrf-token, \
                 so the unconditional prefetch surfaces as a benign-but-noisy 404 plus a \
                 request-guard warn-once on every boot."
        }

        test "SDK.Client.fs body has exactly one CsrfClient.prefetch invocation (excludes doc-comments)" {
            let testFile = System.Reflection.Assembly.GetExecutingAssembly().Location

            let repoRoot =
                let assemblyDir = Path.GetDirectoryName testFile
                Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

            let sdkClientPath =
                Path.Combine(repoRoot, "src", "ToolUp.Platform.Client", "Client", "SDK.Client.fs")

            // Defence-in-depth: even if a future edit reshuffled the gate
            // into a form the exact-line check missed, an unguarded second
            // call site would surface here. The Phase 9j / 13a docstrings
            // legitimately reference `CsrfClient.prefetch` as historical
            // context; the line-by-line scan strips those commented lines
            // first.
            let codeLines =
                File.ReadAllLines sdkClientPath
                |> Array.filter (fun line ->
                    let trimmed = line.TrimStart()
                    not (trimmed.StartsWith "//"))

            let prefetchCalls =
                codeLines |> Array.filter (fun line -> line.Contains "CsrfClient.prefetch ()")

            Expect.equal
                prefetchCalls.Length
                1
                "SDK.Client.fs must contain exactly one CsrfClient.prefetch () call — the \
                 one inside the requiresAnyAuth gate in installRequestSeam. A second call \
                 site (or an unguarded one above the gate) re-introduces the dead \
                 /api/csrf-token request in anonymous-only deployments that the gate exists to suppress."
        }
    ]