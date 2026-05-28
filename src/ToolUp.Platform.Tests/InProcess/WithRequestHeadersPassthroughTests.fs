module ToolUp.Platform.Tests.InProcess.WithRequestHeadersPassthroughTests

open System.IO
open Expecto

// ─── Phase 64 — `UserSession.withRequestHeaders` passthrough contract ──
//
// `UserSession.withRequestHeaders` is the customiser that every `*.Client`
// companion in this SDK passes to `Api.makeProxy<…> (customOptions = …)`.
// Phase 9j (and the Phase 13a `installRequestGuard` follow-on) moved
// identity + CSRF header injection out of this customiser and into the
// send-time `CsrfClient` request-guard, reducing the customiser to a no-op
// passthrough. Phase 64 codified the resulting "module-level proxy +
// send-time guard" convention across `ToolUp.Platform.Client/Client/`
// (see `docs/platform/client-remoting-proxies.md`).
//
// This test pins the customiser's no-op contract: a future PR that
// re-introduces header-splicing into `withRequestHeaders` would silently
// re-introduce the pre-9j build-once / read-per-call freeze for every
// module-level proxy that took the convention's advice. The right place
// to fail loud is here, not at a user-session 401.
//
// The check is textual rather than behavioural because `UserSession.fs`
// lives in the Fable-only client tier (`open Fable.Core.JsInterop`,
// `Browser.Dom`) and cannot be compiled by the .NET Expecto runner. If
// Fable-runnable test infrastructure lands in the future, this test
// should be promoted to a behavioural assertion (`withRequestHeaders
// options |> should equal options`) — but the textual form still pins
// the exact contract the convention doc commits to.

[<Tests>]
let tests =
    testList "Phase 64 — UserSession.withRequestHeaders passthrough" [

        test "withRequestHeaders is the canonical passthrough line" {
            // Anchor relative to this test file so the path resolves
            // regardless of where the runner is invoked from.
            let testFile = System.Reflection.Assembly.GetExecutingAssembly().Location
            // bin/Debug/net10.0/ToolUp.Platform.Tests.dll → repo root
            let repoRoot =
                let assemblyDir = Path.GetDirectoryName testFile
                // …/src/ToolUp.Platform.Tests/bin/Debug/net10.0 → …/
                Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

            let userSessionPath =
                Path.Combine(repoRoot, "src", "ToolUp.Platform.Client", "Client", "UserSession.fs")

            Expect.isTrue (File.Exists userSessionPath) (sprintf "expected UserSession.fs at %s" userSessionPath)

            let contents = File.ReadAllText userSessionPath

            // The canonical Phase 9j passthrough — pinned exactly as it
            // appears in source. A change to the function body (e.g.
            // re-introducing `options |> Remoting.withCustomHeader …`)
            // would fail this assertion immediately.
            let canonicalLine =
                "let withRequestHeaders (options: RemoteBuilderOptions) = options"

            Expect.stringContains
                contents
                canonicalLine
                "UserSession.withRequestHeaders must be the canonical Phase 9j passthrough. \
                 If you are intentionally re-introducing header-splicing here, you must \
                 also revisit the module-level proxy convention in \
                 docs/platform/client-remoting-proxies.md — the per-call workaround \
                 pattern returns the moment this customiser stops being a passthrough."
        }

        test "withRequestHeaders body has no Remoting.withCustomHeader splice (excludes doc-comments)" {
            let testFile = System.Reflection.Assembly.GetExecutingAssembly().Location

            let repoRoot =
                let assemblyDir = Path.GetDirectoryName testFile
                Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

            let userSessionPath =
                Path.Combine(repoRoot, "src", "ToolUp.Platform.Client", "Client", "UserSession.fs")

            // Defence-in-depth: even if a future edit reshuffled the
            // function declaration into a multi-line form that the
            // exact-line check missed, an actual header-splice call
            // would surface here. The Phase 9j docstring legitimately
            // references `Remoting.withCustomHeader` as historical
            // context (lines 321 and 347 as of forge HEAD); the
            // line-by-line scan strips those commented lines first.
            let codeLines =
                File.ReadAllLines userSessionPath
                |> Array.filter (fun line ->
                    let trimmed = line.TrimStart()
                    not (trimmed.StartsWith "//"))

            let splicingLines =
                codeLines
                |> Array.filter (fun line -> line.Contains "Remoting.withCustomHeader")

            Expect.isEmpty
                splicingLines
                "UserSession.fs must not call Remoting.withCustomHeader from code — that's \
                 the snapshot-shaped customiser Phase 9j retired. Header attachment \
                 belongs at the send-time CsrfClient request-guard. (Doc-comment \
                 references to the historical API are allowed and pre-filtered.)"
        }
    ]