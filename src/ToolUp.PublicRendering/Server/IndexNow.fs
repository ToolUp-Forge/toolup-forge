// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.BlobStorage

// ─── Phase 109 — IndexNow push-indexing ──────────────────────────────
//
// IndexNow (https://www.indexnow.org/) is a Microsoft + Yandex co-spec
// that lets a site PUSH URLs to participating engines (Bing, Yandex,
// Seznam, Naver) instead of waiting for them to passively crawl. Google
// does not consume IndexNow directly but ingests Bing's index-share
// signal indirectly, so the push is still leverage for the Google index.
//
// This companion generalises a production-proven bespoke module into an
// opt-in `ToolUp.PublicRendering` feature. Two pieces:
//
//   1. Ownership key. Served at `/{key}.txt` — the file body equals the
//      key string. IndexNow verifies the host by GETing this path before
//      accepting batched URL submissions. The key is stable across
//      deploys (IndexNow does not require rotation).
//
//   2. Batched submission. POST every public URL to the IndexNow API in
//      batches of ≤10,000. Throttled by a per-deploy + per-batch
//      persistence marker so app restarts within the same deploy don't
//      re-submit batches that already succeeded — only the failed
//      batches retry. This guards against the failure mode the reference
//      implementation's postmortem documented: a 175k-URL resubmit on
//      every restart whenever ANY batch had previously failed.
//
// **Off by default (GP 11 / GP 13).** A deployment that never composes
// `withIndexNow` registers no route, no hosted service, and does no
// startup work — byte-for-byte identical to a pipeline without it. The
// feature only activates when explicitly composed with `Enabled = true`.

/// Per-deploy submission state. Persisted between app restarts so a
/// partial-failure run resumes from where it left off rather than
/// re-POSTing every batch.
///
/// Semantics:
///   • `Signature` — the `deploySignature` at the time the state was
///     written. A mismatch with the running signature means the state is
///     stale (new deploy) and is discarded.
///   • `SuccessfulBatches` — the 0-based indices (into the chunked batch
///     list) that returned 2xx in a prior run. The EMPTY set with a
///     matching signature is the "fully done" sentinel — written once, at
///     the end of an all-success run, to compactly mark "every batch
///     landed; nothing to do."
type IndexNowSubmissionState = {
    Signature: string
    SuccessfulBatches: Set<int>
}

/// Persistence seam for the resumable submission state (GP 12 — six
/// portability rules):
///
///   1. **Identity by value.** `Read` / `Write` carry an
///      `IndexNowSubmissionState` (a `string` + `Set<int>`). No live
///      handle. ✓
///   2. **Async at every boundary.** Both methods return `Async<_>`. ✓
///   3. **Retry as data.** N/A — a state slot get/put surfaces no retry
///      policy; an impl wraps its own. ✓
///   4. **Stateless between invocations.** Each call carries (or returns)
///      the full state value; the backing file / blob is the only shared
///      store, and an impl holds no in-memory continuity between calls. ✓
///   5. **No cross-shard ordering.** One logical slot per deployment; no
///      cross-key ordering contract. ✓
///   6. **Precision at the lower bound.** N/A — no timing primitives. ✓
type IIndexNowStateStore =
    /// Read the persisted state, or `None` when there is none (absent /
    /// unreadable / corrupt — a bad store is treated as "no prior state").
    abstract Read: unit -> Async<IndexNowSubmissionState option>
    /// Persist the state, overwriting any prior value. Best-effort — a
    /// failed write must not propagate (it just costs a re-submit later).
    abstract Write: state: IndexNowSubmissionState -> Async<unit>

