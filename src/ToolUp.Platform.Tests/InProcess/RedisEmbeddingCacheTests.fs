module ToolUp.Platform.Tests.InProcess.RedisEmbeddingCacheTests

open System
open Expecto
open StackExchange.Redis
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IEmbeddingCache
open ToolUp.RAG.EmbeddingCaches.Redis.RedisEmbeddingCache

// ─── Phase 513 — Redis IEmbeddingCache test pack ─────────────────────
//
// Two arms, mirroring the Phase 507 Pgvector pack:
//
//  • **Structural arm (always on).** Everything provable WITHOUT a Redis
//    server: the key derivation (which is what makes a model swap miss
//    by construction, and what keeps two models with a `:` in their id
//    from colliding), the wire codec (explicitly little-endian, because
//    the whole point is that a DIFFERENT process reads what this one
//    wrote), the option guards, and the fail-loud ordering at `create`.
//
//  • **Live arm (env-gated on `TOOLUP_REDIS_CONNECTION`).** The claim an
//    external cache exists for: two independent clients over one Redis
//    see each other's entries. Plus TTL expiry, model-swap misses,
//    `Clear`, and both hit-rate scopes. Reported **Pending** when the
//    variable is unset, so a fresh checkout is green without a Redis —
//    the same posture `ToolUp.AIProviders.Tests` and the sibling Redis
//    packs take, and the same env var, so one CI-side Redis serves all.
//
// Every live case works under its own GUID-suffixed `KeyPrefix` and
// `Clear`s on the way out, so concurrent runs against one shared Redis
// cannot interfere.

[<Literal>]
let private LiveConnectionEnvVar = "TOOLUP_REDIS_CONNECTION"

let private liveConnectionString =
    match Environment.GetEnvironmentVariable LiveConnectionEnvVar with
    | null
    | "" -> None
    | s -> Some s

let private version providerId modelId dimensions : EmbeddingVersion = {
    ProviderId = providerId
    ModelId = modelId
    Dimensions = dimensions
}

let private cacheKey providerId modelId dimensions textHash : EmbeddingCacheKey = {
    Version = version providerId modelId dimensions
    TextHash = textHash
}

/// Assert that `f` raises, and that the message NAMES the problem. A
/// descriptive error at the right moment is the behaviour under test,
/// not incidental — `Expect.throws` would discard exactly the part that
/// matters.
let private expectRaisesNaming (fragment: string) (message: string) (f: unit -> unit) =
    let caught =
        try
            f ()
            None
        with ex ->
            Some ex

    match caught with
    | None -> failtestf "%s — expected an exception naming '%s'; none was raised" message fragment
    | Some ex -> Expect.stringContains ex.Message fragment message

// ─── Structural arm ──────────────────────────────────────────────────

let private defaultOptions = RedisEmbeddingCacheOptions.defaults

