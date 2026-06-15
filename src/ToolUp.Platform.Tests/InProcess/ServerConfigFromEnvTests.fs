module ToolUp.Platform.Tests.InProcess.ServerConfigFromEnvTests

// Phase 71.A — runtime-resolvable configuration lifts on the
// `ServerConfig.fromEnv` seam. Env vars are process-global, so the whole
// list is `testSequenced` and every case snapshots + restores what it
// touches (mirrors the AuthProvider.fromEnv test convention).

open System
open Expecto
open ToolUp.Platform

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Logger that captures Warn lines so the PublicBaseUrl warnings can be asserted.
let private capturingLogger () =
    let warnings = ResizeArray<string>()

    let logger =
        { new ILogger with
            member _.Debug _ = ()
            member _.Info _ = ()
            member _.Warn m = warnings.Add m
            member _.Error(_, _) = ()
        }

    logger, warnings

let private withEnv (pairs: (string * string option) list) (body: unit -> unit) =
    let priors =
        pairs |> List.map (fun (n, _) -> n, Environment.GetEnvironmentVariable n)

    try
        for n, v in pairs do
            Environment.SetEnvironmentVariable(n, v |> Option.toObj)

        body ()
    finally
        for n, prior in priors do
            Environment.SetEnvironmentVariable(n, prior)

/// The six `Accept*` flags whose documented env vars `fromEnv` never read
/// before Phase 71.A.2, paired with their projection out of `ServerConfig`.
let private sixAcceptFlags: (string * (ServerConfig -> bool)) list = [
    "TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE", _.AcceptInProcessIngestionInMultiInstance
    "TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE", _.AcceptSharedEmbeddingCacheInTeamMode
    "TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE", _.AcceptStickyRoutedAiInMultiInstance
    "TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE", _.AcceptUnboundAudienceWhenAuthRequired
    "TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE", _.AcceptInMemoryOAuthStateInMultiInstance
    "TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE", _.AcceptPendingInviteStoreInMultiInstance
]

let tests =
    testSequenced (
        testList "ServerConfig.fromEnv (Phase 71.A)" [
            // ── 71.A.1 — Surfaces precedence inversion (regression pin) ──
            test "Surfaces: TOOLUP_PLATFORM_SURFACES wins over referenceApp override" {
                withEnv [ "TOOLUP_PLATFORM_SURFACES", Some "multi_team" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.referenceApp

                    Expect.equal
                        cfg.Surfaces
                        [ SurfaceProfile.multiTeam ]
                        "env var must win over the library-default override")
            }

            test "Surfaces: unset env falls back to the referenceApp override (byte-compat)" {
                withEnv [ "TOOLUP_PLATFORM_SURFACES", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.referenceApp

                    Expect.equal
                        cfg.Surfaces
                        Surfaces.individual
                        "unset env must preserve the prior Individual posture")
            }

            // ── 71.A.2 — six Accept* flag reads ──
            test "Accept*: each of the six env vars set to 1 lands true" {
                withEnv (sixAcceptFlags |> List.map (fun (n, _) -> n, Some "1")) (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                    for envName, project in sixAcceptFlags do
                        Expect.isTrue (project cfg) $"{envName}=1 must resolve true")
            }

            test "Accept*: each of the six env vars unset stays false" {
                withEnv (sixAcceptFlags |> List.map (fun (n, _) -> n, None)) (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                    for envName, project in sixAcceptFlags do
                        Expect.isFalse (project cfg) $"{envName} unset must stay false (GP 11)")
            }

            // ── 71.A.3 — Port read inside fromEnv ──
            test "Port: SERVER_PORT is read inside the fromEnv seam" {
                withEnv [ "SERVER_PORT", Some "8137" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.Port 8137 "SERVER_PORT must land on ServerConfig.Port")
            }

            test "Port: an out-of-range SERVER_PORT fails loud" {
                withEnv [ "SERVER_PORT", Some "abc" ] (fun () ->
                    Expect.throws
                        (fun () -> ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty |> ignore)
                        "a non-integer SERVER_PORT must fail loud at fromEnv")
            }

            // ── 71.A.4 — PublicBaseUrl runtime resolution ──
            test "PublicBaseUrl: a trailing slash is stripped (idempotent) + warns" {
                withEnv [ "TOOLUP_PUBLIC_BASE_URL", Some "https://surveys.example.com/" ] (fun () ->
                    let logger, warnings = capturingLogger ()
                    let cfg = ServerConfig.fromEnv logger ServerConfigOverrides.empty

                    Expect.equal
                        cfg.PublicBaseUrl
                        (Some "https://surveys.example.com")
                        "trailing slash must be stripped"

                    Expect.isTrue (warnings |> Seq.exists (fun w -> w.Contains "trailing slash")) "stripping must warn")
            }

            test "PublicBaseUrl: an empty/whitespace value resolves None + warns" {
                withEnv [ "TOOLUP_PUBLIC_BASE_URL", Some "   " ] (fun () ->
                    let logger, warnings = capturingLogger ()
                    let cfg = ServerConfig.fromEnv logger ServerConfigOverrides.empty
                    Expect.isNone cfg.PublicBaseUrl "ambiguous empty value must resolve None"
                    Expect.isTrue (warnings.Count > 0) "the ambiguous empty value must warn")
            }

            test "PublicBaseUrl: unset resolves None (default)" {
                withEnv [ "TOOLUP_PUBLIC_BASE_URL", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.isNone cfg.PublicBaseUrl "unset must preserve the None default")
            }
        ]
    )