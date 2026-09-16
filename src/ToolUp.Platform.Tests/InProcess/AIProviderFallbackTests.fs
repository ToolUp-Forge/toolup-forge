module ToolUp.Platform.Tests.InProcess.AIProviderFallbackTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.AI

// ─── Phase 498 — provider fallback / failover routing ────────────
//
// Phase 43.B shipped `ProviderProfile.Fallback` as data; Phase 498 is
// the runtime that honours it. The four acceptance cases are the four
// `contractTests` below, and each is a claim about a different half of
// the feature:
//
//   * primary down → the secondary serves the turn, and the failover
//     is in the audit trail;
//   * every entry down → a TYPED terminal failure, not a hang and not
//     a synthetic error case invented for the occasion;
//   * a `PermanentClient` error ends the turn with the secondary never
//     touched — the case that decides whether this feature spends a
//     second provider's budget re-asking a question that was already
//     answered "no";
//   * a deployment with no chain gets the SAME PROVIDER OBJECT back
//     (GP 11) — asserted by reference, because "byte-for-byte
//     unchanged" is a claim about the object graph and a behavioural
//     assertion would pass over a wrapper that merely delegates well.
//
// Everything else here pins a decision the implementation had to take
// and could have taken differently.

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

let private descriptor (id: string) : AIProviderDescriptor = {
    Id = id
    DisplayName = id
    SupportedModels = [ "m1" ]
    DefaultModel = "m1"
    Capabilities = {
        noCaps with
            ProviderName = id
            Model = "m1"
    }
}

let private okResponse (content: string) : AIProviderResponse = {
    Content = content
    ToolCalls = []
    StopReason = "end_turn"
    Usage = None
}

/// Ordered log of which provider ids were actually CALLED. The
/// "secondary never touched" assertions are the point — a failover
/// feature that re-routes on a `PermanentClient` error passes every
/// result-shaped assertion and is still wrong.
type private CallLog() =
    let calls = ResizeArray<string>()

    member _.Record(id: string) = lock calls (fun () -> calls.Add id)

    member _.Calls = lock calls (fun () -> List.ofSeq calls)

/// A provider whose every send returns `outcome`, recording the call.
let private stubProvider (log: CallLog) (id: string) (outcome: Result<AIProviderResponse, AIProviderError>) =
    { new IAIProvider with
        member _.Capabilities = {
            noCaps with
                ProviderName = id
                Model = "m1"
        }

        member _.SendMessage(_, _, _, _, _) = async {
            log.Record id
            return outcome
        }

        member _.SendStructuredMessage(_, _, _, _, _) = async {
            log.Record id
            return outcome
        }
    }

let private builderFor
    (log: CallLog)
    (id: string)
    (outcome: Result<AIProviderResponse, AIProviderError>)
    : AIProviderBuilder =
    {
        Descriptor = descriptor id
        Build = fun _apiKey _model -> stubProvider log id outcome
    }

// ─── Provider vendors, one per failure shape ─────────────────────

let private upVendor = "up-vendor"
let private downVendor = "down-vendor"
let private permVendor = "perm-vendor"
let private streamVendor = "stream-vendor"

let private outageError = TransientServer(503, "upstream overloaded")

let private exhaustedError =
    RetriesExhausted(3, TransientNetwork "connection refused")

let private permanentError = PermanentClient(401, "invalid api key")

let private abortedError =
    StreamingAborted("partial answer so far", "socket closed mid-stream")

let private builders (log: CallLog) = [
    builderFor log upVendor (Ok(okResponse "served"))
    builderFor log downVendor (Error outageError)
    builderFor log permVendor (Error permanentError)
    builderFor log streamVendor (Error abortedError)
]

// ─── In-memory substrate ─────────────────────────────────────────