let private optionGuardTests =
    testList "create-time option guards (fail-loud)" [
        test "the shipped defaults validate" {
            Expect.isOk (RedisEmbeddingCacheOptions.validate defaultOptions) "the shipped defaults must be valid"
        }

        test "a KeyPrefix carrying a glob metacharacter is refused" {
            // This is the load-bearing one: Clear () deletes by
            // `SCAN MATCH "<KeyPrefix>:*"`, so a glob in the prefix
            // silently widens an ERASURE beyond this cache's namespace.
            for bad in [ "toolup:*"; "toolup:?x"; "toolup:[a]"; "toolup:a]"; "toolup\\x" ] do
                Expect.isError
                    (RedisEmbeddingCacheOptions.validate { defaultOptions with KeyPrefix = bad })
                    (sprintf "'%s' would widen the SCAN MATCH pattern Clear () deletes by" bad)
        }

        test "an empty or whitespace KeyPrefix is refused" {
            for bad in [ ""; "   "; "\t" ] do
                Expect.isError
                    (RedisEmbeddingCacheOptions.validate { defaultOptions with KeyPrefix = bad })
                    (sprintf "'%s' is not a namespace" bad)
        }

        test "an over-long KeyPrefix is refused" {
            let tooLong =
                String.replicate (RedisEmbeddingCacheOptions.MaxKeyPrefixLength + 1) "a"

            Expect.isError
                (RedisEmbeddingCacheOptions.validate {
                    defaultOptions with
                        KeyPrefix = tooLong
                })
                "a prefix past the sanity bound is a mistake, not a configuration"
        }

        test "an ordinary namespace is accepted" {
            for good in [ "toolup:embeddings"; "app-1:emb"; "e" ] do
                Expect.isOk
                    (RedisEmbeddingCacheOptions.validate { defaultOptions with KeyPrefix = good })
                    (sprintf "'%s' is a plain namespace" good)
        }

        test "a non-positive TTL is refused" {
            for bad in [ TimeSpan.Zero; TimeSpan.FromSeconds -1.0 ] do
                Expect.isError
                    (RedisEmbeddingCacheOptions.validate { defaultOptions with Ttl = bad })
                    "a zero/negative expiry makes every Set a no-op, which reads as a cache that never hits"
        }

        test "a database index below -1 is refused" {
            Expect.isError
                (RedisEmbeddingCacheOptions.validate { defaultOptions with Database = -2 })
                "-1 is the connection default; anything lower is a typo"
        }

        test "createWith raises before any I/O when the options are invalid" {
            // The endpoint is deliberately unroutable (TEST-NET-3): if
            // option validation did not run first, this would fail with
            // a connection error or hang. Asserting on the MESSAGE is
            // what makes the ordering observable.
            expectRaisesNaming
                "KeyPrefix"
                "option validation must precede the connection attempt, and say what is wrong"
                (fun () ->
                    createWith
                        "203.0.113.1:6379,connectTimeout=1,abortConnect=true"
                        {
                            defaultOptions with
                                KeyPrefix = "toolup:*"
                        }
                        None
                        None
                    |> ignore)
        }

        test "createWith refuses an empty connection string" {
            expectRaisesNaming
                "connection string is empty"
                "an empty connection string is a compose-time mistake, named as one"
                (fun () -> createWith "" defaultOptions None None |> ignore)
        }
    ]

let private keyTests =
    testList "key derivation" [
        test "the key carries the namespace, the schema version and every key component" {
            let rendered =
                Key.forKey defaultOptions (cacheKey "openai" "text-embedding-3-small" 1536 "abc123")

            Expect.stringStarts
                rendered
                (defaultOptions.KeyPrefix
                 + ":"
                 + Key.EntryDiscriminator
                 + ":"
                 + Key.SchemaVersion)
                "entries must sit under the configured namespace, discriminated and schema-versioned"

            for component' in [ "openai"; "text-embedding-3-small"; "1536"; "abc123" ] do
                Expect.stringContains rendered component' (sprintf "'%s' is part of the cache identity" component')
        }

        test "a model swap produces a different key — the miss is structural" {
            // Phase 14h's version stamping exists so a model change
            // invalidates without a flush step anyone has to remember.
            let small =
                Key.forKey defaultOptions (cacheKey "openai" "text-embedding-3-small" 1536 "h")

            let large =
                Key.forKey defaultOptions (cacheKey "openai" "text-embedding-3-large" 3072 "h")

            Expect.notEqual small large "two models must never share a cache entry for the same text"
        }

        test "every EmbeddingVersion component participates in the key" {
            let baseline = Key.forKey defaultOptions (cacheKey "p" "m" 8 "h")

            for other in [ cacheKey "p2" "m" 8 "h"; cacheKey "p" "m2" 8 "h"; cacheKey "p" "m" 16 "h" ] do
                Expect.notEqual
                    (Key.forKey defaultOptions other)
                    baseline
                    "provider, model and dimensions must each be discriminating"
        }

        test "a colon inside a provider or model id cannot shift a segment boundary" {
            // Without escaping, ("a:b", "c") and ("a", "b:c") render to
            // the same joined string — two distinct models sharing one
            // cache entry, which would serve the wrong vector.
            let left = Key.forKey defaultOptions (cacheKey "a:b" "c" 8 "h")
            let right = Key.forKey defaultOptions (cacheKey "a" "b:c" 8 "h")
            Expect.notEqual left right "the segment escape must make the key rendering injective"
        }

        test "the escape is injective — a literal escape sequence cannot forge a boundary" {
            // '%' is escaped first, so a model id literally containing
            // "%3A" must not collide with one containing ':'.
            let escaped = Key.forKey defaultOptions (cacheKey "p" "a%3Ab" 8 "h")
            let literal = Key.forKey defaultOptions (cacheKey "p" "a:b" 8 "h")
            Expect.notEqual escaped literal "escaping '%' before ':' is what keeps the mapping injective"
        }

        test "the clear pattern covers entries and the stats counters, and nothing above the namespace" {
            let pattern = Key.clearPattern defaultOptions

            Expect.stringStarts
                pattern
                (defaultOptions.KeyPrefix + ":")
                "Clear must stay inside the configured namespace"

            Expect.stringEnds pattern "*" "Clear must sweep the whole namespace, including older key schemas"

            let prefixOfPattern = pattern.Substring(0, pattern.Length - 1)

            for key in
                [
                    Key.forKey defaultOptions (cacheKey "p" "m" 8 "h")
                    Key.hitsKey defaultOptions
                    Key.missesKey defaultOptions
                ] do
                Expect.stringStarts key prefixOfPattern "every key this companion writes must be reachable by Clear"
        }

        test "the stats counters cannot be produced by any entry key" {
            // A provider id of "s" must not be able to render onto
            // `{prefix}:s:hits` and corrupt the shared hit-rate.
            let adversarial = Key.forKey defaultOptions (cacheKey "s" "hits" 8 "h")
            Expect.notEqual adversarial (Key.hitsKey defaultOptions) "entry and stats keyspaces must be disjoint"
            Expect.notEqual adversarial (Key.missesKey defaultOptions) "entry and stats keyspaces must be disjoint"
        }
    ]

