module ToolUp.Platform.Tests.InProcess.OidcClaimMappingAdvisoryValidatorTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// `OidcClaimMappingAdvisoryValidator` reads TOOLUP_AUTH_MODE +
// TOOLUP_OIDC_ISSUER + the two claim-mapping variables from the
// environment. Env vars are process-global, so the list is
// `testSequenced` and every case saves / restores all four.
//
// The property under test is VISIBILITY, which is easy to ship broken in
// a way no other test notices: a validator that always returns `Ok` is
// indistinguishable from one that is correctly quiet, and a validator
// that always warns trains operators to scroll past the preflight
// summary. So both directions are asserted — silence when nothing is
// configured, and a warning that NAMES the configured claim when
// something is.

let private envKeys = [
    "TOOLUP_AUTH_MODE"
    "TOOLUP_OIDC_ISSUER"
    "TOOLUP_OIDC_USER_ID_CLAIM"
    "TOOLUP_OIDC_TENANT_ID_CLAIM"
]

/// Set the four variables (None = unset), run the validator, restore the
/// prior values unconditionally.
let private validateWithEnv (values: (string * string option) list) : ValidationResult =
    let prior = envKeys |> List.map (fun k -> k, Environment.GetEnvironmentVariable k)

    try
        for key in envKeys do
            let value = values |> List.tryPick (fun (k, v) -> if k = key then Some v else None)

            Environment.SetEnvironmentVariable(key, value |> Option.flatten |> Option.toObj)

        let v =
            OidcClaimMappingAdvisoryValidator.OidcClaimMappingAdvisoryValidator() :> IConfigValidator

        v.Validate() |> Async.RunSynchronously
    finally
        for key, value in prior do
            Environment.SetEnvironmentVariable(key, value)

let private warningMessage (result: ValidationResult) =
    match result with
    | Warning m -> m
    | other -> failtestf "expected Warning, got %A" other

