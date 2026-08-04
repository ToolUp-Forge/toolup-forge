module ToolUp.Platform.Tests.InProcess.CrossRuntimeFederationConformanceTests

// ─── Phase 189 — the generated non-F# clients certify against the seam ─
//
// The harness is in `CrossRuntimeConformanceHarness`; this file is what it
// is held to. Five things are asserted, and the last two are the ones a
// conformance suite usually lacks:
//
//   1. **Emission.** Every request a generated client puts on the wire
//      round-trips through the receiver's own decoder to identical bytes,
//      and declares the same members, in the same order, as the corpus's
//      request vector at every level both documents share. Divergence #1
//      in the specification's JCS table — member order — is the one most
//      likely to bite a third party, so it is checked directly rather
//      than inferred from a successful call.
//   2. **Consumption.** The responses the client is fed are corpus bytes,
//      served verbatim, so what it reports having read is measured
//      against the specification and not against this codebase's habits.
//      That includes the three terminal poll states and the document a
//      reader must refuse.
//   3. **Live round-trip.** The first three legs are dispatched by a real
//      `IPlatformPeer.Handle` over a real `JsonRpcPeerHost.contract`
//      binding, behind a real `JwtPeerAuthProvider` validating a token
//      the same provider minted — the signing-helper path, crossed by a
//      foreign runtime.
//   4. **Cross-runtime parity.** Node's and Python's request documents,
//      normalised only on the two per-call identifiers a client is
//      *supposed* to vary, are byte-identical to each other and to the
//      envelope the .NET emitter produces for the same call. Three
//      runtimes, one document.
//   5. **Teeth, and honest skipping.** A deliberately corrupted generator
//      output is run through the whole pipeline and must be REJECTED; a
//      runtime that is absent is reported as skipped with the probe's own
//      reason, never quietly passed. Every declared runtime is accounted
//      for as one or the other.

open System
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.InProcess.FederationWireCorpus
open ToolUp.Platform.Tests.InProcess.CrossRuntimeConformanceHarness

// ─── Runtime discovery, once ─────────────────────────────────────────
//
// Probing spawns processes, so it happens once at module load and every
// case reads the result. A runtime is either usable or it is not; nothing
// about that answer changes between cases.

let private nodeAvailability = lazy (probeNode ())
let private pythonAvailability = lazy (probePython ())

/// Every runtime this harness claims to cover. The accounting case below
/// asserts each one is either executed or explicitly skipped.
let private declaredRuntimes = [ "node", nodeAvailability; "python", pythonAvailability ]

let private runs =
    System.Collections.Concurrent.ConcurrentDictionary<string, Result<RuntimeRun, string>>()

/// Execute a runtime's leg ONCE and memoise the outcome. Every case below
/// reads the same run: spawning a process, generating a client and
/// serving nine requests per assertion would turn a fast pack into a slow
/// one, and — worse — would make the cases measure different executions
/// of the same thing.
let private runOnce (name: string) : Result<RuntimeRun, string> option =
    let availability =
        declaredRuntimes |> List.find (fun (n, _) -> n = name) |> snd |> _.Value

    match availability with
    | Unavailable _ -> None
    | Available launch ->
        Some(
            runs.GetOrAdd(
                name,
                fun _ ->
                    match name with
                    | "node" -> runNode launch asEmitted
                    | _ -> runPython launch asEmitted
            )
        )

/// Run the body against a runtime, or record a skip carrying the probe's
/// reason. A skipped leg is visible in the test output; it never reads as
/// a pass of the thing it did not do.
let private withRuntime (name: string) (body: RuntimeRun -> unit) =
    match runOnce name with
    | None ->
        let reason =
            match declaredRuntimes |> List.find (fun (n, _) -> n = name) |> snd |> _.Value with
            | Unavailable reason -> reason
            | Available _ -> "available"

        skiptestf "the %s runtime leg is unavailable on this machine and was skipped: %s" name reason
    | Some(Error diagnostic) -> failtest diagnostic
    | Some(Ok run) -> body run

// ─── Document shape helpers ──────────────────────────────────────────

/// A JSON string whose content is itself a document (specification §3.1
/// rule 12 — embedded documents ride as strings). Recognised by its first
/// character so `"2.0"`, which parses perfectly well as a number, is not
/// mistaken for one.
let private embedded (value: string) =
    let trimmed = value.TrimStart()
    trimmed.StartsWith "{" || trimmed.StartsWith "["