let private codecTests =
    testList "payload codec" [
        test "an embedding round-trips exactly" {
            let embedding: float32 array = [| 0.0f; 1.0f; -0.5f; 3.14159f; 1e-8f; -1e8f |]
            Expect.equal (Codec.decode (Codec.encode embedding)) (Ok embedding) "the cache must return what was stored"
        }

        test "an empty embedding round-trips" {
            Expect.equal
                (Codec.decode (Codec.encode Array.empty<float32>))
                (Ok Array.empty<float32>)
                "a zero-dimension vector is degenerate but must not corrupt the frame"
        }

        test "non-finite values survive the frame" {
            let embedding: float32 array = [| Single.NaN; Single.PositiveInfinity; Single.NegativeInfinity |]

            match Codec.decode (Codec.encode embedding) with
            | Ok decoded ->
                Expect.isTrue (Single.IsNaN decoded[0]) "NaN must not become 0"
                Expect.isTrue (Single.IsPositiveInfinity decoded[1]) "+Inf must survive"
                Expect.isTrue (Single.IsNegativeInfinity decoded[2]) "-Inf must survive"
            | Error e -> failtestf "a finite-free vector must still decode: %s" e
        }

        test "the frame is little-endian and length-explicit, not a host-endian blit" {
            // Pinned as BYTES: the entry is written by one process and
            // read by another, so a host-endian regression would only
            // show up on a big-endian replica — i.e. never, here.
            let encoded = Codec.encode [| 1.0f |]

            Expect.equal encoded.Length (Codec.HeaderLength + 4) "header plus one single"
            Expect.equal (encoded[0..3]) [| 0x54uy; 0x55uy; 0x45uy; 0x43uy |] "the magic identifies our writer"
            Expect.equal (encoded[4..7]) [| 1uy; 0uy; 0uy; 0uy |] "format version, little-endian"
            Expect.equal (encoded[8..11]) [| 1uy; 0uy; 0uy; 0uy |] "dimension count, little-endian"
            Expect.equal (encoded[12..15]) [| 0uy; 0uy; 0x80uy; 0x3Fuy |] "1.0f as little-endian IEEE-754"
        }

        test "a value from a foreign writer in the same namespace is rejected, not misread" {
            let foreign = Array.create 32 0x41uy
            Expect.isError (Codec.decode foreign) "a payload without our magic is not a cache entry"
        }

        test "a truncated payload is rejected" {
            let encoded = Codec.encode [| 1.0f; 2.0f; 3.0f |]
            Expect.isError (Codec.decode encoded[0 .. Codec.HeaderLength - 2]) "a short header must be refused"
            Expect.isError (Codec.decode encoded[0 .. encoded.Length - 2]) "a truncated body must be refused"
        }

        test "a payload whose declared dimension disagrees with its length is rejected" {
            let encoded = Codec.encode [| 1.0f; 2.0f |]
            // Overstate the dimension count without lengthening the body.
            encoded[8] <- 9uy
            Expect.isError (Codec.decode encoded) "a length/dimension disagreement must never decode"
        }

        test "a future format version is rejected rather than misread" {
            let encoded = Codec.encode [| 1.0f |]
            encoded[4] <- 99uy
            Expect.isError (Codec.decode encoded) "an unknown frame version must be a miss, not a guess"
        }

        test "a null payload is rejected" {
            Expect.isError (Codec.decode null) "a null must be reported, not thrown from"
        }
    ]

