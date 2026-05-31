module ToolUp.Platform.Tests.InProcess.SubjectKindClientFlowTests

open System.IO
open Expecto

// ─── Phase 66 Stream B.8 — Client identity flow keyed on SubjectKind ────
//
// Pins the textual shape of the four files Stream B.8 retired the
// `ClientConfig.Mode: PlatformMode` field from in favour of
// `ClientConfig.Surfaces: SurfaceProfile list` + a resolved `SubjectKind`
// per render:
//
//   * `Client/UserSession.fs` — `configure` takes `SubjectKind`;
//     `getSubjectKind` is the read accessor; storage selection
//     branches on `AnonymousKind` vs the authenticated kinds; identity
//     header pairs follow the same shape.
//   * `Client/AuthUIProvider.fs` — `gate` takes `SubjectKind`;
//     `AnonymousKind` and `ClaimBearerKind` always pass through
//     (no sign-in mount); `UserKind` / `TeamMemberKind` reach the
//     handler-registry lookup the same way the retiring per-mode
//     match did.
//   * `Client/SDK.ClientTypes.fs` — `ClientConfig.Surfaces` field
//     replaces `Mode`; defaults to `Surfaces.anonymous`; helper
//     module exposes `resolveSubjectKind`, `requiresAnyAuth`,
//     `hasTeamScope`, `hasMultiTeamSwitcher`, and `isClaimBearerOnly`.
//   * `Client/ClientConfigFromBundleConstants.fs` — the override
//     record carries `Surfaces: SurfaceProfile list option` (not
//     `Mode`); `fromBundleConstantValues` parses
//     `__TOOLUP_PLATFORM_SURFACES__` into a `SurfaceProfile list`
//     (mirrors the server-side `TOOLUP_PLATFORM_SURFACES` env var
//     resolution).
//
// Regression pin — the anonymous-only pathology: a deployment whose
// `Surfaces` is `[ SurfaceProfile.anonymous ]` must NOT mount the
// sign-in flow. The `AuthUIProvider.gate`'s leading `AnonymousKind, _`
// arm carries that guarantee; this test pack pins it.
//
// The checks are textual rather than behavioural because all four
// files live in the Fable-only client tier (`open Fable.Core.JsInterop`,
// `Browser.Dom`, Feliz bindings) and cannot be compiled by the .NET
// Expecto runner. Same pattern as the sibling
// `CsrfPrefetchAnonymousGateTests` and `WithRequestHeadersPassthroughTests`.
// If Fable-runnable test infrastructure lands in the future, these
// should be promoted to behavioural assertions against
// `UserSession.configure` / `AuthUIProvider.gate` / `ClientConfig.defaults`;
// the textual form pins the contract today.

let private repoRoot () =
    let testFile = System.Reflection.Assembly.GetExecutingAssembly().Location

    let assemblyDir = Path.GetDirectoryName testFile
    // …/src/ToolUp.Platform.Tests/bin/Debug/net10.0 → …/
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private clientFile (name: string) =
    Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", name)

let private readClient (name: string) =
    let path = clientFile name
    Expect.isTrue (File.Exists path) (sprintf "expected %s at %s" name path)
    // Normalise CRLF to LF so substring patterns authored with `\n`
    // line breaks match regardless of the source file's line-ending
    // convention. Forge files canonicalise as CRLF on Windows; tests
    // are git-ignored from text-normalisation by checkout.
    (File.ReadAllText path).Replace("\r\n", "\n")

let private codeLines (contents: string) =
    contents.Split([| '\n' |])
    |> Array.map (fun line -> line.TrimEnd('\r'))
    |> Array.filter (fun line ->
        let trimmed = line.TrimStart()
        not (trimmed.StartsWith "//"))

