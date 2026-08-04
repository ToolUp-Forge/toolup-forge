// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.EmbeddingCaches.Redis.RedisEmbeddingCache

open System
open System.Buffers.Binary
open System.Net
open System.Runtime.ExceptionServices
open StackExchange.Redis
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.IEmbeddingCache

// ─── Phase 513 — Redis distributed embedding-cache companion ─────────
//
// The shipped `IEmbeddingCache` (`InMemoryEmbeddingCache`) is a
// per-process LRU: correct, but each replica pays the embedding call for
// text a sibling replica already embedded, and the same query hits on
// one replica and misses on another. This companion moves the cache into
// Redis so every replica reads and writes the same entries — the point
// Phase 14h deferred as "distributed cache (Redis) is a future companion
// paralleling NotificationChannels/Redis/".
//
// **Distributed-ready (portability rule 4).** Nothing here is carried
// between calls except local hit/miss counters, which are telemetry and
// never affect a lookup's answer. `TryGet` / `Set` each require no prior
// call in this process to succeed — the entry may have been written by a
// different replica, a different process, or a previous deployment.
//
// **GP 1** — `StackExchange.Redis` is isolated to this companion and
// never reaches `ToolUp.Platform.*`. **GP 2** — Redis is OSS.
// **GP 13** — a deployment that never composes it pays nothing; the
// in-memory default is unchanged.
//
// **The cache is an optimisation, never a source of truth.** Every Redis
// failure degrades to a miss (for `TryGet`) or a dropped write (for
// `Set`), logged at Warn. A Redis outage makes embeddings more expensive;
// it must never make them wrong or unavailable. `Clear` is the one
// exception — it is the privacy-correct response to an erasure request,
// so a failure there is raised rather than swallowed.

/// Raised for a misconfiguration detected at `create` time — an invalid
/// option or an unusable connection string. Never raised from
/// `TryGet` / `Set` / `HitRate`.
exception RedisEmbeddingCacheException of message: string

/// Which population `HitRate ()` reports over.
///
/// `LocalProcess` (the default) counts only this instance's lookups —
/// zero extra Redis round-trips, and the figure every in-tree cache
/// already reports. `SharedAcrossReplicas` additionally maintains two
/// Redis counters so `HitRate ()` answers for the whole fleet; that
/// costs one extra round-trip per lookup and one per read, which is why
/// it is opt-in (GP 11 — the default behaviour is the prior behaviour).
type HitRateScope =
    | LocalProcess
    | SharedAcrossReplicas

/// Compose-time configuration. Every field has a default that is safe
/// for a deployment that thinks about none of them.
type RedisEmbeddingCacheOptions = {
    /// Namespace for every key this companion writes. Kept distinct from
    /// `toolup:notifications:` so one Redis instance can back several
    /// substrates. Glob metacharacters are refused — `Clear ()` deletes
    /// by `SCAN MATCH {KeyPrefix}:*`, and a prefix containing `*`, `?`
    /// or `[` would silently widen that pattern beyond this cache.
    KeyPrefix: string
    /// Per-entry expiry. An embedding is deterministic for a given
    /// `(provider, model, dimensions, text)`, so a TTL is not about
    /// correctness — it bounds the keyspace of a cache nobody prunes.
    Ttl: TimeSpan
    /// Population `HitRate ()` reports over. See `HitRateScope`.
    HitRateScope: HitRateScope
    /// Redis logical database. `-1` is StackExchange.Redis' "whatever
    /// the connection string selected", which is the right default.
    Database: int
}

