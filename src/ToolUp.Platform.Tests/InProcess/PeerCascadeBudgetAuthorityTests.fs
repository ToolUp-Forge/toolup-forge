module ToolUp.Platform.Tests.InProcess.PeerCascadeBudgetAuthorityTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open Giraffe
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform

// ─── Phase 331 — receiver-side cascade-budget authority ──────────────
//
// The receiver used to rebuild `Peer` and `User` from the validated
// principal and copy the four cascade fields — `HopsRemaining`, `Route`,
// `RootRequestId`, `ParentRequestId` — verbatim out of the request body.
// The peer token carries none of them, so they were unauthenticated by
// construction, and `DefaultPlatformPeer.Handle`'s hop-limit and loop
// guards were evaluating numbers the caller had chosen: `HopsRemaining =
// Int32.MaxValue` puts the budget guard out of reach and `Route = []`
// puts the loop guard out of reach.
//
// Every probe below is paired, because "the forgery was refused" proves
// nothing on its own — it would pass equally against a receiver that had
// broken and started refusing everything:
//
//   * a **pre-331 control** that ADMITS the same forgery, driven through
//     the same `DefaultPlatformPeer` with the context the old host would
//     have handed it (`{ wire with Peer = principal.Caller; User =
//     principal.User }`). It dispatches, with `Int32.MaxValue` intact.
//     That is the defect, executed.
//   * a **legitimate control** showing the honest shape still completes,
//     and — for the GP 11 claim — that the derived context is *equal* to
//     what the pre-331 host produced for a `create` call and for a
//     `PeerCascade.deriveNext` continuation. Byte-for-byte is asserted as
//     record equality, not argued.
//
// The forged cases drive `JsonRpcPeerHost.routes` against a hand-built
// `HttpContext`, with a capturing contract registered on the peer, so
// what is asserted is the context that reached DISPATCH — the actual
// input to every guard and to the audit row — rather than a status code
// that would be the same for several different reasons.

// ─── Fixtures ────────────────────────────────────────────────────────

type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ToolUp.Platform.Secrets.ISecretStore with
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

/// Records every audit row the host emits, so the "the audit carries the
/// DERIVED correlation id" claim is read off the log rather than assumed.
type private CapturingAuditLog() =
    let events = ResizeArray<AuditEvent>()

    member _.PeerRows =
        lock events (fun () ->
            events
            |> Seq.choose (function
                | PeerCallCompleted p -> Some p
                | _ -> None)
            |> List.ofSeq)

    interface IAuditLog with
        member _.Record(_, audit) = async { lock events (fun () -> events.Add audit) }

        member _.GetAuditTrail(_, _, _) = async { return lock events (fun () -> List.ofSeq events) }

let private callerId: PeerIdentity = {
    PeerId = "buyer"
    DisplayName = "Buyer Deployment"
}

let private receiverId: PeerIdentity = {
    PeerId = "seller"
    DisplayName = "Seller Deployment"
}