type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    member _.Seed(container: string, key: string, value: string) = store[(container, key)] <- value

    interface ISecretStore with
        member _.GetSecret(container, key) = async {
            match store.TryGetValue((container, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(container, key, value) = async {
            store[(container, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(container, key) = async {
            store.TryRemove((container, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(container) = async {
            return
                store.Keys
                |> Seq.filter (fun (c, _) -> c = container)
                |> Seq.map snd
                |> List.ofSeq
        }

type private InMemoryProviderProfile() =
    let blobs = ConcurrentDictionary<string, ProviderProfile>()

    member _.Seed(scope: StorageScope, profile: ProviderProfile) = blobs[scope.ScopeId] <- profile

    interface IProviderProfile with
        member _.Get scope = async {
            match blobs.TryGetValue scope.ScopeId with
            | true, p -> return Some p
            | false, _ -> return None
        }

        member _.Set(scope, profile) = async {
            blobs[scope.ScopeId] <- profile
            return Ok()
        }

        member _.Clear scope = async {
            blobs.TryRemove scope.ScopeId |> ignore
            return ()
        }

        member _.ResolveEntry(scope, surface, context) = async {
            match blobs.TryGetValue scope.ScopeId with
            | true, profile -> return ProviderProfile.resolveEntry surface context profile
            | false, _ -> return None
        }

        member _.SetEntryHealth(_, _, _) = async { return Ok() }

// ─── Harness ─────────────────────────────────────────────────────

let private ctx = AccessContext.unrestricted (AuthenticatedUser "user-1")

let private scope =
    match AccessContext.configScope ctx with
    | Some s -> s
    | None -> failwith "AuthenticatedUser must yield a persistent config scope"

/// Seed a profile routing `ai.assistant` to a `primary` entry backed by
/// `primaryVendor`, with `chain` as the declared `FallbackChain` and
/// `others` as the additional labelled entries the chain may name.
let private seedProfile
    (profileStore: InMemoryProviderProfile)
    (secrets: InMemorySecretStore)
    (primaryVendor: string)
    (others: (string * string) list)
    (chain: string list)
    =
    let entries =
        ProviderEntry.pastedKey "primary" primaryVendor None "key-primary"
        :: (others
            |> List.map (fun (label, vendor) -> ProviderEntry.pastedKey label vendor None $"key-{label}"))

    entries
    |> List.iter (fun e -> secrets.Seed(scope.Container, e.SecretKeyName, $"secret-for-{e.Label}"))

    profileStore.Seed(
        scope,
        {
            ProviderProfile.empty () with
                Entries = entries
                Routing = [
                    {
                        Surface = AIProviderSurface.aiAssistant
                        Context = None
                        EntryLabel = "primary"
                    }
                ]
                Fallback = { Ordered = chain }
        }
    )

/// Build the full stack under test: the real `DefaultAIProviderFactory`
/// with `withFailoverChain` over it, plus the recorded failover sink.
let private harness (primaryVendor: string) (others: (string * string) list) (chain: string list) =
    let log = CallLog()
    let secrets = InMemorySecretStore()
    let profileStore = InMemoryProviderProfile()
    seedProfile profileStore secrets primaryVendor others chain

    let records = ResizeArray<AIProviderFailoverRecord>()

    let inner =
        DefaultAIProviderFactory.create
            (builders log)
            (profileStore :> IProviderProfile)
            (secrets :> ISecretStore)
            StrictBYOK
            []
            None

    let factory =
        DefaultAIProviderFactory.withFailoverChain
            (profileStore :> IProviderProfile)
            (fun r -> async { lock records (fun () -> records.Add r) })
            inner

    factory, log, records

let private resolveOrFail (factory: IAIProviderFactory) (c: AccessContext) = async {
    let! resolved = factory.Resolve c

    match resolved with
    | Ok p -> return p
    | Error e -> return failtestf "expected a resolved provider, got %s" (ProviderResolutionError.toMessage e)
}

let private send (provider: IAIProvider) =
    provider.SendMessage([], [], None, None, RetryPolicy.defaults)

// ─── Tests ───────────────────────────────────────────────────────

let tests =
    testList "Phase 498 — AI provider fallback and failover routing" [

        // ── 498.B — the classification, on its own ─────────────────
        //
        // Pinned separately from the routing because it is the part a
        // future error case has to be considered against: adding an
        // `AIProviderError` case without deciding its failover
        // disposition means it silently inherits `isRetryable`'s.

        testCase "isOutageClass — transport and server outages re-route"
        <| fun () ->
            Expect.isTrue (AIProviderFailover.isOutageClass (TransientNetwork "dns")) "TransientNetwork is an outage"

            Expect.isTrue
                (AIProviderFailover.isOutageClass (TransientServer(503, "overloaded")))
                "TransientServer is an outage"

            Expect.isTrue
                (AIProviderFailover.isOutageClass (TransientServer(429, "rate limited")))
                "a 429 is an outage of the endpoint's availability to THIS caller"

        testCase "isOutageClass — RetriesExhausted is judged on its inner cause"
        <| fun () ->
            Expect.isTrue
                (AIProviderFailover.isOutageClass (RetriesExhausted(3, TransientServer(500, "boom"))))
                "exhausted against an outage IS the commonest failover trigger"

            Expect.isTrue
                (AIProviderFailover.isOutageClass (RetriesExhausted(2, RetriesExhausted(2, TransientNetwork "reset"))))
                "nesting is unwrapped to the root cause"

            Expect.isFalse
                (AIProviderFailover.isOutageClass (RetriesExhausted(3, PermanentClient(400, "bad request"))))
                "exhausted against a client error is still a client error"

        testCase "isOutageClass — answered-but-wrong and already-streamed never re-route"
        <| fun () ->
            Expect.isFalse
                (AIProviderFailover.isOutageClass (PermanentClient(401, "bad key")))
                "the same request fails identically at the secondary"

            Expect.isFalse
                (AIProviderFailover.isOutageClass (StreamingAborted("partial", "socket closed")))
                "partial content already reached the user; a re-route would duplicate output"

            Expect.isFalse
                (AIProviderFailover.isOutageClass (MalformedResponse "not json"))
                "the endpoint answered — the shape is wrong, not the endpoint"

            Expect.isFalse
                (AIProviderFailover.isOutageClass (UnsupportedCapability("vision", "model has no vision")))
                "a capability gap follows the model, not the endpoint"

            Expect.isFalse
                (AIProviderFailover.isOutageClass (SchemaUnsupported("oneOf", "$.a")))
                "a schema gap follows the model, not the endpoint"

        // ── Acceptance 1 — primary down, secondary serves ──────────

        testCaseAsync "primary down → the secondary serves the turn and the failover is recorded"
        <| async {
            let factory, log, records =
                harness downVendor [ "secondary", upVendor ] [ "secondary" ]

            let! provider = resolveOrFail factory ctx
            let! result = send provider

            match result with
            | Ok response -> Expect.equal response.Content "served" "the secondary's response is returned"
            | Error e -> failtestf "expected the secondary to serve, got %s" (AIProviderError.toMessage e)

            Expect.equal log.Calls [ downVendor; upVendor ] "the primary was tried first, then the secondary"

            Expect.hasLength records 1 "exactly one failover was recorded"
            let r = records[0]
            Expect.equal r.FromProvider downVendor "the record names the provider that failed"
            Expect.equal r.ToProvider upVendor "the record names the provider that took over"
            Expect.equal r.ToLabel "secondary" "the record names the chain entry label"
            Expect.isTrue r.Resolved "the advance resolved"
            Expect.equal r.ChainPosition 1 "first chain position"
            Expect.equal r.ChainLength 1 "chain length as declared"
            Expect.equal r.ScopeId scope.ScopeId "the record joins the latency stream on ScopeId"
            Expect.isTrue (r.Reason.Contains "503") "the vendor's own diagnostic is preserved"
        }

        testCaseAsync "RetriesExhausted from the primary re-routes exactly as a bare outage does"
        <| async {
            // The provider's own retry budget lapsing is the shape a
            // real outage arrives in — the bare transient cases are
            // mostly seen from fail-fast providers.
            let log = CallLog()
            let secrets = InMemorySecretStore()
            let profileStore = InMemoryProviderProfile()
            seedProfile profileStore secrets "exhausted-vendor" [ "secondary", upVendor ] [ "secondary" ]

            let records = ResizeArray<AIProviderFailoverRecord>()

            let inner =
                DefaultAIProviderFactory.create
                    (builderFor log "exhausted-vendor" (Error exhaustedError) :: builders log)
                    (profileStore :> IProviderProfile)
                    (secrets :> ISecretStore)
                    StrictBYOK
                    []
                    None

            let factory =
                DefaultAIProviderFactory.withFailoverChain
                    (profileStore :> IProviderProfile)
                    (fun r -> async { lock records (fun () -> records.Add r) })
                    inner

            let! provider = resolveOrFail factory ctx
            let! result = send provider

            match result with
            | Ok response -> Expect.equal response.Content "served" "the secondary served"
            | Error e -> failtestf "expected a re-route, got %s" (AIProviderError.toMessage e)

            Expect.equal log.Calls [ "exhausted-vendor"; upVendor ] "the chain advanced past the exhausted primary"
            Expect.hasLength records 1 "one failover recorded"
        }

        // ── Acceptance 2 — every entry down → typed terminal failure ─

        testCaseAsync "every chain entry down → the last typed error is returned, chain walked once"
        <| async {
            let factory, log, records =
                harness downVendor [ "secondary", downVendor ] [ "secondary" ]

            let! provider = resolveOrFail factory ctx
            let! result = send provider

            match result with
            | Ok _ -> failtest "expected a terminal failure once the chain was exhausted"
            | Error e ->
                // The LAST error, unchanged — not a synthetic
                // "everything is down" case. Every existing consumer's
                // handling of `AIProviderError` still applies.
                Expect.equal e outageError "the last entry's own typed error surfaces"

            Expect.equal log.Calls [ downVendor; downVendor ] "each entry was tried exactly once"
            Expect.hasLength records 1 "one advance was recorded before the chain ran out"
        }

        // ── Acceptance 3 — PermanentClient never re-routes ──────────

        testCaseAsync "PermanentClient on the primary ends the turn without touching the secondary"
        <| async {
            let factory, log, records =
                harness permVendor [ "secondary", upVendor ] [ "secondary" ]

            let! provider = resolveOrFail factory ctx
            let! result = send provider

            match result with
            | Ok _ -> failtest "a bad key must not be answered by spending a second provider's budget"
            | Error e -> Expect.equal e permanentError "the client error surfaces unchanged"

            Expect.equal log.Calls [ permVendor ] "the secondary was never called"
            Expect.isEmpty records "no failover was recorded"
        }

        testCaseAsync "StreamingAborted on the primary ends the turn without touching the secondary"
        <| async {
            // The safety half of the same rule: partial content has
            // already been delivered to the user's stream, so a
            // re-route would replay the answer from the top.
            let factory, log, records =
                harness streamVendor [ "secondary", upVendor ] [ "secondary" ]

            let! provider = resolveOrFail factory ctx
            let! result = send provider

            match result with
            | Ok _ -> failtest "a mid-stream abort must not be re-answered from the top"
            | Error e -> Expect.equal e abortedError "the abort surfaces with its partial text intact"

            Expect.equal log.Calls [ streamVendor ] "the secondary was never called"
            Expect.isEmpty records "no failover was recorded"
        }

        // ── Acceptance 4 — no chain, byte-for-byte unchanged ───────

        testCaseAsync "no FallbackChain → the SAME provider object is returned (GP 11)"
        <| async {
            let log = CallLog()
            let secrets = InMemorySecretStore()
            let profileStore = InMemoryProviderProfile()
            seedProfile profileStore secrets upVendor [] []

            // `singleProvider` hands back one fixed instance from every
            // Resolve, so reference equality is a direct assertion that
            // the failover layer added nothing to the object graph.
            let sentinel = stubProvider log upVendor (Ok(okResponse "served"))
            let inner = DefaultAIProviderFactory.singleProvider (descriptor upVendor) sentinel

            let factory =
                DefaultAIProviderFactory.withFailoverChain
                    (profileStore :> IProviderProfile)
                    (fun _ -> async { return () })
                    inner

            let! provider = resolveOrFail factory ctx

            Expect.isTrue (obj.ReferenceEquals(provider, sentinel)) "an empty chain wraps nothing at all"
        }

        testCaseAsync "a chain naming only the routed primary is empty after filtering → unchanged"
        <| async {
            // Re-issuing to the endpoint that just failed is a RETRY,
            // and the provider's own `RetryPolicy` has already spent
            // its budget doing exactly that.
            let log = CallLog()
            let secrets = InMemorySecretStore()
            let profileStore = InMemoryProviderProfile()
            seedProfile profileStore secrets upVendor [] [ "primary"; "  "; "primary" ]

            let sentinel = stubProvider log upVendor (Ok(okResponse "served"))
            let inner = DefaultAIProviderFactory.singleProvider (descriptor upVendor) sentinel

            let factory =
                DefaultAIProviderFactory.withFailoverChain
                    (profileStore :> IProviderProfile)
                    (fun _ -> async { return () })
                    inner

            let! provider = resolveOrFail factory ctx

            Expect.isTrue
                (obj.ReferenceEquals(provider, sentinel))
                "the routed label, blanks and duplicates leave nothing to fall back to"
        }

        testCaseAsync "no persistent config scope → unchanged"
        <| async {
            let log = CallLog()
            let profileStore = InMemoryProviderProfile()

            let sentinel = stubProvider log upVendor (Ok(okResponse "served"))
            let inner = DefaultAIProviderFactory.singleProvider (descriptor upVendor) sentinel

            let factory =
                DefaultAIProviderFactory.withFailoverChain
                    (profileStore :> IProviderProfile)
                    (fun _ -> async { return () })
                    inner

            let anonCtx = AccessContext.unrestricted (AnonymousSession "anon-1")
            let! provider = resolveOrFail factory anonCtx

            Expect.isTrue
                (obj.ReferenceEquals(provider, sentinel))
                "an anonymous request has no profile, so it has no chain"
        }

        // ── Decisions the implementation had to take ───────────────

        testCaseAsync "the advance is sticky — a later send goes straight to the entry that took over"
        <| async {
            // What makes "a single turn tries at most the whole chain
            // once" true across an agent loop's tool-use iterations
            // rather than merely within one SendMessage.
            let factory, log, records =
                harness downVendor [ "secondary", upVendor ] [ "secondary" ]

            let! provider = resolveOrFail factory ctx
            let! _ = send provider
            let! second = send provider

            match second with
            | Ok response -> Expect.equal response.Content "served" "the second send served too"
            | Error e -> failtestf "expected the sticky secondary to serve, got %s" (AIProviderError.toMessage e)

            Expect.equal
                log.Calls
                [ downVendor; upVendor; upVendor ]
                "the dead primary was not re-probed on the second send"

            Expect.hasLength records 1 "and no second failover was recorded"
        }

        testCaseAsync "Capabilities reports the entry currently serving"
        <| async {
            // The property metering and the turn's `AILatencyRecord`
            // both read, and therefore what attributes a failed-over
            // turn's tokens and latency to the provider that served it.
            let factory, _, _ = harness downVendor [ "secondary", upVendor ] [ "secondary" ]

            let! provider = resolveOrFail factory ctx
            Expect.equal provider.Capabilities.ProviderName downVendor "before any call, the primary"

            let! _ = send provider
            Expect.equal provider.Capabilities.ProviderName upVendor "after the failover, the entry that took over"
        }

        testCaseAsync "an unresolvable chain entry is skipped and recorded, not treated as the end"
        <| async {
            // One stale label should not cost a deployment the entries
            // behind it — and a silent skip would leave an operator
            // with a chain that quietly does less than it says.
            let factory, log, records =
                harness downVendor [ "secondary", upVendor ] [ "ghost"; "secondary" ]

            let! provider = resolveOrFail factory ctx
            let! result = send provider

            match result with
            | Ok response -> Expect.equal response.Content "served" "the entry behind the stale label served"
            | Error e -> failtestf "expected the skip to continue the walk, got %s" (AIProviderError.toMessage e)

            Expect.equal log.Calls [ downVendor; upVendor ] "the ghost label produced no provider call"

            Expect.hasLength records 2 "both the skip and the successful advance are recorded"
            Expect.isFalse records[0].Resolved "the skip is recorded as unresolved"
            Expect.equal records[0].ToLabel "ghost" "and names the label that could not be resolved"
            Expect.equal records[0].ToProvider "" "with no provider to name"
            Expect.isTrue records[1].Resolved "the following entry resolved"
            Expect.equal records[1].ToLabel "secondary" "and is the one that served"
            Expect.equal records[1].ChainPosition 2 "second position in the declared chain"
        }

        testCaseAsync "TryResolveByLabel is forwarded unwrapped — a connection test can still fail"
        <| async {
            // A caller naming a specific entry has asked about THAT
            // entry. Answering from a different one would make the
            // settings-UI test-connection probe unable to fail.
            let factory, log, records =
                harness downVendor [ "secondary", upVendor ] [ "secondary" ]

            let! resolved = factory.TryResolveByLabel(ctx, "primary")

            match resolved with
            | Error e -> failtestf "expected the named entry to resolve, got %s" (ProviderResolutionError.toMessage e)
            | Ok provider ->
                let! result = send provider

                match result with
                | Ok _ -> failtest "the named entry is down; the probe must report that"
                | Error e -> Expect.equal e outageError "the probed entry's own failure surfaces"

            Expect.equal log.Calls [ downVendor ] "no chain entry was consulted"
            Expect.isEmpty records "and no failover was recorded"
        }

        testCaseAsync "the structured-output path honours the chain identically"
        <| async {
            let factory, log, _ = harness downVendor [ "secondary", upVendor ] [ "secondary" ]

            let! provider = resolveOrFail factory ctx
            let! result = provider.SendStructuredMessage([], [], None, "{}", RetryPolicy.defaults)

            match result with
            | Ok response -> Expect.equal response.Content "served" "the secondary served the structured call"
            | Error e -> failtestf "expected a re-route, got %s" (AIProviderError.toMessage e)

            Expect.equal log.Calls [ downVendor; upVendor ] "a structured call survives an outage too"
        }
    ]