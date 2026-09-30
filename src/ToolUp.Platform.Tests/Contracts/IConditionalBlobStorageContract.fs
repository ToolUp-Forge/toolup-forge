module ToolUp.Platform.Tests.Contracts.IConditionalBlobStorageContract

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Testing

// ─── Phase 864 — IConditionalBlobStorage contract ────────────────────
//
// Parametrised conformance pack for the Phase 600 ETag seam AND for the
// guarded read-modify-write it exists to serve. Any backend claiming
// `IConditionalBlobStorage` binds the same cases:
//
//   * the seam itself — `IfAbsent` create, fresh-etag CAS, stale-etag
//     refusal, `IfMatch` on an absent blob (moved here from the Phase 600
//     test file so the pack is bindable);
//   * the stores that sit on it — a failed membership read never becomes a
//     membership write, two concurrent `AddMember` calls on two replicas
//     both land, N concurrent `MarkUsed` against `UseLimit = 1` admit
//     exactly one.
//
// The factory hands the pack a FRESH backend per case. The pack wraps it
// in `ScriptedStorage`, which forwards every call unchanged except where a
// case scripts a fault or an interleaving: fail the next read of one blob,
// or hold the first N readers of one blob until all N have read (so two
// racers provably read the same state before either writes — a race test
// that only sometimes races only sometimes tests anything).

let private bytes (s: string) = Encoding.UTF8.GetBytes s

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A forwarding decorator the pack scripts per case. Forwards the ETag
/// seam to the inner store's, so a case exercises the real backend's CAS.
type ScriptedStorage(inner: IBlobStorage) =
    let cas =
        match box inner with
        | :? IConditionalBlobStorage as c -> c
        | _ -> failwith "IConditionalBlobStorageContract: the factory must return an IConditionalBlobStorage"

    let failNext = ConcurrentDictionary<string * string, int>()
    let rendezvous = ConcurrentDictionary<string * string, CountdownEvent>()

    /// Fail the next `count` reads (`Download` or `DownloadWithETag`) of
    /// one blob with an infrastructure error. `Exists` is not faulted:
    /// the blob is there, the read of it failed.
    member _.FailNextReads(container: string, blobName: string, count: int) =
        failNext[(container, blobName)] <- count

    /// Hold each of the first `parties` reads of one blob until all of
    /// them have read. Later reads pass straight through.
    member _.Rendezvous(container: string, blobName: string, parties: int) =
        rendezvous[(container, blobName)] <- new CountdownEvent(parties)

    member private _.BeforeRead(container: string, blobName: string) : Async<string option> = async {
        let key = (container, blobName)

        let failed =
            let mutable result = false

            failNext.AddOrUpdate(
                key,
                (fun _ -> 0),
                (fun _ n ->
                    if n > 0 then
                        result <- true
                        n - 1
                    else
                        0)
            )
            |> ignore

            result

        if failed then
            return Some $"scripted read failure: {container}/{blobName}"
        else
            return None
    }

    member private _.AfterRead(container: string, blobName: string) : Async<unit> = async {
        match rendezvous.TryGetValue((container, blobName)) with
        | true, gate when not gate.IsSet ->
            let signalled =
                try
                    gate.Signal() |> ignore
                    true
                with :? InvalidOperationException ->
                    false

            if signalled then
                // Bounded: a case whose racers never all arrive fails on its
                // own assertions rather than hanging the pack.
                gate.Wait(TimeSpan.FromSeconds 10.0) |> ignore
        | _ -> ()
    }

    interface IConditionalBlobStorage with
        member this.DownloadWithETag(container, blobName) = async {
            match! this.BeforeRead(container, blobName) with
            | Some failure -> return Error failure
            | None ->
                let! result = cas.DownloadWithETag(container, blobName)
                do! this.AfterRead(container, blobName)
                return result
        }

        member _.UploadWithETag(container, blobName, content, condition) =
            cas.UploadWithETag(container, blobName, content, condition)

    interface IBlobStorage with
        member _.CanComposeFrom = inner.CanComposeFrom

        member _.ComposeFrom(container, targetBlobName, sourceBlobNames) =
            inner.ComposeFrom(container, targetBlobName, sourceBlobNames)

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member this.Download(container, blobName) = async {
            match! this.BeforeRead(container, blobName) with
            | Some failure -> return Error failure
            | None ->
                let! result = inner.Download(container, blobName)
                do! this.AfterRead(container, blobName)
                return result
        }

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// A JSON string-list codec for the helper's own cases: anything that is
/// not a JSON string array fails to decode.
let private listCodec: BlobCodec<string list> = {
    Encode = fun items -> Text.Json.JsonSerializer.SerializeToUtf8Bytes(Array.ofList items)
    Decode =
        fun content ->
            try
                match Text.Json.JsonSerializer.Deserialize<string[]>(ReadOnlySpan content) with
                | null -> Error "null"
                | items -> Ok(List.ofArray items)
            with ex ->
                Error ex.Message
}

