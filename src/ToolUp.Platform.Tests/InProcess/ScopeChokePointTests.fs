// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ScopeChokePointTests

// ─── Phase 797 — scope as a choke point ────────────────────────────
//
// Before this phase every fact door read its storage scope as a STRING
// from the request items, with a fallback to the user id and then to a
// literal, and the store keyed on whatever it was handed. The
// admissibility theorem over the model's input had to ASSUME the scope on
// a disclosed fact was the principal's, because nothing made it so.
//
// Now the scope is a value only the platform's scope resolution can mint:
// `ResolvedScope` has a private representation and an internal
// constructor; `ScopeResolution.remember` is the middleware's one write
// and `ScopeResolution.forRequest` the doors' one read; the store and the
// gate take the type. Three things are pinned here, each in the direction
// the phase's acceptance criteria name:
//
//   A. UNCONSTRUCTIBLE. A store handed a scope the resolver did not mint
//      cannot be constructed — the compiler refuses it, and this pack
//      pins the shape that makes the compiler refuse it (no public
//      constructor, no public union case, the mint internal) so a later
//      "helpful" public constructor cannot arrive silently.
//   B. NOT READ FROM THE BAG. A `StorageScope` planted in the request
//      items by anything other than the middleware's mint does not reach
//      a door; the door reads the anonymous scope instead. This is the
//      leaky-store test of Phases 559 / 703 inverted: not "a leak is
//      re-denied at the gate" but "the scope cannot be leaked INTO".
//   C. THE SOURCE SAYS SO. A source-reading guard over the three doors
//      refuses the old spelling — a `"ToolUp.StorageScope"` read, a
//      `StorageScope` pattern, the `scopeIdOf` fallback reader — with a classifier
//      pinned in both directions so the guard is known to fire before it
//      is trusted.
//
// The existing re-denial tests (a leaky store's cross-scope member is
// denied at the gate) are kept where they were; belt and braces both
// still hold.

open System
open System.IO
open System.Reflection
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ── Harness ───────────────────────────────────────────────────────

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ "acme" ]
    }
    Metric = MetricRef "revenue"
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ "h1" ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

let private queryArgs =
    """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue"}"""

let private storage (scopeId: string) : StorageScope = {
    ScopeId = scopeId
    Container = "container-" + scopeId
    Persist = true
}

let private newScopeId () = "team-" + Guid.NewGuid().ToString("N")

let private freshTier () =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    let gate = FactDisclosureGate.create store events
    store, gate

