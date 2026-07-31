// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PresenceApiTests

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open Microsoft.Extensions.DependencyInjection
open FSharp.Reflection
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 622 — presence + lock platform API ────────────────────────
//
// **Scope isolation is asserted first, and deliberately so.** A presence
// roster is a list of who is working inside a tenant right now; a lock
// holder names a person and what they are editing. Getting the scope
// resolution wrong does not degrade the feature, it turns an awareness
// surface into a cross-tenant disclosure — which is strictly worse than
// having shipped no presence API at all. So the isolation properties lead
// the pack, and the functional lock / heartbeat behaviour follows.
//
// The tests drive `PresenceApiHandler.forScope` rather than an
// `HttpContext`, because that is precisely where the isolation decision
// is made: `forScope` closes over the scope and principal the composed
// path resolves server-side, and `IPresenceApi` gives a client no syntax
// for either. Two records over ONE shared substrate is therefore the
// sharpest available statement of "these two tenants cannot see each
// other" — a shared store is the condition under which a leak would
// actually be possible.

let private jsonOptions = FableConverters.create ()

/// Records every publish so the pack can assert the `_platform.*`
/// fan-out shape without async subscription timing. Same shape as the
/// Phase 442 contract packs'.
type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()

    member _.PresenceEvents =
        published
        |> Seq.choose (fun (scope, n) ->
            match n with
            | CustomNotification(key, json) when key = CollaborationTopics.Presence ->
                Some(scope, JsonSerializer.Deserialize<PresenceEvent>(json, jsonOptions))
            | _ -> None)
        |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue(scopeId, notification) }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }

/// A mutable clock the test advances by hand.
type private Clock(start: DateTime) =
    let mutable now = start
    member _.Now() = now
    member _.Advance(ts: TimeSpan) = now <- now + ts

let private expiry = TimeSpan.FromSeconds 90.0

/// One shared substrate, plus a factory for per-(scope, user) API
/// records over it — the arrangement every isolation test needs.
let private substrate (clock: Clock) =
    let channel = RecordingChannel()

    let tracker =
        InMemoryPresenceTracker(channel, expiry = expiry, now = clock.Now) :> IPresenceTracker

    let locks = InMemoryEntityLockStore(channel, now = clock.Now) :> IEntityLockStore

    let apiFor (scopeId: string) (userId: string) =
        PresenceApiHandler.forScope tracker locks scopeId userId (Some userId)

    channel, apiFor

let private loc m = PresenceLocation.ofModule m

let private entityRef (t: string) (id: string) : EntityLockRef = { EntityType = t; EntityId = id }