let private brokerId: PeerIdentity = {
    PeerId = "broker"
    DisplayName = "Broker Deployment"
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private contractId = "ledger"

let private callerKey = "cascade-authority-buyer-signing-key-01234567"

let private seedSigningKey (store: ToolUp.Platform.Secrets.ISecretStore) (peerId: string) (key: string) =
    store.SetSecret("_platform", $"peers/{peerId}/signing-key", key)
    |> Async.RunSynchronously
    |> ignore

/// A receiver whose auth provider binds the audience to its own id, in
/// the same shape `PeerCompose` builds.
let private authFor (secrets: ToolUp.Platform.Secrets.ISecretStore) =
    JwtPeerAuthProvider(secrets, receiverId.PeerId) :> IPeerAuthProvider

/// The caller's own provider: same key material, no audience binding of
/// its own (it is issuing, not validating).
let private issuerFor (secrets: ToolUp.Platform.Secrets.ISecretStore) =
    JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

let private issue (provider: IPeerAuthProvider) (caller: PeerIdentity) = async {
    match! provider.IssuePeerToken(caller, receiverId, Anonymous) with
    | Ok token -> return token
    | Error e -> return failtestf "Expected a minted token, got %A" e
}

/// One receiver: the contract table, a capturing dispatch, the audit log,
/// and the DI set the handlers resolve from. `policy = None` is the
/// composition that never called `withCascadePolicy` — the host must then
/// fall back to `PeerCascadePolicy.defaults` rather than to no ceiling.
/// `localPeerId` is what `PeerServerApp.run` passes; `""` is the pre-331
/// parameterless shape.
let private receiver (auth: IPeerAuthProvider) (policy: PeerCascadePolicy option) (localPeerId: string) =
    let captured = ResizeArray<PeerCallContext>()
    let peer = DefaultPlatformPeer(localPeerId) :> IPlatformPeer

    peer.RegisterContract {
        ContractId = contractId
        Versions = [ v1 ]
        Dispatch =
            fun context _ _ -> async {
                lock captured (fun () -> captured.Add context)
                return Ok(JsonRpc.serialize "ok")
            }
    }

    let audit = CapturingAuditLog()
    let services = ServiceCollection()
    services.AddSingleton<IPeerAuthProvider>(auth) |> ignore
    services.AddSingleton<IPlatformPeer>(peer) |> ignore
    services.AddSingleton<IAuditLog>(audit :> IAuditLog) |> ignore

    match policy with
    | Some p -> services.AddSingleton<PeerCascadePolicy>(p) |> ignore
    | None -> ()

    captured, audit, peer, services.BuildServiceProvider() :> IServiceProvider

/// `POST /peer/v1/{contractId}` against a hand-built context, returning
/// the status and the response body.
let private post (services: IServiceProvider) (token: string) (envelopeId: string) (context: PeerCallContext) = task {
    let payload: PeerWirePayload = {
        Context = context
        Arguments = """["hello"]"""
    }

    let envelope = JsonRpc.request envelopeId "Measure" payload
    let bytes = Encoding.UTF8.GetBytes(JsonRpc.serialize envelope)

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services
    ctx.Request.Method <- "POST"
    ctx.Request.Path <- PathString $"/peer/v1/{contractId}"
    ctx.Request.Body <- new MemoryStream(bytes)
    ctx.Request.ContentLength <- Nullable<int64>(int64 bytes.Length)
    ctx.Request.Headers["Authorization"] <- StringValues $"Bearer {token}"

    use responseBody = new MemoryStream()
    ctx.Response.Body <- responseBody

    let! _ = JsonRpcPeerHost.routes earlyReturn ctx

    return ctx.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray())
}

let private structuredError (body: string) =
    let response = JsonRpc.deserialize<JsonRpcResponse> body

    match response.Error with
    | None -> failtestf "expected a JSON-RPC error response, got %s" body
    | Some err ->
        match err.Data with
        | Some data -> JsonRpc.deserialize<PeerError> data
        | None -> failtestf "expected the structured PeerError in `data`, got %s" body

/// The context the PRE-331 host would have handed `Handle`: identity from
/// the principal, the four cascade fields copied straight off the wire.
let private preAuthorityContext (caller: PeerIdentity) (user: UserContext) (wire: PeerCallContext) = {
    wire with
        Peer = caller
        User = user
}

/// A receiver's whole request/response cycle for one wire context.
let private drive
    (policy: PeerCascadePolicy option)
    (localPeerId: string)
    (envelopeId: string)
    (wire: PeerCallContext)
    =
    async {
        let secrets = InMemorySecretStore() :> ToolUp.Platform.Secrets.ISecretStore
        seedSigningKey secrets callerId.PeerId callerKey
        let auth = authFor secrets
        let! token = issue (issuerFor secrets) callerId
        let captured, audit, peer, services = receiver auth policy localPeerId
        let! status, body = post services token envelopeId wire |> Async.AwaitTask
        return status, body, List.ofSeq captured, audit.PeerRows, peer
    }