module RedisEmbeddingCacheOptions =
    /// Longest accepted `KeyPrefix`. Redis keys are binary-safe up to
    /// 512 MB, so this is a sanity bound rather than a protocol limit —
    /// a prefix longer than this is a mistake, not a configuration.
    [<Literal>]
    let MaxKeyPrefixLength = 128

    /// `toolup:embeddings`, 7-day TTL, local-process hit-rate, default
    /// database.
    let defaults: RedisEmbeddingCacheOptions = {
        KeyPrefix = "toolup:embeddings"
        Ttl = TimeSpan.FromDays 7.0
        HitRateScope = LocalProcess
        Database = -1
    }

    /// A prefix safe to interpolate into a `SCAN MATCH` pattern. Glob
    /// metacharacters (`*`, `?`, `[`, `]`, `\`) are the load-bearing
    /// exclusion; whitespace and control characters are refused because
    /// they make an operator's `redis-cli` session lie to them.
    let isSafeKeyPrefix (prefix: string) =
        not (String.IsNullOrWhiteSpace prefix)
        && prefix.Length <= MaxKeyPrefixLength
        && prefix
           |> Seq.forall (fun c ->
               not (Char.IsWhiteSpace c)
               && not (Char.IsControl c)
               && c <> '*'
               && c <> '?'
               && c <> '['
               && c <> ']'
               && c <> '\\')

    /// Validate every option before any I/O. Returns the first problem
    /// as a message an operator can act on.
    let validate (options: RedisEmbeddingCacheOptions) : Result<unit, string> =
        if not (isSafeKeyPrefix options.KeyPrefix) then
            Error(
                sprintf
                    "KeyPrefix '%s' is not usable: it must be 1-%d non-whitespace, non-control characters and must not contain the glob metacharacters * ? [ ] \\ (Clear () deletes by SCAN MATCH '<KeyPrefix>:*', so a glob in the prefix would widen the deletion beyond this cache)."
                    options.KeyPrefix
                    MaxKeyPrefixLength
            )
        elif options.Ttl <= TimeSpan.Zero then
            Error(
                sprintf
                    "Ttl must be positive; got %O. A zero or negative expiry would make every Set a no-op, which reads as a cache that silently never hits."
                    options.Ttl
            )
        elif options.Database < -1 then
            Error(sprintf "Database must be -1 (connection default) or a non-negative index; got %d." options.Database)
        else
            Ok()

/// Key derivation. The cache key is the caller's `EmbeddingCacheKey`
/// rendered into the Redis keyspace — no raw user text ever appears
/// (`TextHash` is already a SHA-256 hex digest by the time it reaches
/// here; see `CachingEmbeddingProvider`).
module Key =
    /// Bumped only when the key layout changes. An old-layout entry is
    /// then unreachable rather than misread, and expires on its TTL.
    [<Literal>]
    let SchemaVersion = "1"

    /// Entry-key discriminator, kept distinct from the stats
    /// discriminator so `{prefix}:s:hits` can never be produced by a
    /// provider id that happens to be `"s"`.
    [<Literal>]
    let EntryDiscriminator = "k"

    [<Literal>]
    let StatsDiscriminator = "s"

    /// Escape a segment so `:` inside a provider or model id cannot
    /// shift the boundary between segments. `%` is escaped first, so the
    /// mapping is injective and two distinct `EmbeddingVersion`s can
    /// never render to one key.
    let escapeSegment (segment: string) =
        (if isNull segment then "" else segment).Replace("%", "%25").Replace(":", "%3A")

    /// `{prefix}:k:{schema}:{provider}:{model}:{dims}:{textHash}`.
    let forKey (options: RedisEmbeddingCacheOptions) (key: EmbeddingCacheKey) =
        String.Join(
            ":",
            [|
                options.KeyPrefix
                EntryDiscriminator
                SchemaVersion
                escapeSegment key.Version.ProviderId
                escapeSegment key.Version.ModelId
                string key.Version.Dimensions
                escapeSegment key.TextHash
            |]
        )

    /// `SCAN MATCH` pattern covering EVERY key this companion writes —
    /// entries under any schema version, and the stats counters. `Clear`
    /// is a data-subject-erasure path, so it deletes the whole namespace
    /// rather than only the layout this build happens to write.
    let clearPattern (options: RedisEmbeddingCacheOptions) = options.KeyPrefix + ":*"

    let hitsKey (options: RedisEmbeddingCacheOptions) =
        String.Join(":", [| options.KeyPrefix; StatsDiscriminator; "hits" |])

    let missesKey (options: RedisEmbeddingCacheOptions) =
        String.Join(":", [| options.KeyPrefix; StatsDiscriminator; "misses" |])

