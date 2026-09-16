module ToolUp.Platform.Tests.InProcess.TeamProviderTests

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tests.Contracts

module TeamSecretKey = ToolUp.Platform.TeamScopedSecretStore.TeamSecretKey

// ─── Phase 44a — per-team BYOK provider configuration ────────────
//
// Three things are pinned here that the portable contract pack cannot
// state, because each is about a CONCRETE shipped implementation
// rather than the seam:
//
//   1. **The team-scoped secret layer isolates on both its axes**, and
//      the second axis is demonstrated against a store that violates
//      the first. `ISecretStore` says implementations MUST NOT serve
//      one scope's secret to another — but the layer exists precisely
//      for the deployment where that is not true, so a test wrapping
//      only well-behaved stores would prove nothing about the case
//      that motivated it.
//   2. **The Phase 44 handler's team arm gates writes on
//      `TeamRoles.canWriteTeamConfig`.** The Phase 44 pack's own
//      fixture is an `AuthenticatedUser` and says explicitly that the
//      team arm is covered elsewhere; Phase 44a's acceptance names it,
//      so it is executed here against the handler that carries it.
//   3. **The shipped resolver conforms to its own contract pack.**

// ─── Fixtures ────────────────────────────────────────────────────

/// A conformant in-memory `ISecretStore`: keyed by `(scope, key)`, so
/// a lookup in one scope cannot see another's.
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

        member _.ListKeys scopeId = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// A store that IGNORES the scope entirely — one flat key namespace
