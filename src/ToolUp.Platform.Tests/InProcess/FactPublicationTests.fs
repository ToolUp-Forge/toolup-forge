// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactPublicationTests

// ─── Phase 897 — team-to-team fact publication ───────────────────────
//
// What this pack pins, one list each:
//
//   1. **A consolidated view by publishing.** Two source teams publish one
//      table each into a target team's table; the target answers a
//      population question across both, and one scoped to an origin as a
//      path-prefix query, from its own scope. Rows carry `Imported`
//      provenance naming the grant and evidence naming the origin run.
//   2. **Nothing crosses without a grant in force.** No grant, a grant with
//      one consent, a withdrawn grant, a non-owner, no viewer: nothing is
//      written to the target, and an unpublished fact cannot be read from
//      another team's scope at all.
//   3. **The source's gate decides what leaves.** A row with a cell the
//      source's gate denies at the `FactTeamPublication` door is absent; a
//      `Restricted` cell is not published past its team's output level;
//      disclosure narrows on import and never widens.
//   4. **Withdrawal.** After a grant is withdrawn and the target refreshes,
//      the target holds no row from that origin, and the facts minted from
//      them are superseded by absences naming the withdrawal.
//   5. **Audit, signing, the seam's callers, the composition.** Both sides
//      record each run and cite each other; the regulated profile signs and
//      verifies; the two-scope form is held by the one seam alone; and a
//      deployment with no publication composed is unchanged.

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.Secrets
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.ArtefactSigning
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Fixtures ────────────────────────────────────────────────────────

let private north = "north"
let private south = "south"
let private group = "group"

/// Owners, and one member who is not an owner.
let private roles: Map<string * string, TeamRole> =
    Map.ofList [
        (north, "ann"), TeamRole.Owner
        (south, "bob"), TeamRole.Owner
        (group, "cat"), TeamRole.Owner
        (group, "dan"), TeamRole.Member
        (north, "eve"), TeamRole.Member
    ]

let private teamRole (teamId: string) (userId: string) : Async<TeamRole option> =
    async.Return(roles.TryFind(teamId, userId))

let private teamScope (teamId: string) : ResolvedScope =
    ScopeResolution.ofStorageScope {
        ScopeId = teamId
        Container = $"team-{teamId}"
        Persist = true
    }