/// JSON serialisation for the on-disk / on-blob state format. Tolerant of
/// corrupt / legacy content (parse → `None`), so a bad file is treated as
/// "no prior state" rather than crashing the submission.
module IndexNowSubmissionState =

    /// Parse the `{"sig":…,"successfulBatches":[…]}` form. Returns `None`
    /// on any error (malformed JSON, missing keys, legacy plain-string
    /// content), so the caller treats a bad payload as "no prior state".
    let tryParse (json: string) : IndexNowSubmissionState option =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let sigEl =
                let ok, el = root.TryGetProperty "sig"
                if ok then Some el else None

            let batchesEl =
                let ok, el = root.TryGetProperty "successfulBatches"
                if ok then Some el else None

            match sigEl, batchesEl with
            | Some s, Some bs when bs.ValueKind = JsonValueKind.Array ->
                let sigStr =
                    match s.GetString() with
                    | null -> ""
                    | v -> v

                let indices =
                    bs.EnumerateArray()
                    |> Seq.choose (fun e ->
                        if e.ValueKind = JsonValueKind.Number then
                            let ok, v = e.TryGetInt32()
                            if ok then Some v else None
                        else
                            None)
                    |> Set.ofSeq

                Some {
                    Signature = sigStr
                    SuccessfulBatches = indices
                }
            | _ -> None
        with _ ->
            None

    /// Serialise to the on-disk JSON format. Indices are emitted sorted
    /// ascending so the file is operator-readable.
    let serialize (state: IndexNowSubmissionState) : string =
        let escapedSig = JsonSerializer.Serialize(state.Signature)

        let indicesJson =
            state.SuccessfulBatches
            |> Set.toArray
            |> Array.sort
            |> Array.map string
            |> String.concat ","

        sprintf "{\"sig\":%s,\"successfulBatches\":[%s]}" escapedSig indicesJson

/// File-backed `IIndexNowStateStore`. Stores the state under the system
/// temp dir so app restarts preserve it but a container redeploy (fresh
/// temp dir) loses it — which is exactly the trigger we want: a fresh
/// container re-pushes the whole universe. The default impl when a
/// deployment composes `withIndexNow` without supplying its own store.
type FileIndexNowStateStore(path: string) =
    interface IIndexNowStateStore with
        member _.Read() : Async<IndexNowSubmissionState option> = async {
            try
                if File.Exists path then
                    return File.ReadAllText path |> IndexNowSubmissionState.tryParse
                else
                    return None
            with _ ->
                return None
        }

        member _.Write(state: IndexNowSubmissionState) : Async<unit> = async {
            try
                File.WriteAllText(path, IndexNowSubmissionState.serialize state)
            with _ ->
                ()
        }

module FileIndexNowStateStore =
    /// The default state-file path under the system temp dir.
    let defaultPath: string =
        Path.Combine(Path.GetTempPath(), "toolup-indexnow-last-submission.json")

    /// Construct the default file-backed store at the standard temp path.
    let create () : IIndexNowStateStore =
        FileIndexNowStateStore(defaultPath) :> IIndexNowStateStore

    /// Construct a file-backed store at an explicit path. Use this when
    /// several deployments share one host and the single default path
    /// would collide.
    let createAt (path: string) : IIndexNowStateStore =
        FileIndexNowStateStore(path) :> IIndexNowStateStore

/// `IBlobStorage`-backed `IIndexNowStateStore` for multi-instance
/// deployments: every replica reads/writes the same blob, so a partial
/// submission by one replica is resumed (not restarted) by another. The
/// blob lives in the reserved `_platform` container (the IndexNow URL
/// universe is a deployment-level, non-tenant concern, so it is not
/// scope-partitioned — GP 4: `_platform` is the SDK-reserved container).
type BlobIndexNowStateStore(blobStorage: IBlobStorage, container: string, blobName: string) =
    interface IIndexNowStateStore with
        member _.Read() : Async<IndexNowSubmissionState option> = async {
            let! result = blobStorage.Download(container, blobName)

            match result with
            | Error _ -> return None
            | Ok bytes ->
                try
                    return Encoding.UTF8.GetString bytes |> IndexNowSubmissionState.tryParse
                with _ ->
                    return None
        }

        member _.Write(state: IndexNowSubmissionState) : Async<unit> = async {
            try
                let bytes = Encoding.UTF8.GetBytes(IndexNowSubmissionState.serialize state)
                let! _ = blobStorage.Upload(container, blobName, bytes)
                return ()
            with _ ->
                return ()
        }

module BlobIndexNowStateStore =
    [<Literal>]
    let DefaultContainer = "_platform"

    [<Literal>]
    let DefaultBlobName = "indexnow/last-submission.json"

    /// Construct a blob-backed store over the supplied `IBlobStorage`,
    /// using the reserved `_platform` container.
    let create (blobStorage: IBlobStorage) : IIndexNowStateStore =
        BlobIndexNowStateStore(blobStorage, DefaultContainer, DefaultBlobName) :> IIndexNowStateStore

    /// Construct a blob-backed store with explicit container + blob name.
    let createIn (container: string) (blobName: string) (blobStorage: IBlobStorage) : IIndexNowStateStore =
        BlobIndexNowStateStore(blobStorage, container, blobName) :> IIndexNowStateStore