/// A well-behaved single-hop call, exactly as `JsonRpcPeerClient.create`
/// seeds it.
let private honestRoot (rootId: string) : PeerCallContext = {
    Peer = callerId
    User = Anonymous
    ContractVersion = v1
    Route = [ callerId.PeerId ]
    RootRequestId = rootId
    ParentRequestId = None
    HopsRemaining = 8
}

// ─── (1) The forged budget ───────────────────────────────────────────

/// A tight receiver: three hops, four route entries, short identifiers.
/// Every ceiling below the forged value, so each refusal names one cause.
let private tight: PeerCascadePolicy =
    PeerCascadePolicy.defaults
    |> PeerCascadePolicy.withMaxHopsRemaining 3
    |> PeerCascadePolicy.withMaxRouteLength 4
    |> PeerCascadePolicy.withMaxIdentifierLength 64
    |> PeerCascadePolicy.withLocalPeerId receiverId.PeerId

let budgetAuthorityTests =
    testList "Phase 331 — the receiver derives the cascade context rather than copying it" [

        testCaseAsync "a wire body claiming HopsRemaining = Int32.MaxValue with no route is clamped and re-rooted"
        <| async {
            // The whole attack in one body: an unbounded budget, an
            // erased route, a forged correlation id, a forged parent, and
            // an identity naming somebody else. Only `ContractVersion`
            // is left alone — and that one is measured against the
            // receiver's own supported set a moment later.
            let forged: PeerCallContext = {
                Peer = {
                    PeerId = "not-the-caller"
                    DisplayName = "Spoofed"
                }
                User = Anonymous
                ContractVersion = v1
                Route = []
                RootRequestId = ""
                ParentRequestId = Some "forged-parent"
                HopsRemaining = Int32.MaxValue
            }

            let! status, _, captured, rows, _ = drive (Some tight) receiverId.PeerId "envelope-331" forged

            Expect.equal status 200 "the call is not refused — a clamped budget is still a usable budget"

            let dispatched =
                match captured with
                | [ one ] -> one
                | other -> failtestf "expected exactly one dispatch, got %i" (List.length other)

            Expect.equal
                dispatched.HopsRemaining
                3
                "the budget the guards run on is the RECEIVER's ceiling, not the caller's Int32.MaxValue"

            Expect.equal
                dispatched.Route
                [ callerId.PeerId ]
                "an erased route gains back the one hop the receiver can prove — its own validated caller"

            Expect.equal
                dispatched.Peer
                callerId
                "identity still comes from the validated principal (pre-331 behaviour)"

            Expect.isTrue
                (Guid.TryParse(dispatched.RootRequestId) |> fst)
                "an absent correlation id is minted by the receiver, not left blank for the caller to fill"

            Expect.equal
                dispatched.ParentRequestId
                None
                "the forged parent is discarded outright: the derived route names only the caller, so this IS the originating hop and it has no parent"

            match rows with
            | [ row ] ->
                Expect.equal
                    row.RootRequestId
                    dispatched.RootRequestId
                    "the audit row is filed under the DERIVED correlation id"

                Expect.equal row.CallerPeerId callerId.PeerId "…attributed to the validated caller"
            | other -> failtestf "expected exactly one audit row, got %i" (List.length other)
        }

        testCaseAsync "PRE-331 CONTROL — the identical forgery is ADMITTED when the wire values are trusted"
        <| async {
            // Without this the case above would pass just as happily
            // against a receiver that clamped nothing and simply had a
            // small budget of its own. Here the pre-331 context — the
            // one the old host built — goes through the SAME
            // `DefaultPlatformPeer`, and the forgery survives intact:
            // `Int32.MaxValue` reaches dispatch and the empty route
            // defeats the loop guard. That is the defect, executed.
            let forged: PeerCallContext = {
                Peer = {
                    PeerId = "not-the-caller"
                    DisplayName = "Spoofed"
                }
                User = Anonymous
                ContractVersion = v1
                Route = []
                RootRequestId = "forged-root"
                ParentRequestId = Some "forged-parent"
                HopsRemaining = Int32.MaxValue
            }

            let captured = ResizeArray<PeerCallContext>()
            let peer = DefaultPlatformPeer(receiverId.PeerId) :> IPlatformPeer

            peer.RegisterContract {
                ContractId = contractId
                Versions = [ v1 ]
                Dispatch =
                    fun context _ _ -> async {
                        captured.Add context
                        return Ok(JsonRpc.serialize "ok")
                    }
            }

            let! result = peer.Handle(contractId, preAuthorityContext callerId Anonymous forged, "Measure", "[]")

            Expect.isTrue (Result.isOk result) "the pre-331 context dispatches — nothing about it is refused"

            match List.ofSeq captured with
            | [ one ] ->
                Expect.equal
                    one.HopsRemaining
                    Int32.MaxValue
                    "the forged budget reached the handler untouched — the hop guard could never fire"

                Expect.equal one.Route [] "…and the erased route left the loop guard with nothing to detect"
                Expect.equal one.RootRequestId "forged-root" "…and the audit correlation id was the caller's to choose"
            | other -> failtestf "expected exactly one dispatch, got %i" (List.length other)
        }

        testCaseAsync "a route naming someone other than the caller keeps its history AND gains the caller"
        <| async {
            // Truncating history is the other half of the route attack:
            // the caller cannot be erased, but nor is the upstream
            // discarded — a receiver that replaced the route outright
            // would break cascade diagnosis for every honest caller.
            let wire = {
                honestRoot "root-331" with
                    Route = [ brokerId.PeerId ]
                    ParentRequestId = Some "forged-parent"
            }

            let! _, _, captured, _, _ = drive (Some tight) receiverId.PeerId "root-331" wire

            match captured with
            | [ one ] ->
                Expect.equal
                    one.Route
                    [ brokerId.PeerId; callerId.PeerId ]
                    "the asserted upstream is kept, the validated caller is appended last"

                Expect.notEqual
                    one.ParentRequestId
                    (Some "forged-parent")
                    "the body's own ParentRequestId is never carried — this is the hop where a parent DOES exist, and it is still not the caller's to name"

                Expect.equal
                    one.ParentRequestId
                    (Some "root-331")
                    "a route with an upstream hop has a parent — the inbound request"
            | other -> failtestf "expected exactly one dispatch, got %i" (List.length other)
        }

        testCaseAsync "a correlation id the receiver will not carry is replaced, and never reaches the audit log"
        <| async {
            // Length and control characters, not because a peer id is
            // likely to contain either, but because this string is
            // written into an audit row and read back by whatever reads
            // audit rows. Escaped, never a raw control byte in the source.
            let wire = {
                honestRoot "root-\u0000-injected" with
                    HopsRemaining = 2
            }

            let! status, _, captured, rows, _ = drive (Some tight) receiverId.PeerId "envelope-331" wire

            Expect.equal status 200 "the call still completes — a bad id is replaced, not a reason to refuse a peer"

            match captured, rows with
            | [ one ], [ row ] ->
                Expect.notEqual one.RootRequestId "root-\u0000-injected" "the malformed id did not survive derivation"

                Expect.isTrue
                    (Guid.TryParse(one.RootRequestId) |> fst)
                    "…it was replaced by a receiver-minted one, the same as an absent id"

                Expect.isFalse (row.RootRequestId.Contains "\u0000") "…and nothing control-shaped reached the audit row"
            | c, r -> failtestf "expected one dispatch and one audit row, got %i / %i" (List.length c) (List.length r)
        }

        testCaseAsync "CONTROL — a well-shaped correlation id is PRESERVED, so a cascade stays one cascade"
        <| async {
            // The counterweight to the case above: if the receiver minted
            // a fresh id every time, the two would look identical from
            // the outside and cross-hop audit correlation (GP 7) would be
            // silently gone.
            let! _, _, captured, rows, _ =
                drive (Some tight) receiverId.PeerId "cascade-root-42" (honestRoot "cascade-root-42")

            match captured, rows with
            | [ one ], [ row ] ->
                Expect.equal
                    one.RootRequestId
                    "cascade-root-42"
                    "the caller's usable correlation id is carried, not replaced"

                Expect.equal row.RootRequestId "cascade-root-42" "…and the audit row joins the same cascade"
            | c, r -> failtestf "expected one dispatch and one audit row, got %i / %i" (List.length c) (List.length r)
        }
    ]

