module ToolUp.Platform.Tests.AI.PlatformAIKeysHandlerRbacTests

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement
open ToolUp.AI

// ─── Phase 70 — PlatformAIKeysApi handler RBAC tests ─────────────
//
// Every method on `PlatformAIKeysApi` is gated on
// `AccessContext.canModifyPlatformConfig`. Two gate shapes:
//
//   * Write / test methods (`Set*Key`, `Delete*Key`, `Test*Key`):
//     `withAdmin` short-circuits non-admins with
//     `Error "platform admin role required"` (identical wording to
//     `PlatformAdminApiHandler` so the client error banner renders
//     uniformly).
//   * Read methods (`ListPlatformDescriptors`,
//     `ListPlatformKeyStatuses`, `ListTeams`, `ListTeamKeyStatuses`):
//     non-admins receive an empty list — the affordance doesn't even
//     surface for the caller.
//
// The admin pass-through tests confirm the gate doesn't accidentally
// block legitimate calls and that the handler's other validations
// (unknown provider id, blank key, missing team) still fire.

// ─── Fixtures ────────────────────────────────────────────────────

let private noCaps: AIProviderCapabilities = {
    Streaming = false
    ToolUse = false
    Vision = false
    SupportsPromptCaching = false
    SupportsTriage = false
    TriageModelId = None
    ProviderName = "test"
    Model = "test"
}

let private descriptor (id: string) (defaultModel: string) : AIProviderDescriptor = {
    Id = id
    DisplayName = id
    SupportedModels = [ defaultModel ]
    DefaultModel = defaultModel
    Capabilities = {
        noCaps with
            ProviderName = id
            Model = defaultModel
    }
}

let private anthropic = descriptor "anthropic-claude" "claude-haiku-4-5-20251001"
let private openai = descriptor "openai-gpt" "gpt-4o"

/// Mock provider whose SendMessage always returns Ok — used to drive
/// the `TestPlatformKey` / `TestTeamKey` admin-pass-through cases.
let private okProvider: IAIProvider =
    { new IAIProvider with
        member _.Capabilities = noCaps

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
            return Error(SchemaUnsupported("structured-output", "okProvider does not implement structured output"))
        }
    }

/// Stub `IAIProviderFactory` carrying a fixed `PlatformDescriptors`
/// list. `BuildPlatform` returns `Some okProvider` for any id matching
/// one of the descriptors; otherwise `None` (mirrors the production
/// factory's "id is not one of the wired platform providers" semantics).
let private stubFactory (descriptors: AIProviderDescriptor list) : IAIProviderFactory =
    { new IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = descriptors
        member _.PlatformDescriptor = descriptors |> List.tryHead
        member _.Resolve _ = async { return Error NoProviderConfigured }
        member _.TryResolveByLabel(_, _) = async { return Error NoProviderConfigured }

        member _.BuildPlatform(providerId, _apiKey, _model) =
            if descriptors |> List.exists (fun d -> d.Id = providerId) then
                Some okProvider
            else
                None
    }

// ─── In-memory ISecretStore (BlobPlatformAIKeyStore backing) ─────

type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

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

// ─── Stub ITeamStore ─────────────────────────────────────────────

let private stubTeamStore (teams: TeamInfo list) : ITeamStore =
    let byId = teams |> List.map (fun t -> t.TeamId, t) |> Map.ofList

    { new ITeamStore with
        member _.ListTeams() = async { return teams }
        member _.GetTeam teamId = async { return Map.tryFind teamId byId }

        member _.CreateTeam(_, _) =
            failwith "stubTeamStore.CreateTeam not used"

        member _.DeleteTeam _ =
            failwith "stubTeamStore.DeleteTeam not used"

        member _.AddMember(_, _, _) =
            failwith "stubTeamStore.AddMember not used"

        member _.RemoveMember(_, _) =
            failwith "stubTeamStore.RemoveMember not used"

        member _.ChangeMemberRole(_, _, _) =
            failwith "stubTeamStore.ChangeMemberRole not used"

        member _.GetTeamsForUser _ =
            failwith "stubTeamStore.GetTeamsForUser not used"

        member _.GetTeamMembers _ =
            failwith "stubTeamStore.GetTeamMembers not used"

        member _.GetMemberRole(_, _) =
            failwith "stubTeamStore.GetMemberRole not used"

        member _.GetActiveTeam _ =
            failwith "stubTeamStore.GetActiveTeam not used"

        member _.SetActiveTeam(_, _) =
            failwith "stubTeamStore.SetActiveTeam not used"

        member _.SetArchived(_, _) =
            failwith "stubTeamStore.SetArchived not used"

        member _.PurgeTeam _ =
            failwith "stubTeamStore.PurgeTeam not used"

        member _.PurgeUser _ =
            failwith "stubTeamStore.PurgeUser not used"
    }