/// Payload codec. A bare `Buffer.BlockCopy` of the float array would be
/// host-endian and length-implicit; both matter here, because the whole
/// point is that a DIFFERENT process reads what this one wrote. The
/// frame is therefore explicit: magic, version, dimension count, then
/// little-endian IEEE-754 singles.
module Codec =
    /// `TUEC` — ToolUp Embedding Cache.
    let private magic = [| 0x54uy; 0x55uy; 0x45uy; 0x43uy |]

    [<Literal>]
    let FormatVersion = 1

    /// magic (4) + version (4) + dimensions (4).
    [<Literal>]
    let HeaderLength = 12

    let encode (embedding: float32 array) : byte array =
        let dims = embedding.Length
        let buffer = Array.zeroCreate<byte> (HeaderLength + dims * 4)
        Array.blit magic 0 buffer 0 4
        BinaryPrimitives.WriteInt32LittleEndian(Span(buffer, 4, 4), FormatVersion)
        BinaryPrimitives.WriteInt32LittleEndian(Span(buffer, 8, 4), dims)

        for i in 0 .. dims - 1 do
            BinaryPrimitives.WriteSingleLittleEndian(Span(buffer, HeaderLength + i * 4, 4), embedding[i])

        buffer

    /// Decode a stored payload. Every rejection names what was wrong —
    /// the caller logs it and treats the entry as a miss, so a corrupt
    /// or foreign value costs one recomputation rather than a wrong
    /// vector silently entering retrieval.
    let decode (payload: byte array) : Result<float32 array, string> =
        if isNull payload then
            Error "payload is null"
        elif payload.Length < HeaderLength then
            Error(sprintf "payload is %d bytes, shorter than the %d-byte header" payload.Length HeaderLength)
        elif
            payload[0] <> magic[0]
            || payload[1] <> magic[1]
            || payload[2] <> magic[2]
            || payload[3] <> magic[3]
        then
            Error
                "payload does not carry the ToolUp embedding-cache magic — the key namespace is shared with another writer"
        else
            let version = BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(payload, 4, 4))

            if version <> FormatVersion then
                Error(sprintf "payload format version %d is not %d" version FormatVersion)
            else
                let dims = BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(payload, 8, 4))

                if dims < 0 then
                    Error(sprintf "payload declares %d dimensions" dims)
                elif payload.Length <> HeaderLength + dims * 4 then
                    Error(
                        sprintf
                            "payload declares %d dimensions (%d bytes expected) but is %d bytes"
                            dims
                            (HeaderLength + dims * 4)
                            payload.Length
                    )
                else
                    let result = Array.zeroCreate<float32> dims

                    for i in 0 .. dims - 1 do
                        result[i] <-
                            BinaryPrimitives.ReadSingleLittleEndian(ReadOnlySpan(payload, HeaderLength + i * 4, 4))

                    Ok result

/// Hit / miss counts, local and (when `SharedAcrossReplicas` is
/// configured) fleet-wide. `Shared*` are `None` under `LocalProcess`
/// scope, and also when the shared counters could not be read — the
/// absence of a figure is reported as absence, never as zero.
type EmbeddingCacheStats = {
    LocalHits: int64
    LocalMisses: int64
    LocalHitRate: float
    SharedHits: int64 option
    SharedMisses: int64 option
    SharedHitRate: float option
}