let private freshChannel () =
    InMemoryNotificationChannel(None) :> INotificationChannel

let private freshId (prefix: string) =
    prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 12)

/// The contract pack. `factory` returns a FRESH backend implementing both
/// `IBlobStorage` and `IConditionalBlobStorage`.
let tests (name: string) (factory: unit -> IBlobStorage) =
    let container = "team-cas"

    let freshName () =
        "cas/" + Guid.NewGuid().ToString("N") + ".json"

    let freshCas () =
        factory () :> obj :?> IConditionalBlobStorage

    testList $"{name} — IConditionalBlobStorage contract" [

        // ── The seam (Phase 600) ────────────────────────────────────

        testCaseAsync "IfAbsent creates; a second IfAbsent loses with the current etag disclosed"
        <| async {
            let store = freshCas ()
            let blob = freshName ()

            let! first = store.UploadWithETag(container, blob, bytes "v1", IfAbsent)

            let etag1 =
                match first with
                | Ok e -> e
                | Error e -> failtestf "first IfAbsent should create: %A" e

            let! second = store.UploadWithETag(container, blob, bytes "v2", IfAbsent)

            match second with
            | Error(ETagMismatch(Some current)) -> Expect.equal current etag1 "loser sees the winner's etag"
            | other -> failtestf "second IfAbsent must lose with the current etag, got %A" other
        }

        testCaseAsync "read-modify-write with a fresh etag succeeds and mints a new etag"
        <| async {
            let store = freshCas ()
            let blob = freshName ()
            let! _ = store.UploadWithETag(container, blob, bytes "v1", IfAbsent)

            let! downloaded = store.DownloadWithETag(container, blob)

            let content, etag =
                match downloaded with
                | Ok(c, e) -> c, e
                | Error e -> failtestf "download failed: %s" e

            Expect.equal (Encoding.UTF8.GetString content) "v1" "content round-trips"

            let! updated = store.UploadWithETag(container, blob, bytes "v2", IfMatch etag)

            match updated with
            | Ok newEtag ->
                Expect.notEqual newEtag etag "a successful CAS mints a new etag"
                let! after = store.DownloadWithETag(container, blob)

                match after with
                | Ok(c, e) ->
                    Expect.equal (Encoding.UTF8.GetString c) "v2" "the CAS write landed"
                    Expect.equal e newEtag "the read-back etag is the minted one"
                | Error e -> failtestf "re-download failed: %s" e
            | Error e -> failtestf "fresh-etag CAS should succeed: %A" e
        }

        testCaseAsync "a stale etag loses and leaves the stored blob untouched"
        <| async {
            let store = freshCas ()
            let blob = freshName ()
            let! first = store.UploadWithETag(container, blob, bytes "v1", IfAbsent)

            let staleEtag =
                match first with
                | Ok e -> e
                | Error e -> failtestf "setup failed: %A" e

            // Another writer advances the blob.
            let! second = store.UploadWithETag(container, blob, bytes "v2", IfMatch staleEtag)

            let currentEtag =
                match second with
                | Ok e -> e
                | Error e -> failtestf "setup CAS failed: %A" e

            // The stale writer now loses — lost-update prevention.
            let! stale = store.UploadWithETag(container, blob, bytes "v3-lost", IfMatch staleEtag)

            match stale with
            | Error(ETagMismatch(Some current)) -> Expect.equal current currentEtag "loser sees the live etag"
            | other -> failtestf "stale CAS must lose, got %A" other

            let! after = store.DownloadWithETag(container, blob)

            match after with
            | Ok(c, _) -> Expect.equal (Encoding.UTF8.GetString c) "v2" "the refused write did not land"
            | Error e -> failtestf "re-download failed: %s" e
        }

        testCaseAsync "IfMatch on an absent blob loses with currentETag = None"
        <| async {
            let store = freshCas ()
            let blob = freshName ()

            let! result = store.UploadWithETag(container, blob, bytes "v1", IfMatch "deadbeef")

            match result with
            | Error(ETagMismatch None) -> ()
            | other -> failtestf "IfMatch on absent must report ETagMismatch None, got %A" other
        }

        // ── Team membership over the seam (Phase 864) ───────────────

        testCaseAsync "a failed membership read never becomes a membership write"
        <| async {
            let storage = ScriptedStorage(factory ())
            let teams = TeamStore(storage, freshChannel ())
            let user = freshId "user"
            let teamA = freshId "team-a"
            let teamB = freshId "team-b"
            let teamC = freshId "team-c"

            let! a = teams.AddMember(teamA, user, Owner)
            Expect.isOk a "seed membership A"
            let! b = teams.AddMember(teamB, user, Member)
            Expect.isOk b "seed membership B"

            // One transient read failure on the user's membership blob.
            storage.FailNextReads("_platform", membershipBlobName user, 1)

            let! c = teams.AddMember(teamC, user, Member)
            Expect.isError c "AddMember over an unreadable membership blob must fail, not write"

            let! roleA = teams.GetMemberRole(teamA, user)
            let! roleB = teams.GetMemberRole(teamB, user)
            let! roleC = teams.GetMemberRole(teamC, user)
            Expect.equal roleA (Some Owner) "membership A survives the failed read"
            Expect.equal roleB (Some Member) "membership B survives the failed read"
            Expect.equal roleC None "nothing was written for C"
        }

        testCaseAsync "two concurrent AddMember calls on two replicas both land"
        <| async {
            let storage = ScriptedStorage(factory ())
            // Two replicas: two stores, each with its own in-process lock,
            // over ONE shared backend. Only the backend can arbitrate.
            let replica1 = TeamStore(storage, freshChannel ())
            let replica2 = TeamStore(storage, freshChannel ())
            let user = freshId "user"
            let teamA = freshId "team-a"
            let teamB = freshId "team-b"

            // Both replicas provably read the same (absent) membership blob
            // before either writes.
            storage.Rendezvous("_platform", membershipBlobName user, 2)

            let! results =
                [
                    replica1.AddMember(teamA, user, Member)
                    replica2.AddMember(teamB, user, Member)
                ]
                |> Async.Parallel

            Expect.all results Result.isOk "both concurrent AddMember calls report success"

            let! roleA = replica1.GetMemberRole(teamA, user)
            let! roleB = replica1.GetMemberRole(teamB, user)
            Expect.equal roleA (Some Member) "replica 1's membership landed"
            Expect.equal roleB (Some Member) "replica 2's membership landed"
        }

        // ── Share-token use counting over the seam (Phase 864) ──────

        testCaseAsync "N concurrent MarkUsed against UseLimit = 1 on N replicas admit exactly one"
        <| async {
            let storage = ScriptedStorage(factory ())
            let secrets = Fakes.TestSecretStore() :> ISecretStore
            let n = 6

            let replicas =
                List.init n (fun _ -> ShareTokenStore.create storage secrets None silentLogger)

            let scope = freshId "team"

            let! issued =
                replicas.Head.Issue {
                    ScopeId = scope
                    ResourceKind = "forms.publishable"
                    ResourceId = freshId "form"
                    AttributedHandle = None
                    IssuedBy = "issuer@example.com"
                    ExpiresAt = None
                    UseLimit = Some(Some 1)
                    RateLimit = None
                }

            let token = Expect.wantOk issued "issue a single-use token"

            // Every replica reads UsedCount = 0 before any of them writes.
            storage.Rendezvous("_platform", $"share-tokens/{scope}/{token.Claim.TokenId}.json", n)

            let! results =
                replicas
                |> List.map (fun r -> r.MarkUsed(scope, token.Claim.TokenId))
                |> Async.Parallel

            let admitted = results |> Array.filter Result.isOk |> Array.length
            Expect.equal admitted 1 "exactly one of N concurrent MarkUsed is admitted across replicas"

            let! validated = replicas.Head.Validate token.Token

            match validated with
            | Error ShareTokenError.UseLimitExceeded -> ()
            | other -> failtestf "the token must now be spent, got %A" other
        }

        testCaseAsync "a corrupted membership blob is quarantined, reported, and never overwritten"
        <| async {
            let storage = ScriptedStorage(factory ())
            let raw = storage :> IBlobStorage
            let teams = TeamStore(storage, freshChannel (), silentLogger)
            let user = freshId "user"
            let blob = membershipBlobName user
            let corrupt = bytes "{ this is not a membership list"
            let! seeded = raw.Upload("_platform", blob, corrupt)
            Expect.isOk seeded "seed a corrupt membership blob"

            let! added = teams.AddMember(freshId "team", user, Member)
            Expect.isError added "AddMember over an undecodable membership blob must fail"

            let! after = raw.Download("_platform", blob)

            Expect.equal
                (Expect.wantOk after "the canonical blob is still there")
                corrupt
                "the corrupt blob was not overwritten"

            let! siblings = raw.List("_platform", "memberships/")

            let quarantined =
                siblings
                |> List.filter (fun n -> n.StartsWith(blob + ".corrupt-", StringComparison.Ordinal))

            Expect.hasLength quarantined 1 "the bytes were copied aside exactly once"
            let! copy = raw.Download("_platform", quarantined.Head)
            Expect.equal (Expect.wantOk copy "the quarantine copy reads back") corrupt "the copy holds the bytes"
        }

        testCaseAsync "two concurrent CreateTeam calls for one id on two replicas admit exactly one"
        <| async {
            let storage = ScriptedStorage(factory ())
            let replica1 = TeamStore(storage, freshChannel (), silentLogger)
            let replica2 = TeamStore(storage, freshChannel (), silentLogger)
            let teamId = freshId "team"
            storage.Rendezvous("_platform", teamBlobName teamId, 2)

            let! results =
                [ replica1.CreateTeam(teamId, "first"); replica2.CreateTeam(teamId, "second") ]
                |> Async.Parallel

            let created = results |> Array.filter Result.isOk
            Expect.hasLength created 1 "exactly one create of one team id is admitted"

            let! stored = replica1.GetTeam teamId
            let winner = Expect.wantOk created[0] "the admitted create"
            Expect.equal (stored |> Option.map _.Name) (Some winner.Name) "the stored team is the winner's"
        }

        // ── The helper itself (Phase 864) ───────────────────────────

        testCaseAsync "BlobMapStore: absent reads as None and the first write creates"
        <| async {
            let storage = ScriptedStorage(factory ())
            let store = BlobMapStore<string list>(storage, listCodec, silentLogger)
            let blob = freshName ()

            let! before = store.Read(container, blob)
            Expect.equal (Expect.wantOk before "an absent blob reads cleanly") None "absent is None"

            let! created =
                store.Update(
                    container,
                    blob,
                    fun current -> BlobUpdate.Write("a" :: Option.defaultValue [] current, current.IsNone)
                )

            Expect.equal (Expect.wantOk created "the first write creates") true "the transform saw absence"
            let! after = store.Read(container, blob)
            Expect.equal (Expect.wantOk after "read back") (Some [ "a" ]) "the created value reads back"
        }

        testCaseAsync "BlobMapStore: a present blob that cannot be read is Unreadable and nothing is written"
        <| async {
            let storage = ScriptedStorage(factory ())
            let raw = storage :> IBlobStorage
            let store = BlobMapStore<string list>(storage, listCodec, silentLogger)
            let blob = freshName ()
            let original = listCodec.Encode [ "kept" ]
            let! _ = raw.Upload(container, blob, original)
            storage.FailNextReads(container, blob, 1)
            let transformed = ref false

            let! result =
                store.Update(
                    container,
                    blob,
                    fun _ ->
                        transformed.Value <- true
                        BlobUpdate.Write([ "overwrite" ], ())
                )

            match result with
            | Error(BlobMapStoreError.Unreadable _) -> ()
            | other -> failtestf "an unreadable blob must be Unreadable, got %A" other

            Expect.isFalse transformed.Value "the transform never ran over a failed read"
            let! after = raw.Download(container, blob)
            Expect.equal (Expect.wantOk after "read back") original "the blob is untouched"
        }

        testCaseAsync "BlobMapStore: undecodable bytes are Quarantined and the blob is not overwritten"
        <| async {
            let storage = ScriptedStorage(factory ())
            let raw = storage :> IBlobStorage
            let store = BlobMapStore<string list>(storage, listCodec, silentLogger)
            let blob = freshName ()
            let corrupt = bytes "not|a|list"
            let! _ = raw.Upload(container, blob, corrupt)

            let! result = store.Update(container, blob, fun _ -> BlobUpdate.Write([ "overwrite" ], ()))

            let quarantinedTo =
                match result with
                | Error(BlobMapStoreError.Quarantined(_, target, _)) -> target
                | other -> failtestf "an undecodable blob must be Quarantined, got %A" other

            let! after = raw.Download(container, blob)
            Expect.equal (Expect.wantOk after "read back") corrupt "the canonical blob is untouched"
            let! copy = raw.Download(container, quarantinedTo)
            Expect.equal (Expect.wantOk copy "the quarantine copy") corrupt "the bytes were copied aside"

            // Re-reading the same bad bytes quarantines to the same name.
            let! again = store.Read(container, blob)

            match again with
            | Error(BlobMapStoreError.Quarantined(_, target, _)) ->
                Expect.equal target quarantinedTo "quarantine is content-addressed"
            | other -> failtestf "a re-read must quarantine again, got %A" other
        }

        testCaseAsync "BlobMapStore: two concurrent updates on two instances both land"
        <| async {
            let storage = ScriptedStorage(factory ())
            let a = BlobMapStore<string list>(storage, listCodec, silentLogger)
            let b = BlobMapStore<string list>(storage, listCodec, silentLogger)
            let blob = freshName ()
            let! _ = a.Update(container, blob, fun _ -> BlobUpdate.Write([ "seed" ], ()))
            storage.Rendezvous(container, blob, 2)

            let append (store: BlobMapStore<string list>) (item: string) =
                store.Update(
                    container,
                    blob,
                    fun current -> BlobUpdate.Write(item :: Option.defaultValue [] current, ())
                )

            let! results = [ append a "from-a"; append b "from-b" ] |> Async.Parallel
            Expect.all results Result.isOk "both updates report success"

            let! after = a.Read(container, blob)

            Expect.equal
                (Expect.wantOk after "read back" |> Option.map Set.ofList)
                (Some(Set.ofList [ "seed"; "from-a"; "from-b" ]))
                "neither update was lost"
        }
    ]