let private hitRateTests =
    testList "hit-rate arithmetic" [
        test "a cold cache reports 0.0, not NaN" {
            Expect.equal (hitRateOf 0L 0L) 0.0 "the same convention InMemoryEmbeddingCache uses"
        }

        test "the rate is hits over lookups" {
            Expect.floatClose Accuracy.high (hitRateOf 3L 1L) 0.75 "3 hits in 4 lookups is 0.75"
            Expect.equal (hitRateOf 5L 0L) 1.0 "an all-hit window is 1.0"
            Expect.equal (hitRateOf 0L 5L) 0.0 "an all-miss window is 0.0"
        }
    ]

let private structuralTests =
    testList "structural (no Redis required)" [ optionGuardTests; keyTests; codecTests; hitRateTests ]

// ─── Live arm ────────────────────────────────────────────────────────

let private freshPrefix () =
    "toolup:embcache-test:" + Guid.NewGuid().ToString "N"

let private embedding (seed: int) : float32 array =
    Array.init 8 (fun i -> float32 seed + float32 i / 10.0f)

/// A cache over its own namespace. The returned disposer clears the
/// namespace and drops the connection, so a live run leaves nothing
/// behind on a shared Redis.
let private makeCacheWith (connectionString: string) (options: RedisEmbeddingCacheOptions) =
    let cache = createWith connectionString options None None

    let dispose =
        { new IDisposable with
            member _.Dispose() =
                try
                    cache.Clear() |> Async.RunSynchronously
                with _ ->
                    ()

                (cache :?> IDisposable).Dispose()
        }

    cache, dispose

let private waitForMiss (cache: IEmbeddingCache) (key: EmbeddingCacheKey) (budget: TimeSpan) = async {
    let deadline = DateTime.UtcNow + budget

    let rec poll () = async {
        match! cache.TryGet key with
        | None -> return true
        | Some _ when DateTime.UtcNow > deadline -> return false
        | Some _ ->
            do! Async.Sleep 200
            return! poll ()
    }

    return! poll ()
}