// ─── (2) The shape bounds + the receiver-on-route loop ───────────────

let cascadeShapeTests =
    testList "Phase 331 — the receiver bounds the shape it is willing to carry" [

        testCaseAsync "a route already naming the receiver is refused as a loop, before dispatch"
        <| async {
            let wire = {
                honestRoot "root-331" with
                    Route = [ receiverId.PeerId; callerId.PeerId ]
            }

            let! status, body, captured, _, _ = drive (Some tight) receiverId.PeerId "root-331" wire

            Expect.equal status 200 "a guard rejection rides the JSON-RPC envelope, as it always has"

            match structuredError body with
            | PeerLoopDetected route ->
                Expect.contains route receiverId.PeerId "the refusal names the route that doubled back through here"
            | other -> failtestf "expected PeerLoopDetected, got %A" other

            Expect.isEmpty captured "…and nothing was dispatched"
        }

        testCaseAsync "CONTROL — the same route without the receiver dispatches, and so does it on a pre-331 peer"
        <| async {
            // Two controls in one, because the assertion above has two
            // ways to be vacuous. (a) The receiver is not refusing every
            // multi-hop route — swap its id out and the call completes.
            // (b) The refusal is the NEW arm of the loop guard, not the
            // old duplicate-detection: replay the identical route through
            // a peer built the pre-331 way (no declared identity) and it
            // dispatches.
            let benign = {
                honestRoot "root-331" with
                    Route = [ brokerId.PeerId; callerId.PeerId ]
            }

            let! status, _, captured, _, _ = drive (Some tight) receiverId.PeerId "root-331" benign
            Expect.equal status 200 "a two-hop route not naming the receiver is ordinary traffic"
            Expect.equal (List.length captured) 1 "…and it dispatches"

            let looping = {
                honestRoot "root-331" with
                    Route = [ receiverId.PeerId; callerId.PeerId ]
            }

            let! _, _, preCaptured, _, _ = drive (Some tight) "" "root-331" looping

            Expect.equal
                (List.length preCaptured)
                1
                "the SAME route dispatches on a peer with no declared identity — the refusal above is the new guard, not a broken host"
        }

        testCaseAsync "a route deeper than the receiver's ceiling is over budget, however the budget field reads"
        <| async {
            let deep = {
                honestRoot "root-331" with
                    Route = [ "a"; "b"; "c"; "d"; callerId.PeerId ]
                    HopsRemaining = 3
            }

            let! _, body, captured, _, _ = drive (Some tight) receiverId.PeerId "root-331" deep

            Expect.equal (structuredError body) PeerHopLimitExceeded "five entries against a ceiling of four is refused"
            Expect.isEmpty captured "…before dispatch"

            // CONTROL — exactly at the ceiling still passes, so the
            // refusal is the bound and not an off-by-anything.
            let atCeiling = {
                honestRoot "root-331" with
                    Route = [ "a"; "b"; "c"; callerId.PeerId ]
                    HopsRemaining = 3
            }

            let! _, _, ok, _, _ = drive (Some tight) receiverId.PeerId "root-331" atCeiling
            Expect.equal (List.length ok) 1 "a route exactly at the ceiling is carried"
        }

        testCaseAsync "a route entry the receiver cannot safely carry forward is refused outright"
        <| async {
            let hostile = {
                honestRoot "root-331" with
                    Route = [ "broker\u0000injected"; callerId.PeerId ]
            }

            let! _, body, captured, _, _ = drive (Some tight) receiverId.PeerId "root-331" hostile

            match structuredError body with
            | PeerUnauthorized reason ->
                Expect.stringContains reason "Route" "the refusal says which self-asserted field was rejected"
            | other -> failtestf "expected PeerUnauthorized, got %A" other

            Expect.isEmpty captured "…and nothing was dispatched"

            // CONTROL — the same route, one control character lighter.
            let benign = {
                honestRoot "root-331" with
                    Route = [ "brokerinjected"; callerId.PeerId ]
            }

            let! _, _, ok, _, _ = drive (Some tight) receiverId.PeerId "root-331" benign
            Expect.equal (List.length ok) 1 "the entry is refused for its shape, not for existing"
        }

        testCaseAsync "the DEFAULT ceilings are in force with no PeerCascadePolicy registered"
        <| async {
            // GP 11's other half: the tunable defaults to something, and
            // that something is enforced. A partial host registering
            // nothing still clamps.
            let forged = {
                honestRoot "root-331" with
                    HopsRemaining = Int32.MaxValue
            }

            let! _, _, captured, _, _ = drive None "" "root-331" forged

            match captured with
            | [ one ] ->
                Expect.equal
                    one.HopsRemaining
                    PeerCascadePolicy.defaultMaxHopsRemaining
                    "a composition that never called withCascadePolicy still has a ceiling, and it is the documented one"
            | other -> failtestf "expected exactly one dispatch, got %i" (List.length other)
        }
    ]