/// Run `body` as the platform's request plumbing would for `userId` in
/// `teamId`: with that viewer established as the ambient request viewer.
let private asUser (userId: string) (teamId: string) (body: Async<'T>) : 'T =
    async {
        use _ =
            RequestViewerContext.establish (fun () ->
                Some {
                    UserId = userId
                    ActiveTeamId = Some teamId
                    IsPlatformAdmin = false
                })

        return! body
    }
    |> Async.RunSynchronously

let private metric (id: string) : MetricDefinition = {
    Id = id
    Name = id
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = HigherIsBetter
    DisplayFormat = ""
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private products: SubjectDefinition = {
    Id = "products"
    Name = "Products"
    Levels = [ "sku" ]
    Calendar = None
}

let private groupProducts: SubjectDefinition = {
    Id = "group-products"
    Name = "Group products"
    Levels = [ "region"; "sku" ]
    Calendar = None
}

let private registry: IMetricRegistry =
    MetricRegistry.build [
        for id in [ "revenue"; "margin" ] do
            {
                MetricRegistration.Module = "sales"
                Definition = metric id
            }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = products
        }
        {
            SubjectRegistration.Module = "group"
            Definition = groupProducts
        }
    ]

/// The source table: regional sales per SKU. `margin` is published under a
/// named policy so the disclosure arms have something to decide.
let private regionalSales: FactTableDefinition = {
    Id = "regional-sales"
    SchemaVersion = 1
    Hierarchy = "products"
    Level = "sku"
    Columns = [
        FactTableDefinition.column "revenue" FactTableValueShape.Scalar
        {
            (FactTableDefinition.column "margin" FactTableValueShape.Scalar) with
                Disclosure = Some(FactTableDisclosure.Restricted "sales")
        }
    ]
    PeriodGrain = FactTablePeriodGrain.Month
    ProducingOperation = "regional-rollup"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.Replace
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

/// The target table: the origin team is the root level of its hierarchy.
let private groupSales: FactTableDefinition = {
    regionalSales with
        Id = "group-sales"
        Hierarchy = "group-products"
        Columns = [
            FactTableDefinition.column "revenue" FactTableValueShape.Scalar
            FactTableDefinition.column "margin" FactTableValueShape.Scalar
        ]
        ProducingOperation = "group-consolidation"
}

let private tables: IFactTableRegistry =
    FactTableRegistry.build [
        {
            FactTableRegistration.Module = "sales"
            Definition = regionalSales
        }
        {
            FactTableRegistration.Module = "group"
            Definition = groupSales
        }
    ] [ BindAllFactTables DefaultFactTableWriter.Destination ]

let private groupTarget = FactPublicationTarget.create "group-sales" "region"

let private september: TemporalExtent = {
    From = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-09"
}

let private row (sku: string) (revenue: decimal) (margin: decimal) : FactTableRow = {
    Subject = [ sku ]
    Period = september
    Values = Map.ofList [ "revenue", Scalar revenue; "margin", Scalar margin ]
}

/// A `Restricted` policy the deployment permits at every door, so what
/// decides it is the door list and the team's output level.
let private salesPolicy (surfaces: FactEgressSurface list) : DisclosurePolicy = {
    PolicyRef = "sales"
    Mode = Plain
    PermitSurfaces = surfaces
    ContributorScope = None
}

let private everyDoor = [
    FactRetrieval
    FactToolResult
    FactNarrativePublication
    FactExport
    FactWebhook
    FactPeerEgress
    FactBrowse
    FactTeamPublication
]

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

/// A verifier that refuses everything — the tamper arm of the signed profile.
type private RefusingVerifier() =
    interface IArtefactVerifier with
        member _.Verify(_, _) =
            async.Return(Error VerificationError.Tampered)

type private World = {
    Publication: IFactPublication
    Store: IFactStore
    Storage: InMemoryBlobStorage
    Events: IEventStore
    Writer: IFactTableWriter
    /// Phase 935 — a second service over the same substrate, as a restarted
    /// process builds it: durable over `carrier`, or in process without one.
    Restart: ScopeCarrier option -> IFactPublication
}

type private WorldOptions = {
    Permit: FactEgressSurface list
    OutputLevel: TeamVisibilityLevel option
    Signing: PublicationSigning
    Verifier: (ISecretStore -> IArtefactVerifier) option
    /// Decorates the table writer the world composes (Phase 932): the
    /// writer the publication service is handed is the decorated one.
    Decorate: IFactTableWriter -> IFactTableWriter
    /// Phase 935 — build the service with a carrier over the world's own
    /// key ring, so grants persist.
    Durable: bool
}

let private defaults = {
    Permit = everyDoor
    OutputLevel = None
    Signing = PublicationSigning.RecordedProvenance
    Verifier = None
    Decorate = id
    Durable = false
}

let private worldWith (options: WorldOptions) : World =
    let storage = InMemoryBlobStorage()
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let mutable tick = DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc)

    let clock () =
        tick <- tick.AddSeconds 1.0
        tick

    let store =
        BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

    let writer =
        DefaultFactTableWriter.createWithClock store storage events tables (Some registry) clock
        |> options.Decorate

    let taint = DisclosureTaintConfig.ofLists [ salesPolicy options.Permit ] []
    let plain = FactDisclosureGate(store, events, taint = taint)

    let gate =
        match options.OutputLevel with
        | None -> plain :> IFactDisclosureGate
        | Some level ->
            plain.WithViewerAwareness {
                Resolver = None
                Sources = {
                    OutputVisibility = fun _ -> async.Return(Ok level)
                    TeamRole = teamRole
                    IsTeam = fun scopeId -> async.Return(List.contains scopeId [ north; south; group ])
                }
            }

    let secrets = InMemorySecretStore() :> ISecretStore

    let signer, verifier =
        match options.Signing with
        | PublicationSigning.RecordedProvenance -> None, None
        | PublicationSigning.SignedCertificate ->
            let signer =
                DefaultArtefactSigner.createSystem secrets (AuditLog.NoOpAuditLog()) "publication-v1" Ed25519

            let verifier =
                match options.Verifier with
                | Some make -> make secrets
                | None -> DefaultArtefactVerifier.create secrets

            Some signer, Some verifier

    let config = {
        FactPublicationConfig.create [ groupTarget ] with
            Signing = options.Signing
    }

    let build (carrier: ScopeCarrier option) =
        let create =
            match carrier with
            | Some carrier -> FactPublication.createDurable carrier
            | None -> FactPublication.createWith

        create config store storage events gate tables (Some registry) (Some writer) teamRole signer verifier clock

    {
        Publication =
            build (
                if options.Durable then
                    Some(ScopeCarrier.ofKeyRepository (BlobXmlRepository(storage :> BlobStorage.IBlobStorage)))
                else
                    None
            )
        Restart = build
        Store = store
        Storage = storage
        Events = events
        Writer = writer
    }

let private world () = worldWith defaults

/// Commit a run of the source table in a team's own scope.
let private commitSource (w: World) (teamId: string) (rows: FactTableRow list) : unit =
    let run =
        w.Writer.OpenRun(teamId, regionalSales.Id)
        |> Async.RunSynchronously
        |> Result.defaultWith (fun e -> failtestf "open: %s" (FactTableWriteError.describe e))

    w.Writer.WriteRows(teamId, run.RunId, rows)
    |> Async.RunSynchronously
    |> Result.defaultWith (fun e -> failtestf "write: %s" (FactTableWriteError.describe e))
    |> ignore

    w.Writer.Commit(teamId, run.RunId)
    |> Async.RunSynchronously
    |> Result.defaultWith (fun e -> failtestf "commit: %s" (FactTableWriteError.describe e))
    |> ignore

let private proposal (source: string) : PublicationProposal = {
    SourceTeam = source
    SourceTable = regionalSales.Id
    TargetTeam = group
    TargetTable = groupSales.Id
    Visibility = PublicationVisibility.AggregatesOnly
}

let private owners = Map.ofList [ north, "ann"; south, "bob"; group, "cat" ]

let private propose (w: World) (source: string) : PublicationGrant =
    asUser owners[source] source (w.Publication.Propose(teamScope source, proposal source))
    |> Result.defaultWith (fun e -> failtestf "propose: %s" (PublicationRefusal.describe e))

let private consent (w: World) (teamId: string) (grantId: string) : PublicationGrant =
    asUser owners[teamId] teamId (w.Publication.Consent(teamScope teamId, grantId))
    |> Result.defaultWith (fun e -> failtestf "consent: %s" (PublicationRefusal.describe e))

/// A grant from `source` to the group with both consents.
let private grantInForce (w: World) (source: string) : PublicationGrant =
    let grant = propose w source
    consent w source grant.GrantId |> ignore
    consent w group grant.GrantId

/// The publication job's body: it runs under the source's own scope.
let private publish (w: World) (source: string) (grantId: string) =
    w.Publication.Publish(teamScope source, grantId) |> Async.RunSynchronously

let private published (w: World) (source: string) (grantId: string) : PublicationReceipt =
    publish w source grantId
    |> Result.defaultWith (fun e -> failtestf "publish: %s" (PublicationRefusal.describe e))

/// Every current fact in a scope.
let private currentFacts (w: World) (scopeId: string) : Fact list =
    w.Store.Query(scopeId, FactQuery.all) |> Async.RunSynchronously

/// The target's current, present (non-absent) facts of the group table.
let private groupRows (w: World) : Fact list =
    currentFacts w group
    |> List.filter (fun f ->
        f.Subject.Hierarchy = groupProducts.Id
        && (match f.Value with
            | Absent _ -> false
            | _ -> true))

let private population (w: World) (prefix: string list option) : PopulationResult =
    let query = {
        PopulationQuery.create (MetricRef "revenue") groupProducts.Id with
            Level = Some 2
            PathPrefix = prefix
            TopK = 100
    }

    w.Store.QueryPopulation(teamScope group, query)
    |> Async.RunSynchronously
    |> Result.defaultWith (fun e -> failtestf "population: %s" e)

let private publicationEvents (w: World) (scopeId: string) : (ModuleEvent * FactPublicationEvent) list =
    w.Events.ReadBySource(scopeId, FactEvents.SourceModule)
    |> Async.RunSynchronously
    |> List.filter (fun e -> e.EventType.StartsWith "FactPublication")
    |> List.map (fun e ->
        e,
        JsonSerializer.Deserialize<FactPublicationEvent>(
            e.Payload,
            ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
        ))

let private ledgerBlobs (w: World) (scopeId: string) : string list =
    (w.Storage :> BlobStorage.IBlobStorage).List(scopeId, "_fact-publication/")
    |> Async.RunSynchronously

// ─── 1. A consolidated view by publishing ────────────────────────────

let private consolidationTests =
    testList "a consolidated view is built by publishing" [

        test "two sources publish; the target answers across both and per origin, in its own scope" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m; row "sku-2" 300m 30m ]
            commitSource w south [ row "sku-1" 200m 20m ]
            let fromNorth = grantInForce w north
            let fromSouth = grantInForce w south

            let r1 = published w north fromNorth.GrantId
            let r2 = published w south fromSouth.GrantId
            Expect.equal r1.RowsPublished 2 "north published both of its rows"
            Expect.equal r2.RowsPublished 1 "south published its row"

            let everything = population w None
            Expect.equal everything.Stats.SubjectCount 3 "three SKUs across both origins"

            Expect.equal
                (everything.Ranked |> List.map _.Subject.Path)
                [ [ north; "sku-2" ]; [ south; "sku-1" ]; [ north; "sku-1" ] ]
                "one ranking over both origins, each row under its origin team"

            let northOnly = population w (Some [ north ])
            Expect.equal northOnly.Stats.SubjectCount 2 "a path-prefix query scopes to one origin"

            Expect.isTrue
                (northOnly.Ranked |> List.forall (fun f -> f.Subject.Path.Head = north))
                "and admits no other origin's rows"
        }

        test "each row carries its origin team and run, and its provenance is Imported" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            let receipt = published w north grant.GrantId

            let origin =
                w.Writer.Status(north, regionalSales.Id)
                |> Async.RunSynchronously
                |> Result.map (fun s -> s.LastCommit.Value.Watermark |> FactTableWatermark.render)
                |> Result.defaultWith (fun _ -> failtest "status")

            Expect.equal receipt.OriginRun (Some origin) "the receipt names the source's committed run"

            for fact in groupRows w do
                Expect.equal fact.Method (Imported(PublicationGrant.certificateRef grant)) "Imported, naming the grant"
                Expect.equal fact.Subject.Path.Head north "the origin team heads the path"

                Expect.stringContains
                    (fact.Evidence.TriggerRef |> Option.defaultValue "")
                    (sprintf "origin-run:%s" origin)
                    "the evidence names the origin run"

            Expect.isNonEmpty (groupRows w) "the target holds the published row"
        }

        test "a republished row stays in one lineage: a changed value supersedes, an unchanged one is idempotent" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m; row "sku-2" 50m 5m ]
            let grant = grantInForce w north
            published w north grant.GrantId |> ignore
            commitSource w north [ row "sku-1" 120m 10m; row "sku-2" 50m 5m ]
            published w north grant.GrantId |> ignore

            let revenue =
                groupRows w
                |> List.filter (fun f -> f.Metric.Value = "revenue")
                |> List.map (fun f -> f.Subject.Path, f.Value)
                |> Map.ofList

            Expect.equal revenue[[ north; "sku-1" ]] (Scalar 120m) "the new value is the current one"
            Expect.equal revenue.Count 2 "no duplicate current heads"
        }

        test "the declaration states the origin level, and a non-root origin level is a defect" {
            Expect.isEmpty
                (FactPublication.defects (FactPublicationConfig.create [ groupTarget ]) tables (Some registry))
                "valid"

            let wrong = FactPublicationTarget.create "group-sales" "sku"

            let defects =
                FactPublication.defects (FactPublicationConfig.create [ wrong ]) tables (Some registry)

            Expect.isNonEmpty defects "the origin must be the hierarchy's root"
            Expect.stringContains (String.concat " " defects) "root level" "and the defect says so"

            let undeclared =
                FactPublication.defects
                    (FactPublicationConfig.create [ FactPublicationTarget.create "nope" "region" ])
                    tables
                    (Some registry)

            Expect.stringContains (String.concat " " undeclared) "not a declared fact table" "names the table"
        }

        test "a grant below the target's required visibility is refused and writes nothing" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]

            let strict =
                FactPublication.createWith
                    (FactPublicationConfig.create [
                        {
                            groupTarget with
                                Requires = PublicationVisibility.Full
                        }
                    ])
                    w.Store
                    w.Storage
                    w.Events
                    (FactDisclosureGate.create w.Store w.Events)
                    tables
                    (Some registry)
                    (Some w.Writer)
                    teamRole
                    None
                    None
                    (fun () -> DateTime.UtcNow)

            let grant =
                asUser "ann" north (strict.Propose(teamScope north, proposal north))
                |> Result.defaultWith (fun _ -> failtest "propose")

            asUser "ann" north (strict.Consent(teamScope north, grant.GrantId)) |> ignore
            asUser "cat" group (strict.Consent(teamScope group, grant.GrantId)) |> ignore

            match strict.Publish(teamScope north, grant.GrantId) |> Async.RunSynchronously with
            | Error(PublicationVisibilityBelowRequired(PublicationVisibility.AggregatesOnly, PublicationVisibility.Full)) ->
                ()
            | other -> failtestf "expected a visibility refusal, got %A" other

            Expect.isEmpty (groupRows w) "nothing written"
        }
    ]