/// Compose-time IndexNow options. `defaults` (the `disabled` shape)
/// reproduces "no IndexNow" exactly — `withIndexNow` only does work when
/// `Enabled = true` (GP 11 / GP 13).
type IndexNowOptions = {
    /// Master switch. `false` (default) → no route, no hosted service, no
    /// startup work, regardless of the other fields.
    Enabled: bool
    /// The bare host the submission declares (`"example.com"`). `None`
    /// (default) → derived from `ServerConfig.PublicBaseUrl` at compose
    /// time (the same origin the sitemap uses).
    Host: string option
    /// Explicit ownership key. `None` (default) → derived as the SHA-256
    /// of `KeySeed` (or the deploy signature when no seed), truncated to
    /// 32 lowercase-hex chars.
    Key: string option
    /// Seed for the derived key. `None` (default) → the deploy signature
    /// is the seed. Set this to pin a stable key across content changes.
    KeySeed: string option
    /// Max URLs per POST. Spec ceiling is 10,000 (the default).
    BatchSize: int
    /// Submission endpoint. Overridable for tests / staging. Defaults to
    /// the generic `https://api.indexnow.org/IndexNow` aggregator.
    Endpoint: string
    /// When `true` (default), fire the resumable bulk submission once at
    /// startup (fire-and-forget; never blocks startup).
    SubmitOnStartup: bool
    /// When `true` (default), push a single URL the moment a page is
    /// published / updated through `PublicRenderingNarrativePagePublisher`.
    PingOnPublish: bool
    /// State store for the resumable submission. `None` (default) → the
    /// file-backed store under the system temp dir. Supply
    /// `BlobIndexNowStateStore.create storage` for multi-instance.
    StateStore: IIndexNowStateStore option
}

module IndexNowOptions =
    /// The disabled default — composing this is byte-for-byte "no IndexNow".
    let defaults: IndexNowOptions = {
        Enabled = false
        Host = None
        Key = None
        KeySeed = None
        BatchSize = 10_000
        Endpoint = "https://api.indexnow.org/IndexNow"
        SubmitOnStartup = true
        PingOnPublish = true
        StateStore = None
    }

    /// The recommended opt-in: enabled with both triggers on and the
    /// file-backed state store. Override host / key / state store as
    /// needed with the `with*` helpers.
    let enabled: IndexNowOptions = { defaults with Enabled = true }

    let withHost (host: string) (o: IndexNowOptions) : IndexNowOptions = { o with Host = Some host }
    let withKey (key: string) (o: IndexNowOptions) : IndexNowOptions = { o with Key = Some key }
    let withKeySeed (seed: string) (o: IndexNowOptions) : IndexNowOptions = { o with KeySeed = Some seed }
    let withBatchSize (n: int) (o: IndexNowOptions) : IndexNowOptions = { o with BatchSize = n }
    let withEndpoint (url: string) (o: IndexNowOptions) : IndexNowOptions = { o with Endpoint = url }

    let withStateStore (store: IIndexNowStateStore) (o: IndexNowOptions) : IndexNowOptions = {
        o with
            StateStore = Some store
    }