let tests =
    testList "Presence + lock platform API (Phase 622)" [

        // ─── 622.E — scope isolation, asserted before anything else ──

        testCaseAsync "roster is scope-isolated — a second tenant's peers are invisible (GP 4)"
        <| async {
            let _, apiFor = substrate (Clock(DateTime(2026, 1, 1)))
            let teamA = apiFor "team-a" "ada"
            let teamB = apiFor "team-b" "grace"

            let! _ = teamA.Heartbeat(loc "reports")
            let! _ = teamB.Heartbeat(loc "reports")

            let! rosterA = teamA.Roster()
            let! rosterB = teamB.Roster()

            Expect.equal (rosterA |> List.map _.UserId) [ "ada" ] "team-a sees only its own member"
            Expect.equal (rosterB |> List.map _.UserId) [ "grace" ] "team-b sees only its own member"
        }

        testCaseAsync "the roster a heartbeat returns is scope-isolated too"
        <| async {
            // The heartbeat's return value is the roster the client
            // actually renders — isolating `Roster()` while leaking
            // through `Heartbeat()` would be a leak in the path that
            // matters most, since the shell polls the latter.
            let _, apiFor = substrate (Clock(DateTime(2026, 1, 1)))
            let teamA = apiFor "team-a" "ada"
            let teamB = apiFor "team-b" "grace"

            let! _ = teamB.Heartbeat(loc "reports")
            let! returned = teamA.Heartbeat(loc "reports")

            Expect.equal (returned |> List.map _.UserId) [ "ada" ] "heartbeat's roster excludes the other tenant"
        }

        testCaseAsync "the same entity ref locks independently in two scopes"
        <| async {
            // A lock ref is `type + id`, with no tenant component: two
            // tenants can hold entities whose ids collide. If the store
            // key were not scope-qualified, tenant B acquiring would be
            // refused by tenant A's lease — leaking both that the entity
            // is being edited and by whom.
            let _, apiFor = substrate (Clock(DateTime(2026, 1, 1)))
            let teamA = apiFor "team-a" "ada"
            let teamB = apiFor "team-b" "grace"
            let sharedId = entityRef "Invoice" "42"

            let! outcomeA = teamA.AcquireLock sharedId
            let! outcomeB = teamB.AcquireLock sharedId

            match outcomeA, outcomeB with
            | LockOutcome.Acquired a, LockOutcome.Acquired b ->
                Expect.equal a.Holder "ada" "team-a holds its own lease"
                Expect.equal b.Holder "grace" "team-b holds its own lease, uncontested"
            | _ -> failtest "both tenants must acquire the same ref independently"

            let! holderA = teamA.LockHolder sharedId
            Expect.equal (holderA |> Option.map _.Holder) (Some "ada") "team-a never sees team-b's holder"
        }

        testCaseAsync "presence events publish on the caller's own scope, never a shared one"
        <| async {
            // The fan-out is the other way presence could cross a
            // boundary: a correctly-isolated roster read is no help if
            // the SSE event announcing it goes out on a scope every
            // tenant subscribes to.
            let channel, apiFor = substrate (Clock(DateTime(2026, 1, 1)))
            let! _ = (apiFor "team-a" "ada").Heartbeat(loc "reports")

            let scopes = channel.PresenceEvents |> List.map fst |> List.distinct
            Expect.equal scopes [ "team-a" ] "published only on the caller's own scope"
        }

        // ─── 622.B — every method is auth-classified (Phase 69d) ─────

        test "every IPresenceApi method carries an auth classification" {
            // The dispatcher's startup classifier refuses to boot on an
            // unclassified method, so an unannotated field added later
            // would take the whole deployment down rather than fail
            // open — but it would do so at a consumer's start-up, not
            // here. Matching on the attribute's simple name is what the
            // classifier itself does (it recognises both the Core and
            // the Remoting.Server families that way).
            let unclassified =
                FSharpType.GetRecordFields typeof<IPresenceApi>
                |> Array.filter (fun field ->
                    field.GetCustomAttributes false
                    |> Array.exists (fun attr -> attr.GetType().Name = "TenantScopedAttribute")
                    |> not)
                |> Array.map _.Name

            Expect.isEmpty unclassified "every method must be [<TenantScoped>]"
        }

        // ─── 622.E — lock lifecycle + contention ─────────────────────

        testCaseAsync "a second user is refused and told who holds the lease"
        <| async {
            let _, apiFor = substrate (Clock(DateTime(2026, 1, 1)))
            let ada = apiFor "team-a" "ada"
            let grace = apiFor "team-a" "grace"
            let invoice = entityRef "Invoice" "42"

            let! first = ada.AcquireLock invoice
            let! second = grace.AcquireLock invoice

            match first with
            | LockOutcome.Acquired lease -> Expect.equal lease.Holder "ada" "first caller acquires"
            | LockOutcome.HeldByOther _ -> failtest "an uncontested lock must be granted"

            match second with
            | LockOutcome.HeldByOther lease ->
                Expect.equal lease.Holder "ada" "the refusal names the live holder"
                Expect.equal lease.Ref invoice "and the ref it holds"
            | LockOutcome.Acquired _ -> failtest "a live lease held by another user must refuse"
        }

        testCaseAsync "renew extends the caller's own lease"
        <| async {
            let clock = Clock(DateTime(2026, 1, 1))
            let _, apiFor = substrate clock
            let ada = apiFor "team-a" "ada"
            let invoice = entityRef "Invoice" "42"

            let! acquired = ada.AcquireLock invoice
            clock.Advance(TimeSpan.FromSeconds 30.0)
            let! renewed = ada.RenewLock invoice

            match acquired, renewed with
            | LockOutcome.Acquired a, LockOutcome.Acquired r ->
                Expect.isGreaterThan r.ExpiresAt a.ExpiresAt "renew pushes the expiry out"
            | _ -> failtest "the holder's own renew must succeed"
        }

        testCaseAsync "release frees the lease for the next caller"
        <| async {
            let _, apiFor = substrate (Clock(DateTime(2026, 1, 1)))
            let ada = apiFor "team-a" "ada"
            let grace = apiFor "team-a" "grace"
            let invoice = entityRef "Invoice" "42"

            let! _ = ada.AcquireLock invoice
            do! ada.ReleaseLock invoice
            let! afterRelease = grace.AcquireLock invoice

            match afterRelease with
            | LockOutcome.Acquired lease -> Expect.equal lease.Holder "grace" "the freed slot is takeable"
            | LockOutcome.HeldByOther _ -> failtest "a released lease must not still block"
        }

        testCaseAsync "a non-holder cannot release someone else's lease"
        <| async {
            // Release is scoped to the caller's own lease. Were it not,
            // any member of a tenant could silently steal an entity out
            // from under whoever was editing it.
            let _, apiFor = substrate (Clock(DateTime(2026, 1, 1)))
            let ada = apiFor "team-a" "ada"
            let grace = apiFor "team-a" "grace"
            let invoice = entityRef "Invoice" "42"

            let! _ = ada.AcquireLock invoice
            do! grace.ReleaseLock invoice

            let! holder = ada.LockHolder invoice
            Expect.equal (holder |> Option.map _.Holder) (Some "ada") "the original holder still holds it"
        }

        testCaseAsync "an expired lease is re-acquirable and reports no holder"
        <| async {
            let clock = Clock(DateTime(2026, 1, 1))
            let _, apiFor = substrate clock
            let ada = apiFor "team-a" "ada"
            let grace = apiFor "team-a" "grace"
            let invoice = entityRef "Invoice" "42"

            let! _ = ada.AcquireLock invoice
            clock.Advance(PresenceApi.lockTtl + TimeSpan.FromSeconds 1.0)

            let! holder = ada.LockHolder invoice
            Expect.isNone holder "a lapsed lease reports no holder"

            let! taken = grace.AcquireLock invoice

            match taken with
            | LockOutcome.Acquired lease -> Expect.equal lease.Holder "grace" "a lapsed lease is re-acquirable"
            | LockOutcome.HeldByOther _ -> failtest "a lapsed lease must not block"
        }

        // ─── 622.E — heartbeat expiry + the Join/Move/Heartbeat fold ──

        testCaseAsync "a peer that stops beating expires out of the roster"
        <| async {
            let clock = Clock(DateTime(2026, 1, 1))
            let _, apiFor = substrate clock
            let ada = apiFor "team-a" "ada"

            let! present = ada.Heartbeat(loc "reports")
            Expect.equal (present |> List.map _.UserId) [ "ada" ] "present after the first beat"

            clock.Advance(expiry + TimeSpan.FromSeconds 1.0)
            let! roster = ada.Roster()
            Expect.isEmpty roster "expired out once the heartbeat window lapsed"
        }

        testCaseAsync "a beat after expiry re-joins — and announces it"
        <| async {
            // This is the fold's reason for existing. `IPresenceTracker`
            // contracts `Heartbeat` as a no-op for a peer that is no
            // longer present, so a client that only ever heartbeats
            // would vanish after one missed window and never come back.
            // Asserting the `Joined` EVENT (not merely the restored
            // roster) is what distinguishes taking the Join branch from
            // the in-memory tracker's more lenient silent revival — the
            // roster alone would pass either way, and only the event
            // tells the rest of the tenant the peer is back.
            let clock = Clock(DateTime(2026, 1, 1))
            let channel, apiFor = substrate clock
            let ada = apiFor "team-a" "ada"

            let! _ = ada.Heartbeat(loc "reports")
            clock.Advance(expiry + TimeSpan.FromSeconds 1.0)
            let! restored = ada.Heartbeat(loc "reports")

            Expect.equal (restored |> List.map _.UserId) [ "ada" ] "back on the roster"

            let joins =
                channel.PresenceEvents
                |> List.filter (fun (_, e) -> e.Change = PresenceChange.Joined)

            Expect.equal joins.Length 2 "the re-appearance announced a second Joined"
        }

        testCaseAsync "a beat from a new location announces Moved, not Joined"
        <| async {
            let clock = Clock(DateTime(2026, 1, 1))
            let channel, apiFor = substrate clock
            let ada = apiFor "team-a" "ada"

            let! _ = ada.Heartbeat(loc "reports")
            let! moved = ada.Heartbeat(loc "settings")

            Expect.equal (moved |> List.map _.Location) [ loc "settings" ] "roster carries the new location"

            let changes = channel.PresenceEvents |> List.map (fun (_, e) -> e.Change)
            Expect.equal changes [ PresenceChange.Joined; PresenceChange.Moved ] "join then move"
        }

        testCaseAsync "a beat from an unchanged location announces nothing"
        <| async {
            // The quiet path matters: the shell beats every 20 seconds
            // per tab, and announcing each one would turn an awareness
            // feature into a fan-out storm across the tenant.
            let clock = Clock(DateTime(2026, 1, 1))
            let channel, apiFor = substrate clock
            let ada = apiFor "team-a" "ada"

            let! _ = ada.Heartbeat(loc "reports")
            let! _ = ada.Heartbeat(loc "reports")
            let! _ = ada.Heartbeat(loc "reports")

            Expect.equal channel.PresenceEvents.Length 1 "only the initial Joined was published"
        }

        testCaseAsync "leave removes the caller and announces Left"
        <| async {
            let clock = Clock(DateTime(2026, 1, 1))
            let channel, apiFor = substrate clock
            let ada = apiFor "team-a" "ada"
            let grace = apiFor "team-a" "grace"

            let! _ = ada.Heartbeat(loc "reports")
            let! _ = grace.Heartbeat(loc "reports")
            do! ada.Leave()

            let! roster = grace.Roster()
            Expect.equal (roster |> List.map _.UserId) [ "grace" ] "the departed peer is gone"

            let lefts =
                channel.PresenceEvents
                |> List.filter (fun (_, e) -> e.Change = PresenceChange.Left)

            Expect.equal (lefts |> List.map (fun (_, e) -> e.Peer.UserId)) [ "ada" ] "Left names the departed peer"
        }

        // ─── 622.D — the hand-mounted consumer path still composes ───

        test "EnabledPresence still resolves the substrate for a hand-mounted API" {
            // The documented pre-622 contract is "the SDK registers the
            // substrate, the deployment owns the wire and the client
            // mounting", and a live consumer builds on exactly that. The
            // batteries-included path is additive, so what that consumer
            // depends on — resolving the two services out of DI to back
            // its OWN module-owned API — has to keep working untouched.
            let services = ServiceCollection()
            let channel = RecordingChannel()

            let pair =
                Some(
                    InMemoryPresenceTracker(channel) :> IPresenceTracker,
                    InMemoryEntityLockStore(channel) :> IEntityLockStore
                )

            ComposeNotifications.registerPresenceSubstrate services pair
            let provider = services.BuildServiceProvider()

            Expect.isNotNull (provider.GetService typeof<IPresenceTracker>) "IPresenceTracker resolves"
            Expect.isNotNull (provider.GetService typeof<IEntityLockStore>) "IEntityLockStore resolves"
        }

        test "a hand-mounted consumer can build the platform handler over its own scope" {
            // `forScope` is public precisely so a deployment that wants
            // the platform semantics but its own route (its own auth
            // gate, its own scope convention) can build the handler
            // itself rather than reimplementing the fold.
            let _, apiFor = substrate (Clock(DateTime(2026, 1, 1)))
            let custom = apiFor "consumer-chosen-scope" "user-1"
            let roster = custom.Roster() |> Async.RunSynchronously
            Expect.isEmpty roster "an unheard-of scope starts empty rather than throwing"
        }

        test "NoPresence registers nothing" {
            // GP 13 in its most literal form: the default composes no
            // service at all, so nothing downstream can accidentally
            // depend on presence being there.
            let services = ServiceCollection()
            ComposeNotifications.registerPresenceSubstrate services None
            let provider = services.BuildServiceProvider()

            Expect.isNull (provider.GetService typeof<IPresenceTracker>) "no tracker registered"
            Expect.isNull (provider.GetService typeof<IEntityLockStore>) "no lock store registered"
        }
    ]