// ─── 2. Nothing crosses without a grant in force ─────────────────────

let private grantTests =
    testList "nothing crosses without a grant in force" [

        test "an unpublished fact cannot be read across teams" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let northFacts = currentFacts w north
            Expect.isNonEmpty northFacts "the source holds its facts"

            Expect.isEmpty (currentFacts w group) "the target's scope holds none of them"

            for fact in northFacts do
                let seen = w.Store.Get(teamScope group, fact.FactId) |> Async.RunSynchronously

                Expect.isNone seen "a source fact id resolves to nothing in the target's scope"

            // The target cannot run the source's publication either: the job
            // body refuses any scope but the source's own.
            match publish w group "no-such-grant" with
            | Error(PublicationGrantUnknown _) -> ()
            | other -> failtestf "expected unknown grant, got %A" other
        }

        test "with no grant, nothing is written to the target" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]

            match publish w north "missing" with
            | Error(PublicationGrantUnknown "missing") -> ()
            | other -> failtestf "expected unknown grant, got %A" other

            Expect.isEmpty (currentFacts w group) "no fact in the target"
            Expect.isEmpty (ledgerBlobs w group) "no ledger entry in the target"
        }

        test "a grant with only the source's consent publishes nothing" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = propose w north
            consent w north grant.GrantId |> ignore

            match publish w north grant.GrantId with
            | Error(PublicationGrantNotInForce(_, [ PublicationSide.Target ])) -> ()
            | other -> failtestf "expected the missing target consent, got %A" other

            Expect.isEmpty (currentFacts w group) "no fact in the target"
        }

        test "a grant with only the target's consent publishes nothing" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = propose w north
            consent w group grant.GrantId |> ignore

            match publish w north grant.GrantId with
            | Error(PublicationGrantNotInForce(_, [ PublicationSide.Source ])) -> ()
            | other -> failtestf "expected the missing source consent, got %A" other

            Expect.isEmpty (currentFacts w group) "no fact in the target"
        }

        test "with both consents it publishes (the arm above is not vacuous)" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            Expect.isTrue (PublicationGrant.inForce grant) "in force"
            published w north grant.GrantId |> ignore
            Expect.isNonEmpty (groupRows w) "rows arrived"
        }

        test "consent is an owner's act: a member, a non-party and no viewer are refused" {
            let w = world ()
            let grant = propose w north

            match asUser "dan" group (w.Publication.Consent(teamScope group, grant.GrantId)) with
            | Error(PublicationNotOwner(team, Some "dan")) -> Expect.equal team group "names the team"
            | other -> failtestf "a member may not consent, got %A" other

            match asUser "bob" south (w.Publication.Consent(teamScope south, grant.GrantId)) with
            | Error(PublicationNotAParty _) -> ()
            | other -> failtestf "a third team may not consent, got %A" other

            match w.Publication.Consent(teamScope group, grant.GrantId) |> Async.RunSynchronously with
            | Error(PublicationNotOwner(_, None)) -> ()
            | other -> failtestf "no resolved viewer may not consent, got %A" other

            match asUser "cat" north (w.Publication.Consent(teamScope north, grant.GrantId)) with
            | Error(PublicationNotOwner _) -> ()
            | other -> failtestf "an owner of another team may not consent for this one, got %A" other
        }

        test "publication runs only in the source's own scope" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north

            match
                w.Publication.Publish(ResolvedScope.anonymous, grant.GrantId)
                |> Async.RunSynchronously
            with
            | Error(PublicationWrongScope(_, PublicationSide.Source, _, _)) -> ()
            | other -> failtestf "the anonymous scope publishes nothing, got %A" other

            match publish w group grant.GrantId with
            | Error(PublicationWrongScope(_, PublicationSide.Source, expected, actual)) ->
                Expect.equal (expected, actual) (north, group) "the target cannot publish on the source's behalf"
            | other -> failtestf "expected wrong scope, got %A" other

            Expect.isEmpty (currentFacts w group) "nothing written"
        }

        test "there is no notion of team rank: a team cannot publish to itself" {
            let w = world ()

            match
                asUser
                    "cat"
                    group
                    (w.Publication.Propose(
                        teamScope group,
                        {
                            proposal group with
                                SourceTeam = group
                        }
                    ))
            with
            | Error(PublicationSameTeam _) -> ()
            | other -> failtestf "expected same-team refusal, got %A" other
        }
    ]

