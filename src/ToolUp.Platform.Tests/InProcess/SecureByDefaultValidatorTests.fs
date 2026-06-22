module ToolUp.Platform.Tests.InProcess.SecureByDefaultValidatorTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 129 — secure-by-default refusals / warnings ──────────────

let private run (v: IConfigValidator) : ValidationResult = v.Validate() |> Async.RunSynchronously

// ── Phase 230 — AutoBootstrapDevAdmin: Error unless the explicit opt-in
//    env var is set (closes the proxy-production gap where RequireHttps is
//    false). Warning only on a deliberate local auth-dev opt-in. ──
let private devAdminTests =
    let validator cfg =
        AutoBootstrapDevAdminModeValidator.AutoBootstrapDevAdminModeValidator(cfg) :> IConfigValidator

    // The opt-in is a process-global env var — set/restore around each case
    // and sequence the block so a sibling case never sees a stale value.
    let withOptIn (value: string option) (f: unit -> 'a) : 'a =
        let key = PlatformAdminStore.allowDevAdminBootstrapEnvVar
        let prior = Environment.GetEnvironmentVariable key
        Environment.SetEnvironmentVariable(key, Option.toObj value)

        try
            f ()
        finally
            Environment.SetEnvironmentVariable(key, prior)

    testSequenced
    <| testList "Phase 230 — AutoBootstrapDevAdmin escalation guard" [
        test "auth + dev-admin set + opt-in UNSET → Error (incl. behind a TLS proxy)" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    AutoBootstrapDevAdmin = Some "dev-1"
            }

            withOptIn None (fun () ->
                match run (validator cfg) with
                | Error _ -> ()
                | other -> failtestf "expected Error when the opt-in is unset, got %A" other)
        }

        test "auth + dev-admin set + RequireHttps + opt-in UNSET → Error" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = true
                    AutoBootstrapDevAdmin = Some "dev-1"
            }

            withOptIn None (fun () ->
                match run (validator cfg) with
                | Error _ -> ()
                | other -> failtestf "expected Error on the production shape, got %A" other)
        }

        test "auth + dev-admin set + opt-in SET → Warning (deliberate local auth-dev)" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    AutoBootstrapDevAdmin = Some "dev-1"
            }

            withOptIn (Some "1") (fun () ->
                match run (validator cfg) with
                | Warning _ -> ()
                | other -> failtestf "expected Warning when the opt-in is set, got %A" other)
        }

        test "dev-admin unset → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = true
            }

            withOptIn None (fun () -> Expect.equal (run (validator cfg)) Ok "no dev-admin → no finding")
        }
    ]

// ── 129c/129d — CSRF off-by-default under cookie auth (Error + opt-out) ──
let private csrfDefaultTests =
    let validator cfg =
        CsrfDefaultModeValidator.CsrfDefaultModeValidator(cfg) :> IConfigValidator

    testList "Phase 129d — CSRF default mode" [
        test "auth + CookieRequired + NoSecurityHardening → Error (refuse startup)" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = CookieRequired
                    SecurityHardening = NoSecurityHardening
            }

            match run (validator cfg) with
            | Error msg ->
                Expect.stringContains
                    msg
                    "AcceptSameSiteOnlyCsrfWhenAuthRequired"
                    "names the typed acknowledged-downgrade escape hatch"
            | other -> failtestf "expected CSRF-exposure Error, got %A" other
        }

        test "auth + CookieRequired + NoSecurityHardening + acknowledged → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = CookieRequired
                    SecurityHardening = NoSecurityHardening
                    AcceptSameSiteOnlyCsrfWhenAuthRequired = true
            }

            Expect.equal
                (run (validator cfg))
                Ok
                "explicit SameSite-only acknowledgement → conscious downgrade, no refusal"
        }

        test "auth + CookieRequired + DefaultSecurityHardening → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = CookieRequired
                    SecurityHardening = DefaultSecurityHardening
            }

            Expect.equal (run (validator cfg)) Ok "hardening enabled → no exposure"
        }

        test "auth + QueryParamFallback (not cookie auth) → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = QueryParamFallback
                    SecurityHardening = NoSecurityHardening
            }

            Expect.equal (run (validator cfg)) Ok "no cookie auth → no CSRF exposure here"
        }

        test "anonymous + CookieRequired + NoSecurityHardening → Ok (no auth surface)" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.anonymous
                    SseAuthMode = CookieRequired
                    SecurityHardening = NoSecurityHardening
            }

            Expect.equal (run (validator cfg)) Ok "no authenticated surface → gatedAuthValidation short-circuits"
        }
    ]