/// Metric names emitted through the optional `IMetricsSink`. Exposed as
/// literals so a deployment's dashboards and the emission site cannot
/// drift apart.
module CacheMetrics =
    /// Counter, tag `result` ∈ `hit` | `miss` | `error`.
    [<Literal>]
    let LookupsTotal = "toolup.rag.embedding_cache.lookups.total"

    /// Gauge in `[0, 1]`, tag `scope` ∈ `local` | `shared`. Set on every
    /// `HitRate ()` read, so an operator polling the admin endpoint also
    /// feeds the dashboard.
    [<Literal>]
    let HitRateGauge = "toolup.rag.embedding_cache.hit_rate"

    /// Register these with the deployment's metric registry — the sink's
    /// tag allowlist drops tags for an unregistered metric, so an
    /// unregistered `LookupsTotal` would aggregate hits and misses into
    /// one untagged series.
    let definitions: MetricDefinition list = [
        {
            Name = LookupsTotal
            Kind = Counter
            Description = "Embedding-cache lookups by outcome (hit / miss / error)."
            Unit = "1"
            Tags = [ "result" ]
        }
        {
            Name = HitRateGauge
            Kind = Gauge
            Description = "Embedding-cache hit rate in [0, 1], as observed at the last HitRate () read."
            Unit = "1"
            Tags = [ "scope" ]
        }
    ]

/// Hits over lookups, with an empty cache reporting `0.0` rather than
/// `NaN` — the same convention `InMemoryEmbeddingCache` uses.
let hitRateOf (hits: int64) (misses: int64) =
    let total = hits + misses
    if total <= 0L then 0.0 else float hits / float total