// ─── AccessContext + HttpContext helpers ─────────────────────────

let private adminContext: AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser "admin-user") with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private nonAdminContext: AccessContext =
    AccessContext.unrestricted (AuthenticatedUser "regular-user")

/// Build an HttpContext with `AccessContext` + `IPlatformAIKeyStore`
/// (+ optionally `ITeamStore`) registered in DI, mirroring the
/// production composition. `keyStore` is constructed fresh per test;
/// pre-populate it with `SetPlatformKey` / `SetTeamKey` calls in the
/// test body when admin-pass-through paths need stored state.
let private buildContext
    (accessContext: AccessContext)
    (teamStore: ITeamStore option)
    : HttpContext * IPlatformAIKeyStore =
    let services = ServiceCollection()
    let secretStore = InMemorySecretStore() :> ISecretStore
    let keyStore = BlobPlatformAIKeyStore.create secretStore

    services.AddSingleton<AccessContext>(accessContext) |> ignore
    services.AddSingleton<IPlatformAIKeyStore>(keyStore) |> ignore

    match teamStore with
    | Some ts -> services.AddSingleton<ITeamStore>(ts) |> ignore
    | None -> ()

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx, keyStore

/// Build a handler against an HttpContext + factory descriptors,
/// returning the live handler + the underlying key store so tests
/// can seed it.
let private buildHandler
    (accessContext: AccessContext)
    (descriptors: AIProviderDescriptor list)
    (teamStore: ITeamStore option)
    =
    let factory = stubFactory descriptors
    let ctx, keyStore = buildContext accessContext teamStore
    let api = PlatformAIKeysHandler.platformAIKeysApi factory ctx
    api, keyStore

let private teamAlpha: TeamInfo = {
    TeamId = "team-alpha"
    Name = "Team Alpha"
    CreatedAt = DateTime.UnixEpoch
    Archived = false
}