// ── 129b — OAuth redirect-base: Error in auth modes, Warning anonymous ──
let private withRedirectBaseUnset (body: unit -> unit) =
    let saved = Environment.GetEnvironmentVariable "TOOLUP_OAUTH_REDIRECT_BASE"

    try
        Environment.SetEnvironmentVariable("TOOLUP_OAUTH_REDIRECT_BASE", null)
        body ()
    finally
        Environment.SetEnvironmentVariable("TOOLUP_OAUTH_REDIRECT_BASE", saved)

let private redirectBaseTests =
    let validator cfg =
        OAuthFlowValidator.OAuthFlowValidator(cfg) :> IConfigValidator

    testSequenced
    <| testList "Phase 129b — OAuth redirect-base" [
        test "unset + auth mode → Error" {
            withRedirectBaseUnset (fun () ->
                let cfg = {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.individual
                }

                match run (validator cfg) with
                | Error _ -> ()
                | other -> failtestf "expected Error (host-poisoning) in auth mode, got %A" other)
        }

        test "unset + anonymous mode → Warning" {
            withRedirectBaseUnset (fun () ->
                let cfg = {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.anonymous
                }

                match run (validator cfg) with
                | Warning _ -> ()
                | other -> failtestf "expected Warning in anonymous mode, got %A" other)
        }
    ]

// ── Phase 230 — the bootstrap itself fails closed: in an auth-requiring
//    deployment the AutoBootstrapDevAdmin fallback elevates only when the
//    explicit opt-in is set. Proves the runtime refusal, not just the
//    preflight signal. ──
let private bootstrapTests =
    let silentLogger =
        { new ILogger with
            member _.Debug _ = ()
            member _.Info _ = ()
            member _.Warn _ = ()
            member _.Error(_, _) = ()
        }

    let noOpAudit =
        { new IAuditLog with
            member _.Record(_, _) = async { return () }
            member _.GetAuditTrail(_, _, _) = async { return [] }
        }

    let mkStore () : IPlatformAdminStore =
        let storage = InMemoryBlobStorage() :> IBlobStorage
        PlatformAdminStore.BlobBackedPlatformAdminStore(storage, noOpAudit) :> _

    // Both the env-path admin and the opt-in are process-global — snapshot
    // and restore around each case, and sequence the block.
    let withEnv (initialAdmin: string option) (optIn: string option) (f: unit -> unit) =
        let k1 = "TOOLUP_INITIAL_PLATFORM_ADMIN"
        let k2 = PlatformAdminStore.allowDevAdminBootstrapEnvVar
        let p1 = Environment.GetEnvironmentVariable k1
        let p2 = Environment.GetEnvironmentVariable k2
        Environment.SetEnvironmentVariable(k1, Option.toObj initialAdmin)
        Environment.SetEnvironmentVariable(k2, Option.toObj optIn)

        try
            f ()
        finally
            Environment.SetEnvironmentVariable(k1, p1)
            Environment.SetEnvironmentVariable(k2, p2)

    let adminsAfterBootstrap (requiresAuth: bool) (initialAdmin: string option) (optIn: string option) =
        let store = mkStore ()

        withEnv initialAdmin optIn (fun () ->
            PlatformAdminStore.bootstrap silentLogger (Some "dev-1") requiresAuth store
            |> Async.RunSynchronously)

        store.ListPlatformAdmins() |> Async.RunSynchronously

    testSequenced
    <| testList "Phase 230 — bootstrap dev-admin fail-closed" [
        test "auth + dev-admin + opt-in UNSET → refuses (no elevation)" {
            Expect.isEmpty (adminsAfterBootstrap true None None) "no elevation when the opt-in is unset in auth mode"
        }

        test "auth + dev-admin + opt-in SET → elevates" {
            Expect.equal (adminsAfterBootstrap true None (Some "1")) [ "dev-1" ] "elevates with the deliberate opt-in"
        }

        test "non-auth (anonymous) + dev-admin → elevates (no opt-in needed)" {
            Expect.equal (adminsAfterBootstrap false None None) [ "dev-1" ] "anonymous dev needs no opt-in"
        }

        test "env-path admin always elevates (priority 1, ungated)" {
            Expect.equal
                (adminsAfterBootstrap true (Some "env-admin") None)
                [ "env-admin" ]
                "TOOLUP_INITIAL_PLATFORM_ADMIN is never gated"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 129 — secure-by-default" [ devAdminTests; bootstrapTests; csrfDefaultTests; redirectBaseTests ]