module ToolUp.Platform.Tests.InProcess.CorsCredentialsWildcardBootTests

open Expecto
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open ToolUp.Platform
open ToolUp.Platform.ComposeRuntimeServices
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ConfigValidatorAggregator

// ─── Phase 462 — CORS credentials × wildcard: refuse at boot ─────────
//
// `registerCors` used to detect `AllowCredentials = true` + wildcard
// origins, log a single `Warn`, and register the policy with credentials
// silently dropped. `CorsConfigValidator` would have refused the same
// config — but preflight runs at the compose tail, so the downgrade had
// already happened and the operator met the misconfiguration as runtime
// 401s on credentialed cross-origin / SSE flows.
//
// Three properties are pinned here, one per phase task:
//
//   A. the conflict raises BEFORE any policy is registered, with an
//      actionable message, and the preflight validator agrees with the
//      pre-registration refusal (both read the same pure check);
//   B. a wildcard policy states `SupportsCredentials = false`
//      explicitly rather than inheriting ASP.NET's implicit default;
//   C. a legal explicit-origins + credentials config is unaffected —
//      it boots and the policy really does carry credentials (GP 11).

/// Captures `Error` lines so the refusal's operator-facing message can be
/// asserted on the log path as well as on the exception.
type private RecordingLogger() =
    let errors = ResizeArray<string>()
    member _.Errors = List.ofSeq errors

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(message, _) = errors.Add message

let private logger () = RecordingLogger()

let private withCors (cors: CorsConfig) = {
    ServerConfig.defaults with
        Cors = Some cors
}

let private isEmptyCollection (services: ServiceCollection) =
    Seq.isEmpty (services :> seq<ServiceDescriptor>)

/// The policy `registerCors` actually hands ASP.NET — resolved the same
/// way the CORS middleware resolves it at request time.
let private registeredPolicy (config: ServerConfig) : CorsPolicy =
    let services = ServiceCollection()
    registerCors services config (logger () :> ILogger)

    use provider = services.BuildServiceProvider()
    let options = provider.GetRequiredService<IOptions<CorsOptions>>().Value
    options.GetPolicy options.DefaultPolicyName

let tests =
    testList "CorsCredentialsWildcardBoot" [
        // ── A — the refusal ──────────────────────────────────────────
        testCase "AllowCredentials + wildcard origins refuses before any policy is registered"
        <| fun _ ->
            let config =
                withCors {
                    Origins = [ "*" ]
                    Methods = [ "*" ]
                    Headers = [ "*" ]
                    AllowCredentials = true
                }

            let log = logger ()
            let services = ServiceCollection()

            let message =
                try
                    assertCorsCredentialsCompatible config (log :> ILogger)
                    failtest "credentials + wildcard must abort startup, not downgrade silently"
                with :? ConfigPreflightFailedException as ex ->
                    ex.Message

            // Actionable: names the offending fields and both remedies.
            Expect.stringContains message "AllowCredentials" "message names the offending field"
            Expect.stringContains message "Origins" "message names the offending field"

            Expect.stringContains
                message
                "https://app.example.com"
                "message shows the explicit-origins remedy concretely"

            Expect.stringContains message "AllowCredentials = false" "message shows the drop-credentials remedy"

            Expect.equal log.Errors [ message ] "the refusal is logged at Error before it is raised"

            // The refusal precedes registration in `compose`, so nothing
            // was registered by the time it fired.
            Expect.isTrue (isEmptyCollection services) "no CORS policy is registered on the refused path"

        testCase "the preflight validator reports the same conflict as the pre-registration refusal"
        <| fun _ ->
            let config =
                withCors {
                    Origins = [ "https://app.example.com"; "*" ]
                    Methods = [ "GET" ]
                    Headers = [ "*" ]
                    AllowCredentials = true
                }

            let validator = CorsConfigValidator.CorsConfigValidator(config) :> IConfigValidator

            match validator.Validate() |> Async.RunSynchronously with
            | Error message ->
                Expect.equal
                    (Some message)
                    (CorsConfigValidator.credentialsWildcardConflict config)
                    "validator and boot refusal read the same pure check — they cannot disagree"
            | other -> failtestf "expected Error from the CORS validator, got %A" other

        // ── B — explicit credentials-off on the wildcard fallback ────
        testCase "wildcard-without-credentials boots with credentials explicitly off"
        <| fun _ ->
            let config = withCors CorsConfig.permissive

            // Legal shape — no refusal.
            assertCorsCredentialsCompatible config (logger () :> ILogger)

            let policy = registeredPolicy config

            Expect.isTrue policy.AllowAnyOrigin "wildcard origins map to AllowAnyOrigin"

            Expect.isFalse
                policy.SupportsCredentials
                "the wildcard path calls DisallowCredentials() explicitly, not by framework default"

        testCase "explicit origins without credentials also state the posture explicitly"
        <| fun _ ->
            let config =
                withCors {
                    (CorsConfig.forOrigins [ "https://app.example.com" ]) with
                        AllowCredentials = false
                }

            let policy = registeredPolicy config

            Expect.isFalse policy.AllowAnyOrigin "explicit origins are an allowlist, not a wildcard"
            Expect.isFalse policy.SupportsCredentials "credentials stay off when the deployment did not ask for them"

        // ── C — the legal credentialed shape is unaffected (GP 11) ───
        testCase "explicit origins + credentials boots and the policy really sends credentials"
        <| fun _ ->
            let config =
                withCors (CorsConfig.forOrigins [ "https://app.example.com"; "https://staging.example.com" ])

            assertCorsCredentialsCompatible config (logger () :> ILogger)

            let policy = registeredPolicy config

            Expect.isTrue policy.SupportsCredentials "a legal credentialed deployment keeps its credentials"
            Expect.isFalse policy.AllowAnyOrigin "credentialed CORS requires an explicit allowlist"

            Expect.sequenceEqual
                (List.ofSeq policy.Origins)
                [ "https://app.example.com"; "https://staging.example.com" ]
                "both configured origins reach the policy"

        testCase "Cors = None is untouched — no refusal, no CORS services"
        <| fun _ ->
            let config = ServerConfig.defaults

            assertCorsCredentialsCompatible config (logger () :> ILogger)

            let services = ServiceCollection()
            registerCors services config (logger () :> ILogger)

            Expect.isTrue (isEmptyCollection services) "a deployment without CORS pays nothing (GP 13)"
    ]