/// Redis-backed `IEmbeddingCache`. Takes an `IConnectionMultiplexer` the
/// caller owns — StackExchange.Redis wants one multiplexer per process,
/// so a deployment already running `RedisNotificationChannel` /
/// `RedisDistributedLock` should pass the SAME instance and pay for one
/// connection pool.
///
/// `ownsMultiplexer` is set only by the connection-string factory, which
/// created the multiplexer and therefore must dispose it.
type RedisEmbeddingCache
    (
        multiplexer: IConnectionMultiplexer,
        options: RedisEmbeddingCacheOptions,
        ownsMultiplexer: bool,
        logger: ILogger option,
        metrics: IMetricsSink option
    ) =

    let db = multiplexer.GetDatabase options.Database

    // Telemetry only — never consulted when answering a lookup, so
    // portability rule 4 (stateless between calls) holds. Guarded by a
    // coarse lock for the same reason `InMemoryEmbeddingCache` uses one:
    // one increment per embedding lookup is not a contended path.
    let statsLock = obj ()
    let mutable hits = 0L
    let mutable misses = 0L

    let warn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    let bumpLocal (isHit: bool) =
        lock statsLock (fun () -> if isHit then hits <- hits + 1L else misses <- misses + 1L)

    let readLocal () = lock statsLock (fun () -> hits, misses)

    let countLookup (result: string) =
        match metrics with
        | Some sink -> sink.Increment(CacheMetrics.LookupsTotal, Map.ofList [ "result", result ])
        | None -> ()

    let gaugeHitRate (scope: string) (rate: float) =
        match metrics with
        | Some sink -> sink.SetGauge(CacheMetrics.HitRateGauge, rate, Map.ofList [ "scope", scope ])
        | None -> ()

    let sharedScope = options.HitRateScope = SharedAcrossReplicas

    /// Bump a shared counter. Awaited rather than fire-and-forget: an
    /// unobserved faulted `Task` is worse than the round-trip, and this
    /// path only runs when the deployment opted into shared hit-rate.
    let bumpShared (key: string) = async {
        if sharedScope then
            try
                do!
                    db.StringIncrementAsync(RedisKey.op_Implicit key)
                    |> Async.AwaitTask
                    |> Async.Ignore
            with ex ->
                warn $"RedisEmbeddingCache: could not increment shared counter {key}: {ex.Message}"
    }

    /// Every connected primary endpoint, so `Clear` sweeps a cluster
    /// rather than only the node the connection happens to prefer.
    let scannableServers () =
        multiplexer.GetEndPoints()
        |> Array.toList
        |> List.map (fun (endpoint: EndPoint) -> multiplexer.GetServer endpoint)
        |> List.filter (fun server -> server.IsConnected && not server.IsReplica)

    member _.Options = options

    /// Local and (when configured) shared counters. The richer sibling
    /// of `IEmbeddingCache.HitRate`, which the interface constrains to a
    /// single float.
    member _.Stats() : Async<EmbeddingCacheStats> = async {
        let localHits, localMisses = readLocal ()

        let baseStats = {
            LocalHits = localHits
            LocalMisses = localMisses
            LocalHitRate = hitRateOf localHits localMisses
            SharedHits = None
            SharedMisses = None
            SharedHitRate = None
        }

        if not sharedScope then
            return baseStats
        else
            try
                let keys = [|
                    RedisKey.op_Implicit (Key.hitsKey options)
                    RedisKey.op_Implicit (Key.missesKey options)
                |]

                let! values = db.StringGetAsync keys |> Async.AwaitTask

                let read (value: RedisValue) =
                    match Int64.TryParse(value.ToString()) with
                    | true, parsed -> parsed
                    | _ -> 0L

                let sharedHits = read values[0]
                let sharedMisses = read values[1]

                return {
                    baseStats with
                        SharedHits = Some sharedHits
                        SharedMisses = Some sharedMisses
                        SharedHitRate = Some(hitRateOf sharedHits sharedMisses)
                }
            with ex ->
                warn
                    $"RedisEmbeddingCache: could not read the shared hit-rate counters ({ex.Message}); reporting the local figure only"

                return baseStats
    }

    interface IEmbeddingCache with

        member _.TryGet(key: EmbeddingCacheKey) = async {
            let redisKey = RedisKey.op_Implicit (Key.forKey options key)

            let missed () = async {
                bumpLocal false
                countLookup "miss"
                do! bumpShared (Key.missesKey options)
                return None
            }

            try
                let! value = db.StringGetAsync redisKey |> Async.AwaitTask

                if value.IsNullOrEmpty then
                    return! missed ()
                else
                    // StackExchange.Redis exposes RedisValue → byte[] as
                    // an IMPLICIT conversion (there is no explicit one),
                    // so the annotation is what selects it out of the
                    // String / ReadOnlyMemory siblings.
                    let payload: byte array = RedisValue.op_Implicit value

                    match Codec.decode payload with
                    | Ok embedding ->
                        bumpLocal true
                        countLookup "hit"
                        do! bumpShared (Key.hitsKey options)
                        return Some embedding
                    | Error reason ->
                        // A value we cannot decode is not a cache entry.
                        // Drop it so the next writer replaces it rather
                        // than every replica re-paying the decode failure.
                        warn $"RedisEmbeddingCache: discarding undecodable entry ({reason})"

                        try
                            do! db.KeyDeleteAsync redisKey |> Async.AwaitTask |> Async.Ignore
                        with _ ->
                            ()

                        return! missed ()
            with ex ->
                // Redis is unreachable. The caller must still get an
                // embedding, so this is a miss — the cost is a
                // recomputation, which is exactly what an uncached
                // deployment pays.
                warn $"RedisEmbeddingCache: lookup failed ({ex.Message}); treating as a miss"
                bumpLocal false
                countLookup "error"
                return None
        }

        member _.Set (key: EmbeddingCacheKey) (embedding: float32 array) = async {
            try
                let redisKey = RedisKey.op_Implicit (Key.forKey options key)
                let payload = RedisValue.op_Implicit (Codec.encode embedding)

                do!
                    db.StringSetAsync(redisKey, payload, Nullable options.Ttl, false, When.Always, CommandFlags.None)
                    |> Async.AwaitTask
                    |> Async.Ignore
            with ex ->
                // A dropped write costs a future recomputation. Never
                // raise — `CachingEmbeddingProvider` calls `Set` after
                // the embedding is already in the caller's hand.
                warn $"RedisEmbeddingCache: write failed ({ex.Message}); the entry was not cached"
        }

        member this.HitRate() = async {
            let! stats = this.Stats()

            match stats.SharedHitRate with
            | Some shared ->
                gaugeHitRate "shared" shared
                return shared
            | None ->
                gaugeHitRate "local" stats.LocalHitRate
                return stats.LocalHitRate
        }

        member _.Clear() = async {
            // DSR path (Phase 9h): the whole namespace goes, including
            // entries written under an older key schema and the stats
            // counters. SCAN, never KEYS — this runs against a live
            // server that must keep answering.
            let pattern = RedisValue.op_Implicit (Key.clearPattern options)

            let! outcome = async {
                try
                    for server in scannableServers () do
                        let mutable batch = ResizeArray<RedisKey>()

                        for redisKey in server.Keys(database = options.Database, pattern = pattern, pageSize = 512) do
                            batch.Add redisKey

                            if batch.Count >= 512 then
                                do! db.KeyDeleteAsync(batch.ToArray()) |> Async.AwaitTask |> Async.Ignore
                                batch <- ResizeArray<RedisKey>()

                        if batch.Count > 0 then
                            do! db.KeyDeleteAsync(batch.ToArray()) |> Async.AwaitTask |> Async.Ignore

                    return Ok()
                with ex ->
                    return Error ex
            }

            match outcome with
            | Error ex ->
                // Unlike a lookup, a failed Clear is NOT safe to swallow
                // — it is the privacy-correct response to an erasure
                // request. Surface it with its original stack.
                warn
                    $"RedisEmbeddingCache: Clear failed ({ex.Message}); cached embeddings of erased content may survive"

                ExceptionDispatchInfo.Capture(ex).Throw()
            | Ok() ->
                lock statsLock (fun () ->
                    hits <- 0L
                    misses <- 0L)
        }

    interface IDisposable with
        member _.Dispose() =
            if ownsMultiplexer then
                multiplexer.Dispose()