let private assertUnder (store: IFactStore) (scope: ResolvedScope) (d: FactDraft) : Fact =
    match store.Assert(scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

let private factsOf (raw: string) : JsonElement list =
    (JsonDocument.Parse raw).RootElement.GetProperty("facts").EnumerateArray()
    |> Seq.map _.Clone()
    |> List.ofSeq

/// A request whose DI can serve the composed store + gate, with NOTHING
/// scope-shaped on it unless a case adds it.
let private bareRequest (store: IFactStore) (gate: IFactDisclosureGate) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IFactStore>(store) |> ignore
    services.AddSingleton<IFactDisclosureGate>(gate) |> ignore
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.UserId"] <- box "user-1"
    ctx :> HttpContext

// ── A. Unconstructible ────────────────────────────────────────────

let private resolvedScopeType = typeof<ResolvedScope>

let private publicSurfaceTests =
    testList "A — a scope the resolver did not mint cannot be constructed" [

        test "ResolvedScope exposes no public constructor" {
            let ctors =
                resolvedScopeType.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance)

            Expect.isEmpty ctors "a public constructor would let any caller mint a scope from a string"
        }

        test "ResolvedScope exposes no public union case — FSharpType does not even see it as a union" {
            // With a private representation the case constructors, tags
            // and `New*` factories are all non-public; FSharp.Reflection's
            // public view therefore reports NOT a union, and only the
            // non-public view sees the two cases.
            Expect.isFalse (FSharpType.IsUnion resolvedScopeType) "no public case is visible"

            Expect.isTrue
                (FSharpType.IsUnion(resolvedScopeType, BindingFlags.NonPublic))
                "the representation IS a union — it is private, not absent"

            let cases =
                FSharpType.GetUnionCases(resolvedScopeType, BindingFlags.NonPublic ||| BindingFlags.Public)

            Expect.sequenceEqual
                (cases |> Array.map _.Name |> Array.sort)
                [| "AnonymousScope"; "Resolved" |]
                "exactly the two cases the phase names — a resolved scope and the explicit anonymous one"
        }

        test "the mint on the core module is internal, and the anonymous scope is the only public entry" {
            let moduleType =
                resolvedScopeType.Assembly.GetType("ToolUp.Platform.ResolvedScopeModule")

            Expect.isNotNull moduleType "the companion module compiles under the ModuleSuffix name"

            let mint =
                moduleType.GetMethod("ofStorageScope", BindingFlags.NonPublic ||| BindingFlags.Static)

            Expect.isNotNull mint "the mint exists — the middleware needs it"
            Expect.isTrue mint.IsAssembly "and it is INTERNAL: reachable through InternalsVisibleTo, not by a consumer"

            Expect.isNull
                (moduleType.GetMethod("ofStorageScope", BindingFlags.Public ||| BindingFlags.Static))
                "there is no public spelling of the mint"

            let publicStatics =
                moduleType.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
                |> Array.map _.Name
                |> Array.filter (fun n -> not (n.StartsWith "get_"))
                |> Array.sort

            Expect.sequenceEqual publicStatics [| "scopeId" |] "the only public function is the accessor"

            Expect.isTrue ResolvedScope.anonymous.IsAnonymous "the anonymous scope is public, and says what it is"
        }

        test "the server-side mint and memo are internal too" {
            let scopeResolution =
                typeof<IStorageScopeResolver>.Assembly.GetType("ToolUp.Platform.StorageScopeResolver+ScopeResolution")

            Expect.isNotNull
                scopeResolution
                "the ScopeResolution module compiles as a nested type of the resolver module"

            for name in [ "ofStorageScope"; "remember" ] do
                let m =
                    scopeResolution.GetMethod(name, BindingFlags.NonPublic ||| BindingFlags.Static)

                Expect.isNotNull m (sprintf "%s exists" name)
                Expect.isTrue m.IsAssembly (sprintf "%s is internal" name)

                Expect.isNull
                    (scopeResolution.GetMethod(name, BindingFlags.Public ||| BindingFlags.Static))
                    (sprintf "%s has no public spelling" name)

            Expect.isNotNull
                (scopeResolution.GetMethod("forRequest", BindingFlags.Public ||| BindingFlags.Static))
                "forRequest — the doors' one read — is the public half"
        }

        test "the anonymous scope keys the anonymous shard and reports no storage" {
            Expect.equal
                ResolvedScope.anonymous.ScopeId
                ResolvedScope.AnonymousScopeId
                "the literal the shard is keyed on"

            Expect.equal (ResolvedScope.scopeId ResolvedScope.anonymous) "anonymous" "and the accessor agrees"
            Expect.isNone ResolvedScope.anonymous.Storage "no container, nothing persists"

            let minted = ScopeResolution.ofStorageScope (storage "team-x")
            Expect.isFalse minted.IsAnonymous "a minted scope is not anonymous"
            Expect.equal minted.ScopeId "team-x" "and keys its own shard"
            Expect.equal minted.Storage (Some(storage "team-x")) "carrying the resolver's record"
        }
    ]

// ── B. Not read from the bag ──────────────────────────────────────

let private requestPathTests =
    testList "B — the door reads the resolver's mint, never the request items" [

        test "forRequest returns the remembered scope, and the anonymous scope when nothing was remembered" {
            let ctx = DefaultHttpContext() :> HttpContext

            Expect.isTrue (ScopeResolution.forRequest ctx).IsAnonymous "an unresolved request is anonymous — explicitly"

            let remembered = ScopeResolution.remember ctx (storage "team-r")

            Expect.equal (ScopeResolution.forRequest ctx) remembered "the middleware's one write is the door's one read"
        }

        test "a StorageScope planted in the request items is NOT a resolved scope" {
            // The pre-797 fallback chain read exactly this key. A caller
            // (or a test harness, or a module) that plants it no longer
            // steers the fact doors: the door sees the anonymous scope.
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.Items["ToolUp.StorageScope"] <- box (storage "team-planted")

            let seen = ScopeResolution.forRequest ctx

            Expect.isTrue seen.IsAnonymous "the planted record is not honoured"
            Expect.notEqual seen.ScopeId "team-planted" "and its id never reaches the door"
        }

        test "a user id on the request is not a scope either — the old middle rung is gone" {
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.Items["ToolUp.UserId"] <- box "user-42"

            Expect.isTrue (ScopeResolution.forRequest ctx).IsAnonymous "no fallback to the principal's id"
        }

        testCaseAsync "end to end: a fact under a resolved scope is invisible to a request that planted that scope"
        <| async {
            let store, gate = freshTier ()
            let scopeId = newScopeId ()
            let minted = ScopeResolution.ofStorageScope (storage scopeId)
            let fact = assertUnder store minted (draft 12345.67m)

            // The same request the old code would have read as `scopeId`.
            let planted = bareRequest store gate
            planted.Items["ToolUp.StorageScope"] <- box (storage scopeId)

            let! raw = FactQueryTool.execute planted queryArgs

            Expect.isEmpty (factsOf raw) "the door queried the anonymous shard, not the planted one"
            Expect.isFalse (raw.Contains fact.FactId) "the fact's id is nowhere in the payload"
            Expect.isFalse (raw.Contains "12345.67") "nor its value"

            // And the request the MIDDLEWARE resolved sees it.
            let resolved = bareRequest store gate
            ScopeResolution.remember resolved (storage scopeId) |> ignore

            let! raw = FactQueryTool.execute resolved queryArgs

            Expect.equal (List.length (factsOf raw)) 1 "the resolver's scope reads its own shard"
        }

        testCaseAsync "the anonymous scope is its own shard, never a wildcard"
        <| async {
            let store, gate = freshTier ()
            let scoped = ScopeResolution.ofStorageScope (storage (newScopeId ()))

            assertUnder store scoped (draft 100m) |> ignore
            let anonymousFact = assertUnder store ResolvedScope.anonymous (draft 200m)

            let! raw = FactQueryTool.execute (bareRequest store gate) queryArgs
            let facts = factsOf raw

            Expect.equal (List.length facts) 1 "exactly the anonymously-asserted fact, not the scoped one as well"

            Expect.equal
                (facts.Head.GetProperty("factId").GetString())
                anonymousFact.FactId
                "and it is the anonymous shard's own fact"
        }

        test "the typed and string store forms key one shard — the typed form is the string form over ScopeId" {
            let store, _ = freshTier ()
            let scopeId = newScopeId ()
            let minted = ScopeResolution.ofStorageScope (storage scopeId)
            let viaTyped = assertUnder store minted (draft 5m)

            let viaString = store.Get(scopeId, viaTyped.FactId) |> Async.RunSynchronously

            Expect.equal viaString (Some viaTyped) "read back through the string key"

            let viaTypedAgain = store.Get(minted, viaTyped.FactId) |> Async.RunSynchronously

            Expect.equal viaTypedAgain (Some viaTyped) "and through the typed one"

            Expect.isNone
                (store.Get(ResolvedScope.anonymous, viaTyped.FactId) |> Async.RunSynchronously)
                "and not through a different scope"
        }
    ]