let private liveTests (connectionString: string) =
    testList $"live ({LiveConnectionEnvVar} set)" [

        // THE acceptance criterion: this is what the in-process default
        // cannot do, and the entire reason the companion exists.
        testCaseAsync "two clients sharing one Redis see each other's entries"
        <| async {
            let options = {
                RedisEmbeddingCacheOptions.defaults with
                    KeyPrefix = freshPrefix ()
            }

            // Two independent connections — as close to two replicas as
            // one process gets.
            let replicaA, disposeA = makeCacheWith connectionString options
            let replicaB, disposeB = makeCacheWith connectionString options

            try
                let key = cacheKey "openai" "text-embedding-3-small" 8 "shared-hash"

                let! coldOnB = replicaB.TryGet key
                Expect.isNone coldOnB "nothing has been written yet"

                do! replicaA.Set key (embedding 1)

                let! seenByB = replicaB.TryGet key

                Expect.equal
                    seenByB
                    (Some(embedding 1))
                    "replica B must serve replica A's entry — a miss on one becomes a hit on the other"
            finally
                disposeA.Dispose()
                disposeB.Dispose()
        }

        testCaseAsync "a model swap misses on the shared cache"
        <| async {
            let options = {
                RedisEmbeddingCacheOptions.defaults with
                    KeyPrefix = freshPrefix ()
            }

            let cache, dispose = makeCacheWith connectionString options

            try
                let small = cacheKey "openai" "text-embedding-3-small" 8 "same-text"
                let large = cacheKey "openai" "text-embedding-3-large" 16 "same-text"

                do! cache.Set small (embedding 2)

                let! hit = cache.TryGet small
                Expect.isSome hit "the entry just written must be readable"

                let! afterSwap = cache.TryGet large

                Expect.isNone
                    afterSwap
                    "the embedding version is part of the key, so a model swap invalidates with no flush step"
            finally
                dispose.Dispose()
        }

        testCaseAsync "an entry expires on its TTL"
        <| async {
            let options = {
                RedisEmbeddingCacheOptions.defaults with
                    KeyPrefix = freshPrefix ()
                    Ttl = TimeSpan.FromSeconds 1.0
            }

            let cache, dispose = makeCacheWith connectionString options

            try
                let key = cacheKey "openai" "m" 8 "expiring"
                do! cache.Set key (embedding 3)

                let! immediate = cache.TryGet key
                Expect.isSome immediate "the entry must be live before its TTL elapses"

                let! expired = waitForMiss cache key (TimeSpan.FromSeconds 10.0)
                Expect.isTrue expired "the configured TTL must actually bound the entry"
            finally
                dispose.Dispose()
        }

        testCaseAsync "Clear erases the namespace for every client"
        <| async {
            let options = {
                RedisEmbeddingCacheOptions.defaults with
                    KeyPrefix = freshPrefix ()
            }

            let replicaA, disposeA = makeCacheWith connectionString options
            let replicaB, disposeB = makeCacheWith connectionString options

            try
                let key = cacheKey "openai" "m" 8 "erase-me"
                do! replicaA.Set key (embedding 4)

                let! before = replicaB.TryGet key
                Expect.isSome before "precondition: B can see the entry"

                // The DSR path (Phase 9h) — a cached embedding of erased
                // content must not survive on ANY replica.
                do! replicaB.Clear()

                let! after = replicaA.TryGet key
                Expect.isNone after "Clear must erase for every client, not just the caller"
            finally
                disposeA.Dispose()
                disposeB.Dispose()
        }

        testCaseAsync "a foreign value in the namespace is discarded rather than misread"
        <| async {
            let options = {
                RedisEmbeddingCacheOptions.defaults with
                    KeyPrefix = freshPrefix ()
            }

            let cache, dispose = makeCacheWith connectionString options
            use multiplexer = ConnectionMultiplexer.Connect connectionString

            try
                let key = cacheKey "openai" "m" 8 "foreign"
                let redisKey = RedisKey.op_Implicit (Key.forKey options key)

                do!
                    multiplexer.GetDatabase().StringSetAsync(redisKey, RedisValue.op_Implicit "not an embedding")
                    |> Async.AwaitTask
                    |> Async.Ignore

                let! result = cache.TryGet key
                Expect.isNone result "an undecodable value is a miss, never a fabricated vector"

                let! stillThere = multiplexer.GetDatabase().KeyExistsAsync redisKey |> Async.AwaitTask
                Expect.isFalse stillThere "the undecodable entry must be dropped, not re-read by every replica"
            finally
                dispose.Dispose()
        }

        testCaseAsync "local hit-rate counts this instance only"
        <| async {
            let options = {
                RedisEmbeddingCacheOptions.defaults with
                    KeyPrefix = freshPrefix ()
                    HitRateScope = LocalProcess
            }

            let cache, dispose = makeCacheWith connectionString options

            try
                let key = cacheKey "openai" "m" 8 "rate"

                let! cold = cache.HitRate()
                Expect.equal cold 0.0 "a cache with no lookups reports 0.0"

                let! _ = cache.TryGet key // miss
                do! cache.Set key (embedding 5)
                let! _ = cache.TryGet key // hit

                let! rate = cache.HitRate()
                Expect.floatClose Accuracy.high rate 0.5 "one hit in two lookups"

                let stats = cache :?> RedisEmbeddingCache
                let! detail = stats.Stats()
                Expect.equal detail.LocalHits 1L "one hit"
                Expect.equal detail.LocalMisses 1L "one miss"

                Expect.isNone
                    detail.SharedHitRate
                    "under LocalProcess scope the fleet figure is absent, and absence is reported as absence"
            finally
                dispose.Dispose()
        }

        testCaseAsync "shared hit-rate aggregates across clients"
        <| async {
            let options = {
                RedisEmbeddingCacheOptions.defaults with
                    KeyPrefix = freshPrefix ()
                    HitRateScope = SharedAcrossReplicas
            }

            let replicaA, disposeA = makeCacheWith connectionString options
            let replicaB, disposeB = makeCacheWith connectionString options

            try
                let key = cacheKey "openai" "m" 8 "shared-rate"

                let! _ = replicaA.TryGet key // miss on A
                do! replicaA.Set key (embedding 6)
                let! _ = replicaB.TryGet key // hit on B

                // Each instance has seen ONE lookup, so a per-instance
                // figure would be 0.0 on A and 1.0 on B. The fleet
                // figure is what an operator wants, and both agree on it.
                let! rateFromA = replicaA.HitRate()
                let! rateFromB = replicaB.HitRate()

                Expect.floatClose Accuracy.high rateFromA 0.5 "the fleet saw one hit in two lookups"
                Expect.floatClose Accuracy.high rateFromB rateFromA "both clients must report the same fleet figure"

                let detailB = replicaB :?> RedisEmbeddingCache
                let! stats = detailB.Stats()
                Expect.equal stats.LocalHits 1L "B's own lookup was the hit"
                Expect.equal stats.LocalMisses 0L "B never missed"
                Expect.equal stats.SharedHits (Some 1L) "the fleet counters see A's miss and B's hit"
                Expect.equal stats.SharedMisses (Some 1L) "the fleet counters see A's miss and B's hit"
            finally
                disposeA.Dispose()
                disposeB.Dispose()
        }

        testCaseAsync "the health probe reports Healthy against a reachable Redis"
        <| async {
            use multiplexer = ConnectionMultiplexer.Connect connectionString
            let probe = ToolUp.RAG.EmbeddingCaches.Redis.Health.create multiplexer

            Expect.equal probe.Name "embedding_cache:redis" "the probe name is the registration key"

            let! result = probe.Check()

            Expect.equal
                result
                ToolUp.Platform.HealthChecks.Healthy
                "a reachable Redis with normal latency must probe Healthy"
        }
    ]

