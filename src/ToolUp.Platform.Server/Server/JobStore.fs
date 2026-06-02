module ToolUp.Platform.JobStore

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.SecondaryIndex

// ─── Storage layout ──────────────────────────────────────────────
//
// Container: always `_platform`.
// Definition blob: `jobs/{scopeId}/definitions/{jobId:N}.json`
// Run blob:        `jobs/{scopeId}/runs/{jobId:N}/{ts}-{runId:N}.json`
// Secondary indexes (Phase 9f):
//   `jobs/{scopeId}/_idempotency/{sha256(key)}/{jobId:N}.ref` — payload = (jobId, createdAt) JSON
//   `jobs/{scopeId}/_next-run/{yyyyMMddHHmm}/{jobId:N}.ref`   — empty payload
//
// Mirrors the `webhooks/` layout (see `WebhookRegistry.fs:10-21`) so
// operators see one consistent shape across `_platform/` subsystems
// (`audit/`, `events/`, `webhooks/`, `jobs/`, `lineage/`).
//
// The scope sits in the path prefix so per-scope operations are a
// cheap `List(container, "jobs/{scopeId}/...")` call and cross-scope
// leakage is structurally impossible — the store never widens the
// prefix during a scoped operation. The index prefixes
// (`_idempotency/`, `_next-run/` in Phase 9f Step 4) live under
// `jobs/{scopeId}/` so the same isolation property holds for them.
// Cross-scope listing isn't needed (the in-process scheduler ticks
// per scope).
//
// Idempotency key segment: `sha256(key)` (32-byte hex). User-
// controlled keys could otherwise contain path-incompatible
// characters or exceed reasonable path lengths; sha256 bounds both.

[<Literal>]
let MaintenanceSourceModule = "_platform.maintenance"

let private platformContainer = "_platform"

let private definitionBlob (scopeId: string) (jobId: JobId) =
    $"jobs/{scopeId}/definitions/{jobId:N}.json"

let private definitionsPrefix (scopeId: string) = $"jobs/{scopeId}/definitions/"

let private idempotencyPrefix (scopeId: string) = $"jobs/{scopeId}/_idempotency"

let private nextRunPrefix (scopeId: string) = $"jobs/{scopeId}/_next-run"

let private bucketFromTime (t: DateTime) =
    t.ToUniversalTime().ToString("yyyyMMddHHmm")

let private runBlob (run: JobRun) =
    let ts = run.StartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ss-fffffffZ")

    $"jobs/{run.ScopeId}/runs/{run.JobId:N}/{ts}-{run.RunId:N}.json"

let private runsPrefix (scopeId: string) (jobId: JobId) = $"jobs/{scopeId}/runs/{jobId:N}/"

let private hashKey (key: string) =
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes key)
    Convert.ToHexString(bytes).ToLowerInvariant()