// ─── 3. The source's gate decides what leaves ────────────────────────

let private disclosureTests =
    testList "the source's gate decides what leaves" [

        test "a row the source's gate denies at FactTeamPublication is absent from the target" {
            // `sales` is permitted everywhere EXCEPT the publication door.
            let w =
                worldWith {
                    defaults with
                        Permit = everyDoor |> List.filter ((<>) FactTeamPublication)
                }

            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            let receipt = published w north grant.GrantId
            Expect.equal receipt.RowsPublished 0 "the row's margin cell is denied, so the row is withheld"
            Expect.equal receipt.RowsWithheld 1 "and counted as withheld"
            Expect.isEmpty (groupRows w) "the row is absent from the target"

            let denied =
                w.Events.ReadBySource(north, FactEvents.SourceModule)
                |> Async.RunSynchronously
                |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.isNonEmpty denied "the source's own gate audited the deny"
            Expect.stringContains denied.Head.Payload "TeamPublication" "at the publication door"
        }

        test "the same row crosses when the door is permitted (the probe above is not vacuous)" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            Expect.equal (published w north grant.GrantId).RowsPublished 1 "published"
        }

        test "a Restricted fact is not published past its team's output level" {
            for level, expected in [ TeamVisible, 1; TeamAdmins, 0; PlatformAdmins, 0 ] do
                let w =
                    worldWith {
                        defaults with
                            OutputLevel = Some level
                    }

                commitSource w north [ row "sku-1" 100m 10m ]
                let grant = grantInForce w north

                // Even with the source owner as the ambient viewer: the
                // publication's audience is the target, so the gate decides
                // for the least-privileged viewer of the source team.
                let receipt =
                    asUser "ann" north (w.Publication.Publish(teamScope north, grant.GrantId))
                    |> Result.defaultWith (fun e -> failtestf "publish: %s" (PublicationRefusal.describe e))

                Expect.equal
                    receipt.RowsPublished
                    expected
                    (sprintf
                        "at %s the restricted margin %s"
                        (TeamVisibilityLevel.name level)
                        (if expected = 1 then "leaves" else "stays"))

                Expect.equal (groupRows w |> List.length) (expected * 2) "the target holds exactly what left"
        }

        test "disclosure narrows on import and never widens: the floor of source and target" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            published w north grant.GrantId |> ignore

            let byMetric =
                groupRows w |> List.map (fun f -> f.Metric.Value, f.Disclosure) |> Map.ofList

            Expect.equal byMetric["revenue"] Surfaceable "surfaceable on both sides stays surfaceable"

            Expect.equal
                byMetric["margin"]
                (Restricted "sales")
                "the target table declares the column Surfaceable, but the source published it Restricted: the floor is Restricted"
        }

        test "Disclosure.floor is the import rule it reuses" {
            Expect.equal (Disclosure.floor (Restricted "sales") Surfaceable) (Restricted "sales") "never widens"
            Expect.equal (Disclosure.floor Surfaceable Internal) Internal "narrows to the target's ceiling"

            Expect.equal
                (Disclosure.floor (Restricted "a") (Restricted "b"))
                Internal
                "incomparable meets at the bottom"
        }

        test "the door has its own canonical name and is decided for the least-privileged viewer" {
            Expect.equal (FactEgressSurface.toString FactTeamPublication) "TeamPublication" "canonical audit name"

            Expect.contains
                DisclosurePolicyRefSnapshot.factEgressSurfaces
                "TeamPublication"
                "the pinned vocabulary lists it"

            Expect.isFalse
                (ViewerAwareDisclosure.audienceIsRequester FactTeamPublication)
                "a publication reaches the target team, not the requester"
        }
    ]