// ─── Phase 633 — the compose seam (`RAGServerApp.withEmbeddingCache`) ──
//
// Phase 513 shipped this companion, but `composeWithRAG` hard-constructed
// `InMemoryEmbeddingCache` at the compose site — so the companion was
// reachable only by raw DI, while two docs pages already described a
// `withEmbeddingCache` builder that did not exist. Phase 633 ships the
// builder and closes 513's deferred 513.C: the Team-mode shared-cache
// validator was constructed from `ServerConfig` alone and therefore could
// not see that a deployment had ALREADY addressed the concern, so it
// warned at a correctly-configured fleet.
//
// Three things are pinned here, and the third is the one that matters:
//
//  • the hook plumbs through to the composition (the resolved
//    `IEmbeddingCache` is the composed instance, not a fresh default);
//  • unset is byte-identical to the pre-633 behaviour (GP 11) — an
//    `InMemoryEmbeddingCache`, and the warning still fires;
//  • the LIFT is conditioned on the cache being genuinely cross-replica,
//    not on the hook having been called. Composing
//    `InMemoryEmbeddingCache` by hand changes nothing about the
//    per-replica divergence, so it must keep warning.

let private stubFactory =
    { new ToolUp.AI.IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None

        member _.Resolve _ = async { return Error ToolUp.AI.NoProviderConfigured }

        member _.TryResolveByLabel(_, _) = async { return Error ToolUp.AI.NoProviderConfigured }

        member _.BuildPlatform(_, _, _) = None
    }

let private stubProfile =
    { new ToolUp.Platform.Providers.IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

let private stubEmbedder =
    { new IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return Array.zeroCreate 8 }

        member _.GenerateEmbeddings texts = async {
            return texts |> Seq.map (fun _ -> Array.zeroCreate<float32> 8) |> Seq.toArray
        }

        member _.ProviderId = "stub"
        member _.ModelId = "stub-model"
        member _.Dimensions = 8
    }

/// An `IEmbeddingCache` that is NOT `InMemoryEmbeddingCache` — structurally
/// what every cross-replica companion (`RedisEmbeddingCache` included) looks
/// like to the composition. Used so the always-on arm can prove the lift
/// without a broker; the live arm below repeats it against the real
/// companion type.
let private crossReplicaStub () =
    { new IEmbeddingCache with
        member _.TryGet _ = async { return None }
        member _.Set _ _ = async { return () }
        member _.HitRate() = async { return 0.0 }
        member _.Clear() = async { return () }
    }