let private expectAdminGate (label: string) (result: Result<'a, string>) =
    match result with
    | Error msg -> Expect.equal msg "platform admin role required" label
    | Ok _ -> failtestf "%s: expected Error 'platform admin role required', got Ok" label

let private expectOk (label: string) (result: Result<'a, string>) =
    match result with
    | Ok _ -> ()
    | Error err -> failtestf "%s: expected Ok, got Error %s" label err

// ─── Tests ───────────────────────────────────────────────────────

let tests =
    testList "Phase 70 — PlatformAIKeysApi handler RBAC" [

        // ─── Non-admin caller — 10 methods covered ────────────────

        testCaseAsync "non-admin ListPlatformDescriptors → empty list (no gate-error; affordance is hidden)"
        <| async {
            let api, _ = buildHandler nonAdminContext [ anthropic; openai ] None
            let! result = api.ListPlatformDescriptors()
            Expect.isEmpty result "read methods short-circuit to []"
        }

        testCaseAsync "non-admin ListPlatformKeyStatuses → empty list"
        <| async {
            let api, _ = buildHandler nonAdminContext [ anthropic ] None
            let! result = api.ListPlatformKeyStatuses()
            Expect.isEmpty result "read methods short-circuit to []"
        }

        testCaseAsync "non-admin SetPlatformKey → Error 'platform admin role required'"
        <| async {
            let api, _ = buildHandler nonAdminContext [ anthropic ] None

            let! result =
                api.SetPlatformKey {
                    ProviderId = anthropic.Id
                    ApiKey = "sk-test"
                }

            expectAdminGate "SetPlatformKey non-admin gate" result
        }

        testCaseAsync "non-admin DeletePlatformKey → Error 'platform admin role required'"
        <| async {
            let api, _ = buildHandler nonAdminContext [ anthropic ] None
            let! result = api.DeletePlatformKey anthropic.Id
            expectAdminGate "DeletePlatformKey non-admin gate" result
        }

        testCaseAsync "non-admin TestPlatformKey → Error 'platform admin role required'"
        <| async {
            let api, _ = buildHandler nonAdminContext [ anthropic ] None
            let! result = api.TestPlatformKey anthropic.Id
            expectAdminGate "TestPlatformKey non-admin gate" result
        }

        testCaseAsync "non-admin ListTeams → empty list"
        <| async {
            let teamStore = stubTeamStore [ teamAlpha ]
            let api, _ = buildHandler nonAdminContext [ anthropic ] (Some teamStore)
            let! result = api.ListTeams()
            Expect.isEmpty result "ListTeams short-circuits for non-admins even with seeded teams"
        }

        testCaseAsync "non-admin ListTeamKeyStatuses → empty list"
        <| async {
            let api, _ = buildHandler nonAdminContext [ anthropic ] None
            let! result = api.ListTeamKeyStatuses "team-alpha"
            Expect.isEmpty result "read methods short-circuit to []"
        }

        testCaseAsync "non-admin SetTeamKey → Error 'platform admin role required'"
        <| async {
            let api, _ = buildHandler nonAdminContext [ anthropic ] None

            let! result =
                api.SetTeamKey {
                    TeamId = "team-alpha"
                    ProviderId = anthropic.Id
                    ApiKey = "sk-test"
                }

            expectAdminGate "SetTeamKey non-admin gate" result
        }

        testCaseAsync "non-admin DeleteTeamKey → Error 'platform admin role required'"
        <| async {
            let api, _ = buildHandler nonAdminContext [ anthropic ] None
            let! result = api.DeleteTeamKey("team-alpha", anthropic.Id)
            expectAdminGate "DeleteTeamKey non-admin gate" result
        }

        testCaseAsync "non-admin TestTeamKey → Error 'platform admin role required'"
        <| async {
            let api, _ = buildHandler nonAdminContext [ anthropic ] None
            let! result = api.TestTeamKey("team-alpha", anthropic.Id)
            expectAdminGate "TestTeamKey non-admin gate" result
        }

        // ─── Admin caller — pass-through behaviour ────────────────

        testCaseAsync "admin ListPlatformDescriptors → wired descriptor list"
        <| async {
            let api, _ = buildHandler adminContext [ anthropic; openai ] None
            let! result = api.ListPlatformDescriptors()
            Expect.equal (result |> List.map _.Id) [ anthropic.Id; openai.Id ] "admin sees wired descriptors"
        }

        testCaseAsync "admin ListPlatformKeyStatuses → one row per wired descriptor with hasKey reflecting store"
        <| async {
            let api, keyStore = buildHandler adminContext [ anthropic; openai ] None
            let! _ = keyStore.SetPlatformKey(anthropic.Id, "sk-ant")
            let! statuses = api.ListPlatformKeyStatuses()
            let byId = statuses |> List.map (fun s -> s.ProviderId, s.HasKey) |> Map.ofList
            Expect.equal (Map.tryFind anthropic.Id byId) (Some true) "seeded provider has key"
            Expect.equal (Map.tryFind openai.Id byId) (Some false) "non-seeded provider has no key"
        }

        testCaseAsync "admin SetPlatformKey → Ok on valid provider + non-blank key"
        <| async {
            let api, _ = buildHandler adminContext [ anthropic ] None

            let! result =
                api.SetPlatformKey {
                    ProviderId = anthropic.Id
                    ApiKey = "sk-ant-test"
                }

            expectOk "SetPlatformKey admin pass-through" result
        }

        testCaseAsync "admin SetPlatformKey → Error on blank key (validation pass-through, not gate)"
        <| async {
            let api, _ = buildHandler adminContext [ anthropic ] None

            let! result =
                api.SetPlatformKey {
                    ProviderId = anthropic.Id
                    ApiKey = "   "
                }

            match result with
            | Error msg -> Expect.stringContains msg "empty" "blank key rejected by validation, not by gate"
            | Ok _ -> failtest "expected Error from blank-key validation"
        }

        testCaseAsync "admin SetPlatformKey → Error on unknown providerId (validation, not gate)"
        <| async {
            let api, _ = buildHandler adminContext [ anthropic ] None

            let! result =
                api.SetPlatformKey {
                    ProviderId = "not-wired"
                    ApiKey = "sk-test"
                }

            match result with
            | Error msg -> Expect.stringContains msg "not-wired" "validation error names the unknown provider id"
            | Ok _ -> failtest "expected Error from unknown-provider validation"
        }

        testCaseAsync "admin DeletePlatformKey → Ok (idempotent, no validation gate)"
        <| async {
            let api, _ = buildHandler adminContext [ anthropic ] None
            let! result = api.DeletePlatformKey anthropic.Id
            expectOk "DeletePlatformKey admin idempotent" result
        }

        testCaseAsync "admin TestPlatformKey → Error when no key configured (resolved Error path, not gate)"
        <| async {
            let api, _ = buildHandler adminContext [ anthropic ] None
            let! result = api.TestPlatformKey anthropic.Id

            match result with
            | Error msg -> Expect.stringContains msg "not configured" "missing-key error path, not gate error"
            | Ok _ -> failtest "expected Error when no platform key configured"
        }

        testCaseAsync "admin TestPlatformKey → Ok with seeded key + passing provider mock"
        <| async {
            let api, keyStore = buildHandler adminContext [ anthropic ] None
            let! _ = keyStore.SetPlatformKey(anthropic.Id, "sk-test")
            let! result = api.TestPlatformKey anthropic.Id
            expectOk "TestPlatformKey admin happy path" result
        }

        testCaseAsync "admin ListTeams → projects ITeamStore.ListTeams to PlatformAIKeysTeamView"
        <| async {
            let teamStore = stubTeamStore [ teamAlpha ]
            let api, _ = buildHandler adminContext [ anthropic ] (Some teamStore)
            let! result = api.ListTeams()

            match result with
            | [ row ] ->
                Expect.equal row.TeamId "team-alpha" "team id projected"
                Expect.equal row.DisplayName "Team Alpha" "display name projected"
            | other -> failtestf "expected single-row projection, got %A" other
        }

        testCaseAsync "admin ListTeams → empty when ITeamStore not registered in DI"
        <| async {
            // Mirrors a deployment without teams configured: composeAI
            // doesn't register ITeamStore. The handler's
            // `teamStoreOpt = None` branch returns [].
            let api, _ = buildHandler adminContext [ anthropic ] None
            let! result = api.ListTeams()
            Expect.isEmpty result "ListTeams = [] when ITeamStore absent"
        }

        testCaseAsync "admin ListTeamKeyStatuses → one row per wired provider with hasKey reflecting store"
        <| async {
            let api, keyStore = buildHandler adminContext [ anthropic; openai ] None
            let! _ = keyStore.SetTeamKey("team-alpha", openai.Id, "sk-team-openai")
            let! statuses = api.ListTeamKeyStatuses "team-alpha"

            let byProvider =
                statuses |> List.map (fun s -> s.ProviderId, s.HasKey) |> Map.ofList

            Expect.equal (Map.tryFind anthropic.Id byProvider) (Some false) "no key for anthropic"
            Expect.equal (Map.tryFind openai.Id byProvider) (Some true) "key set for openai"
        }

        testCaseAsync "admin SetTeamKey → Ok on valid team + provider + key"
        <| async {
            let teamStore = stubTeamStore [ teamAlpha ]
            let api, _ = buildHandler adminContext [ anthropic ] (Some teamStore)

            let! result =
                api.SetTeamKey {
                    TeamId = "team-alpha"
                    ProviderId = anthropic.Id
                    ApiKey = "sk-team"
                }

            expectOk "SetTeamKey admin pass-through" result
        }

        testCaseAsync "admin SetTeamKey → Error on unknown team id (validation pass-through)"
        <| async {
            let teamStore = stubTeamStore [ teamAlpha ]
            let api, _ = buildHandler adminContext [ anthropic ] (Some teamStore)

            let! result =
                api.SetTeamKey {
                    TeamId = "team-zeta"
                    ProviderId = anthropic.Id
                    ApiKey = "sk-test"
                }

            match result with
            | Error msg -> Expect.stringContains msg "team-zeta" "validation error names the unknown team id"
            | Ok _ -> failtest "expected Error from unknown-team validation"
        }

        testCaseAsync "admin DeleteTeamKey → Ok (idempotent)"
        <| async {
            let api, _ = buildHandler adminContext [ anthropic ] None
            let! result = api.DeleteTeamKey("team-alpha", anthropic.Id)
            expectOk "DeleteTeamKey admin idempotent" result
        }

        testCaseAsync "admin TestTeamKey → Error when no team key configured"
        <| async {
            let api, _ = buildHandler adminContext [ anthropic ] None
            let! result = api.TestTeamKey("team-alpha", anthropic.Id)

            match result with
            | Error msg -> Expect.stringContains msg "not configured" "missing-team-key error, not gate error"
            | Ok _ -> failtest "expected Error when no team key configured"
        }

        testCaseAsync "admin TestTeamKey → Ok with seeded team key + passing provider mock"
        <| async {
            let api, keyStore = buildHandler adminContext [ anthropic ] None
            let! _ = keyStore.SetTeamKey("team-alpha", anthropic.Id, "sk-team")
            let! result = api.TestTeamKey("team-alpha", anthropic.Id)
            expectOk "TestTeamKey admin happy path" result
        }
    ]