// ─── 4. Withdrawal ───────────────────────────────────────────────────

let private withdrawalTests =
    testList "withdrawal removes the origin at the next refresh" [

        test "after withdrawal and one refresh the target holds no row from that origin" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m; row "sku-2" 300m 30m ]
            commitSource w south [ row "sku-1" 200m 20m ]
            let fromNorth = grantInForce w north
            let fromSouth = grantInForce w south
            published w north fromNorth.GrantId |> ignore
            published w south fromSouth.GrantId |> ignore

            asUser "ann" north (w.Publication.Revoke(teamScope north, fromNorth.GrantId))
            |> Result.defaultWith (fun e -> failtestf "revoke: %s" (PublicationRefusal.describe e))
            |> ignore

            Expect.equal (population w (Some [ north ])).Ranked.Length 2 "rows stay until the refresh"

            w.Publication.Refresh(teamScope group, groupSales.Id)
            |> Async.RunSynchronously
            |> Result.defaultWith (fun e -> failtestf "refresh: %s" (PublicationRefusal.describe e))
            |> ignore

            Expect.equal
                (population w (Some [ north ])).Ranked.Length
                0
                "no row from the withdrawn origin: its cells are absences, which rank nowhere"

            Expect.equal (population w (Some [ south ])).Ranked.Length 1 "the other origin is untouched"

            Expect.isEmpty
                (groupRows w |> List.filter (fun f -> f.Subject.Path.Head = north))
                "no present fact from that origin"

            match publish w north fromNorth.GrantId with
            | Error(PublicationGrantRevoked _) -> ()
            | other -> failtestf "a withdrawn grant publishes nothing, got %A" other
        }

        test "facts minted from a withdrawn origin are superseded by absences naming the withdrawal" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            published w north grant.GrantId |> ignore

            let before = groupRows w |> List.find (fun f -> f.Metric.Value = "revenue")

            asUser "cat" group (w.Publication.Revoke(teamScope group, grant.GrantId))
            |> ignore

            w.Publication.Refresh(teamScope group, groupSales.Id)
            |> Async.RunSynchronously
            |> ignore

            let after =
                currentFacts w group
                |> List.find (fun f -> f.Metric.Value = "revenue" && f.Subject.Path = [ north; "sku-1" ])

            match after.Value with
            | Absent reason ->
                Expect.stringContains reason "withdrawn" "the absence names the withdrawal"
                Expect.stringContains reason grant.GrantId "and the grant"
            | other -> failtestf "expected an absence, got %A" other

            Expect.equal after.Supersedes (Some before.FactId) "it supersedes the imported fact, in the same lineage"
            Expect.equal after.Method before.Method "one lineage: the grant's Imported method"
            Expect.isEmpty (ledgerBlobs w group |> List.filter (fun n -> n.Contains north)) "the ledger entry is gone"
        }

        test "an origin whose grant this process no longer holds reads as withdrawn (fail closed)" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            published w north grant.GrantId |> ignore

            // A second service over the same storage: the grants of the first
            // are not held, as after a restart.
            let restarted =
                FactPublication.createWith
                    (FactPublicationConfig.create [ groupTarget ])
                    w.Store
                    w.Storage
                    w.Events
                    (FactDisclosureGate.create w.Store w.Events)
                    tables
                    (Some registry)
                    (Some w.Writer)
                    teamRole
                    None
                    None
                    (fun () -> DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc))

            restarted.Refresh(teamScope group, groupSales.Id)
            |> Async.RunSynchronously
            |> ignore

            Expect.isEmpty (groupRows w) "nothing survives a grant nobody holds"
        }
    ]

// ─── 5. Audit, signing, the seam, the composition ────────────────────

/// Where the two-scope form may appear, by path under `src/`.
let private seamHome = "ToolUp.Facts.Server/Server/FactPublication.fs"

/// Where it is defined — excluded from the caller scan.
let private pairDefinition = "ToolUp.Platform.Core/Shared/Types/ResolvedScope.fs"

/// The pair's type name, assembled so this file does not match itself.
let private pairType = "ResolvedScope" + "Pair"

/// Lines of a source that READ a held second scope — a pair's members, or
/// the target consent's minted scope — outside the seam markers. Pure, so
/// the go-red arm can run it over planted text.
let private readsOutsideSeam (source: string) : string list =
    let lines = source.Split '\n'
    let mutable inSeam = false

    [
        for line in lines do
            if line.Contains("SEAM-" + "BEGIN") then
                inSeam <- true
            elif line.Contains("SEAM-" + "END") then
                inSeam <- false
            elif not inSeam then
                let trimmed = line.Trim()

                let reads =
                    trimmed.Contains "pair.Target"
                    || trimmed.Contains "pair.Source"
                    || trimmed.Contains ".ConsentedTarget"
                    || trimmed.Contains(pairType + ".ofMinted")

                if reads && not (trimmed.StartsWith "//") then
                    yield trimmed
    ]