[<Tests>]
let tests =
    testSequenced
    <| testList "OIDC claim mapping advisory validator" [

        test "no claim mapping configured is silent" {
            // The overwhelmingly common deployment. A validator that
            // spoke here would be noise on every boot in the estate.
            let result =
                validateWithEnv [
                    "TOOLUP_AUTH_MODE", Some "oidc"
                    "TOOLUP_OIDC_ISSUER", Some "https://idp.example.com"
                    "TOOLUP_OIDC_USER_ID_CLAIM", None
                    "TOOLUP_OIDC_TENANT_ID_CLAIM", None
                ]

            Expect.equal result Ok "an unmapped deployment gets no advisory line"
        }

        test "a whitespace-only variable is treated as unset, matching the reader" {
            // The advisory must agree with `claimMappingFromEnv` about
            // what counts as set; disagreeing either way is worse than
            // not checking, because the operator is trusting the line.
            let result =
                validateWithEnv [
                    "TOOLUP_AUTH_MODE", Some "oidc"
                    "TOOLUP_OIDC_ISSUER", Some "https://idp.example.com"
                    "TOOLUP_OIDC_USER_ID_CLAIM", Some "   "
                    "TOOLUP_OIDC_TENANT_ID_CLAIM", None
                ]

            Expect.equal result Ok "an empty variable configures no mapping, so there is nothing to announce"
        }

        test "a configured UserId claim warns, naming the claim and the fail-closed semantics" {
            let message =
                validateWithEnv [
                    "TOOLUP_AUTH_MODE", Some "oidc"
                    "TOOLUP_OIDC_ISSUER", Some "https://idp.example.com"
                    "TOOLUP_OIDC_USER_ID_CLAIM", Some "employee_number"
                    "TOOLUP_OIDC_TENANT_ID_CLAIM", None
                ]
                |> warningMessage

            Expect.stringContains message "employee_number" "the advisory names the configured claim"
            Expect.stringContains message "TOOLUP_OIDC_USER_ID_CLAIM" "the advisory names the variable that set it"
            Expect.stringContains message "FAIL-CLOSED" "the advisory states the semantics an operator must expect"
        }

        test "a configured TenantId claim alone also warns" {
            let message =
                validateWithEnv [
                    "TOOLUP_AUTH_MODE", Some "oidc"
                    "TOOLUP_OIDC_ISSUER", Some "https://idp.example.com"
                    "TOOLUP_OIDC_USER_ID_CLAIM", None
                    "TOOLUP_OIDC_TENANT_ID_CLAIM", Some "org_id"
                ]
                |> warningMessage

            Expect.stringContains message "org_id" "the advisory names the configured claim"
            Expect.isFalse (message.Contains "TOOLUP_OIDC_USER_ID_CLAIM=") "it does not report a mapping that is unset"
        }

        test "a mapping set outside oidc mode is reported as configured-but-unread" {
            // The typo class: the variables are set, nothing reads them,
            // and today nothing says so.
            let message =
                validateWithEnv [
                    "TOOLUP_AUTH_MODE", None
                    "TOOLUP_OIDC_ISSUER", None
                    "TOOLUP_OIDC_USER_ID_CLAIM", Some "oid"
                    "TOOLUP_OIDC_TENANT_ID_CLAIM", None
                ]
                |> warningMessage

            Expect.stringContains
                message
                "TOOLUP_AUTH_MODE"
                "the advisory names what would have to change for it to apply"

            Expect.stringContains message "nothing reads it" "the advisory says plainly that the setting is inert"
        }

        test "an Entra issuer with a UserId mapping notes the PreferOidWhenPresent overlap" {
            // Both target UserId and `fromEnv` auto-enables the flag for
            // this issuer family. They agree on a token carrying `oid`
            // and differ on one that does not, which is precisely the
            // case an operator will hit and not expect.
            let message =
                validateWithEnv [
                    "TOOLUP_AUTH_MODE", Some "oidc"
                    "TOOLUP_OIDC_ISSUER", Some "https://login.microsoftonline.com/tenant-id/v2.0"
                    "TOOLUP_OIDC_USER_ID_CLAIM", Some "oid"
                    "TOOLUP_OIDC_TENANT_ID_CLAIM", Some "tid"
                ]
                |> warningMessage

            Expect.stringContains message "PreferOidWhenPresent" "the overlap is named"

            Expect.stringContains
                message
                "the explicit mapping wins"
                "the resolution order is stated, not left to inference"
        }

        test "a non-Entra issuer carries no overlap note" {
            // The negative control: without it, a validator that always
            // appended the note would pass the case above.
            let message =
                validateWithEnv [
                    "TOOLUP_AUTH_MODE", Some "oidc"
                    "TOOLUP_OIDC_ISSUER", Some "https://your-domain.auth0.com"
                    "TOOLUP_OIDC_USER_ID_CLAIM", Some "oid"
                    "TOOLUP_OIDC_TENANT_ID_CLAIM", None
                ]
                |> warningMessage

            Expect.isFalse
                (message.Contains "PreferOidWhenPresent")
                "an issuer for which the flag is never auto-enabled gets no note about it"
        }

        test "the advisory never refuses a boot" {
            // It is advisory by decision, not by accident. A future edit
            // that promoted it to Error would silently turn a supported
            // configuration into a startup failure.
            let results = [
                validateWithEnv [
                    "TOOLUP_AUTH_MODE", Some "oidc"
                    "TOOLUP_OIDC_ISSUER", Some "https://idp.example.com"
                    "TOOLUP_OIDC_USER_ID_CLAIM", Some "oid"
                    "TOOLUP_OIDC_TENANT_ID_CLAIM", Some "tid"
                ]
                validateWithEnv [
                    "TOOLUP_AUTH_MODE", None
                    "TOOLUP_OIDC_ISSUER", None
                    "TOOLUP_OIDC_USER_ID_CLAIM", Some "oid"
                    "TOOLUP_OIDC_TENANT_ID_CLAIM", None
                ]
            ]

            for result in results do
                match result with
                | Error m -> failtestf "the advisory must never refuse startup; got Error: %s" m
                | _ -> ()
        }

        test "it is structural-class, so SkipPreflight cannot hide it" {
            // The point of the validator is that a changed identity
            // source is visible. An emergency-boot lever aimed at
            // external probes must not also silence it.
            let v =
                OidcClaimMappingAdvisoryValidator.OidcClaimMappingAdvisoryValidator() :> IConfigValidator

            Expect.isTrue ((v :> obj) :? IStructuralClassValidator) "carries the structural-class marker"

            Expect.isFalse
                ((v :> obj) :? ISecurityClassValidator)
                "it is not a security guard — it announces a supported configuration, it does not gate one"
        }

        test "Validator metadata is well-formed" {
            let v =
                OidcClaimMappingAdvisoryValidator.OidcClaimMappingAdvisoryValidator() :> IConfigValidator

            Expect.equal v.Name "oidc-claim-mapping-advisory" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]