let private guardOptions (options: RedisEmbeddingCacheOptions) =
    match RedisEmbeddingCacheOptions.validate options with
    | Error problem -> raise (RedisEmbeddingCacheException problem)
    | Ok() -> ()

/// Wrap a multiplexer the deployment already owns — the same instance
/// backing `RedisNotificationChannel` / `RedisDistributedLock`, so all
/// three share one connection pool. Options are validated before the
/// first command; disposing the returned cache does NOT dispose the
/// caller's multiplexer.
let fromMultiplexer
    (multiplexer: IConnectionMultiplexer)
    (options: RedisEmbeddingCacheOptions)
    (logger: ILogger option)
    (metrics: IMetricsSink option)
    : IEmbeddingCache =
    guardOptions options
    new RedisEmbeddingCache(multiplexer, options, false, logger, metrics) :> IEmbeddingCache

/// Connect and wrap. The returned cache owns the multiplexer and
/// disposes it. Option validation runs BEFORE the connection attempt, so
/// a typo in the options is reported as a typo rather than surfacing as
/// a connection error.
let createWith
    (connectionString: string)
    (options: RedisEmbeddingCacheOptions)
    (logger: ILogger option)
    (metrics: IMetricsSink option)
    : IEmbeddingCache =
    guardOptions options

    if String.IsNullOrWhiteSpace connectionString then
        raise (
            RedisEmbeddingCacheException
                "Redis connection string is empty. Pass the value of TOOLUP_REDIS_CONNECTION (e.g. 'localhost:6379'); the companion never reads environment variables itself."
        )

    let multiplexer =
        try
            ConnectionMultiplexer.Connect connectionString :> IConnectionMultiplexer
        with ex ->
            raise (
                RedisEmbeddingCacheException(
                    sprintf "could not connect to Redis with the supplied connection string: %s" ex.Message
                )
            )

    new RedisEmbeddingCache(multiplexer, options, true, logger, metrics) :> IEmbeddingCache

/// Shortest form: connect with the shipped defaults.
let create (connectionString: string) (logger: ILogger option) : IEmbeddingCache =
    createWith connectionString RedisEmbeddingCacheOptions.defaults logger None