let private auditTests =
    testList "audit, signing, the seam and the composition" [

        test "both sides audit each run, and the two records cite each other" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            let receipt = published w north grant.GrantId

            let sourceRecord, sourcePayload =
                publicationEvents w north
                |> List.find (fun (e, _) -> e.EventType = FactPublicationEvents.PublishedType)

            let targetRecord, targetPayload =
                publicationEvents w group
                |> List.find (fun (e, _) -> e.EventType = FactPublicationEvents.ReceivedType)

            Expect.equal sourceRecord.Id receipt.SourceRecordId "the source record is the receipt's"
            Expect.equal targetRecord.Id receipt.TargetRecordId "the target record is the receipt's"
            Expect.equal sourcePayload.CounterpartRecordId (Some targetRecord.Id) "the source cites the target"
            Expect.equal targetPayload.CounterpartRecordId (Some sourceRecord.Id) "the target cites the source"
            Expect.equal sourcePayload.PublicationRunId targetPayload.PublicationRunId "one run id on both"
            Expect.equal targetPayload.OriginRun receipt.OriginRun "the target names the origin run"
        }

        test "owner acts are audited in the acting team's scope" {
            let w = world ()
            let grant = grantInForce w north

            let consentedIn scopeId =
                publicationEvents w scopeId
                |> List.filter (fun (e, _) -> e.EventType = FactPublicationEvents.ConsentedType)
                |> List.map (fun (_, p) -> p.ByUserId)

            Expect.equal (consentedIn north) [ Some "ann" ] "the source owner's consent, in the source"
            Expect.equal (consentedIn group) [ Some "cat" ] "the target owner's consent, in the target"
            Expect.equal grant.SourceConsent.Value.ByUserId "ann" "recorded on the grant"
        }

        test "a refused run is audited on the source side and writes nothing" {
            let w = world ()
            let grant = propose w north
            publish w north grant.GrantId |> ignore

            let refused =
                publicationEvents w north
                |> List.filter (fun (e, _) -> e.EventType = FactPublicationEvents.RefusedType)

            Expect.hasLength refused 1 "one refusal record"
            Expect.stringContains (snd refused.Head).Reason.Value "not in force" "naming why"

            Expect.isEmpty
                (publicationEvents w group
                 |> List.filter (fun (e, _) -> e.EventType = FactPublicationEvents.ReceivedType))
                "no receipt"
        }

        test "the regulated profile signs each run and the seam verifies it" {
            let w =
                worldWith {
                    defaults with
                        Signing = PublicationSigning.SignedCertificate
                }

            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            let receipt = published w north grant.GrantId
            Expect.equal receipt.Signing PublicationSigning.SignedCertificate "signed"

            let _, payload =
                publicationEvents w group
                |> List.find (fun (e, _) -> e.EventType = FactPublicationEvents.ReceivedType)

            Expect.equal payload.SignatureKeyId (Some "publication-v1") "the target records the key"
            Expect.isNonEmpty (groupRows w) "and the rows arrived"
        }

        test "a signature the target cannot verify writes nothing" {
            let w =
                worldWith {
                    defaults with
                        Signing = PublicationSigning.SignedCertificate
                        Verifier = Some(fun _ -> RefusingVerifier() :> IArtefactVerifier)
                }

            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north

            match publish w north grant.GrantId with
            | Error(PublicationSignatureRefused _) -> ()
            | other -> failtestf "expected a signature refusal, got %A" other

            Expect.isEmpty (groupRows w) "nothing written"
            Expect.isEmpty (ledgerBlobs w group) "nothing recorded"
        }

        test "the regulated profile without a signer fails at construction" {
            let w = world ()

            Expect.throws
                (fun () ->
                    FactPublication.createWith
                        (FactPublicationConfig.signed (FactPublicationConfig.create [ groupTarget ]))
                        w.Store
                        w.Storage
                        w.Events
                        (FactDisclosureGate.create w.Store w.Events)
                        tables
                        (Some registry)
                        None
                        teamRole
                        None
                        None
                        (fun () -> DateTime.UtcNow)
                    |> ignore)
                "SignedCertificate needs a signer and a verifier"
        }

        test "the two-scope form is built from two minted scopes only" {
            Expect.isError (ResolvedScopePair.ofMinted ResolvedScope.anonymous (teamScope group)) "anonymous source"
            Expect.isError (ResolvedScopePair.ofMinted (teamScope north) ResolvedScope.anonymous) "anonymous target"
            Expect.isError (ResolvedScopePair.ofMinted (teamScope north) (teamScope north)) "one scope twice"

            match ResolvedScopePair.ofMinted (teamScope north) (teamScope group) with
            | Ok pair -> Expect.equal (pair.Source.ScopeId, pair.Target.ScopeId) (north, group) "source then target"
            | Error e -> failtest e
        }

        test "the seam classifier fires on every read outside the markers (go-red) and is quiet inside them" {
            let planted =
                String.concat "\n" [
                    "let a = pair.Target.ScopeId"
                    "let b = entry.ConsentedTarget"
                    "// " + "SEAM-" + "BEGIN"
                    "let c = pair.Target.ScopeId"
                    "// " + "SEAM-" + "END"
                    "let d = " + pairType + ".ofMinted x y"
                ]

            Expect.hasLength (readsOutsideSeam planted) 3 "two reads and a pair built outside, none inside"
        }

        test "the fact tier's only holder of two teams' scopes is the one seam" {
            let src = Path.Combine(ArchitectureFitness.repoRoot (), "src")
            let pruned = set [ "bin"; "obj"; "node_modules"; "output"; ".fable" ]

            let rec sources (dir: string) : string seq = seq {
                yield! Directory.EnumerateFiles(dir, "*.fs")

                for sub in Directory.EnumerateDirectories dir do
                    if not (pruned.Contains(Path.GetFileName sub)) then
                        yield! sources sub
            }

            let relative (path: string) =
                Path.GetRelativePath(src, path).Replace('\\', '/')

            let holders =
                sources src
                |> Seq.map (fun path -> relative path, path)
                |> Seq.filter (fun (rel, _) -> rel <> pairDefinition && not (rel.Contains ".Tests/"))
                |> Seq.filter (fun (_, path) -> (File.ReadAllText path).Contains pairType)
                |> Seq.map fst
                |> List.ofSeq

            Expect.equal holders [ seamHome ] "a second holder of the pair is a second read across teams"

            let seam = File.ReadAllText(Path.Combine(src, seamHome))
            Expect.stringContains seam ("SEAM-" + "BEGIN") "the seam is marked"

            match readsOutsideSeam seam with
            | [] -> ()
            | found ->
                failtestf
                    "the pair or the target consent's scope is read outside the seam:\n%s"
                    (found |> List.map (fun l -> "  " + l) |> String.concat "\n")
        }

        test "a deployment with no grant writes nothing anywhere" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]

            for scopeId in [ north; south; group ] do
                Expect.isEmpty (ledgerBlobs w scopeId) (sprintf "no publication ledger in %s" scopeId)
                Expect.isEmpty (publicationEvents w scopeId) (sprintf "no publication audit in %s" scopeId)

            Expect.isEmpty (currentFacts w group) "and no fact in the would-be target"
        }

        test "the compose knob leaves a deployment without the fact store unchanged" {
            let app = {
                ServerApp.empty with
                    Config = {
                        ServerApp.empty.Config with
                            FactStore = NoFactStore
                    }
            }

            let composed =
                FactsCompose.withFactPublication (FactPublicationConfig.create [ groupTarget ]) app

            Expect.isTrue (obj.ReferenceEquals(app, composed)) "NoFactStore: the very same app"
        }
    ]

