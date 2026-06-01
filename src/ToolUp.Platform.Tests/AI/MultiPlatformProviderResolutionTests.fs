module ToolUp.Platform.Tests.AI.MultiPlatformProviderResolutionTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.AI
open ToolUp.AI.DefaultAIProviderFactory

// ─── Phase 70 — DefaultAIProviderFactory resolution-chain tests ──
//
// Exercises the four-step platform-fallback chain
// (`DefaultAIProviderFactory.resolvePlatformApiKey`):
//
//   1. BYOK active route (PermissiveWithPlatformFallback / StrictBYOK
//      under a profile that routes `ai.assistant` to an Entry).
//   2. Team-scope key via `IPlatformAIKeyStore`.
//   3. Platform-scope key via `IPlatformAIKeyStore`.
//   4. `BootstrapKeyFromEnv` migration shim.
//   5. `MissingApiKey` when all four miss.
//
// Plus the multi-provider read-side: `PlatformProviderOverride` picks
// among `PlatformDescriptors`; `PlatformModelOverride` is validated
// against the active provider's `SupportedModels`.
//
// Plus the byte-identical-state regression bar: a single-provider
// deployment with no `IPlatformAIKeyStore` writes (the v0.4 shape)
// resolves byte-for-byte the same key as a pre-Phase-70 deployment.

// ─── Test fixtures ───────────────────────────────────────────────

/// Records every `(apiKey, model)` pair the recording-provider
/// `Build` closure is invoked with. `factory.Resolve` invokes the
/// recorded `Build` exactly once per successful resolution; tests
/// assert against the last recorded inputs.
type private Recorder = {
    mutable ProviderId: string option
    mutable ApiKey: string option
    mutable Model: string option
}

let private newRecorder () : Recorder = {
    ProviderId = None
    ApiKey = None
    Model = None
}

let private noCaps: AIProviderCapabilities = {
    Streaming = false
    ToolUse = false
    Vision = false
    SupportsPromptCaching = false
    ProviderName = "test"
    Model = "test"
}

let private descriptor (id: string) (defaultModel: string) (supported: string list) : AIProviderDescriptor = {
    Id = id
    DisplayName = id
    SupportedModels = supported
    DefaultModel = defaultModel
    Capabilities = {
        noCaps with
            ProviderName = id
            Model = defaultModel
    }
}