/// Every object in a document, keyed by its path, with its members in the
/// order they were emitted. Descends through embedded documents, because
/// the call context rides inside one and its member order is exactly what
/// a third-party implementation gets wrong.
let private memberOrders (document: string) : Map<string, string list> =
    let collected = System.Collections.Generic.Dictionary<string, string list>()

    let rec walk (path: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            let names = element.EnumerateObject() |> Seq.map _.Name |> List.ofSeq
            collected[path] <- names

            for property in element.EnumerateObject() do
                walk $"{path}.{property.Name}" property.Value
        | JsonValueKind.Array ->
            element.EnumerateArray()
            |> Seq.iteri (fun index item -> walk $"{path}[{index}]" item)
        | JsonValueKind.String ->
            let value = element.GetString()

            if embedded value then
                use inner = JsonDocument.Parse value
                walk path inner.RootElement
        | _ -> ()

    use document = JsonDocument.Parse document
    walk "$" document.RootElement
    Map.ofSeq (collected |> Seq.map (fun entry -> entry.Key, entry.Value))

/// The two fields a client is *supposed* to vary per call. Everything
/// else in the envelope is fixed by the contract and the caller's
/// identity, so substituting exactly these two is the whole of the
/// normalisation — anything more would be normalising away the
/// divergences this comparison exists to find.
let private normalise (body: string) : string =
    let envelope = JsonRpc.deserialize<JsonRpcRequest> body
    let payload = JsonRpc.deserialize<PeerWirePayload> envelope.Params

    let context = {
        payload.Context with
            RootRequestId = "00000000-0000-0000-0000-000000000000"
    }

    JsonRpc.serialize {
        envelope with
            Id = "00000000-0000-0000-0000-000000000000"
            Params = JsonRpc.serialize { payload with Context = context }
    }

/// The envelope the .NET emitter produces for the same call the drivers
/// make, with the same normalised identifiers. `Arguments` is taken from
/// the corpus's own request vector rather than re-authored, so the third
/// runtime in the parity check is anchored to the corpus too.
let private dotnetReferenceEnvelope () : string =
    let corpusRequest =
        readCommitted "contract-invocation/request.json"
        |> JsonRpc.deserialize<JsonRpcRequest>

    let corpusArguments =
        (JsonRpc.deserialize<PeerWirePayload> corpusRequest.Params).Arguments

    let context = {
        Peer = {
            PeerId = callerPeer.PeerId
            DisplayName = callerPeer.PeerId
        }
        User = Anonymous
        ContractVersion = { Major = 1; Minor = 0 }
        Route = [ callerPeer.PeerId ]
        RootRequestId = "00000000-0000-0000-0000-000000000000"
        ParentRequestId = None
        HopsRemaining = 8
    }

    JsonRpc.serialize {
        JsonRpc = JsonRpc.version
        Method = "PlaceOrder"
        Params =
            JsonRpc.serialize {
                Context = context
                Arguments = corpusArguments
            }
        Id = "00000000-0000-0000-0000-000000000000"
    }

let private postBodies (run: RuntimeRun) =
    run.Requests |> List.filter (fun request -> request.Verb = "POST")

/// The leg the driver reported under this name.
let private legNamed (run: RuntimeRun) (name: string) =
    match run.Legs |> List.tryFind (fun leg -> leg.Name = name) with
    | Some leg -> leg
    | None -> failtestf "the %s driver reported no leg named '%s'" run.Runtime name

let private legValueText (leg: ReportedLeg) =
    match leg.Value with
    | Some value -> value.GetRawText()
    | None -> failtestf "leg '%s' reported no value" leg.Name

/// One member of a leg's reported value, which Phase 631 made a tagged
/// object rather than a bare result-or-null. Reading it by name is what
/// lets the two runtimes report the shape each language is idiomatic in —
/// a TypeScript discriminated union carries only the members its case
/// declares, a Python dataclass carries all four with `None` defaults —
/// without the assertions having to know which produced the report.
let private legMember (leg: ReportedLeg) (name: string) : JsonElement =
    match leg.Value with
    | Some value when value.ValueKind = JsonValueKind.Object ->
        match value.TryGetProperty name with
        | true, found -> found
        | _ -> failtestf "leg '%s' reported no '%s' member: %s" leg.Name name (value.GetRawText())
    | Some value ->
        failtestf
            "leg '%s' reported a %A rather than the tagged poll object: %s"
            leg.Name
            value.ValueKind
            (value.GetRawText())
    | None -> failtestf "leg '%s' reported no value" leg.Name

/// The state the corpus's third poll vector pins, with the outcome string
/// the receiver would record for it. `errorCaseName` is the receiver's own
/// function — the one `PeerJobCompletedPayload.Outcome` is built from — so
/// the expectation is anchored to what the receiver says, not to a
/// literal re-typed here.
let private corpusFailure () =
    match (pollStates ())[2] with
    | PeerJobStatus.Failed err ->
        let message =
            match err with
            | PeerHandler message -> message
            | other -> failtestf "the corpus's failed poll state must carry a PeerHandler error; it carries %A" other

        JsonRpc.errorCaseName err, message
    | other -> failtestf "the corpus's third poll state must be Failed; it is %A" other

// ─── Per-runtime certification ───────────────────────────────────────

let private emissionTests (runtime: string) =
    testList $"{runtime} — what the generated client emits" [
        test "every request round-trips through the receiver's own decoder to identical bytes" {
            withRuntime runtime (fun run ->
                let bodies = postBodies run

                Expect.equal
                    bodies.Length
                    5
                    "the driver makes five POSTs; a different count means the driver and the receiver's script have drifted apart"

                for request in bodies do
                    let envelope = JsonRpc.deserialize<JsonRpcRequest> request.Body

                    Expect.equal
                        (JsonRpc.serialize envelope)
                        request.Body
                        $"the {runtime} client's request #{request.Ordinal} must re-serialise to the bytes it sent. A difference here is a canonical-encoding defect in the generated client — insignificant whitespace, a member out of declaration order, or an escaping divergence (specification §3.1)")
        }

        test "the request declares the corpus vector's members, in the corpus vector's order" {
            withRuntime runtime (fun run ->
                let corpus = memberOrders (readCommitted "contract-invocation/request.json")
                let emitted = memberOrders ((postBodies run |> List.head).Body)

                let shared =
                    corpus
                    |> Map.toList
                    |> List.filter (fun (path, _) -> Map.containsKey path emitted)

                // The one path the two documents legitimately disagree on
                // is `User`: the corpus carries a `Direct` assertion and a
                // generated client carries `Anonymous`, which rides as a
                // bare string (§3.1 rule 11) and so contributes no object.
                // It drops out of `shared` on its own; the floor below is
                // what stops the comparison quietly degenerating to that
                // one difference and nothing else.
                Expect.isGreaterThan
                    shared.Length
                    4
                    "fewer than five shared objects means the two documents barely overlap, and this comparison is measuring nothing"

                for path, expected in shared do
                    Expect.equal
                        (Map.find path emitted)
                        expected
                        $"the {runtime} client's request must declare the members of '{path}' in the specification's declaration order (JCS divergence #1); a lexicographic sort produces a different, non-conformant document")
        }

        test "the receiver reads the context the driver was told to send" {
            withRuntime runtime (fun run ->
                let envelope =
                    JsonRpc.deserialize<JsonRpcRequest> (postBodies run |> List.head).Body

                let payload = JsonRpc.deserialize<PeerWirePayload> envelope.Params

                Expect.equal envelope.JsonRpc "2.0" "the envelope declares JSON-RPC 2.0"
                Expect.equal envelope.Method "PlaceOrder" "the method name rides in the envelope, not the route"
                Expect.equal payload.Context.Peer.PeerId callerPeer.PeerId "the calling peer is asserted"
                Expect.equal payload.Context.User Anonymous "a deployment-to-deployment call carries no end user"
                Expect.equal payload.Context.Route [ callerPeer.PeerId ] "the originating hop is on the route"
                Expect.equal payload.Context.ParentRequestId None "the originating hop has no parent"

                Expect.equal
                    payload.Arguments
                    "[\"order-42\",{\"Quantity\":3}]"
                    "the positional arguments ride as an embedded canonical document (§3.1 rule 12)")
        }

        test "every request carried the minted bearer token" {
            withRuntime runtime (fun run ->
                Expect.equal run.Requests.Length 9 "the driver runs nine legs"

                for request in run.Requests do
                    let header =
                        Expect.wantSome
                            request.Authorization
                            $"request #{request.Ordinal} reached a fail-closed receiver with no Authorization header"

                    Expect.stringStarts header "Bearer " "the peer token rides as a bearer credential")
        }
    ]

let private consumptionTests (runtime: string) =
    testList $"{runtime} — what the generated client reads" [
        test "the live receiver's dispatch is reported as its result" {
            withRuntime runtime (fun run ->
                let leg = legNamed run "immediate"
                Expect.isTrue leg.Ok $"the immediate call must succeed; it reported: {leg.Error}"

                Expect.equal
                    (JsonRpc.deserialize<OrderAck> (legValueText leg))
                    {
                        OrderId = "order-42"
                        Accepted = true
                    }
                    "the result the live IPlatformPeer.Handle produced must arrive intact")
        }

        test "a structured PeerError on a 200 is raised, not returned as a value" {
            withRuntime runtime (fun run ->
                let leg = legNamed run "handlerError"
                Expect.isFalse leg.Ok "a failing handler must surface as an error, never as a result"

                let message =
                    Expect.wantSome leg.Error "the raised failure must carry the receiver's diagnostic"

                Expect.stringContains
                    message
                    (string JsonRpc.handlerError)
                    "the raised failure must name the JSON-RPC code, so a caller can branch on it"

                Expect.stringContains message "ledger snapshot expired" "the receiver's message must reach the caller")
        }

        test "the corpus's success response is read as the corpus says" {
            withRuntime runtime (fun run ->
                let leg = legNamed run "corpusResult"
                Expect.isTrue leg.Ok $"the corpus response vector must parse; it reported: {leg.Error}"

                let corpusResult =
                    readCommitted "contract-invocation/response.json"
                    |> JsonRpc.deserialize<JsonRpcResponse>
                    |> _.Result
                    |> Option.get

                Expect.equal
                    (JsonRpc.deserialize<OrderAck> (legValueText leg))
                    (JsonRpc.deserialize<OrderAck> corpusResult)
                    "the value the client reported must be the one the corpus's embedded result document carries")
        }

        test "the corpus's failure response is raised with its specified code and message" {
            withRuntime runtime (fun run ->
                let leg = legNamed run "corpusError"
                Expect.isFalse leg.Ok "a failure response must be raised"

                let corpusFailure =
                    readCommitted "contract-invocation/errors.json"
                    |> JsonRpc.deserialize<JsonRpcResponse list>
                    |> List.head
                    |> _.Error
                    |> Option.get

                let message = Expect.wantSome leg.Error "the raised failure must carry a diagnostic"

                Expect.stringContains
                    message
                    (string corpusFailure.Code)
                    "the code the corpus pins must reach the caller"

                Expect.stringContains message corpusFailure.Message "the message the corpus pins must reach the caller")
        }

        test "a document that is not a well-formed envelope is refused" {
            withRuntime runtime (fun run ->
                let leg = legNamed run "corpusMalformed"

                Expect.isFalse
                    leg.Ok
                    "the corpus's unparseable document must be refused. A client that returns a value here has done a partial read of a response it cannot understand — the exact failure the reject vector exists to forbid")
        }

        test "the three terminal poll states are read as the corpus pins them" {
            withRuntime runtime (fun run ->
                let states = pollStates ()

                let completedPayload =
                    match states[1] with
                    | PeerJobStatus.Completed payload -> payload
                    | other -> failtestf "the corpus's second poll state must be Completed; it is %A" other

                let pending = legNamed run "pollPending"
                Expect.isTrue pending.Ok $"polling a pending job must not raise; it reported: {pending.Error}"

                Expect.equal
                    ((legMember pending "state").GetString())
                    "pending"
                    "a Pending state rides as the bare case-name string (§3.1 rule 11) and must read as the non-terminal 'ask again' case"

                let completed = legNamed run "pollCompleted"
                Expect.isTrue completed.Ok $"polling a completed job must not raise; it reported: {completed.Error}"

                Expect.equal
                    ((legMember completed "state").GetString())
                    "succeeded"
                    "a Completed state must read as the terminal success case"

                // Phase 631 — the embedded result document is DECODED, not
                // handed back as the string it rides in (§3.1 rule 12).
                // The old helper's return type promised the value and gave
                // back the string; parsing is what makes the type true.
                Expect.equal
                    (JsonRpc.deserialize<LedgerReconciliation> ((legMember completed "result").GetRawText()))
                    (JsonRpc.deserialize<LedgerReconciliation> completedPayload)
                    "a Completed state must yield the decoded result document the corpus carries")
        }

        // ─── Phase 631 — the terminal-failure leg ─────────────────────
        //
        // Phase 189 found and recorded this: both generated poll helpers
        // reported a failed job as "no result yet", so a caller following
        // the generated idiom — poll until a result appears — polled a
        // job that had already terminally failed, forever. The receiver
        // was answering correctly; the client threw the answer away
        // because the emitted return type had room for two of the three
        // states the specification defines.
        test "a terminally-failed job is reported as failed, carrying the receiver's outcome" {
            withRuntime runtime (fun run ->
                let expectedOutcome, expectedMessage = corpusFailure ()
                let failed = legNamed run "pollFailed"

                Expect.isTrue failed.Ok $"polling a failed job must not raise; it reported: {failed.Error}"

                let state = (legMember failed "state").GetString()

                Expect.equal
                    state
                    "failed"
                    "a Failed state must be reported as failed. Reporting it as 'pending' — which is what an optional-result return type forces — makes a terminally-dead job indistinguishable from a running one, and a caller polling until a result appears never stops"

                Expect.notEqual
                    state
                    "pending"
                    "a terminal state must never read as the non-terminal one; that is the whole of the poll-forever defect"

                Expect.equal
                    ((legMember failed "outcome").GetString())
                    expectedOutcome
                    "the failure must carry the receiver's outcome string — the failing error's case name, the same value the receiver records against the job — so a caller can log WHY rather than only THAT it failed"

                Expect.stringContains
                    ((legMember failed "detail").GetRawText())
                    expectedMessage
                    "the failing error's payload must reach the caller alongside its class")
        }

        test "the capability handshake answers with the receiver's registered contract" {
            withRuntime runtime (fun run ->
                let leg = legNamed run "capabilities"
                Expect.isTrue leg.Ok $"the handshake must succeed; it reported: {leg.Error}"

                let capabilities = JsonRpc.deserialize<CapabilityList> (legValueText leg)

                Expect.equal
                    capabilities
                    [
                        {
                            ContractId = contractId
                            Versions = contractVersions
                        }
                    ]
                    "the generated client must read back exactly the contract the live receiver hosts")
        }

        test "the driver ran every scripted leg, in the scripted order" {
            withRuntime runtime (fun run ->
                Expect.sequenceEqual
                    (run.Legs |> List.map _.Name)
                    (legScript |> List.map fst)
                    "the driver's legs and the receiver's script are two halves of one contract; a harness that silently ran fewer legs reports success for the same reason it reports anything")
        }
    ]

// ─── Cross-runtime parity ────────────────────────────────────────────

let private parityTests =
    testList "the three runtimes emit one document" [
        test "Node, Python and .NET produce byte-identical request envelopes" {
            let available =
                declaredRuntimes
                |> List.filter (fun (_, availability) ->
                    match availability.Value with
                    | Available _ -> true
                    | Unavailable _ -> false)
                |> List.map fst

            if List.isEmpty available then
                skiptest "no foreign runtime is available on this machine, so there is nothing to compare"
            else
                let emitted =
                    available
                    |> List.map (fun runtime ->
                        match runOnce runtime with
                        | Some(Ok run) -> runtime, normalise (postBodies run |> List.head).Body
                        | Some(Error diagnostic) -> failtest diagnostic
                        | None -> failtestf "runtime '%s' was probed as available and then would not run" runtime)

                let reference = dotnetReferenceEnvelope ()

                for runtime, document in emitted do
                    Expect.equal
                        document
                        reference
                        $"the {runtime} client's request envelope must be byte-identical to the .NET emitter's for the same call, once the two per-call identifiers are normalised. Everything else — member order, whitespace, the union encoding of an absent user, the embedded arguments document — is fixed by the specification, so a difference here is a divergence rather than a variation"
        }
    ]

// ─── Accounting, and the negative control ────────────────────────────

let private accountingTests =
    testList "the harness accounts for itself" [
        test "every declared runtime is either executed or skipped with a reason" {
            for name, availability in declaredRuntimes do
                match availability.Value with
                | Available launch ->
                    Expect.isNotEmpty launch.Executable $"runtime '{name}' reported available with no executable"

                    match runOnce name with
                    | Some(Ok run) ->
                        Expect.isNotEmpty run.GeneratedClient $"the {name} leg generated an empty client"
                        Expect.isNonEmpty run.Legs $"the {name} leg reported no legs at all"
                    | Some(Error diagnostic) -> failtest diagnostic
                    | None -> failtestf "runtime '%s' was probed as available and then would not run" name
                | Unavailable reason ->
                    Expect.isNotEmpty
                        reason
                        $"runtime '{name}' is unavailable and must say why — a silent skip is indistinguishable from a pass"
        }

        test "at least one runtime leg is exercised, or the machine says which are missing" {
            // Forced here rather than read from whatever earlier cases
            // happened to run: an accounting case that depends on
            // execution order is not accounting for anything.
            let executed =
                declaredRuntimes
                |> List.sumBy (fun (name, _) ->
                    match runOnce name with
                    | Some(Ok _) -> 1
                    | _ -> 0)

            let missing =
                declaredRuntimes
                |> List.choose (fun (name, availability) ->
                    match availability.Value with
                    | Unavailable reason -> Some $"{name}: {reason}"
                    | Available _ -> None)

            let missingSummary = String.concat "; " missing

            Expect.isTrue
                (executed > 0 || missing.Length = declaredRuntimes.Length)
                $"neither runtime executed and neither was reported missing — that combination cannot happen honestly. Missing: {missingSummary}"
        }
    ]

/// The control the whole harness rests on. A generated client whose
/// canonical encoding has been broken must make the emission checks go
/// RED; without this, a harness that had quietly stopped comparing would
/// look exactly like a green one.
let private probeTests =
    testList "a corrupted generator output is rejected" [
        test "an envelope member renamed out of the specification's order fails the round-trip check" {
            let runtime, mutate =
                match nodeAvailability.Value, pythonAvailability.Value with
                | Available _, _ ->
                    // Move `Id` ahead of `Params` — a member-order change
                    // that is still perfectly valid JSON and still parses,
                    // which is precisely why an ordering rule needs a test
                    // rather than a comment.
                    "node",
                    fun (source: string) ->
                        source.Replace(
                            "const body = { JsonRpc: \"2.0\", Method: method, Params: JSON.stringify(payload), Id: crypto.randomUUID() };",
                            "const body = { JsonRpc: \"2.0\", Method: method, Id: crypto.randomUUID(), Params: JSON.stringify(payload) };"
                        )
                | _, Available _ ->
                    "python",
                    fun (source: string) -> source.Replace("separators=(\",\", \":\")", "separators=(\", \", \": \")")
                | _ -> "none", id

            match runtime with
            | "none" -> skiptest "no foreign runtime is available, so the negative control cannot be run"
            | _ ->
                let launch =
                    match
                        (if runtime = "node" then
                             nodeAvailability.Value
                         else
                             pythonAvailability.Value)
                    with
                    | Available launch -> launch
                    | Unavailable reason -> failtest reason

                let outcome =
                    if runtime = "node" then
                        runNode launch mutate
                    else
                        runPython launch mutate

                match outcome with
                | Error _ ->
                    // The corrupted client failed to run at all. That is a
                    // rejection too, and a louder one.
                    ()
                | Ok run ->
                    Expect.isTrue
                        (mutate run.GeneratedClient <> run.GeneratedClient)
                        "the mutation matched nothing in the generated source, so this control proved nothing — the generator's output has changed shape and the mutation must be re-aimed"

                    let corpus = memberOrders (readCommitted "contract-invocation/request.json")

                    let broken =
                        postBodies run
                        |> List.filter (fun request ->
                            let roundTrips =
                                try
                                    JsonRpc.serialize (JsonRpc.deserialize<JsonRpcRequest> request.Body) = request.Body
                                with _ ->
                                    false

                            let ordersAgree =
                                try
                                    memberOrders request.Body
                                    |> Map.toList
                                    |> List.forall (fun (path, names) ->
                                        match Map.tryFind path corpus with
                                        | Some expected -> names = expected
                                        | None -> true)
                                with _ ->
                                    false

                            not (roundTrips && ordersAgree))

                    Expect.isNonEmpty
                        broken
                        $"the corrupted {runtime} client produced requests the emission checks accepted. The checks are decorative until this case can fail"
        }
    ]

let tests =
    testList "Phase 189 — cross-runtime federation conformance" [
        emissionTests "node"
        consumptionTests "node"
        emissionTests "python"
        consumptionTests "python"
        parityTests
        accountingTests
        probeTests
    ]