// ── C. The source says so ─────────────────────────────────────────

/// The three doors, by path under `src/`.
let private doors = [
    "ToolUp.Facts.Server/Server/FactQueryTool.fs"
    "ToolUp.Facts.Server/Server/PopulationQueryTool.fs"
    "ToolUp.Facts.Server/Server/CoverageTool.fs"
]

/// The spellings the doors must not carry. Assembled at runtime so this
/// file, which is itself inside `src/`, does not trip its own guard if the
/// scan is ever widened.
let private forbidden = [
    "\"ToolUp." + "StorageScope\"", "a `\"ToolUp.StorageScope\"` items read — the pre-797 scope source"
    ":? " + "StorageScope", "a `StorageScope` type-test pattern — the pre-797 cast"
    "let private " + "scopeIdOf", "a `scopeIdOf` reader — the pre-797 fallback chain (user id, then a literal)"
]

/// Pure classifier: the offences a door's source carries, as (needle, why).
let private offences (source: string) : (string * string) list =
    forbidden |> List.filter (fun (needle, _) -> source.Contains needle)

let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private sourceGuardTests =
    testList "C — no fact door reads the storage scope from the request items" [

        test "the classifier fires on the pre-797 shape (go-red)" {
            let planted =
                String.concat "\n" [
                    "    let private " + "scopeIdOf (ctx: HttpContext) : string ="
                    "        match ctx.Items.TryGetValue " + "\"ToolUp." + "StorageScope\" with"
                    "        | true, (" + ":? " + "StorageScope as scope) -> scope.ScopeId"
                    "        | _ -> " + "\"anonymous\""
                ]

            Expect.equal (List.length (offences planted)) 3 "all three spellings are caught"
        }

        test "the classifier is quiet on the post-797 shape" {
            let clean =
                "            executeWith store gate registry (StorageScopeResolver.ScopeResolution.forRequest ctx) (userIdOf ctx) argsJson"

            Expect.isEmpty (offences clean) "the resolver read is not an offence"
        }

        test "every door reads its scope through ScopeResolution.forRequest and carries none of the old spellings" {
            let src = Path.Combine(repoRoot (), "src")

            for door in doors do
                let path = Path.Combine(src, door)
                Expect.isTrue (File.Exists path) (sprintf "%s exists" door)
                let source = File.ReadAllText path

                Expect.stringContains
                    source
                    "ScopeResolution.forRequest ctx"
                    (sprintf "%s takes its scope from the resolver" door)

                Expect.stringContains
                    source
                    "(scope: ResolvedScope)"
                    (sprintf "%s's executor takes the typed scope" door)

                match offences source with
                | [] -> ()
                | found ->
                    failtestf
                        "%s carries pre-797 scope spellings:\n%s"
                        door
                        (found |> List.map (fun (_, why) -> "  - " + why) |> String.concat "\n")
        }
    ]

let tests =
    testList "Phase 797 — scope as a choke point" [ publicSurfaceTests; requestPathTests; sourceGuardTests ]