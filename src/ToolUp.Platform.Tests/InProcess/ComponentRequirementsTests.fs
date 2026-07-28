module ToolUp.Platform.Tests.InProcess.ComponentRequirementsTests

open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Secrets
open ToolUp.Platform.ComponentRequirementsPreflight

// ─── Phase 432 — component secret & config requirements manifest ──────
//
// Covers the acceptance shape: a composition missing a REQUIRED secret
// fails preflight with the ComponentId + the requirement named; a missing
// OPTIONAL requirement is a warning, not a failure; a required config
// knob that binds no value fails naming the id-scoped override that would
// bind it; a deployment declaring nothing registers no validator and is
// byte-for-byte unchanged (GP 11 / GP 13).
//
// And the property the whole design exists to guarantee: **no secret
// VALUE ever appears in any report**. Every store in this file holds a
// distinctive sentinel value; each report-producing path is asserted not
// to contain it. The probe is `Async<bool>` by type, so the assertion is
// checking a structural property rather than a formatting habit.

/// The value planted in every fake store. If this string ever reaches a
/// report or a validator message, a secret leaked.
[<Literal>]
let private SentinelValue = "SENTINEL-SECRET-VALUE-4f2b9c"

let private billingId = ComponentId.forCompanionImpl "IAuditSink" "SplunkHec"
let private ordersId = ComponentId.ofModule "orders-service"

/// An `ISecretStore` over an in-memory `(scope, key)` map. Every present
/// key holds `SentinelValue`; `SetSecret` / `DeleteSecret` are unused by
/// the presence probe and fail loudly if a path ever reaches them.
type private FakeSecretStore(present: (string * string) list) =
    let keys = Set.ofList present

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            return
                if keys.Contains((scopeId, key)) then
                    Some SentinelValue
                else
                    None
        }

        member _.SetSecret(_, _, _) = async { return Result.Error "read-only test store" }
        member _.DeleteSecret(_, _) = async { return Result.Error "read-only test store" }

        member _.ListKeys scopeId = async {
            return keys |> Set.filter (fun (s, _) -> s = scopeId) |> Set.toList |> List.map snd
        }

/// A store that returns a blank value for a key — "present but blank"
/// must count as absent, else a deployment satisfies a credential
/// requirement with an empty string.
type private BlankSecretStore() =
    interface ISecretStore with
        member _.GetSecret(_, _) = async { return Some "   " }
        member _.SetSecret(_, _, _) = async { return Result.Error "read-only test store" }
        member _.DeleteSecret(_, _) = async { return Result.Error "read-only test store" }
        member _.ListKeys _ = async { return [] }

let private apiKeyRequirement =
    SecretRequirement.required
        ComponentRequirements.PlatformScope
        "SPLUNK_HEC_TOKEN"
        ApiKeySecret
        "authenticates audit-event delivery to the collector"

let private optionalSigningKey =
    SecretRequirement.optional
        ComponentRequirements.PlatformScope
        "SPLUNK_HEC_SIGNING_KEY"
        SigningKeySecret
        "signs delivered batches when the collector verifies signatures"

let private billingRequirements =
    ComponentRequirements.create billingId [ apiKeyRequirement ] []

let private signature (entries: ComponentRequirements list) : RequirementsSignature =
    ComponentRequirements.signatureOf entries

let private runValidator (v: IConfigValidator) = v.Validate() |> Async.RunSynchronously