// ─── 6. Consolidation runs keep the composed writer (Phase 932) ─────

/// Records every notification a channel is handed, by scope.
type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()

    /// The browse run notices published under a scope, in order.
    member _.RunNotices(scopeId: string) : FactTableRunNotice list =
        published
        |> Seq.choose (fun (scope, notification) ->
            match notification with
            | CustomNotification(key, payload) when
                scope = scopeId && key = FactBrowseLinks.RunCommittedNotificationKey
                ->
                Some(
                    JsonSerializer.Deserialize<FactTableRunNotice>(
                        payload,
                        ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
                    )
                )
            | _ -> None)
        |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue((scopeId, notification)) }

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()

/// A world whose composed writer is decorated the way `withFactBrowse`
/// decorates it: every committed run publishes one notice to its scope.
let private notifyingWorld () : World * RecordingChannel =
    let channel = RecordingChannel()

    let w =
        worldWith {
            defaults with
                Decorate = FactBrowseHandler.notifyingWriter channel
        }

    w, channel

let private composedWriterTests =
    testList "consolidation runs keep the composed writer" [

        test "an ordinary run raises one browse notice in its own scope (the probe below is not vacuous)" {
            let w, channel = notifyingWorld ()
            commitSource w north [ row "sku-1" 100m 10m ]

            Expect.equal
                (channel.RunNotices north |> List.map _.TableId)
                [ regionalSales.Id ]
                "the source run notified its own scope once"
        }

        test "a consolidation run raises the same per-run browse notice an ordinary run does" {
            let w, channel = notifyingWorld ()
            commitSource w north [ row "sku-1" 100m 10m; row "sku-2" 300m 30m ]
            let grant = grantInForce w north
            let receipt = published w north grant.GrantId

            match channel.RunNotices group with
            | [ notice ] ->
                Expect.equal notice.TableId groupSales.Id "the notice names the target table"
                Expect.equal notice.RowCount 2 "and carries the consolidation run's counts"
                Expect.equal notice.New 2 "both rows are new to the target"

                let runs =
                    w.Writer.Runs(group, groupSales.Id)
                    |> Async.RunSynchronously
                    |> Result.defaultWith (fun e -> failtestf "runs: %s" (FactTableWriteError.describe e))

                Expect.equal (runs |> List.map _.RunId) [ notice.RunId ] "the notice names the run the writer holds"

                Expect.equal
                    (Some(
                        FactTableWatermark.render (
                            runs.Head.Status
                            |> function
                                | FactTableRunStatus.Committed c -> c.Watermark
                                | other -> failtestf "the run is %A" other
                        )
                    ))
                    (Some receipt.TargetRun)
                    "the receipt names that run's watermark"
            | other -> failtestf "expected one notice in the target scope, got %d" other.Length

            for fact in groupRows w do
                Expect.equal fact.Method (Imported(PublicationGrant.certificateRef grant)) "still Imported"
        }

        test "a refresh is a consolidation run too, and notifies once" {
            let w, channel = notifyingWorld ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            published w north grant.GrantId |> ignore

            asUser owners[north] north (w.Publication.Revoke(teamScope north, grant.GrantId))
            |> Result.defaultWith (fun e -> failtestf "revoke: %s" (PublicationRefusal.describe e))
            |> ignore

            w.Publication.Refresh(teamScope group, groupSales.Id)
            |> Async.RunSynchronously
            |> Result.defaultWith (fun e -> failtestf "refresh: %s" (PublicationRefusal.describe e))
            |> ignore

            match channel.RunNotices group with
            | [ _; refresh ] ->
                Expect.equal refresh.RowCount 0 "the refresh left no row from the withdrawn origin"
                Expect.equal refresh.Removed 1 "and its notice counts the removal"
            | other -> failtestf "expected two notices in the target scope, got %d" other.Length
        }
    ]


// ─── 7. Grants survive a restart (Phase 935) ─────────────────────────

/// A carrier over the key ring the world's own storage persists — what a
/// restarted process builds over the same deployment.
let private ringOf (w: World) : ScopeCarrier =
    ScopeCarrier.ofKeyRepository (BlobXmlRepository(w.Storage :> BlobStorage.IBlobStorage))

let private durableWorld () =
    worldWith { defaults with Durable = true }

/// The persisted grant record, as the store holds it.
let private grantBlob (w: World) (grantId: string) : string =
    (w.Storage :> BlobStorage.IBlobStorage).Download("_platform", "_fact-publication/grants/" + grantId + ".json")
    |> Async.RunSynchronously
    |> Result.map Text.Encoding.UTF8.GetString
    |> Result.defaultWith (fun e -> failtestf "grant blob: %s" e)

let private rewriteGrantBlob (w: World) (grantId: string) (edit: string -> string) : unit =
    let before = grantBlob w grantId
    let after = edit before
    Expect.notEqual after before "the planted edit changed the record"

    (w.Storage :> BlobStorage.IBlobStorage)
        .Upload("_platform", "_fact-publication/grants/" + grantId + ".json", Text.Encoding.UTF8.GetBytes after)
    |> Async.RunSynchronously
    |> Result.defaultWith (fun e -> failtestf "rewrite: %s" e)
    |> ignore