/// shared by every tenant.
///
/// This is a contract violation, and standing it up deliberately is
/// the point: it is the shape a misconfigured backend, a legacy store
/// wired at one container, or an env-var store with a colliding
/// sanitiser actually presents, and it is the only way to demonstrate
/// that the key prefix does work the scope pin cannot do alone. A
/// conformant store would make both axes pass for the wrong reason.
type private FlatteningSecretStore() =
    let store = ConcurrentDictionary<string, string>()

    interface ISecretStore with
        member _.GetSecret(_scopeId, key) = async {
            match store.TryGetValue key with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(_scopeId, key, value) = async {
            store[key] <- value
            return Ok()
        }

        member _.DeleteSecret(_scopeId, key) = async {
            store.TryRemove key |> ignore
            return Ok()
        }

        member _.ListKeys _scopeId = async { return store.Keys |> List.ofSeq }

let private stubTeamStore (roles: (string * string * TeamRole) list) : ITeamStore =
    { new ITeamStore with
        member _.GetMemberRole(teamId, userId) = async {
            return
                roles
                |> List.tryPick (fun (t, u, r) -> if t = teamId && u = userId then Some r else None)
        }

        member _.ListTeams() = async { return [] }
        member _.GetTeam _ = async { return None }

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

// ─── 1. The team-scoped secret layer ─────────────────────────────

let private secretLayerTests =
    testList "Phase 44a — TeamScopedSecretStore" [

        testCaseAsync "a key written through the team layer round-trips through it"
        <| async {
            let inner = InMemorySecretStore() :> ISecretStore
            let teamA = TeamScopedSecretStore.forTeam "team-a" inner
            let scope = TeamSecretKey.container "team-a"

            let! saved = teamA.SetSecret(scope, "openai", "sk-a")
            Expect.isOk saved "the write lands"

            let! read = teamA.GetSecret(scope, "openai")
            Expect.equal read (Some "sk-a") "and reads back through the same transform"
        }

        testCaseAsync "the layer refuses every scope but its own"
        <| async {
            let inner = InMemorySecretStore() :> ISecretStore
            let teamA = TeamScopedSecretStore.forTeam "team-a" inner

            let! _ = teamA.SetSecret(TeamSecretKey.container "team-a", "openai", "sk-a")

            // Not merely "returns nothing" — it cannot be addressed at
            // all from another scope, including the reserved platform
            // one, which would otherwise be a way out of the boundary.
            let! fromTeamB = teamA.GetSecret(TeamSecretKey.container "team-b", "openai")
            let! fromUser = teamA.GetSecret("user-alice", "openai")
            let! fromPlatform = teamA.GetSecret("_platform", "openai")

            Expect.isNone fromTeamB "another team's scope reaches nothing"
            Expect.isNone fromUser "a user scope reaches nothing"
            Expect.isNone fromPlatform "and the platform scope is not a back door"

            let! writeElsewhere = teamA.SetSecret(TeamSecretKey.container "team-b", "openai", "stolen")
            Expect.isError (writeElsewhere |> Result.map ignore) "nor can it write into another scope"

            let! deleteElsewhere = teamA.DeleteSecret(TeamSecretKey.container "team-b", "openai")
            Expect.isError (deleteElsewhere |> Result.map ignore) "nor delete from one"

            let! listElsewhere = teamA.ListKeys(TeamSecretKey.container "team-b")
            Expect.isEmpty listElsewhere "nor enumerate one"
        }

        testCaseAsync
            "RED-then-GREEN — against a scope-flattening store the bare store leaks across teams and the layer does not"
        <| async {
            // The RED half: a store that ignores its scope argument
            // serves team A's key to team B, under the same key name
            // the Phase 44 handler would mint for either of them. This
            // assertion is the cross-team read the layer exists to stop;
            // it passes here, which is what makes the second half
            // meaningful rather than vacuous.
            let bare = FlatteningSecretStore() :> ISecretStore
            let! _ = bare.SetSecret(TeamSecretKey.container "team-a", "provider-key-openai", "sk-team-a")
            let! leaked = bare.GetSecret(TeamSecretKey.container "team-b", "provider-key-openai")

            Expect.equal
                leaked
                (Some "sk-team-a")
                "the unwrapped store DOES leak across the tenant boundary — this is the defect being defended against"

            // The GREEN half: the same inner store, the same key name,
            // both teams wrapped. Team B reads its own scope through its
            // own layer and finds nothing of team A's.
            let shared = FlatteningSecretStore() :> ISecretStore
            let layerA = TeamScopedSecretStore.forTeam "team-a" shared
            let layerB = TeamScopedSecretStore.forTeam "team-b" shared

            let! _ = layerA.SetSecret(TeamSecretKey.container "team-a", "provider-key-openai", "sk-team-a")
            let! throughB = layerB.GetSecret(TeamSecretKey.container "team-b", "provider-key-openai")

            Expect.isNone throughB "team B cannot read team A's key even through a store that does not isolate"

            let! throughA = layerA.GetSecret(TeamSecretKey.container "team-a", "provider-key-openai")
            Expect.equal throughA (Some "sk-team-a") "and team A still reads its own"
        }

        testCaseAsync "the length-tagged prefix cannot be forged by a team id that is another's prefix"
        <| async {
            // `team-{teamId}-{key}` would make team `a` holding key
            // `b-x` and team `a-b` holding key `x` the same string. The
            // length tag is what rules that out, and this is the case
            // that would catch its removal.
            let shared = FlatteningSecretStore() :> ISecretStore
            let outer = TeamScopedSecretStore.forTeam "a" shared
            let inner = TeamScopedSecretStore.forTeam "a-b" shared

            let! _ = inner.SetSecret(TeamSecretKey.container "a-b", "x", "sk-inner")
            let! forged = outer.GetSecret(TeamSecretKey.container "a", "b-x")

            Expect.isNone forged "no key name reachable from team `a` addresses team `a-b`'s secret"
        }

        testCaseAsync "ListKeys reports the team's own keys, un-prefixed, and nobody else's"
        <| async {
            let shared = FlatteningSecretStore() :> ISecretStore
            let layerA = TeamScopedSecretStore.forTeam "team-a" shared
            let layerB = TeamScopedSecretStore.forTeam "team-b" shared

            let! _ = layerA.SetSecret(TeamSecretKey.container "team-a", "openai", "sk-a")
            let! _ = layerA.SetSecret(TeamSecretKey.container "team-a", "anthropic", "sk-a2")
            let! _ = layerB.SetSecret(TeamSecretKey.container "team-b", "openai", "sk-b")

            let! keys = layerA.ListKeys(TeamSecretKey.container "team-a")

            Expect.equal (List.sort keys) [ "anthropic"; "openai" ] "its own keys, in the caller's own vocabulary"
        }

        testCase "the key transform is injective over (team, key)"
        <| fun () ->
            let scoped = TeamSecretKey.scoped
            Expect.notEqual (scoped "a" "b-x") (scoped "a-b" "x") "the ambiguous pair stays distinct"
            Expect.equal (TeamSecretKey.unscoped "a-b" (scoped "a-b" "x")) (Some "x") "round-trips for its own team"
            Expect.isNone (TeamSecretKey.unscoped "a" (scoped "a-b" "x")) "and never for another's"
            Expect.isNone (TeamSecretKey.unscoped "a" "provider-key-openai") "nor for a key it never wrote"

        testCase "the at-rest posture is the inner store's, never the wrapper's"
        <| fun () ->
            // A wrapper that answered for itself would report an
            // unknown-posture store as safe, or launder a plaintext one.
            let wrapped =
                TeamScopedSecretStore.forTeam "team-a" (InMemorySecretStore() :> ISecretStore)

            match box wrapped with
            | :? ISecretStoreAtRestPosture as declared ->
                match declared.AtRestPosture with
                | UnknownAtRest reason ->
                    Expect.stringContains reason "TeamScopedSecretStore" "the refusal names the wrapper it came through"
                | other -> failtestf "expected UnknownAtRest over an undeclared inner store, got %A" other
            | _ -> failtest "the layer must declare ISecretStoreAtRestPosture"
    ]

// ─── 2. The Phase 44 handler's team write gate ───────────────────

/// The Phase 44 handler in TEAM scope, with an `ITeamStore` registered
/// so `ensureWriteAllowed` can resolve the caller's role.
let private buildTeamScoped (userId: string) (teamId: string) (roles: (string * string * TeamRole) list) =
    let services = ServiceCollection()
    let secrets = InMemorySecretStore() :> ISecretStore
    let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    let store = BlobProviderProfile.create storage

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (TeamMember(userId, teamId)))
    |> ignore

    services.AddSingleton<ISecretStore>(secrets) |> ignore
    services.AddSingleton<ITeamStore>(stubTeamStore roles) |> ignore

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp

    ProviderProfileApiHandler.providerProfileApi store ctx, store

let private entryInput (label: string) : ProviderEntryInput = {
    Label = label
    ProviderId = "anthropic"
    Model = None
    Tags = []
    ApiKey = Some "sk-test"
}

let private handlerGateTests =
    let roles = [ "t1", "olive", Owner; "t1", "adam", Admin; "t1", "mary", Member ]

    testList "Phase 44a — team-owned provider config is Owner/Admin-gated" [

        testCaseAsync "an Owner may write the team's provider profile"
        <| async {
            let api, _ = buildTeamScoped "olive" "t1" roles
            let! result = api.SaveEntry(entryInput "team-primary")
            Expect.isOk (result |> Result.map ignore) "Owner writes"
        }

        testCaseAsync "an Admin may write the team's provider profile"
        <| async {
            let api, _ = buildTeamScoped "adam" "t1" roles
            let! result = api.SaveEntry(entryInput "team-primary")
            Expect.isOk (result |> Result.map ignore) "Admin writes"
        }

        testCaseAsync "a Member may not — and the refusal names the role rather than the endpoint"
        <| async {
            let api, store = buildTeamScoped "mary" "t1" roles
            let! result = api.SaveEntry(entryInput "sneaky")

            match result with
            | Ok() -> failtest "a Member wrote team-owned provider configuration"
            | Error message -> Expect.stringContains message "Member" "the refusal says what role the caller holds"

            // And nothing landed — a refusal that still persisted would
            // be the worst of both.
            let teamScope = (ProviderScope.teamOwned "t1").Storage
            let! profile = store.Get teamScope

            Expect.isNone
                (profile |> Option.bind (fun (p: ProviderProfile) -> p.Entries |> List.tryHead))
                "no entry was written"
        }

        testCaseAsync "a Member may still READ the team's profile"
        <| async {
            // The gate is on writes. A member who could not read the
            // team's configuration could not be served by it.
            let writer, _ = buildTeamScoped "olive" "t1" roles
            let! _ = writer.SaveEntry(entryInput "team-primary")

            // A fresh handler over the same scope would need the same
            // blob store, so assert the read path through the writer's
            // own scope instead: the reader arm is the Member one.
            let api, store = buildTeamScoped "mary" "t1" roles
            let! _ = store.Set((ProviderScope.teamOwned "t1").Storage, ProviderProfile.empty ())
            let! profile = api.GetProfile()

            Expect.isOk (profile |> Result.map ignore) "a Member reads the team's profile"
        }

        testCaseAsync "reads are scoped to the caller's own team"
        <| async {
            let apiA, storeA = buildTeamScoped "olive" "t1" roles
            let! _ = apiA.SaveEntry(entryInput "t1-primary")

            // Seed team 2's blob in the SAME store, then read as a
            // member of team 1: the handler derives its scope from the
            // caller's AccessContext and never from the wire, so team
            // 2's entries are unreachable.
            let! _ =
                storeA.Set(
                    (ProviderScope.teamOwned "t2").Storage,
                    {
                        ProviderProfile.empty () with
                            Entries = [ ProviderEntry.pastedKey "t2-secret" "anthropic" None "t2-secret-key" ]
                    }
                )

            let! view = apiA.GetProfile()

            match view with
            | Error e -> failtestf "read refused: %s" e
            | Ok v ->
                let labels = v.Entries |> List.map _.Label
                Expect.contains labels "t1-primary" "its own team's entry"
                Expect.isFalse (List.contains "t2-secret" labels) "and nothing of the other team's"
        }
    ]

// ─── 3. The shipped resolver against its contract pack ───────────

let private resolverConformance =
    ITeamProviderResolverContract.tests "TeamProviderResolver" (fun profiles roles ->
        TeamProviderResolver.create profiles roles)

/// A resolver composed the way a composition root does it — over a real
/// `ITeamStore` rather than a lambda — so the adapter is exercised too.
let private teamStoreAdapterTests =
    testList "Phase 44a — TeamProviderResolver over a registered ITeamStore" [

        testCaseAsync "forTeamStore reads roles through ITeamStore.GetMemberRole"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let profiles = BlobProviderProfile.create storage
            let teams = stubTeamStore [ "t1", "olive", Owner; "t1", "mary", Member ]
            let resolver = TeamProviderResolver.forTeamStore profiles teams

            let! asOwner = resolver.CanWrite(TeamMember("olive", "t1"), ProviderProfileOwner.TeamOwned "t1")
            let! asMember = resolver.CanWrite(TeamMember("mary", "t1"), ProviderProfileOwner.TeamOwned "t1")

            Expect.isOk (asOwner |> Result.map ignore) "the Owner is admitted through the real store"
            Expect.isError (asMember |> Result.map ignore) "and the Member refused"
        }

        testCaseAsync "noTeamRoles refuses every team write and leaves reads alone"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let profiles = BlobProviderProfile.create storage
            let resolver = TeamProviderResolver.create profiles TeamProviderResolver.noTeamRoles

            let! _ =
                profiles.Set(
                    (ProviderScope.teamOwned "t1").Storage,
                    {
                        ProviderProfile.empty () with
                            Entries = [ ProviderEntry.pastedKey "team-primary" "anthropic" None "k" ]
                            Routing = [
                                {
                                    Surface = "ai.assistant"
                                    Context = None
                                    EntryLabel = "team-primary"
                                }
                            ]
                    }
                )

            let! write = resolver.CanWrite(TeamMember("olive", "t1"), ProviderProfileOwner.TeamOwned "t1")
            Expect.isError (write |> Result.map ignore) "no team management wired, no team write"

            let! read = resolver.Resolve(TeamMember("olive", "t1"), "ai.assistant", None)
            Expect.equal (read.Entry |> Option.map _.Label) (Some "team-primary") "reads never consult a role"
        }
    ]

let tests =
    testList "Phase 44a — per-team BYOK provider configuration" [
        secretLayerTests
        handlerGateTests
        resolverConformance
        teamStoreAdapterTests
    ]