let tests =
    testList "ComponentRequirements" [

        // ── 432.B — derivation over declaration ───────────────────────
        testCase "config requirements derive from the component's declared config section"
        <| fun _ ->
            let section =
                ComponentConfig.create ordersId [
                    "maxItems", "100"
                    "endpoint", "https://orders.example"
                    "debug", "false"
                ]

            let derived = fromComponentConfig section

            Expect.equal derived.Component ordersId "the derived set is keyed by the section's component"

            Expect.equal
                derived.Secrets
                []
                "a config section declares no secrets — those are the residual declared half"

            Expect.equal (List.length derived.Config) 3 "one requirement per declared key"

            let byPath = derived.Config |> List.map (fun c -> c.Path, c) |> Map.ofList

            Expect.equal byPath["maxItems"].KnobType IntKnob "an integer default infers IntKnob"
            Expect.equal byPath["endpoint"].KnobType UriKnob "an http(s) default infers UriKnob"
            Expect.equal byPath["debug"].KnobType BoolKnob "a true/false default infers BoolKnob"

            Expect.isFalse
                (ConfigRequirement.isRequired byPath["maxItems"])
                "a knob with a declared default always binds — never a preflight failure"

        testCase "a declared key with a blank value derives as a required knob"
        <| fun _ ->
            let section = ComponentConfig.create ordersId [ "apiBase", "" ]
            let derived = fromComponentConfig section

            Expect.equal (List.length derived.Config) 1 "the key still derives a requirement"

            Expect.isTrue
                (ConfigRequirement.isRequired derived.Config.Head)
                "no default ⇒ the deployment must supply it"

            Expect.equal derived.Config.Head.KnobType OpaqueKnob "no default to infer from ⇒ OpaqueKnob, never a guess"

        testCase "declaration wins over derivation on a colliding knob path"
        <| fun _ ->
            let section = ComponentConfig.create ordersId [ "mode", "fast" ]

            let declared =
                signature [
                    ComponentRequirements.create ordersId [] [
                        ConfigRequirement.required "mode" (EnumKnob [ "fast"; "thorough" ]) "selects the pipeline"
                    ]
                ]

            let merged = derive [ section ] declared
            let reqs = ComponentRequirements.resolve merged ordersId

            Expect.equal (List.length reqs.Config) 1 "the collision merges rather than duplicating"

            Expect.equal
                reqs.Config.Head.KnobType
                (EnumKnob [ "fast"; "thorough" ])
                "the declaration's exact type wins over the inferred one"

        testCase "derive keeps a derived knob the declaration does not mention"
        <| fun _ ->
            let section = ComponentConfig.create ordersId [ "mode", "fast"; "retries", "3" ]

            let declared =
                signature [ ComponentRequirements.create ordersId [ apiKeyRequirement ] [] ]

            let reqs = ComponentRequirements.resolve (derive [ section ] declared) ordersId

            Expect.equal (List.length reqs.Config) 2 "both derived knobs survive the merge"
            Expect.equal (List.length reqs.Secrets) 1 "the declared secret requirement is folded in"

        // ── 432.C — required secret missing fails, naming the component ─
        testCase "a missing required secret fails preflight naming the ComponentId and the requirement"
        <| fun _ ->
            let store = FakeSecretStore [] :> ISecretStore

            let validator =
                ComponentSecretRequirementsValidator(probeOf store, signature [ billingRequirements ])
                :> IConfigValidator

            let result = runValidator validator
            let message = ValidationResult.message result

            Expect.equal (ValidationResult.status result) "Error" "a missing required credential aborts startup"
            Expect.stringContains message (ComponentId.value billingId) "the report names the ComponentId"
            Expect.stringContains message "SPLUNK_HEC_TOKEN" "the report names the requirement key"
            Expect.stringContains message "api-key" "the report names the requirement class"

            Expect.stringContains
                message
                ComponentRequirements.PlatformScope
                "the report names the scope the key must be provisioned under"

        testCase "a present required secret passes preflight"
        <| fun _ ->
            let store =
                FakeSecretStore [ ComponentRequirements.PlatformScope, "SPLUNK_HEC_TOKEN" ] :> ISecretStore

            let validator =
                ComponentSecretRequirementsValidator(probeOf store, signature [ billingRequirements ])
                :> IConfigValidator

            Expect.equal (ValidationResult.status (runValidator validator)) "Ok" "a resolvable credential validates Ok"

        testCase "a present-but-blank secret counts as absent"
        <| fun _ ->
            let validator =
                ComponentSecretRequirementsValidator(probeOf (BlankSecretStore()), signature [ billingRequirements ])
                :> IConfigValidator

            Expect.equal
                (ValidationResult.status (runValidator validator))
                "Error"
                "an empty string satisfies no credential requirement"

        // ── 432.E — a missing OPTIONAL requirement is a warning ───────
        testCase "a missing optional secret is a warning, not a failure"
        <| fun _ ->
            let store =
                FakeSecretStore [ ComponentRequirements.PlatformScope, "SPLUNK_HEC_TOKEN" ] :> ISecretStore

            let reqs =
                ComponentRequirements.create billingId [ apiKeyRequirement; optionalSigningKey ] []

            let validator =
                ComponentSecretRequirementsValidator(probeOf store, signature [ reqs ]) :> IConfigValidator

            let result = runValidator validator

            Expect.equal
                (ValidationResult.status result)
                "Warning"
                "an optional requirement degrades, it does not abort"

            Expect.stringContains
                (ValidationResult.message result)
                "SPLUNK_HEC_SIGNING_KEY"
                "the warning still names the requirement so the operator can provision it"

        testCase "a required miss beside an optional miss is still an Error"
        <| fun _ ->
            let reqs =
                ComponentRequirements.create billingId [ apiKeyRequirement; optionalSigningKey ] []

            let validator =
                ComponentSecretRequirementsValidator(probeOf (FakeSecretStore [] :> ISecretStore), signature [ reqs ])
                :> IConfigValidator

            Expect.equal
                (ValidationResult.status (runValidator validator))
                "Error"
                "warnings never mask a required-credential failure"

        // ── 432.E — no secret VALUE ever reaches a report ─────────────
        testCase "no secret value appears in any report — present, missing, or aggregated"
        <| fun _ ->
            let reqs =
                ComponentRequirements.create billingId [ apiKeyRequirement; optionalSigningKey ] []

            // The api key resolves (so the store DOES hold the sentinel and
            // the probe DOES read it); the signing key does not.
            let store =
                FakeSecretStore [ ComponentRequirements.PlatformScope, "SPLUNK_HEC_TOKEN" ] :> ISecretStore

            let sig' = signature [ reqs ]

            let validatorMessage =
                ValidationResult.message (
                    runValidator (ComponentSecretRequirementsValidator(probeOf store, sig') :> IConfigValidator)
                )

            let defects = check (probeOf store) [] sig' |> Async.RunSynchronously

            let everything =
                validatorMessage
                + String.concat "\n" (defects |> List.map (fun d -> d.Message + d.Requirement + d.Class))
                + String.concat "\n" (reqs.Secrets |> List.map SecretRequirement.describe)

            Expect.isFalse
                (everything.Contains SentinelValue)
                "a secret value must never reach a preflight message, defect record, or rendered requirement"

            Expect.stringContains
                everything
                "SPLUNK_HEC_TOKEN"
                "the key NAME is exactly what the report is supposed to carry"

        testCase "the presence probe returns a boolean — a value cannot escape it"
        <| fun _ ->
            let store =
                FakeSecretStore [ ComponentRequirements.PlatformScope, "SPLUNK_HEC_TOKEN" ] :> ISecretStore

            let present =
                probeOf store ComponentRequirements.PlatformScope "SPLUNK_HEC_TOKEN"
                |> Async.RunSynchronously

            Expect.isTrue present "the probe reports existence"

            let absent =
                probeOf store ComponentRequirements.PlatformScope "MISSING"
                |> Async.RunSynchronously

            Expect.isFalse absent "an unresolvable key reports absent"

        // ── 432.C — required config knobs must bind ───────────────────
        testCase "a required knob that binds no value fails preflight naming the override"
        <| fun _ ->
            let section = ComponentConfig.create ordersId [ "apiBase", "" ]
            let sig' = fromComponentConfigs [ section ]

            let validator =
                ComponentConfigRequirementsValidator([ section ], sig') :> IConfigValidator

            let result = runValidator validator
            let message = ValidationResult.message result

            Expect.equal (ValidationResult.status result) "Error" "an unbound required knob aborts startup"
            Expect.stringContains message (ComponentId.value ordersId) "the report names the ComponentId"
            Expect.stringContains message "apiBase" "the report names the knob path"

            Expect.stringContains
                message
                (ComponentConfig.envVarName ordersId "apiBase")
                "the report names the id-scoped override that would bind it"

        testCase "a required knob bound by the resolved section passes"
        <| fun _ ->
            let declared = ComponentConfig.create ordersId [ "apiBase", "" ]
            let sig' = fromComponentConfigs [ declared ]
            // The resolved section is what ComponentConfigResolver.resolve
            // produced — declared defaults with the id-scoped env override
            // merged on top.
            let resolved =
                ComponentConfig.create ordersId [ "apiBase", "https://orders.example" ]

            let validator =
                ComponentConfigRequirementsValidator([ resolved ], sig') :> IConfigValidator

            Expect.equal (ValidationResult.status (runValidator validator)) "Ok" "a bound required knob validates Ok"

        testCase "a defaulted knob is never a preflight failure"
        <| fun _ ->
            let section = ComponentConfig.create ordersId [ "maxItems", "100" ]
            let sig' = fromComponentConfigs [ section ]

            Expect.isFalse
                (ComponentRequirements.anyRequiredConfig sig')
                "a fully-defaulted section requires nothing of the deployment"

            Expect.equal (configDefects [] sig') [] "and yields no defects even against no resolved section"

        // ── GP 11 / GP 13 — nothing declared, nothing registered ──────
        testCase "an empty signature registers no validator at all"
        <| fun _ ->
            let services = ServiceCollection() :> IServiceCollection
            let before = services.Count

            let after =
                serviceRegistration unavailableProbe [] ComponentRequirements.emptySignature services

            Expect.equal
                after.Count
                before
                "the ServerApp.empty base case composes a byte-for-byte identical service collection"

        testCase "only the half that is actually required is registered"
        <| fun _ ->
            let secretsOnly = ServiceCollection() :> IServiceCollection

            serviceRegistration unavailableProbe [] (signature [ billingRequirements ]) secretsOnly
            |> ignore

            let registered =
                secretsOnly
                |> Seq.filter (fun d -> d.ServiceType = typeof<IConfigValidator>)
                |> Seq.map (fun d -> (d.ImplementationInstance :?> IConfigValidator).Name)
                |> List.ofSeq

            Expect.equal registered [ SecretValidatorName ] "a secrets-only signature registers no knob validator"

            let configOnly = ServiceCollection() :> IServiceCollection
            let section = ComponentConfig.create ordersId [ "apiBase", "" ]

            serviceRegistration unavailableProbe [ section ] (fromComponentConfigs [ section ]) configOnly
            |> ignore

            let registeredConfig =
                configOnly
                |> Seq.filter (fun d -> d.ServiceType = typeof<IConfigValidator>)
                |> Seq.map (fun d -> (d.ImplementationInstance :?> IConfigValidator).Name)
                |> List.ofSeq

            Expect.equal registeredConfig [ ConfigValidatorName ] "a config-only signature registers no secret probe"

        testCase "an empty signature validates Ok even when both validators are constructed directly"
        <| fun _ ->
            let empty = ComponentRequirements.emptySignature

            Expect.equal
                (ValidationResult.status (
                    runValidator (ComponentConfigRequirementsValidator([], empty) :> IConfigValidator)
                ))
                "Ok"
                "nothing required ⇒ nothing to fail"

            Expect.equal
                (ValidationResult.status (
                    runValidator (ComponentSecretRequirementsValidator(unavailableProbe, empty) :> IConfigValidator)
                ))
                "Ok"
                "nothing required ⇒ the probe is never invoked"

        // ── Phase 585 classification — the right lever reaches each half ─
        testCase "the knob check is structural-class and the secret probe is external-probe class"
        <| fun _ ->
            let configValidator =
                ComponentConfigRequirementsValidator([], ComponentRequirements.emptySignature) :> IConfigValidator

            let secretValidator =
                ComponentSecretRequirementsValidator(unavailableProbe, ComponentRequirements.emptySignature)
                :> IConfigValidator

            Expect.equal
                (ConfigValidatorAggregator.classify configValidator)
                ConfigValidatorAggregator.StructuralClass
                "an in-process knob sweep is not what SkipPreflight exists to bypass"

            Expect.equal
                (ConfigValidatorAggregator.classify secretValidator)
                ConfigValidatorAggregator.ExternalProbeClass
                "the store may be a remote vault — an emergency boot must be able to ride its outage"

            Expect.isTrue
                (ConfigValidatorAggregator.alwaysRuns (ConfigValidatorAggregator.classify configValidator))
                "the structural half runs regardless of SkipPreflight"

        // ── 432.D — the ComposableSurface projection ──────────────────
        testCase "slotRequirements is empty for an empty signature"
        <| fun _ ->
            Expect.equal
                (ComposableSurface.slotRequirements ComponentRequirements.emptySignature)
                []
                "nothing declared ⇒ nothing projected (GP 13)"

        testCase "slotRequirements answers 'composing X requires secrets A, B' before composing"
        <| fun _ ->
            let slotId = ComponentId.forCompanionSlot "IAuditSink"

            let sig' =
                signature [
                    ComponentRequirements.create slotId [ apiKeyRequirement; optionalSigningKey ] []
                ]

            let projected = ComposableSurface.slotRequirements sig'

            Expect.equal (List.length projected) 1 "exactly the slot with declared requirements"
            Expect.equal projected.Head.Interface "IAuditSink" "the projection carries the interface name"
            Expect.equal (List.length projected.Head.Secrets) 2 "both declared credentials surface"

            let rendered =
                projected.Head.Secrets
                |> List.map SecretRequirement.describe
                |> String.concat ", "

            Expect.stringContains rendered "api-key" "each requirement renders name + class"
            Expect.isFalse (rendered.Contains SentinelValue) "the surface projection carries no value either"

        testCase "slotRequirements ignores a component that is not a composable slot"
        <| fun _ ->
            let sig' =
                signature [ ComponentRequirements.create ordersId [ apiKeyRequirement ] [] ]

            Expect.equal
                (ComposableSurface.slotRequirements sig')
                []
                "a module id is not a companion slot — the projection is slot-level"

        // ── signature algebra ─────────────────────────────────────────
        testCase "an undeclared component resolves to the empty identity"
        <| fun _ ->
            let resolved =
                ComponentRequirements.resolve ComponentRequirements.emptySignature ordersId

            Expect.isTrue (ComponentRequirements.isEmpty resolved) "absence contributes the identity, never a check"
            Expect.equal resolved.Component ordersId "the identity is still keyed by the id asked for"

        testCase "signatureOf merges two entries sharing a ComponentId"
        <| fun _ ->
            let merged =
                signature [
                    ComponentRequirements.create billingId [ apiKeyRequirement ] []
                    ComponentRequirements.create billingId [ optionalSigningKey ] []
                ]

            let reqs = ComponentRequirements.resolve merged billingId

            Expect.equal (List.length reqs.Secrets) 2 "both entries fold into one requirement set"
            Expect.equal (Map.count merged) 1 "keyed once by the shared id"
    ]