let private newApp () =
    ToolUp.RAG.RAGCompose.RAGServerApp.create stubFactory stubProfile stubEmbedder

/// Resolve the `IEmbeddingCache` the composition actually registered —
/// the only evidence that the hook reaches `composeWithRAG` rather than
/// merely landing in the record.
let private composedCache (app: ToolUp.RAG.RAGCompose.RAGServerApp) : IEmbeddingCache =
    let composed = ToolUp.RAG.RAGCompose.composeRAG app

    let services =
        Microsoft.Extensions.DependencyInjection.ServiceCollection()
        :> Microsoft.Extensions.DependencyInjection.IServiceCollection

    let services =
        match composed.Extensions.ServiceConfig with
        | Some f -> f services
        | None -> services

    let provider =
        Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider
            services

    provider.GetService typeof<IEmbeddingCache> :?> IEmbeddingCache

let private teamCfg (replicaCount: int) (accepted: bool) : ToolUp.Platform.ServerConfig = {
    ToolUp.Platform.ServerConfig.defaults with
        Surfaces = [ ToolUp.Platform.SurfaceProfile.team ]
        ReplicaCount = replicaCount
        AcceptSharedEmbeddingCacheInTeamMode = accepted
}

let private validate
    (config: ToolUp.Platform.ServerConfig)
    (crossReplicaCache: bool)
    : ToolUp.Platform.ConfigValidation.ValidationResult =
    let v =
        ToolUp.RAG.RagConfigValidator.TeamModeSharedEmbeddingCacheValidator(config, crossReplicaCache)
        :> ToolUp.Platform.ConfigValidation.IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private composeSeamTests =
    testList "Phase 633 — RAGServerApp.withEmbeddingCache" [
        testList "the hook" [
            test "unset ⇒ the composition still builds InMemoryEmbeddingCache (GP 11)" {
                let app = newApp ()
                Expect.isNone app.EmbeddingCache "no hook composed ⇒ the field stays None"

                Expect.isTrue
                    (composedCache app :? ToolUp.RAG.InMemoryEmbeddingCache.InMemoryEmbeddingCache)
                    "the pre-633 default must survive unchanged — this is the byte-identical pin"
            }

            test "composed ⇒ the composition resolves THAT instance, not a fresh default" {
                let cache = crossReplicaStub ()
                let app = newApp () |> ToolUp.RAG.RAGCompose.RAGServerApp.withEmbeddingCache cache

                Expect.isTrue
                    (Object.ReferenceEquals(composedCache app, cache))
                    "withEmbeddingCache must reach composeWithRAG, not merely set a record field"
            }
        ]

        testList "hasCrossReplicaEmbeddingCache — what the lift is conditioned on" [
            test "unset ⇒ false" {
                Expect.isFalse
                    (ToolUp.RAG.RAGCompose.hasCrossReplicaEmbeddingCache (newApp ()))
                    "the default cache is process-local"
            }

            test "a cross-replica companion ⇒ true" {
                let app =
                    newApp ()
                    |> ToolUp.RAG.RAGCompose.RAGServerApp.withEmbeddingCache (crossReplicaStub ())

                Expect.isTrue
                    (ToolUp.RAG.RAGCompose.hasCrossReplicaEmbeddingCache app)
                    "a fleet-wide cache spans replicas"
            }

            test "InMemoryEmbeddingCache composed BY HAND ⇒ still false" {
                let app =
                    newApp ()
                    |> ToolUp.RAG.RAGCompose.RAGServerApp.withEmbeddingCache (
                        ToolUp.RAG.InMemoryEmbeddingCache.InMemoryEmbeddingCache() :> IEmbeddingCache
                    )

                Expect.isFalse
                    (ToolUp.RAG.RAGCompose.hasCrossReplicaEmbeddingCache app)
                    "calling the hook is not the point — removing the per-replica divergence is; an operator who wires the process-local cache by hand has changed nothing the validator warns at"
            }
        ]

        testList "513.C — the Team-mode warning is LIFTED by a cross-replica cache, not removed" [
            test "single instance ⇒ Ok (was always fine)" {
                Expect.equal
                    (validate (teamCfg 1 false) false)
                    ToolUp.Platform.ConfigValidation.Ok
                    "one replica cannot diverge from itself"
            }

            test "process-local cache + ReplicaCount = 2 ⇒ Warning (the concern still fires)" {
                match validate (teamCfg 2 false) false with
                | ToolUp.Platform.ConfigValidation.Warning msg ->
                    Expect.stringContains msg "ReplicaCount > 1" "names the multi-replica premise"

                    Expect.stringContains
                        msg
                        "withEmbeddingCache"
                        "points at the fix this phase shipped, not just the escape hatch"

                    Expect.stringContains msg "AcceptSharedEmbeddingCacheInTeamMode" "still documents the escape hatch"
                | other -> failtestf "expected Warning, got %A" other
            }

            test "cross-replica cache + ReplicaCount = 2 ⇒ Ok (the premise no longer holds)" {
                Expect.equal
                    (validate (teamCfg 2 false) true)
                    ToolUp.Platform.ConfigValidation.Ok
                    "every replica reads and writes the same entries, so there is no divergence left to warn about"
            }

            test "the escape hatch still silences it independently of the cache" {
                Expect.equal
                    (validate (teamCfg 2 true) false)
                    ToolUp.Platform.ConfigValidation.Ok
                    "AcceptSharedEmbeddingCacheInTeamMode = true is unchanged by 633"
            }

            test "end to end: compose a cross-replica cache ⇒ Team-mode preflight is silent" {
                let app =
                    newApp ()
                    |> ToolUp.RAG.RAGCompose.RAGServerApp.withEmbeddingCache (crossReplicaStub ())

                Expect.equal
                    (validate (teamCfg 2 false) (ToolUp.RAG.RAGCompose.hasCrossReplicaEmbeddingCache app))
                    ToolUp.Platform.ConfigValidation.Ok
                    "the acceptance criterion, driven through the same predicate composeWithRAG feeds the validator"
            }

            test "end to end: compose nothing ⇒ Team-mode preflight still warns" {
                let app = newApp ()

                match validate (teamCfg 2 false) (ToolUp.RAG.RAGCompose.hasCrossReplicaEmbeddingCache app) with
                | ToolUp.Platform.ConfigValidation.Warning _ -> ()
                | other -> failtestf "an unchanged deployment must be byte-identical to pre-633; got %A" other
            }
        ]
    ]