let private revenueOf (w: World) (sku: string) : Fact option =
    groupRows w
    |> List.tryFind (fun f -> f.Metric.Value = "revenue" && f.Subject.Path = [ north; sku ])

let private restartTests =
    testList "grants survive a restart (Phase 935)" [

        test "without a carrier, a restart forgets the grant — the pre-935 behaviour, pinned" {
            let w = world ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            published w north grant.GrantId |> ignore

            let restarted = w.Restart None

            let held = asUser "ann" north (restarted.Grants(teamScope north))

            Expect.isEmpty held "a service built without a carrier holds nothing it did not record itself"

            match restarted.Publish(teamScope north, grant.GrantId) |> Async.RunSynchronously with
            | Error(PublicationGrantUnknown id) -> Expect.equal id grant.GrantId "the grant is gone"
            | other -> failtestf "expected the grant to be unknown after a restart, got %A" other
        }

        test "a grant recorded with a carrier survives a restart and keeps publishing" {
            let w = durableWorld ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            published w north grant.GrantId |> ignore

            // A new process: a new service and a new carrier over the same
            // storage and the same key ring.
            let restarted = w.Restart(Some(ringOf w))

            let held = asUser "ann" north (restarted.Grants(teamScope north))

            match held with
            | [ restored ] ->
                Expect.equal restored.GrantId grant.GrantId "the grant is held again"
                Expect.isTrue (PublicationGrant.inForce restored) "with both consents in force"
            | other -> failtestf "expected the one grant, got %d" other.Length

            commitSource w north [ row "sku-1" 150m 15m ]

            match restarted.Publish(teamScope north, grant.GrantId) |> Async.RunSynchronously with
            | Ok _ -> ()
            | Error e -> failtestf "publish after a restart: %s" (PublicationRefusal.describe e)

            restarted.Refresh(teamScope group, groupSales.Id)
            |> Async.RunSynchronously
            |> Result.defaultWith (fun e -> failtestf "refresh: %s" (PublicationRefusal.describe e))
            |> ignore

            match revenueOf w "sku-1" with
            | Some fact -> Expect.equal fact.Value (Scalar 150m) "the consolidation received the post-restart run"
            | None -> failtest "the origin's rows are gone after a restart"
        }

        test "a restart under a key ring that did not issue the tokens drops both consents, with a named reason" {
            let w = durableWorld ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            published w north grant.GrantId |> ignore

            let restarted = w.Restart(Some(ScopeCarrier.ephemeral ()))

            match asUser "ann" north (restarted.Grants(teamScope north)) with
            | [ restored ] ->
                Expect.isFalse (PublicationGrant.inForce restored) "the grant is held, but not in force"

                Expect.equal
                    (PublicationGrant.missingConsents restored)
                    [ PublicationSide.Source; PublicationSide.Target ]
                    "both consents were dropped"
            | other -> failtestf "expected the one grant, got %d" other.Length

            let reasons =
                publicationEvents w group
                |> List.filter (fun (e, _) -> e.EventType = FactPublicationEvents.RefusedType)
                |> List.choose (fun (_, p) -> p.Reason)

            Expect.exists
                reasons
                (fun r ->
                    r.Contains "Target team's consent was not restored"
                    && r.Contains "not issued by this deployment")
                "the target's audit trail names why its consent was dropped"

            restarted.Refresh(teamScope group, groupSales.Id)
            |> Async.RunSynchronously
            |> ignore

            match
                currentFacts w group
                |> List.tryFind (fun f -> f.Subject.Path = [ north; "sku-1" ])
            with
            | Some { Value = Absent reason } ->
                Expect.stringContains reason "no longer in force" "the origin is withdrawn with a named reason"
            | other -> failtestf "expected the origin withdrawn, got %A" other
        }

        test "an edit of the persisted grant drops the consents it would re-point; a copied token redeems nothing" {
            let w = durableWorld ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north
            let other = grantInForce w south

            // Widen the grant's visibility in the store.
            rewriteGrantBlob w grant.GrantId (fun json -> json.Replace("AggregatesOnly", "Full"))

            // Copy the south grant's target token onto nothing of its own:
            // lift it into the north grant's record in place of north's.
            let tokenOf (json: string) =
                let doc = Text.Json.JsonDocument.Parse json
                doc.RootElement.GetProperty("TargetConsentToken").GetString()

            let southToken = tokenOf (grantBlob w other.GrantId)

            rewriteGrantBlob w other.GrantId (fun json -> json.Replace(southToken, tokenOf (grantBlob w grant.GrantId)))

            let restarted = w.Restart(Some(ringOf w))
            let held = asUser "ann" north (restarted.Grants(teamScope north))

            match held |> List.tryFind (fun g -> g.GrantId = grant.GrantId) with
            | Some restored ->
                Expect.equal restored.Visibility PublicationVisibility.Full "the edit is in the record"
                Expect.isFalse (PublicationGrant.inForce restored) "but no consent speaks for it"
            | None -> failtest "expected the edited grant"

            match asUser "bob" south (restarted.Grants(teamScope south)) with
            | [ restored ] ->
                Expect.equal
                    (PublicationGrant.missingConsents restored)
                    [ PublicationSide.Target ]
                    "the lifted token was issued for another grant: the target consent is dropped, the source's stands"
            | other -> failtestf "expected the south grant, got %d" other.Length
        }

        test "a withdrawal is persisted without any carried scope" {
            let w = durableWorld ()
            commitSource w north [ row "sku-1" 100m 10m ]
            let grant = grantInForce w north

            asUser "ann" north (w.Publication.Revoke(teamScope north, grant.GrantId))
            |> Result.defaultWith (fun e -> failtestf "revoke: %s" (PublicationRefusal.describe e))
            |> ignore

            let json = grantBlob w grant.GrantId
            let doc = Text.Json.JsonDocument.Parse json

            for field in [ "SourceConsentToken"; "TargetConsentToken" ] do
                let token = doc.RootElement.GetProperty field

                Expect.isTrue
                    (token.ValueKind = Text.Json.JsonValueKind.Null)
                    (sprintf "%s is dropped when the grant is withdrawn" field)
        }
    ]

let tests =
    testList "Phase 897 — team-to-team fact publication" [
        consolidationTests
        grantTests
        disclosureTests
        withdrawalTests
        auditTests
        composedWriterTests
        restartTests
    ]