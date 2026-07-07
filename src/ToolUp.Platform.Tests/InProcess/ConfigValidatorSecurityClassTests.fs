module ToolUp.Platform.Tests.InProcess.ConfigValidatorSecurityClassTests

open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ConfigValidatorAggregator

// ─── Security-class classification guard ──────────────────────────────
//
// `ISecurityClassValidator` is an opt-in marker interface: an
// `IConfigValidator` that also implements it is security-class, so the
// aggregator derives its always-run-under-SkipPreflight set by
// type-testing for the marker rather than a hand-maintained name set that
// could drift. Two properties matter:
//
//   1. Every Error-capable auth / secret / CSRF / provenance validator
//      the SDK ships also implements `ISecurityClassValidator`. If a
//      future security validator forgets the marker, the enumeration below
//      fails naming it — SkipPreflight would otherwise silently bypass it,
//      the exact drift this closes.
//   2. A `SkipPreflight` run still executes every security-class
//      validator (derived from the marker, not a name lookup), so one
//      boolean can't disable the auth-class safety net.

// A minimal non-encrypting ISecretStore — enough to construct the
// secret-tier validators; their classification is independent of the
// store's contents.
let private secretStore () : Secrets.ISecretStore =
    EnvironmentSecretStore.EnvironmentSecretStore() :> Secrets.ISecretStore

/// Every security-class validator the SDK ships, paired with its `Name`
/// for a readable failure. Each is constructed with the lightest
/// dependencies that satisfy its constructor — the marker is a static
/// property of the type, independent of config / substrate state.
let private shippedSecurityClassValidators () : (string * IConfigValidator) list =
    let cfg = ServerConfig.defaults

    [
        "header-auth-mode",
        (HeaderAuthProviderModeValidator.HeaderAuthProviderModeValidator(
            cfg,
            HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider
        )
        :> IConfigValidator)
        "oidc-config-completeness",
        (OidcConfigCompletenessValidator.OidcConfigCompletenessValidator() :> IConfigValidator)
        "oidc-audience-binding", (OidcAudienceBindingValidator.OidcAudienceBindingValidator(cfg) :> IConfigValidator)
        "sse-auth-mode", (SseAuthModeValidator.SseAuthModeValidator(cfg) :> IConfigValidator)
        "encrypted-secret-store-mode",
        (EncryptedSecretStoreModeValidator.EncryptedSecretStoreModeValidator(cfg, secretStore ()) :> IConfigValidator)
        "oauth-secret-encryption-mode",
        (OAuthSecretEncryptionModeValidator.OAuthSecretEncryptionModeValidator(cfg, secretStore ()) :> IConfigValidator)
        "oauth-state-store-instance",
        (OAuthStateStoreInstanceValidator.OAuthStateStoreInstanceValidator(cfg, InMemoryOAuthStateStore())
        :> IConfigValidator)
        "per-scope-key-resolver-distributed",
        (PerScopeKeyResolverDistributedValidator.PerScopeKeyResolverDistributedValidator(None) :> IConfigValidator)
        "auto-bootstrap-dev-admin-mode",
        (AutoBootstrapDevAdminModeValidator.AutoBootstrapDevAdminModeValidator(cfg) :> IConfigValidator)
        "csrf-default-mode", (CsrfDefaultModeValidator.CsrfDefaultModeValidator(cfg) :> IConfigValidator)
        "cors-config", (CorsConfigValidator.CorsConfigValidator(cfg) :> IConfigValidator)
        "forwarded-headers-trust",
        (ForwardedHeadersTrustValidator.ForwardedHeadersTrustValidator(cfg) :> IConfigValidator)
        "share-token-signing-key-provenance",
        (ShareTokenSigningKeyProvenanceValidator.ShareTokenSigningKeyProvenanceValidator(cfg, secretStore ())
        :> IConfigValidator)
    ]

let private isSecurityClass (v: IConfigValidator) =
    match box v with
    | :? ISecurityClassValidator -> true
    | _ -> false

/// A security-class stub — implements the `ISecurityClassValidator`
/// marker alongside `IConfigValidator`, so the aggregator's type-test
/// picks it up as always-run-under-SkipPreflight.
let private mkSecurityValidator (name: string) (result: ValidationResult) =
    { new IConfigValidator with
        member _.Name = name
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return result }
      interface ISecurityClassValidator
    }

/// A plain (non-security) stub — no marker, so SkipPreflight bypasses it.
let private mkValidator (name: string) (result: ValidationResult) =
    { new IConfigValidator with
        member _.Name = name
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return result }
    }

let tests =
    testList "ConfigValidatorSecurityClass" [

        // ── Guard: every shipped security validator carries the marker ──
        testCase "every shipped auth/secret/CSRF/provenance validator implements ISecurityClassValidator"
        <| fun _ ->
            let missing =
                shippedSecurityClassValidators ()
                |> List.filter (fun (_, v) -> not (isSecurityClass v))
                |> List.map fst

            Expect.isEmpty
                missing
                (sprintf
                    "these security-class validators forgot `interface ISecurityClassValidator` — SkipPreflight would silently bypass them: %s"
                    (String.concat ", " missing))

        // ── A non-security validator (no marker) is skipped ──
        testCase "an unmarked validator is non-security — skipped under SkipPreflight (GP 11)"
        <| fun _ ->
            // No marker is the classification an ordinary companion probe
            // has; it preserves the prior SkipPreflight behaviour
            // (bypassable) byte-for-byte — nothing at the impl site changes.
            let probe = mkValidator "legacy-probe" (Error "companion down")

            Expect.isFalse (isSecurityClass probe) "an unmarked probe is non-security"

            let services = ServiceCollection()
            services.AddSingleton<IConfigValidator>(probe) |> ignore
            let outcomes = validate services None true
            Expect.isEmpty outcomes "SkipPreflight skips the non-security probe even though it would Error"

        // ── SkipPreflight still runs every security-class validator ──
        testCase "SkipPreflight = true runs every security-class validator, skips the rest (derived from the marker)"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(mkSecurityValidator "sec-a" Ok)
            |> ignore

            services.AddSingleton<IConfigValidator>(mkSecurityValidator "sec-b" Ok)
            |> ignore

            services.AddSingleton<IConfigValidator>(mkSecurityValidator "sec-c" Ok)
            |> ignore

            services.AddSingleton<IConfigValidator>(mkValidator "noisy-probe" Ok) |> ignore

            let outcomes = validate services None true
            let ran = outcomes |> List.map _.Name |> List.sort

            Expect.equal
                ran
                [ "sec-a"; "sec-b"; "sec-c" ]
                "all three security-class validators ran under SkipPreflight; the non-security probe was skipped"

        // ── A security-class Error still aborts startup under SkipPreflight ──
        testCase "SkipPreflight = true — a security-class Error aborts startup (safety net intact)"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(mkSecurityValidator "sec-guard" (Error "auth hole"))
            |> ignore

            services.AddSingleton<IConfigValidator>(mkValidator "noisy-probe" (Error "companion down"))
            |> ignore

            try
                validate services None true |> ignore
                Expect.isTrue false "expected ConfigPreflightFailedException from the security-class validator"
            with :? ConfigPreflightFailedException as ex ->
                Expect.stringContains ex.Message "sec-guard" "the security-class validator aborted startup"
                Expect.isFalse (ex.Message.Contains "noisy-probe") "the non-security probe was skipped, not run"
    ]