let private recordingProvider (recorder: Recorder) (providerId: string) : IAIProvider =
    { new IAIProvider with
        member _.Capabilities = {
            noCaps with
                ProviderName = providerId
        }

        member _.SendMessage(_, _, _, _, _) = async {
            return
                Ok {
                    Content = "ok"
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

        member _.SendStructuredMessage(_, _, _, _, _) = async {
            return
                Error(SchemaUnsupported("structured-output", "recordingProvider does not implement structured output"))
        }
    }

/// Build an `AIPlatformProvider` whose `Build` closure records the
/// `(providerId, apiKey, model)` triple the factory invokes it with
/// into `recorder`. `BootstrapKeyFromEnv` is the caller-supplied env
/// fallback; pass `None` to test the env-miss path.
let private recordingPlatformProvider
    (recorder: Recorder)
    (desc: AIProviderDescriptor)
    (bootstrapFromEnv: string option)
    : AIPlatformProvider =
    {
        Descriptor = desc
        Build =
            fun apiKey model ->
                recorder.ProviderId <- Some desc.Id
                recorder.ApiKey <- Some apiKey
                recorder.Model <- Some model
                recordingProvider recorder desc.Id
        BootstrapKeyFromEnv = bootstrapFromEnv
    }

let private recordingBuilder (recorder: Recorder) (desc: AIProviderDescriptor) : AIProviderBuilder = {
    Descriptor = desc
    Build =
        fun apiKey model ->
            recorder.ProviderId <- Some desc.Id
            recorder.ApiKey <- Some apiKey
            recorder.Model <- Some model
            recordingProvider recorder desc.Id
}

// ─── In-memory ISecretStore (BYOK + key-store backing) ───────────

type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    member _.Seed(scopeId: string, key: string, value: string) = store[(scopeId, key)] <- value

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

// ─── In-memory IProviderProfile ──────────────────────────────────

type private InMemoryProviderProfile() =
    let blobs = ConcurrentDictionary<string, ProviderProfile>()

    member _.Seed(scope: StorageScope, profile: ProviderProfile) = blobs[scope.ScopeId] <- profile

    interface IProviderProfile with
        member _.Get scope = async {
            match blobs.TryGetValue scope.ScopeId with
            | true, p -> return Some p
            | false, _ -> return None
        }

        member _.Set(scope, profile) = async {
            blobs[scope.ScopeId] <- profile
            return Ok()
        }

        member _.Clear scope = async {
            blobs.TryRemove scope.ScopeId |> ignore
            return ()
        }

        member _.ResolveEntry(scope, surface, context) = async {
            match blobs.TryGetValue scope.ScopeId with
            | true, profile -> return ProviderProfile.resolveEntry surface context profile
            | false, _ -> return None
        }

        member _.SetEntryHealth(_, _, _) = async { return Ok() }

// ─── Subjects ────────────────────────────────────────────────────

let private anonymous = AnonymousSession "anon-session"
let private authenticatedUser = AuthenticatedUser "user-1"
let private teamMember = TeamMember("user-1", "team-alpha")

let private claimBearer =
    ClaimBearer {
        TokenId = "tok-1"
        ScopeId = "scope-claim"
        ResourceKind = "forms.publishable"
        ResourceId = "form-1"
        AttributedHandle = None
        IssuedBy = "issuer-1"
        IssuedAt = DateTimeOffset.UnixEpoch
        ExpiresAt = DateTimeOffset.UnixEpoch.AddDays 1.0
        UseLimit = None
        UsedCount = 0
        Revoked = false
        RateLimit = None
    }

// ─── Descriptors ─────────────────────────────────────────────────

let private anthropic =
    descriptor "anthropic-claude" "claude-haiku-4-5-20251001" [
        "claude-haiku-4-5-20251001"
        "claude-sonnet-4-20250514"
    ]

let private openai = descriptor "openai-gpt" "gpt-4o" [ "gpt-4o"; "gpt-4o-mini" ]

// ─── Factory builder ─────────────────────────────────────────────

/// Construct a fresh factory + fresh substrate. Each test calls this
/// so substrate state never leaks between cases.
let private buildFactory
    (recorder: Recorder)
    (descriptors: AIProviderDescriptor list)
    (bootstrapByProviderId: Map<string, string option>)
    (fallbackPolicy: AIFallbackPolicy)
    (includeKeyStore: bool)
    =
    let secretStore = InMemorySecretStore()
    let providerProfile = InMemoryProviderProfile()

    let platformProviders =
        descriptors
        |> List.map (fun d ->
            let bootstrap =
                bootstrapByProviderId |> Map.tryFind d.Id |> Option.defaultValue None

            recordingPlatformProvider recorder d bootstrap)

    let builders = descriptors |> List.map (recordingBuilder recorder)

    let keyStore =
        if includeKeyStore then
            Some(BlobPlatformAIKeyStore.create (secretStore :> ISecretStore))
        else
            None

    let factory =
        DefaultAIProviderFactory.create
            builders
            (providerProfile :> IProviderProfile)
            (secretStore :> ISecretStore)
            fallbackPolicy
            platformProviders
            keyStore

    factory, secretStore, providerProfile, keyStore

// ─── Tests ───────────────────────────────────────────────────────

let tests =
    testList "Phase 70 — DefaultAIProviderFactory multi-platform resolution" [

        // ─── Step 4 (env-bootstrap) under every Subject shape ─────

        testCaseAsync "AnonymousSession — no key store + BootstrapKeyFromEnv = Some env-key → env-key resolved"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, _, _, _ =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly false

            let ctx = AccessContext.unrestricted anonymous
            let! result = factory.Resolve ctx

            match result with
            | Ok _ ->
                Expect.equal recorder.ApiKey (Some "env-key") "env-bootstrap key flows through"
                Expect.equal recorder.ProviderId (Some anthropic.Id) "platform descriptor's provider id"
                Expect.equal recorder.Model (Some anthropic.DefaultModel) "descriptor's DefaultModel applied"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        testCaseAsync "AuthenticatedUser — empty store, BootstrapKeyFromEnv = Some env-key → env-key resolved"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, _, _, _ =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly true

            let ctx = AccessContext.unrestricted authenticatedUser
            let! result = factory.Resolve ctx

            match result with
            | Ok _ -> Expect.equal recorder.ApiKey (Some "env-key") "env-bootstrap when store is empty"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        testCaseAsync "TeamMember — empty store, BootstrapKeyFromEnv = Some env-key → env-key resolved"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, _, _, _ =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly true

            let ctx = AccessContext.unrestricted teamMember
            let! result = factory.Resolve ctx

            match result with
            | Ok _ ->
                Expect.equal recorder.ApiKey (Some "env-key") "env-bootstrap when neither team nor platform key present"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        testCaseAsync "ClaimBearer — empty store, BootstrapKeyFromEnv = Some env-key → env-key resolved"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, _, _, _ =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly true

            let ctx = AccessContext.unrestricted claimBearer
            let! result = factory.Resolve ctx

            match result with
            | Ok _ ->
                // ClaimBearer has scope but no TeamId — step 2 (team
                // key) short-circuits, step 3 (platform key) misses,
                // step 4 (env bootstrap) wins.
                Expect.equal recorder.ApiKey (Some "env-key") "env-bootstrap for ClaimBearer with empty store"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        // ─── Step 3 (platform-scope key) ──────────────────────────

        testCaseAsync "AuthenticatedUser — platform-scope key present → platform key resolved (overrides env)"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, _, _, keyStoreOpt =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly true

            let keyStore = keyStoreOpt.Value
            let! _ = keyStore.SetPlatformKey(anthropic.Id, "platform-key")

            let ctx = AccessContext.unrestricted authenticatedUser
            let! result = factory.Resolve ctx

            match result with
            | Ok _ ->
                Expect.equal
                    recorder.ApiKey
                    (Some "platform-key")
                    "platform-scope key takes precedence over env bootstrap"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        testCaseAsync "TeamMember — no team key, platform-scope key present → platform key resolved"
        <| async {
            let recorder = newRecorder ()

            let factory, _, _, keyStoreOpt =
                buildFactory recorder [ anthropic ] Map.empty PlatformOnly true

            let keyStore = keyStoreOpt.Value
            let! _ = keyStore.SetPlatformKey(anthropic.Id, "platform-key")

            let ctx = AccessContext.unrestricted teamMember
            let! result = factory.Resolve ctx

            match result with
            | Ok _ -> Expect.equal recorder.ApiKey (Some "platform-key") "team-key miss falls through to platform-scope"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        // ─── Step 2 (team-scope key) ──────────────────────────────

        testCaseAsync "TeamMember — team-scope key present → team key resolved (overrides platform + env)"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, _, _, keyStoreOpt =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly true

            let keyStore = keyStoreOpt.Value
            let! _ = keyStore.SetPlatformKey(anthropic.Id, "platform-key")
            let! _ = keyStore.SetTeamKey("team-alpha", anthropic.Id, "team-key")

            let ctx = AccessContext.unrestricted teamMember
            let! result = factory.Resolve ctx

            match result with
            | Ok _ -> Expect.equal recorder.ApiKey (Some "team-key") "team-scope key wins step 2"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        testCaseAsync "AuthenticatedUser — team-scope key for another team is invisible (no TeamId on subject)"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, _, _, keyStoreOpt =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly true

            let keyStore = keyStoreOpt.Value
            let! _ = keyStore.SetTeamKey("team-alpha", anthropic.Id, "team-key")

            let ctx = AccessContext.unrestricted authenticatedUser
            let! result = factory.Resolve ctx

            match result with
            | Ok _ ->
                // The subject has no TeamId — step 2 short-circuits,
                // every team-scope key is invisible regardless of who
                // owns it. Falls through to env bootstrap.
                Expect.equal recorder.ApiKey (Some "env-key") "AuthenticatedUser bypasses team-scope read"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        // ─── Step 5 (MissingApiKey when all four miss) ────────────

        testCaseAsync "AuthenticatedUser — no keys anywhere → MissingApiKey error"
        <| async {
            let recorder = newRecorder ()
            // No bootstrap, no store entries.
            let factory, _, _, _ =
                buildFactory recorder [ anthropic ] Map.empty PlatformOnly true

            let ctx = AccessContext.unrestricted authenticatedUser
            let! result = factory.Resolve ctx

            match result with
            | Ok _ -> failtest "expected MissingApiKey when no key configured"
            | Error(MissingApiKey(id, _)) -> Expect.equal id anthropic.Id "error names the active descriptor"
            | Error err -> failtestf "expected MissingApiKey, got %A" err
        }

        // ─── Step 1 (BYOK active route) ───────────────────────────

        testCaseAsync
            "AuthenticatedUser + PermissiveWithPlatformFallback — BYOK active route bypasses platform fallback entirely"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, secretStore, providerProfile, keyStoreOpt =
                buildFactory recorder [ anthropic ] bootstraps PermissiveWithPlatformFallback true

            // Seed BYOK plumbing: an Entry routed for `ai.assistant`,
            // its secret in ISecretStore at the user-scope container.
            let userScope = {
                ScopeId = "user-1"
                Container = "user-user-1"
                Persist = true
            }

            let entry: ProviderEntry = {
                Label = "my-key"
                ProviderId = anthropic.Id
                Model = None
                SecretKeyName = "byok-secret-name"
                Tags = []
                Origin = CredentialOrigin.PastedKey
                Health = ProviderHealth.unknown
                UpdatedAt = DateTime.UtcNow
            }

            let profile =
                ProviderProfile.empty ()
                |> fun p -> { p with Entries = [ entry ] }
                |> ProviderProfile.withRoute AIProviderSurface.aiAssistant None entry.Label

            providerProfile.Seed(userScope, profile)
            secretStore.Seed(userScope.Container, entry.SecretKeyName, "byok-key")

            // Even though a platform-scope key would resolve, BYOK
            // active routing pre-empts step 2/3/4 entirely.
            let keyStore = keyStoreOpt.Value
            let! _ = keyStore.SetPlatformKey(anthropic.Id, "platform-key")

            let ctx = AccessContext.unrestricted authenticatedUser
            let! result = factory.Resolve ctx

            match result with
            | Ok _ ->
                Expect.equal recorder.ApiKey (Some "byok-key") "BYOK key wins step 1"

                Expect.equal
                    recorder.Model
                    (Some anthropic.DefaultModel)
                    "BYOK entry without Model uses descriptor default"
            | Error err -> failtestf "expected Ok from BYOK route, got Error %A" err
        }

        // ─── Multi-provider read-side (Stream B integration) ─────

        testCaseAsync "AuthenticatedUser — PlatformProviderOverride picks the second wired descriptor"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ openai.Id, Some "env-key-openai" ]

            let factory, _, providerProfile, _ =
                buildFactory recorder [ anthropic; openai ] bootstraps PlatformOnly true

            let userScope = {
                ScopeId = "user-1"
                Container = "user-user-1"
                Persist = true
            }

            let profile =
                ProviderProfile.empty ()
                |> ProviderProfile.withSurfaceProviderOverride AIProviderSurface.platformProviderKey (Some openai.Id)

            providerProfile.Seed(userScope, profile)

            let ctx = AccessContext.unrestricted authenticatedUser
            let! result = factory.Resolve ctx

            match result with
            | Ok _ ->
                Expect.equal recorder.ProviderId (Some openai.Id) "user's override resolves to the second descriptor"

                Expect.equal
                    recorder.ApiKey
                    (Some "env-key-openai")
                    "key resolved against the chosen provider's bootstrap"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        testCaseAsync "AuthenticatedUser — stale PlatformProviderOverride falls back to first descriptor"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key-anthropic" ]

            let factory, _, providerProfile, _ =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly true

            let userScope = {
                ScopeId = "user-1"
                Container = "user-user-1"
                Persist = true
            }

            // Override references a provider id that's no longer wired
            // — matches the stale-value precedent in Phase 43.A.
            let profile =
                ProviderProfile.empty ()
                |> ProviderProfile.withSurfaceProviderOverride
                    AIProviderSurface.platformProviderKey
                    (Some "retired-provider")

            providerProfile.Seed(userScope, profile)

            let ctx = AccessContext.unrestricted authenticatedUser
            let! result = factory.Resolve ctx

            match result with
            | Ok _ ->
                Expect.equal
                    recorder.ProviderId
                    (Some anthropic.Id)
                    "stale override falls back to first wired descriptor"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        testCaseAsync "AuthenticatedUser — PlatformModelOverride respected when valid for active provider"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, _, providerProfile, _ =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly true

            let userScope = {
                ScopeId = "user-1"
                Container = "user-user-1"
                Persist = true
            }

            let chosenModel = "claude-sonnet-4-20250514"

            let profile =
                ProviderProfile.empty ()
                |> ProviderProfile.withSurfaceModelOverride AIProviderSurface.platformModelKey (Some chosenModel)

            providerProfile.Seed(userScope, profile)

            let ctx = AccessContext.unrestricted authenticatedUser
            let! result = factory.Resolve ctx

            match result with
            | Ok _ -> Expect.equal recorder.Model (Some chosenModel) "valid model override flows through"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        testCaseAsync
            "AuthenticatedUser — PlatformModelOverride for another provider's model silently falls back to default"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "env-key" ]

            let factory, _, providerProfile, _ =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly true

            let userScope = {
                ScopeId = "user-1"
                Container = "user-user-1"
                Persist = true
            }

            // gpt-4o is not in anthropic's SupportedModels.
            let profile =
                ProviderProfile.empty ()
                |> ProviderProfile.withSurfaceModelOverride AIProviderSurface.platformModelKey (Some "gpt-4o")

            providerProfile.Seed(userScope, profile)

            let ctx = AccessContext.unrestricted authenticatedUser
            let! result = factory.Resolve ctx

            match result with
            | Ok _ ->
                Expect.equal
                    recorder.Model
                    (Some anthropic.DefaultModel)
                    "invalid override falls back to active descriptor's DefaultModel"
            | Error err -> failtestf "expected Ok, got Error %A" err
        }

        // ─── Byte-identical-state regression bar ─────────────────

        testCaseAsync
            "Single-provider deployment with no IPlatformAIKeyStore reads byte-identical to pre-Phase-70 shape"
        <| async {
            let recorder = newRecorder ()
            let bootstraps = Map.ofList [ anthropic.Id, Some "deployment-env-key" ]

            // includeKeyStore = false — mirrors the v0.4 deployment
            // that has not opted in to the Platform Admin keys
            // module. The factory falls through directly to
            // BootstrapKeyFromEnv with no key-store read.
            let factory, _, _, _ =
                buildFactory recorder [ anthropic ] bootstraps PlatformOnly false

            for subject in [ anonymous; authenticatedUser; teamMember ] do
                let ctx = AccessContext.unrestricted subject
                let! result = factory.Resolve ctx

                match result with
                | Ok _ ->
                    Expect.equal
                        recorder.ApiKey
                        (Some "deployment-env-key")
                        "every subject resolves the env-bootstrap key"

                    Expect.equal
                        recorder.ProviderId
                        (Some anthropic.Id)
                        "single wired provider is the only resolution target"

                    Expect.equal
                        recorder.Model
                        (Some anthropic.DefaultModel)
                        "model is descriptor default (no override seeded)"
                | Error err ->
                    failtestf
                        "byte-identical regression: subject %A should resolve via env bootstrap, got %A"
                        subject
                        err
        }
    ]