// ─── (3) GP 11 — the legitimate shapes are unchanged ─────────────────

/// The proxy config a forwarding deployment holds. Only the fields
/// `PeerCascade.deriveNext` reads matter here.
let private brokerTarget: TargetPeer = {
    Peer = receiverId
    BaseUrl = "http://localhost"
}

/// Captures the payload a gateway forwards, so Phase 595's cascade
/// bookkeeping can be asserted against a derived inbound context.
type private CapturingPeerClient() =
    let sent = ResizeArray<TargetPeer * string * PeerWirePayload>()

    member _.Sent = List.ofSeq sent

    interface IPeerClient with
        member _.Invoke(target, contractId, _, payload) = async {
            sent.Add((target, contractId, payload))
            return Ok(JsonRpc.serialize "ok")
        }

        member _.PollJob(_, _, _) = async { return Ok PeerJobStatus.Pending }

let cascadeCompatibilityTests =
    testList "Phase 331 — the legitimate cascade shapes are byte-for-byte unchanged (GP 11)" [

        testCase "a single-hop call derives to EXACTLY the context the pre-331 host produced"
        <| fun _ ->
            // The GP 11 claim, asserted as record equality rather than
            // field-by-field agreement, so a future field added to
            // `PeerCallContext` cannot quietly escape it.
            let wire = honestRoot "root-331"

            let derived =
                match
                    PeerCascadeAuthority.derive
                        (PeerCascadePolicy.defaults
                         |> PeerCascadePolicy.withLocalPeerId receiverId.PeerId)
                        callerId
                        Anonymous
                        wire.RootRequestId
                        wire
                with
                | Ok c -> c
                | Error e -> failtestf "a legitimate single-hop call was refused: %A" e

            Expect.equal
                derived
                (preAuthorityContext callerId Anonymous wire)
                "nothing about an honest `create` call changes — same route, same budget, same ids, same absent parent"

        testCase "Phase 314 — a `forward`-derived continuation survives the receiver's derivation intact"
        <| fun _ ->
            // `JsonRpcPeerClient.forward` seeds each outbound call from
            // `PeerCascade.deriveNext`. Phase 331 sits on the other end of
            // that wire, so this is the compatibility question that
            // matters most: what B sends must be what C acts on.
            let inbound = honestRoot "root-331"

            let outbound =
                match PeerCascade.deriveNext inbound brokerId brokerTarget with
                | Ok c -> c
                | Error e -> failtestf "deriveNext refused a legitimate hop: %A" e

            Expect.equal
                outbound.Route
                [ callerId.PeerId; brokerId.PeerId ]
                "sanity — the forwarding peer appended itself"

            Expect.equal outbound.HopsRemaining 7 "sanity — and spent one hop of the budget"

            let derived =
                match
                    PeerCascadeAuthority.derive
                        (PeerCascadePolicy.defaults
                         |> PeerCascadePolicy.withLocalPeerId receiverId.PeerId)
                        brokerId
                        Anonymous
                        outbound.RootRequestId
                        outbound
                with
                | Ok c -> c
                | Error e -> failtestf "the receiver refused a legitimate forwarded hop: %A" e

            Expect.equal
                derived
                outbound
                "the receiver derives back exactly what the forwarding peer sent — 314's bookkeeping is not re-done, re-clamped, or re-rooted"

        testCase "Phase 595 — a gateway forwards from the DERIVED context, cascade bookkeeping intact"
        <| fun _ ->
            // `PeerGateway.forwardingContract` cascades through the same
            // `deriveNext`. Feeding it the context Phase 331 produces
            // shows the gateway hop is still a first-class cascade hop.
            let group: PeerIdentity = {
                PeerId = "consortium"
                DisplayName = "Consortium Gateway"
            }

            let route: AggregateRoute = {
                ContractId = contractId
                Versions = [ v1 ]
                Owner = brokerTarget
            }

            let client = CapturingPeerClient()
            let host = PeerGateway.forwardingContract (client :> IPeerClient) group route

            let derived =
                match
                    PeerCascadeAuthority.derive
                        (PeerCascadePolicy.defaults |> PeerCascadePolicy.withLocalPeerId group.PeerId)
                        callerId
                        Anonymous
                        "root-331"
                        (honestRoot "root-331")
                with
                | Ok c -> c
                | Error e -> failtestf "the gateway refused a legitimate inbound call: %A" e

            let result =
                host.Registration.Dispatch derived "Measure" """["hello"]"""
                |> Async.RunSynchronously

            Expect.isTrue (Result.isOk result) "the gateway forwarded the call"

            match client.Sent with
            | [ (_, _, payload) ] ->
                Expect.equal
                    payload.Context.Route
                    [ callerId.PeerId; group.PeerId ]
                    "the group appended itself to the DERIVED route, not to a wire-supplied one"

                Expect.equal payload.Context.HopsRemaining 7 "…and spent a hop of the derived budget"

                Expect.equal
                    payload.Context.RootRequestId
                    "root-331"
                    "…while the cascade correlation id rode through both derivations"
            | other -> failtestf "expected exactly one forwarded call, got %i" (List.length other)

        testCase "the policy is a value a composition sets, and defaults when it does not"
        <| fun _ ->
            let fresh = PeerCompose.PeerServerApp.create ()

            Expect.equal
                fresh.CascadePolicy
                PeerCascadePolicy.defaults
                "a fresh PeerServerApp carries the defaults — no opt-in required for the derivation to bind"

            Expect.equal
                PeerCascadePolicy.defaults.LocalPeerId
                ""
                "…with the receiver identity blank, because `run` fills it from the composed LocalPeer"

            Expect.equal
                PeerCascadePolicy.defaultMaxHopsRemaining
                32
                "the documented hop ceiling, far above the HopBudget guidance"

            let tightened =
                fresh
                |> PeerCompose.PeerServerApp.withCascadePolicy (
                    PeerCascadePolicy.defaults |> PeerCascadePolicy.withMaxHopsRemaining 4
                )

            Expect.equal
                tightened.CascadePolicy.MaxHopsRemaining
                4
                "…and a deployment that wants a tighter federation boundary sets it in one line"
    ]