/// Pure helpers: key derivation, deploy signature, body composition, and
/// the resumable submission state machine. Kept side-effect-free (the POST
/// + state store are passed in) so the whole machine is testable without
/// touching the network or disk.
module IndexNow =

    /// Counter — one increment per submission outcome.
    [<Literal>]
    let MetricName = "publicrendering.indexnow"

    [<Literal>]
    let OutcomeAccepted = "accepted"

    [<Literal>]
    let OutcomeRejected = "rejected"

    [<Literal>]
    let OutcomeError = "error"

    [<Literal>]
    let OutcomeSkipped = "skipped"

    let private emit (metrics: IMetricsSink) (outcome: string) =
        metrics.Increment(MetricName, Map["outcome", outcome])

    /// Derive a 32-lowercase-hex-char ownership key from `seed` (SHA-256,
    /// first 16 bytes). The IndexNow spec accepts 8–128 chars of
    /// `[a-zA-Z0-9-]`; 32 lowercase hex is a comfortable, stable middle.
    let deriveKey (seed: string) : string =
        use sha = SHA256.Create()
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes seed)

        bytes
        |> Array.take 16
        |> Array.map (fun b -> sprintf "%02x" b)
        |> String.concat ""

    /// Resolve the ownership key: an explicit `Key` wins; otherwise derive
    /// from `KeySeed`, falling back to `fallbackSeed`.
    ///
    /// **Stability is the load-bearing property.** The `/{key}.txt`
    /// endpoint and every submission's `keyLocation` must agree, and the
    /// key should not churn on every content edit. The caller therefore
    /// passes a STABLE `fallbackSeed` (the host) — NOT the content-based
    /// deploy signature, which rolls whenever a page changes. The
    /// production-proven reference seeded the key off its package-version
    /// signature (stable within a deploy); the generic SDK has no
    /// package-version input, so a host-derived seed gives the same
    /// stable-across-deploys / no-rotation guarantee with zero operator
    /// config. Operators wanting a fixed key across host changes set
    /// `KeySeed` or `Key` explicitly.
    let resolveKey (options: IndexNowOptions) (fallbackSeed: string) : string =
        match options.Key with
        | Some k -> k
        | None ->
            let seed =
                match options.KeySeed with
                | Some s -> s
                | None -> "toolup-indexnow:" + fallbackSeed

            deriveKey seed

    /// Reduce an absolute base URL to the bare host IndexNow expects
    /// (`https://example.com/` → `example.com`). Falls back to the trimmed
    /// input when it is not a well-formed absolute URI (e.g. already a bare
    /// host).
    let hostFromBaseUrl (baseUrl: string) : string =
        match Uri.TryCreate(baseUrl, UriKind.Absolute) with
        | true, uri -> uri.Host
        | _ -> baseUrl.Trim().TrimEnd('/')

    /// Resolve the declared host: an explicit `Host` wins; otherwise derive
    /// from the public base URL the sitemap uses.
    let resolveHost (options: IndexNowOptions) (publicBaseUrl: string) : string =
        match options.Host with
        | Some h -> h
        | None -> hostFromBaseUrl publicBaseUrl

    /// The deploy signature — SHA-256 over the sorted `slug@lastmod`
    /// universe. It rolls EXACTLY when the rendered content set changes (a
    /// page added / removed) or any page's lastmod changes, which is the
    /// signal that there is something new to push. Generic by construction
    /// — no package-version or deploy-timestamp input — so it works for any
    /// SDK consumer.
    let computeSignature (entries: (Slug * string option) list) : string =
        let canonical =
            entries
            |> List.map (fun (Slug s, lastmod) -> s + "@" + (lastmod |> Option.defaultValue ""))
            |> List.sort
            |> String.concat "\n"

        use sha = SHA256.Create()
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes canonical)
        Convert.ToHexStringLower bytes

    /// The absolute public URL list for submission: `baseUrl` + each slug
    /// in the shared sitemap universe. Trailing slashes on `baseUrl` are
    /// normalised away.
    let urlsFor (baseUrl: string) (entries: (Slug * string option) list) : string list =
        let normalisedBase = baseUrl.TrimEnd('/')
        entries |> List.map (fun (Slug s, _) -> normalisedBase + "/" + s)

    /// Compose the IndexNow batched-URL POST body.
    let buildBody (host: string) (key: string) (urls: string seq) : string =
        let escaped =
            urls
            |> Seq.map (fun u -> "\"" + u.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
            |> String.concat ","

        sprintf
            """{"host":"%s","key":"%s","keyLocation":"https://%s/%s.txt","urlList":[%s]}"""
            host
            key
            host
            key
            escaped

    /// Decide which batch indices to skip given the prior on-disk state and
    /// the current deploy signature. Returns `(skipSet, fullyDone)` where
    /// `fullyDone` is `true` when the matching-signature state carries the
    /// cleared "all done" sentinel (iteration can short-circuit).
    let decideSkipSet (prior: IndexNowSubmissionState option) (currentSig: string) : Set<int> * bool =
        match prior with
        | Some s when s.Signature = currentSig ->
            if Set.isEmpty s.SuccessfulBatches then
                Set.empty, true
            else
                s.SuccessfulBatches, false
        | _ -> Set.empty, false

    /// Testable resumable batched submission. Takes the state store, the
    /// current deploy signature, the batch size, a body composer, a POST
    /// function (`body -> Async<statusCode>`; 2xx = accepted), a metrics
    /// sink, and a logger — all injected so a test stubs the POST + store.
    /// The production entry point fills in the HttpClient + computed
    /// signature.
    let submitBatched
        (stateStore: IIndexNowStateStore)
        (deploySig: string)
        (batchSize: int)
        (composeBody: string list -> string)
        (postFn: string -> Async<int>)
        (metrics: IMetricsSink)
        (logger: ILogger)
        (urls: string list)
        : Async<unit> =
        async {
            if List.isEmpty urls then
                logger.Info "IndexNow: no URLs to submit"
                return ()
            else
                let! prior = stateStore.Read()
                let skipSet, fullyDone = decideSkipSet prior deploySig

                if fullyDone then
                    emit metrics OutcomeSkipped
                    logger.Info(sprintf "IndexNow: skip — deploy signature already fully submitted (%s)" deploySig)
                    return ()
                else
                    let effectiveBatchSize = max 1 batchSize
                    let batches = urls |> List.chunkBySize effectiveBatchSize
                    let totalBatches = List.length batches

                    let mutable allOk = true
                    let mutable successes = skipSet
                    let mutable submittedCount = 0
                    let mutable skippedCount = 0

                    // Short-circuit when a prior partial run already covers
                    // every current batch (URL-count drift within the same
                    // signature). Cheap correctness guard.
                    let alreadyCoversAll =
                        not (Set.isEmpty skipSet)
                        && [ 0 .. totalBatches - 1 ] |> List.forall (fun i -> Set.contains i skipSet)

                    if alreadyCoversAll then
                        emit metrics OutcomeSkipped

                        logger.Info(
                            sprintf
                                "IndexNow: skip — all %d batch(es) already submitted for signature (%s)"
                                totalBatches
                                deploySig
                        )

                        do!
                            stateStore.Write {
                                Signature = deploySig
                                SuccessfulBatches = Set.empty
                            }

                        return ()
                    else
                        for batchIx in 0 .. totalBatches - 1 do
                            let batch = batches.[batchIx]

                            if Set.contains batchIx successes then
                                skippedCount <- skippedCount + 1
                                emit metrics OutcomeSkipped

                                logger.Info(
                                    sprintf
                                        "IndexNow batch %d/%d: skip — already submitted for this signature"
                                        (batchIx + 1)
                                        totalBatches
                                )
                            else
                                let body = composeBody batch

                                try
                                    let! status = postFn body

                                    if status >= 200 && status < 300 then
                                        submittedCount <- submittedCount + 1
                                        successes <- Set.add batchIx successes
                                        emit metrics OutcomeAccepted

                                        // Persist incrementally so a crash
                                        // mid-run preserves what landed.
                                        do!
                                            stateStore.Write {
                                                Signature = deploySig
                                                SuccessfulBatches = successes
                                            }

                                        logger.Info(
                                            sprintf
                                                "IndexNow batch %d/%d: %d URL(s) accepted (%d)"
                                                (batchIx + 1)
                                                totalBatches
                                                (List.length batch)
                                                status
                                        )
                                    else
                                        allOk <- false
                                        emit metrics OutcomeRejected

                                        logger.Warn(
                                            sprintf
                                                "IndexNow batch %d/%d: %d — %d URL(s) not accepted"
                                                (batchIx + 1)
                                                totalBatches
                                                status
                                                (List.length batch)
                                        )
                                with ex ->
                                    allOk <- false
                                    emit metrics OutcomeError

                                    logger.Error(
                                        sprintf "IndexNow batch %d/%d: exception during POST" (batchIx + 1) totalBatches,
                                        Some ex
                                    )

                        if allOk then
                            // Final state: cleared set + signature is the
                            // "fully done" sentinel. Next startup skips the
                            // whole submission.
                            do!
                                stateStore.Write {
                                    Signature = deploySig
                                    SuccessfulBatches = Set.empty
                                }

                            logger.Info(
                                sprintf
                                    "IndexNow: submission complete; %d batch(es) submitted, %d skipped, %d URL(s) across %d batch(es)"
                                    submittedCount
                                    skippedCount
                                    (List.length urls)
                                    totalBatches
                            )
                        else
                            logger.Warn(
                                sprintf
                                    "IndexNow: at least one batch failed; %d/%d batch(es) recorded — next startup retries the rest"
                                    (Set.count successes)
                                    totalBatches
                            )

                        return ()
        }

    /// Production POST function over a shared `HttpClient`. 2xx = accepted.
    let postWith (httpClient: HttpClient) (endpoint: string) (body: string) : Async<int> = async {
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        let! resp = httpClient.PostAsync(endpoint, content) |> Async.AwaitTask
        return int resp.StatusCode
    }

/// Runtime IndexNow service — the manual / ops resubmit entry point and
/// the per-slug publish ping. Registered in DI by `withIndexNow`; the
/// startup hosted service and `PublicRenderingNarrativePagePublisher`
/// resolve it. All methods are no-ops when `Enabled = false`, so callers
/// need not re-check the toggle.
type IIndexNowService =
    /// Run the resumable bulk submission over the CURRENT public URL
    /// universe (resolved at call time). Used by the startup hook and any
    /// admin / ops resubmit surface.
    abstract SubmitAll: unit -> Async<unit>
    /// Push a single just-published / updated slug immediately. No-op when
    /// `PingOnPublish = false`.
    abstract PingSlug: slug: string -> Async<unit>

/// Default `IIndexNowService`. Closes over the resolved host / key /
/// base URL / state store / endpoint / POST function + a `universe` thunk
/// that resolves the shared sitemap universe at call time (so a republish
/// between startup and a manual resubmit is reflected).
type IndexNowService
    (
        options: IndexNowOptions,
        host: string,
        key: string,
        publicBaseUrl: string,
        stateStore: IIndexNowStateStore,
        postFn: string -> Async<int>,
        metrics: IMetricsSink,
        logger: ILogger,
        universe: unit -> Async<(Slug * string option) list>
    ) =

    let composeBody (urls: string list) = IndexNow.buildBody host key urls

    interface IIndexNowService with
        member _.SubmitAll() : Async<unit> = async {
            if not options.Enabled then
                return ()
            else
                let! entries = universe ()
                let deploySig = IndexNow.computeSignature entries
                let urls = IndexNow.urlsFor publicBaseUrl entries
                do! IndexNow.submitBatched stateStore deploySig options.BatchSize composeBody postFn metrics logger urls
        }

        member _.PingSlug(slug: string) : Async<unit> = async {
            if not options.Enabled || not options.PingOnPublish then
                return ()
            else
                let url = publicBaseUrl.TrimEnd('/') + "/" + slug.TrimStart('/')
                let body = composeBody [ url ]

                try
                    let! status = postFn body

                    if status >= 200 && status < 300 then
                        metrics.Increment(IndexNow.MetricName, Map["outcome", IndexNow.OutcomeAccepted])
                        logger.Info(sprintf "IndexNow: pinged %s (%d)" url status)
                    else
                        metrics.Increment(IndexNow.MetricName, Map["outcome", IndexNow.OutcomeRejected])
                        logger.Warn(sprintf "IndexNow: ping for %s rejected (%d)" url status)
                with ex ->
                    metrics.Increment(IndexNow.MetricName, Map["outcome", IndexNow.OutcomeError])
                    logger.Error(sprintf "IndexNow: ping for %s threw" url, Some ex)
        }

/// `IHostedService` that fires the startup bulk submission fire-and-forget
/// on the thread pool. Failures log but never block or fail app startup
/// (the reference implementation's "never propagate to the startup
/// sequence" guarantee). Registered only when `Enabled && SubmitOnStartup`.
type IndexNowStartupService(service: IIndexNowService, logger: ILogger) =
    interface IHostedService with
        member _.StartAsync(_ct: CancellationToken) : Task =
            Task.Run(fun () ->
                async {
                    try
                        do! service.SubmitAll()
                    with ex ->
                        logger.Error("IndexNow startup submission threw", Some ex)
                }
                |> Async.StartImmediate)
            |> ignore

            Task.CompletedTask

        member _.StopAsync(_ct: CancellationToken) : Task = Task.CompletedTask

/// Ownership-verification endpoint. Serves `/{key}.txt` with body = the
/// key so IndexNow can prove host ownership before accepting URL pushes.
/// Only the canonical key slug is claimed; any other `/*.txt` request
/// falls through to the next handler (it does NOT 404 the chain).
module IndexNowKeyHandler =
    let routes (key: string) : HttpHandler =
        GET
        >=> routef "/%s.txt" (fun slug next ctx ->
            if slug = key then
                (setHttpHeader "Content-Type" "text/plain; charset=utf-8"
                 >=> setHttpHeader "Cache-Control" "public, max-age=86400"
                 >=> setBodyFromString key)
                    next
                    ctx
            else
                // Decline via `skipPipeline` (None) so the surrounding
                // `choose` advances to the next handler; `next ctx` would
                // end the chain as an unwritten 200.
                skipPipeline)