/// The live half of the compose seam: the SHIPPED companion type — not a
/// structural stand-in — lifts the warning. Env-gated like the rest of the
/// live arm.
let private liveComposeSeamTests (connectionString: string) =
    testList "Phase 633 — the shipped Redis companion lifts the warning" [
        test "RedisEmbeddingCache composed via withEmbeddingCache ⇒ preflight Ok" {
            let options = {
                RedisEmbeddingCacheOptions.defaults with
                    KeyPrefix = freshPrefix ()
            }

            let cache, dispose = makeCacheWith connectionString options

            try
                let app = newApp () |> ToolUp.RAG.RAGCompose.RAGServerApp.withEmbeddingCache cache

                Expect.isTrue
                    (ToolUp.RAG.RAGCompose.hasCrossReplicaEmbeddingCache app)
                    "the real companion must satisfy the predicate the stub stands in for"

                Expect.equal
                    (validate (teamCfg 2 false) (ToolUp.RAG.RAGCompose.hasCrossReplicaEmbeddingCache app))
                    ToolUp.Platform.ConfigValidation.Ok
                    "the acceptance criterion, against a real cross-replica cache"
            finally
                dispose.Dispose()
        }
    ]

// ─── Registration ────────────────────────────────────────────────────

let tests =
    testList "RedisEmbeddingCache" [
        structuralTests
        composeSeamTests

        match liveConnectionString with
        | Some connectionString -> testList "live" [ liveTests connectionString; liveComposeSeamTests connectionString ]
        | None ->
            // A single Pending case so the report lists the live arm as
            // explicitly skipped rather than absent — the absence of a
            // gate and a passing gate must not look alike.
            testList $"live ({LiveConnectionEnvVar} set)" [
                ptestCase $"skipped — {LiveConnectionEnvVar} not set" <| fun _ -> ()
            ]
    ]