// ─── JSON ────────────────────────────────────────────────────────
//
// `JobDefinition` and `JobRun` both round-trip to the Fable admin UI
// (Phase 9b's `JobApi`), so use `FableConverters` (DU-aware shape
// compatible with `Fable.SimpleJson` on the client). Same pattern as
// `WebhookRegistry`, `ConfigStore`, `LineageStore`.

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let serializeString (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<'T>(json, options))
        with _ ->
            None

/// Denormalised payload stored inside an idempotency-index `.ref`
/// blob. Carries enough to evaluate the TTL filter and return the
/// matching `JobId` without resolving the canonical definition.
/// Public so the STJ FableConverters record converter can read
/// the synthesised public constructor — a `private` F# record
/// hides it and the converter cannot round-trip.
type IdempotencyEntry = { JobId: JobId; CreatedAt: DateTime }

// ─── Concurrency helper ──────────────────────────────────────────

/// Download many blobs in parallel, tolerating individual
/// deserialisation failures by skipping them. Same shape as
/// `WebhookRegistry.downloadAll` and `PersistentEventStore.downloadAll`.
/// A partial result on the scheduler's hot path is preferable to a
/// total failure if a single corrupt blob slipped in.
let private downloadAll<'T> (storage: IBlobStorage) (blobNames: string list) : Async<'T list> = async {
    let! results =
        blobNames
        |> List.map (fun name -> async {
            let! result = storage.Download(platformContainer, name)

            return
                match result with
                | Ok bytes -> Json.tryDeserialize<'T> bytes
                | Error _ -> None
        })
        |> Async.Parallel

    return results |> Array.choose id |> Array.toList
}

// ─── BlobJobStore ────────────────────────────────────────────────

/// Blob-backed `IJobStore`. Definitions persist as one JSON blob each;
/// run history persists as one blob per attempt.
///
/// **Concurrency.** Updates are read-modify-write — no atomic
/// compare-and-swap. The in-process scheduler serialises updates per
/// `JobId` via a per-job semaphore (set up at the scheduler layer in
/// `InProcessJobScheduler`), so concurrent ticks for the same job
/// cannot interleave. Distributed implementations adding ETag-based
/// CAS drop in without changing the interface (Phase 9c follow-up).
///
/// **Idempotency-key index (Phase 9f).** `Save` writes a
/// `_idempotency/{sha256(key)}/{jobId}.ref` per definition that has
/// `Idempotency = Some k`. `FindByIdempotencyKey` lists the index
/// prefix for the keyed segment and filters by TTL — no full scan
/// of definitions. Drift surfaces in `IndexConsistencyCheck` and is
/// recoverable via `Rebuild`.
type BlobJobStore(storage: IBlobStorage, eventStore: IEventStore) =

    let idempotencyIndexFor (scopeId: string) : BlobIndex<string, JobId> =
        BlobIndex.create
            storage
            platformContainer
            (idempotencyPrefix scopeId)
            hashKey
            (fun (g: JobId) -> g.ToString "N")
            (fun s ->
                match Guid.TryParseExact(s, "N") with
                | true, g -> Some g
                | false, _ -> None)

    let nextRunIndexFor (scopeId: string) : BlobIndex<string, JobId> =
        BlobIndex.create
            storage
            platformContainer
            (nextRunPrefix scopeId)
            id
            (fun (g: JobId) -> g.ToString "N")
            (fun s ->
                match Guid.TryParseExact(s, "N") with
                | true, g -> Some g
                | false, _ -> None)

    /// Sync the idempotency index for a definition. Best-effort —
    /// canonical is authoritative; drift is recoverable.
    let writeIdempotencyEntry (definition: JobDefinition) =
        match definition.Idempotency with
        | None -> async.Return()
        | Some k -> async {
            let entry = {
                JobId = definition.JobId
                CreatedAt = definition.CreatedAt
            }

            let payload = Json.serialize entry
            let index = idempotencyIndexFor definition.ScopeId

            try
                do! index.Add k.Key definition.JobId (Some payload)
            with _ ->
                ()
          }

    /// Compute the bucket entry a definition should currently hold,
    /// or `None` if it should hold none (status non-Active or no
    /// NextRunAt).
    let activeBucket (definition: JobDefinition) : string option =
        match definition.Status, definition.NextRunAt with
        | Active, Some t -> Some(bucketFromTime t)
        | _ -> None

    /// Sync the next-run index for a definition transition. Removes
    /// the prior bucket entry if it differs from the new bucket and
    /// writes the new bucket entry when one is required. Best-effort
    /// — canonical is authoritative; drift is recoverable. The
    /// caller is responsible for serialising concurrent
    /// `(prior, next)` pairs against the same JobId; the in-process
    /// scheduler does this via a per-`JobId` semaphore.
    let syncNextRunEntry (priorBucket: string option) (definition: JobDefinition) = async {
        let index = nextRunIndexFor definition.ScopeId
        let nextBucket = activeBucket definition

        try
            match priorBucket, nextBucket with
            | Some p, Some n when p = n -> ()
            | Some p, _ -> do! index.Remove p definition.JobId
            | None, _ -> ()

            match nextBucket with
            | Some n -> do! index.Add n definition.JobId None
            | None -> ()
        with _ ->
            ()
    }

    let saveDefinition (definition: JobDefinition) = async {
        // Read the prior persisted state (if any) so we know which
        // next-run bucket entry to remove. This pre-read is the only
        // way to keep the index consistent under updates that change
        // either NextRunAt or Status.
        let! prior = storage.Download(platformContainer, definitionBlob definition.ScopeId definition.JobId)

        let priorBucket =
            match prior with
            | Ok bytes ->
                match Json.tryDeserialize<JobDefinition> bytes with
                | Some d -> activeBucket d
                | None -> None
            | Error _ -> None

        let bytes = Json.serialize definition

        let! result = storage.Upload(platformContainer, definitionBlob definition.ScopeId definition.JobId, bytes)

        match result with
        | Ok _ ->
            do! writeIdempotencyEntry definition
            do! syncNextRunEntry priorBucket definition
            return ()
        | Error e -> return failwith $"JobStore: failed to persist definition {definition.JobId}: {e}"
    }

    /// Re-write the idempotency and next-run indexes for `scopeId`
    /// from canonical state. Idempotent — safe to re-run; safe to run
    /// concurrently with `Save`. Does NOT delete pre-existing index
    /// entries (vacuum is a separate operation, deferred). Returns
    /// the count of definitions processed.
    member this.Rebuild(scopeId: string) : Async<int> = async {
        let! names = storage.List(platformContainer, definitionsPrefix scopeId)
        let! defs = downloadAll<JobDefinition> storage names

        // Idempotency entries.
        do! defs |> List.map writeIdempotencyEntry |> Async.Parallel |> Async.Ignore

        // Next-run entries — only for Active jobs with NextRunAt set.
        do!
            defs
            |> List.choose (fun d ->
                match activeBucket d with
                | Some b ->
                    let index = nextRunIndexFor scopeId
                    Some(index.Add b d.JobId None)
                | None -> None)
            |> Async.Parallel
            |> Async.Ignore

        let payload =
            Json.serializeString {|
                store = "jobs"
                scope = scopeId
                entryCount = defs.Length
                indexes = [ "_idempotency"; "_next-run" ]
            |}

        let evt = Events.create scopeId MaintenanceSourceModule "IndexRebuilt" payload
        do! eventStore.Write evt

        return defs.Length
    }

    /// Sample canonical definitions and index entries; verify each
    /// side resolves to the other for both the idempotency and
    /// next-run indexes. Returns one `IndexConsistencyEntry` per
    /// index. Drift > 0 in any dimension flags a recoverable bug
    /// class — surface in `/dev/inspect` without alerting on every
    /// transient miss.
    member _.IndexConsistencyCheck(scopeId: string, sampleSize: int) : Async<IndexConsistencyEntry list> = async {
        let! names = storage.List(platformContainer, definitionsPrefix scopeId)
        let! defs = downloadAll<JobDefinition> storage names
        let knownIds = defs |> List.map _.JobId |> Set.ofList

        // Idempotency: sample definitions with keys.
        let idemSample =
            defs
            |> List.choose (fun d -> d.Idempotency |> Option.map (fun k -> (d, k.Key)))
            |> List.truncate sampleSize

        let idemIndex = idempotencyIndexFor scopeId

        let! idemCanonicalChecks =
            idemSample
            |> List.map (fun (def, key) -> async {
                let! entries = idemIndex.Lookup key
                return entries |> List.exists (fun (id, _) -> id = def.JobId)
            })
            |> Async.Parallel

        let idemUnindexed =
            idemCanonicalChecks |> Array.filter (fun ok -> not ok) |> Array.length

        let idemConsistent = idemCanonicalChecks |> Array.filter id |> Array.length

        let idemDistinctKeys =
            idemSample |> List.map snd |> List.distinct |> List.truncate sampleSize

        let! idemIndexEntries = idemDistinctKeys |> List.map (fun k -> idemIndex.Lookup k) |> Async.Parallel

        let idemOrphans =
            idemIndexEntries
            |> Array.toList
            |> List.collect id
            |> List.filter (fun (id, _) -> not (knownIds.Contains id))
            |> List.length

        // Next-run: sample Active jobs with NextRunAt set.
        let nrSample =
            defs
            |> List.choose (fun d ->
                match activeBucket d with
                | Some b -> Some(d, b)
                | None -> None)
            |> List.truncate sampleSize

        let nrIndex = nextRunIndexFor scopeId

        let! nrCanonicalChecks =
            nrSample
            |> List.map (fun (def, bucket) -> async {
                let! entries = nrIndex.Lookup bucket
                return entries |> List.exists (fun (id, _) -> id = def.JobId)
            })
            |> Async.Parallel

        let nrUnindexed =
            nrCanonicalChecks |> Array.filter (fun ok -> not ok) |> Array.length

        let nrConsistent = nrCanonicalChecks |> Array.filter id |> Array.length

        let nrDistinctBuckets =
            nrSample |> List.map snd |> List.distinct |> List.truncate sampleSize

        let! nrIndexEntries = nrDistinctBuckets |> List.map (fun b -> nrIndex.Lookup b) |> Async.Parallel

        let nrOrphans =
            nrIndexEntries
            |> Array.toList
            |> List.collect id
            |> List.filter (fun (id, _) ->
                match defs |> List.tryFind (fun d -> d.JobId = id) with
                | Some d -> activeBucket d = None // job is no longer Active or has no NextRunAt
                | None -> true) // no canonical at all
            |> List.length

        return [
            {
                StoreName = "jobs"
                IndexName = "_idempotency"
                SampleSize = idemSample.Length
                ConsistentEntries = idemConsistent
                OrphanedIndexEntries = idemOrphans
                UnindexedCanonicals = idemUnindexed
            }
            {
                StoreName = "jobs"
                IndexName = "_next-run"
                SampleSize = nrSample.Length
                ConsistentEntries = nrConsistent
                OrphanedIndexEntries = nrOrphans
                UnindexedCanonicals = nrUnindexed
            }
        ]
    }

    interface IJobStore with
        member _.Save(definition) = saveDefinition definition

        member _.Get(scopeId, jobId) = async {
            let! result = storage.Download(platformContainer, definitionBlob scopeId jobId)

            match result with
            | Ok bytes -> return Json.tryDeserialize<JobDefinition> bytes
            | Error _ -> return None
        }

        member _.ListJobs(scopeId) = async {
            let! names = storage.List(platformContainer, definitionsPrefix scopeId)
            let! defs = downloadAll<JobDefinition> storage names
            return defs |> List.sortBy _.CreatedAt
        }

        member _.Update(definition) = saveDefinition definition

        member _.FindByIdempotencyKey(scopeId, key, ttlSeconds, now) = async {
            let index = idempotencyIndexFor scopeId
            let! entries = index.Lookup key

            let cutoff = now.AddSeconds(-float ttlSeconds)

            let live =
                entries
                |> List.choose (fun (jobId, payloadOpt) ->
                    match payloadOpt with
                    | Some bytes ->
                        match Json.tryDeserialize<IdempotencyEntry> bytes with
                        | Some entry when entry.CreatedAt >= cutoff -> Some entry.JobId
                        | _ -> None
                    | None ->
                        // Empty payload — older or hand-written entry.
                        // Fall back to canonical lookup so the TTL gate
                        // still applies. Rare; cheap when it happens.
                        Some jobId)
                |> List.tryHead

            return live
        }

        member _.RecordRun(run) = async {
            let bytes = Json.serialize run
            let! result = storage.Upload(platformContainer, runBlob run, bytes)

            match result with
            | Ok _ -> return ()
            | Error e -> return failwith $"JobStore: failed to persist run {run.RunId}: {e}"
        }

        member _.GetRecentRuns(scopeId, jobId, count) = async {
            let! names = storage.List(platformContainer, runsPrefix scopeId jobId)
            // Blob names start with the ISO-ordered timestamp prefix,
            // so descending name order = newest first.
            let recent = names |> List.sortDescending |> List.truncate count
            let! runs = downloadAll<JobRun> storage recent
            return runs |> List.sortByDescending _.StartedAt
        }

        member _.DueJobs(scopeId, now) = async {
            // List the next-run index. Each blob name has the shape
            // `jobs/{scope}/_next-run/{bucket}/{jobId:N}.ref`; bucket
            // ordering is lexicographic and matches chronological for
            // the `yyyyMMddHHmm` format. Filter buckets <= now to
            // include both this-minute and overdue entries (a missed
            // tick leaves jobs in a prior bucket).
            let! names = storage.List(platformContainer, nextRunPrefix scopeId + "/")
            let nowBucket = bucketFromTime now

            let candidateIds =
                names
                |> List.choose (fun name ->
                    let parts = name.Split([| '/'; '\\' |])
                    // Last two segments: {bucket}/{jobId}.ref
                    if parts.Length < 2 then
                        None
                    else
                        let last = parts[parts.Length - 1]
                        let bucket = parts[parts.Length - 2]

                        if not (last.EndsWith ".ref") then
                            None
                        elif bucket > nowBucket then
                            None
                        else
                            let idStr = last.Substring(0, last.Length - 4)

                            match Guid.TryParseExact(idStr, "N") with
                            | true, g -> Some g
                            | false, _ -> None)
                |> List.distinct

            // Resolve canonicals and re-verify status / NextRunAt.
            // Defensive — index drift could leave stale refs pointing
            // at jobs that have since been Cancelled or rescheduled.
            let! canonicals =
                candidateIds
                |> List.map (fun jobId -> async {
                    let! result = storage.Download(platformContainer, definitionBlob scopeId jobId)

                    return
                        match result with
                        | Ok bytes -> Json.tryDeserialize<JobDefinition> bytes
                        | Error _ -> None
                })
                |> Async.Parallel

            return
                canonicals
                |> Array.choose id
                |> Array.toList
                |> List.filter (fun d ->
                    d.Status = Active
                    && (match d.NextRunAt with
                        | Some t -> t <= now
                        | None -> false))
        }

        member _.ListScopesWithJobs() = async {
            // List every blob under `jobs/`. Each name has the shape
            // `jobs/{scopeId}/definitions/{jobId:N}.json` or
            // `jobs/{scopeId}/runs/{jobId:N}/...`; either way the
            // second path segment is the scope id. Reading run blobs
            // here is wasted I/O on the metadata listing only — the
            // contents aren't downloaded — but listing runs and
            // definitions in two passes would double the round-trip
            // count for nothing, so we accept the larger result set
            // and dedup in memory.
            //
            // `LocalFileStorage` returns names with the OS-native
            // path separator (backslash on Windows); cloud stores
            // return forward slashes. Split on both so the same
            // implementation works against any backend.
            let! names = storage.List(platformContainer, "jobs/")

            return
                names
                |> List.choose (fun name ->
                    let parts = name.Split([| '/'; '\\' |])

                    if parts.Length >= 3 && parts[1] <> "" then
                        Some parts[1]
                    else
                        None)
                |> List.distinct
        }

// ─── Convenience constructor ─────────────────────────────────────

let create (storage: IBlobStorage) (eventStore: IEventStore) : IJobStore =
    BlobJobStore(storage, eventStore) :> IJobStore