[<Tests>]
let tests =
    testList "Phase 66 Stream B.8 — client identity flow on SubjectKind" [

        // ─── UserSession.fs ─────────────────────────────────────────

        test "UserSession.configure takes SubjectKind" {
            let contents = readClient "UserSession.fs"

            Expect.stringContains
                contents
                "let configure (kind: SubjectKind) = currentSubjectKind <- kind"
                "UserSession.configure must accept the resolved SubjectKind, not the \
                 retiring PlatformMode. Phase 66 Stream B.8 retired the per-deployment \
                 Mode in favour of per-render SubjectKind resolution."
        }

        test "UserSession.getSubjectKind is the read accessor" {
            let contents = readClient "UserSession.fs"

            Expect.stringContains
                contents
                "let getSubjectKind () = currentSubjectKind"
                "UserSession.getSubjectKind replaces the retired getMode (). \
                 Phase 66 Stream B.8 — the KnowledgeBase AI-context panel \
                 (and any future consumer) reads through this accessor."
        }

        test "UserSession.fs source has no PlatformMode reader" {
            let contents = readClient "UserSession.fs"
            let lines = codeLines contents

            // The previous `"= Anonymous"` arm false-positives on
            // `currentSubjectKind = AnonymousKind` — `Anonymous` is a
            // prefix of `AnonymousKind`. The `currentMode` /
            // `PlatformMode` arms cover the retired-shape readers
            // unambiguously; the `match ... Mode` arm catches the
            // pre-B.8 `match currentMode with` pattern without
            // colliding with `SubjectKind`-related identifiers.
            let modeReaders =
                lines
                |> Array.filter (fun line ->
                    line.Contains "currentMode"
                    || line.Contains "PlatformMode"
                    || (line.Contains "match" && line.Contains " Mode"))

            Expect.isEmpty
                modeReaders
                "UserSession.fs must not read PlatformMode in any form post-B.8. \
                 Storage selection + identity header pairs must branch on \
                 SubjectKind only. Doc comments referencing the historical \
                 PlatformMode shape are allowed (pre-filtered)."
        }

        test "UserSession.identityHeaderPairs branches on AnonymousKind" {
            let contents = readClient "UserSession.fs"

            Expect.stringContains
                contents
                "match currentSubjectKind with\n    | AnonymousKind -> [| userIdHeader, getUserId () |]"
                "identityHeaderPairs must use AnonymousKind for the X-User-Id \
                 path. Authenticated kinds (UserKind / TeamMemberKind / \
                 ClaimBearerKind) fall through to the Authorization: Bearer \
                 form when a token is available."
        }

        // ─── AuthUIProvider.fs ──────────────────────────────────────

        test "AuthUIProvider.gate takes SubjectKind" {
            let contents = readClient "AuthUIProvider.fs"

            Expect.stringContains
                contents
                "let gate (authUI: AuthUIMode) (subjectKind: SubjectKind) (shell: ReactElement) : ReactElement ="
                "AuthUIProvider.gate must accept SubjectKind, not PlatformMode. \
                 Phase 66 Stream B.8 — the sign-in UI mount decision is now \
                 per-subject-kind (anonymous + claim-bearer always pass through; \
                 user / team subject kinds reach the handler registry)."
        }

        test "AuthUIProvider.gate leading arm is AnonymousKind pass-through (anonymous-only regression pin)" {
            let contents = readClient "AuthUIProvider.fs"

            // The exact match-arm shape — pinned so a future PR that
            // reshuffles the arms doesn't accidentally cost the
            // anonymous-only deployment its no-sign-in-mount guarantee.
            Expect.stringContains
                contents
                "match subjectKind, authUI with\n        | AnonymousKind, _ -> shell\n        | ClaimBearerKind, _ -> shell"
                "AuthUIProvider.gate must leave AnonymousKind and ClaimBearerKind \
                 as the leading pass-through arms. The anonymous-only pathology \
                 (anonymous-only deployment forced to mount a sign-in flow) \
                 returns the moment either arm is removed or reordered after a \
                 catch-all."
        }

        // ─── SDK.ClientTypes.fs (ClientConfig record + module) ──────

        test "ClientConfig record carries Surfaces, not Mode" {
            let contents = readClient "SDK.ClientTypes.fs"

            Expect.stringContains
                contents
                "Surfaces: SurfaceProfile list"
                "ClientConfig must declare Surfaces: SurfaceProfile list. \
                 Phase 66 Stream B.8 — the per-deployment Mode flag becomes \
                 a per-shape list, mirroring ServerConfig.Surfaces."

            let lines = codeLines contents

            let modeField =
                lines
                |> Array.filter (fun line ->
                    let t = line.TrimStart()
                    t.StartsWith "Mode:" || t.StartsWith "Mode =")

            Expect.isEmpty
                modeField
                "ClientConfig must not carry a Mode: PlatformMode field or a \
                 `Mode = ...` defaults entry post-B.8. Doc-comment references \
                 are pre-filtered."
        }

        test "ClientConfig.defaults seeds Surfaces with the anonymous single-shape list" {
            let contents = readClient "SDK.ClientTypes.fs"

            Expect.stringContains
                contents
                "Surfaces = Surfaces.anonymous"
                "ClientConfig.defaults must seed Surfaces with Surfaces.anonymous \
                 — the byte-for-byte equivalent of the pre-B.8 `Mode = Anonymous` \
                 default. Consumers override per deployment."
        }

        test "ClientConfig module exposes the SubjectKind / surface predicates" {
            let contents = readClient "SDK.ClientTypes.fs"

            for needle in
                [
                    "let hasAnonymous (config: ClientConfig)"
                    "let requiresAnyAuth (config: ClientConfig)"
                    "let hasTeamScope (config: ClientConfig)"
                    "let hasMultiTeamSwitcher (config: ClientConfig)"
                    "let isClaimBearerOnly (config: ClientConfig)"
                    "let resolveSubjectKind (activeTeamId: string option) (config: ClientConfig) : SubjectKind"
                ] do
                Expect.stringContains
                    contents
                    needle
                    (sprintf
                        "ClientConfig module must expose `%s` — SDK.Client.fs derives \
                         the boot-time SubjectKind + every built-in-module gate from \
                         these helpers."
                        needle)
        }

        // ─── ClientConfigFromBundleConstants.fs ─────────────────────

        test "ClientConfigOverrides carries Surfaces, not Mode" {
            let contents = readClient "ClientConfigFromBundleConstants.fs"

            Expect.stringContains
                contents
                "Surfaces: SurfaceProfile list option"
                "ClientConfigOverrides must carry Surfaces: SurfaceProfile list \
                 option. Phase 66 Stream B.8 / Stream A.8 client-side cut-over."

            let lines = codeLines contents

            let modeField =
                lines
                |> Array.filter (fun line ->
                    let t = line.TrimStart()
                    t.StartsWith "Mode:" || t.StartsWith "Mode =")

            Expect.isEmpty
                modeField
                "ClientConfigOverrides must not carry Mode: PlatformMode option \
                 post-B.8 (clean cutover per design D13). Doc-comment references \
                 are pre-filtered."
        }

        test "fromBundleConstantValues reads platformSurfaces and resolves into Surfaces" {
            let contents = readClient "ClientConfigFromBundleConstants.fs"

            Expect.stringContains
                contents
                "(platformSurfaces: string)"
                "fromBundleConstantValues must accept the platformSurfaces string \
                 from the Vite __TOOLUP_PLATFORM_SURFACES__ define — the client-side \
                 counterpart of Phase 66 Stream A.8's TOOLUP_PLATFORM_SURFACES env \
                 var resolution."

            Expect.stringContains
                contents
                "Surfaces = resolvedSurfaces"
                "fromBundleConstantValues must wire the resolved surfaces into \
                 the constructed ClientConfig. `overrides.Surfaces` wins when \
                 `Some _`; else the parsed bundle constant; else \
                 `Surfaces.anonymous` (warn-on-empty-or-bad-token semantics \
                 mirror the server side)."
        }

        test "BundleConstants exposes the platformSurfaces define accessor" {
            let contents = readClient "BundleConstants.fs"

            Expect.stringContains
                contents
                "let platformSurfaces: string = jsNative"
                "BundleConstants must expose `let platformSurfaces` so \
                 `fromBundleConstants` can read the `__TOOLUP_PLATFORM_SURFACES__` \
                 Vite define without re-declaring the [<Emit>] in every consumer."

            Expect.stringContains
                contents
                "__TOOLUP_PLATFORM_SURFACES__"
                "BundleConstants must reference the canonical __TOOLUP_PLATFORM_SURFACES__ \
                 Vite-define name (matching the server-side env var)."
        }

        // ─── SDK.Client.fs sweep — no live config.Mode readers ──────

        test "SDK.Client.fs has no live config.Mode readers (excludes doc-comments)" {
            let contents = readClient "SDK.Client.fs"
            let lines = codeLines contents

            let liveModeReaders =
                lines
                |> Array.filter (fun line -> line.Contains "config.Mode" || line.Contains "_config.Mode")

            Expect.isEmpty
                liveModeReaders
                "SDK.Client.fs must not read config.Mode / _config.Mode in any \
                 live code path post-B.8. Sidebar visibility, auth-UI mount, \
                 CSRF prefetch, and SDK built-in module gates must derive from \
                 ClientConfig.Surfaces via the canonical predicates \
                 (resolveSubjectKind / requiresAnyAuth / hasTeamScope / \
                 hasMultiTeamSwitcher). Doc-comment references to the historical \
                 shape are pre-filtered."
        }
    ]