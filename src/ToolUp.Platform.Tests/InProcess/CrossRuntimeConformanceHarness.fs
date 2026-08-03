module ToolUp.Platform.Tests.InProcess.CrossRuntimeConformanceHarness

// ─── Phase 189 — the cross-runtime federation conformance harness ─────
//
// Phase 596 specified the federation seam and certified the .NET emitters
// against its corpus, and a second emitter written in Node proved the
// specification says enough for somebody else to reproduce the documents.
// Neither of those touches the clients the SDK GENERATES: a consumer's
// non-F# peer runs `PythonClientGen.emit` / `TypeScriptClientGen.emit`
// output, and until this phase nothing anywhere executed that output. The
// generators were pinned by string-containment tests, which cannot see a
// document whose member order, whitespace or union encoding is wrong.
//
// What this harness does, per runtime:
//
//   1. Emits the client from the LIVE generator for a canonical contract.
//   2. Starts a loopback receiver whose dispatch is the LIVE
//      `IPlatformPeer.Handle` over a `JsonRpcPeerHost.contract` binding,
//      guarded by a LIVE `JwtPeerAuthProvider` validating a token minted
//      by the same provider — so the signing-helper path is crossed by a
//      foreign runtime rather than asserted about.
//   3. Runs the runtime's driver, which makes a fixed sequence of calls.
//   4. Hands the captured requests and the driver's report back for
//      assertion.
//
// **Two directions are certified, and both matter.** The requests the
// generated client PRODUCES are round-tripped through the receiver's own
// decoder and compared, member for member, against the corpus's request
// vector; the responses it CONSUMES are corpus bytes, served verbatim,
// so what the client reports having read is measured against the
// specification rather than against this codebase's own habits.
//
// **The receiver is a raw `TcpListener`, not Kestrel or `HttpListener`.**
// A ~90-line HTTP/1.1 responder buys three things a real server costs
// more than it returns here: no URL ACL to reserve on Windows, no ASP.NET
// host to boot inside a unit-test pack, and — the one that matters —
// byte-exact control of what goes back on the wire, which is the whole
// point of serving corpus fixtures verbatim.
//
// Test-tier only. Nothing here ships, and a deployment that does not
// federate is byte-for-byte unchanged (GP 13).

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.InProcess.FederationWireCorpus

// ─── The canonical contract ──────────────────────────────────────────
//
// Shaped to the corpus's `contract-invocation` family: `PlaceOrder` takes
// the two positional arguments the corpus request vector carries
// (`["order-42",{"Quantity":3}]`) and returns the result its response
// vector carries (`{"OrderId":"order-42","Accepted":true}`). The
// long-running method exists so the generators emit their poll helper;
// it is never dispatched (see `pollStates` below).

type OrderLine = { Quantity: int }

type OrderAck = { OrderId: string; Accepted: bool }

type LedgerReconciliation = { Reconciled: int }

type OrdersContract = {
    PlaceOrder: string -> OrderLine -> Async<OrderAck>
    ReconcileLedger: string -> Async<PeerJobHandle<LedgerReconciliation>>
}

/// The contract id the corpus's peer-surface family uses. Dotted, which
/// is the estate convention and — before this phase — the thing that made
/// the generated class name unparseable in both target languages.
let contractId = "example.orders"

let contractVersions: ContractVersion list = [ { Major = 1; Minor = 0 } ]

/// The peer the drivers present themselves as, matching the corpus
/// request vector's `Context.Peer.PeerId`.
let callerPeer: PeerIdentity = {
    PeerId = "buyer-acme"
    DisplayName = "Acme demand-side"
}

/// The receiving deployment.
let receiverPeer: PeerIdentity = {
    PeerId = "seller-ssp"
    DisplayName = "Seller SSP"
}

/// The job id both drivers poll. A Guid because the real jobs route
/// (`GET /peer/v1/{contractId}/jobs/%O`) binds one.
let jobId = "7c9e6679-7425-40de-944b-e07fc1f90ae7"

/// The order id whose handler fails, so the live receiver produces a
/// structured `PeerError` on a 200 rather than a transport failure.
let failingOrderId = "boom"

let private implementation: OrdersContract = {
    PlaceOrder =
        fun orderId line -> async {
            if orderId = failingOrderId then
                failwith "ledger snapshot expired"

            return {
                OrderId = orderId
                Accepted = line.Quantity > 0
            }
        }
    ReconcileLedger =
        fun _ -> async { return failwith "the long-running leg is corpus-served; this handler is never dispatched" }
}

/// The schema the generators consume — reflected from the same contract
/// type the receiver hosts, so a generated client and the live receiver
/// cannot describe different methods.
let schema = PeerSchema.fromContract<OrdersContract> contractId

// ─── Corpus-anchored response bodies ─────────────────────────────────
//
// Read from Phase 596's committed corpus, never re-authored here. A
// second corpus is precisely the drift this phase exists to prevent, so
// where a leg needs a document the corpus already pins, it gets those
// bytes and no others.

/// The specified success response, verbatim.
let corpusSuccessResponse () =
    readCommitted "contract-invocation/response.json"

/// The first structured failure the corpus enumerates, re-serialised on
/// its own — the corpus carries the whole code mapping as one array, and
/// a response envelope carries one member of it.
let corpusFirstFailure () =
    readCommitted "contract-invocation/errors.json"
    |> JsonRpc.deserialize<JsonRpcResponse list>
    |> List.head
    |> JsonRpc.serialize

/// The document that is not a well-formed envelope at all, verbatim. The
/// corpus files it as a REQUEST an implementation must refuse; a client
/// handed it as a response is under the same obligation, and refusing to
/// parse it is the same refusal — so the vector is reused rather than a
/// near-duplicate of it being minted.
let corpusMalformed () =
    readCommitted "contract-invocation/reject-malformed.json"

/// The three terminal poll states the corpus pins, in its own order.
let pollStates () =
    readCommitted "contract-invocation/job-poll.json"
    |> JsonRpc.deserialize<PeerJobStatus<string> list>

/// Wrap one poll state in the envelope the real jobs route answers with
/// (`Result` carries the serialised status; `Id` correlates the job id).
let pollResponse (status: PeerJobStatus<string>) : string =
    JsonRpc.serialize {
        JsonRpc = JsonRpc.version
        Result = Some(JsonRpc.serialize status)
        Error = None
        Id = jobId
    }

// ─── The live receiver ───────────────────────────────────────────────

/// One request as it arrived on the wire, before anything interpreted it.
type CapturedRequest = {
    Ordinal: int
    Verb: string
    Path: string
    Authorization: string option
    Body: string
}

type ScriptedResponse = { Status: int; Body: string }

let private reasonPhrase =
    function
    | 200 -> "OK"
    | 401 -> "Unauthorized"
    | 404 -> "Not Found"
    | _ -> "Internal Server Error"

/// A minimal HTTP/1.1 responder on an ephemeral loopback port. Serves one
/// request per connection (`Connection: close`), records every request
/// verbatim, and answers whatever `respond` returns for it.
type LoopbackReceiver(respond: CapturedRequest -> ScriptedResponse) =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    do listener.Start()

    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    let captured = List<CapturedRequest>()
    let gate = obj ()
    let mutable running = true

    /// Read up to and including the header terminator. Byte at a time —
    /// this serves single-digit request counts, and a buffered read would
    /// have to hand back the overshoot into the body.
    let readHead (stream: NetworkStream) : string option =
        use buffer = new MemoryStream()
        let one = Array.zeroCreate<byte> 1
        let mutable finished = false
        let mutable closed = false

        while not finished && not closed do
            if stream.Read(one, 0, 1) = 0 then
                closed <- true
            else
                buffer.WriteByte one[0]
                let length = int buffer.Length

                if length >= 4 then
                    let bytes = buffer.GetBuffer()

                    if
                        bytes[length - 4] = 13uy
                        && bytes[length - 3] = 10uy
                        && bytes[length - 2] = 13uy
                        && bytes[length - 1] = 10uy
                    then
                        finished <- true

        if finished then
            Some(Encoding.ASCII.GetString(buffer.GetBuffer(), 0, int buffer.Length))
        else
            None

    let readBody (stream: NetworkStream) (length: int) : string =
        if length <= 0 then
            ""
        else
            let bytes = Array.zeroCreate<byte> length
            let mutable read = 0

            while read < length do
                let got = stream.Read(bytes, read, length - read)

                if got = 0 then read <- length else read <- read + got

            Encoding.UTF8.GetString bytes

    let serve (client: TcpClient) =
        use client = client
        client.ReceiveTimeout <- 15000
        client.SendTimeout <- 15000
        use stream = client.GetStream()

        match readHead stream with
        | None -> ()
        | Some head ->
            let lines = head.Split([| "\r\n" |], StringSplitOptions.RemoveEmptyEntries)
            let requestLine = lines[0].Split ' '

            let header (name: string) =
                lines
                |> Array.skip 1
                |> Array.tryPick (fun line ->
                    let separator = line.IndexOf ':'

                    if
                        separator > 0
                        && line.Substring(0, separator).Trim().Equals(name, StringComparison.OrdinalIgnoreCase)
                    then
                        Some(line.Substring(separator + 1).Trim())
                    else
                        None)

            let contentLength =
                match header "Content-Length" with
                | Some value ->
                    match Int32.TryParse value with
                    | true, parsed -> parsed
                    | _ -> 0
                | None -> 0

            let body = readBody stream contentLength

            let request =
                lock gate (fun () ->
                    let request = {
                        Ordinal = captured.Count
                        Verb = requestLine[0]
                        Path = requestLine[1]
                        Authorization = header "Authorization"
                        Body = body
                    }

                    captured.Add request
                    request)

            let response = respond request
            let bytes = Encoding.UTF8.GetBytes response.Body

            let headBytes =
                Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {response.Status} {reasonPhrase response.Status}\r\n"
                    + "Content-Type: application/json\r\n"
                    + $"Content-Length: {bytes.Length}\r\n"
                    + "Connection: close\r\n\r\n"
                )

            stream.Write(headBytes, 0, headBytes.Length)
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush()

    let loop () =
        while running do
            try
                serve (listener.AcceptTcpClient())
            with _ ->
                // A closed listener, a driver that hung up mid-request, a
                // read timeout: none of them are this thread's to report.
                // The assertions read `Captured`, so a request that never
                // arrived shows up as a missing leg, which is the honest
                // signal.
                ()

    let thread = Thread(loop, IsBackground = true)
    do thread.Start()

    member _.BaseUrl = $"http://127.0.0.1:{port}"

    member _.Captured = lock gate (fun () -> List.ofSeq captured)

    interface IDisposable with
        member _.Dispose() =
            running <- false
            listener.Stop()

// ─── Runtime discovery ───────────────────────────────────────────────

/// How to invoke a runtime, once one has been found that works.
type RuntimeLaunch = {
    Executable: string
    Arguments: string list
}

/// A runtime is either usable — with the exact invocation proved by a
/// probe rather than inferred from a version string — or it is not, with
/// the reason recorded so a skipped leg says why it skipped.
type RuntimeAvailability =
    | Available of RuntimeLaunch
    | Unavailable of reason: string

type ProcessOutcome = {
    ExitCode: int
    StdOut: string
    StdErr: string
    TimedOut: bool
}

let private probeMarker = "CROSS-RUNTIME-PROBE-OK"

let private runProcess (launch: RuntimeLaunch) (extra: string list) (workingDirectory: string) : ProcessOutcome =
    let startInfo =
        ProcessStartInfo(
            launch.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        )

    for argument in launch.Arguments @ extra do
        startInfo.ArgumentList.Add argument

    try
        use proc = new Process(StartInfo = startInfo)
        proc.Start() |> ignore

        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        let exited = proc.WaitForExit 120000

        if not exited then
            try
                proc.Kill true
            with _ ->
                ()

        {
            ExitCode = if exited then proc.ExitCode else -1
            StdOut = stdout.Result
            StdErr = stderr.Result
            TimedOut = not exited
        }
    with ex ->
        // An executable that is not on PATH throws rather than exiting;
        // that is a runtime-absent signal, not a harness failure.
        {
            ExitCode = -1
            StdOut = ""
            StdErr = ex.Message
            TimedOut = false
        }

/// A scratch directory under the test binary's own output, so a probe or
/// a run never writes into the repo tree.
let private scratchDir (name: string) =
    let path =
        Path.Combine(Path.GetTempPath(), "toolup-cross-runtime", $"{name}-{Guid.NewGuid():N}")

    Directory.CreateDirectory path |> ignore
    path

/// `docs/interplatform/cross-runtime/` — the committed drivers.
let driverDir () =
    Path.Combine(repoRoot (), "docs", "interplatform", "cross-runtime")

/// Node runs the generated `.ts` through type stripping, which needs a
/// flag before 23.6 and none after. Rather than parse a version, try the
/// bare invocation and then the flagged one, and take whichever actually
/// executed a `.ts` import — the probe mirrors the driver's own shape
/// (an `.mjs` importing a `.ts`), so a node that passes it can run the
/// real thing.
let probeNode () : RuntimeAvailability =
    let dir = scratchDir "node-probe"

    File.WriteAllText(Path.Combine(dir, "probe.ts"), $"export const marker: string = \"{probeMarker}\";\n")

    File.WriteAllText(
        Path.Combine(dir, "probe.mjs"),
        "const m = await import(\"./probe.ts\");\nprocess.stdout.write(m.marker);\n"
    )

    let candidates = [
        { Executable = "node"; Arguments = [] }
        {
            Executable = "node"
            Arguments = [ "--experimental-strip-types" ]
        }
    ]

    let rec attempt remaining lastReason =
        match remaining with
        | [] -> Unavailable lastReason
        | candidate :: rest ->
            let outcome = runProcess candidate [ "probe.mjs" ] dir

            if outcome.StdOut.Contains probeMarker then
                Available candidate
            else
                let reason =
                    let detail =
                        if outcome.StdErr = "" then
                            outcome.StdOut
                        else
                            outcome.StdErr

                    let firstLine =
                        detail.Split('\n') |> Array.tryHead |> Option.defaultValue "" |> _.Trim()

                    let invocation = candidate.Executable :: candidate.Arguments |> String.concat " "

                    $"node could not execute a TypeScript module (tried '{invocation}'): {firstLine}"

                attempt rest reason

    attempt candidates "no node candidate was tried"

/// Python needs no flags, but the executable name differs by platform and
/// Windows ships a Store stub named `python3` that exits non-zero with an
/// install prompt — so, again, the probe runs code rather than trusting a
/// name.
let probePython () : RuntimeAvailability =
    let dir = scratchDir "python-probe"

    let script =
        "import sys, json, uuid, urllib.request, dataclasses, typing\n"
        + $"sys.stdout.write('{probeMarker}' if sys.version_info >= (3, 8) else 'too-old')\n"

    File.WriteAllText(Path.Combine(dir, "probe.py"), script)

    let candidates = [
        {
            Executable = "python3"
            Arguments = []
        }
        {
            Executable = "python"
            Arguments = []
        }
        {
            Executable = "py"
            Arguments = [ "-3" ]
        }
    ]

    let rec attempt remaining lastReason =
        match remaining with
        | [] -> Unavailable lastReason
        | candidate :: rest ->
            let outcome = runProcess candidate [ "probe.py" ] dir

            if outcome.StdOut.Contains probeMarker then
                Available candidate
            else
                let detail =
                    if outcome.StdErr = "" then
                        outcome.StdOut
                    else
                        outcome.StdErr

                let firstLine =
                    detail.Split('\n') |> Array.tryHead |> Option.defaultValue "" |> _.Trim()

                attempt
                    rest
                    $"no usable Python interpreter was found (last tried '{candidate.Executable}'): {firstLine}"

    attempt candidates "no python candidate was tried"

// ─── Authentication ──────────────────────────────────────────────────

let private signingKey = "cross-runtime-conformance-shared-signing-key-0123456789"

/// The per-file in-memory `ISecretStore` every peer pack in this tier
/// carries — kept local rather than shared for the same reason they do:
/// a fixture that several packs mutate is a fixture that couples them.
type private InMemorySecretStore() =
    let store =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, value -> return Some value
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
                |> Seq.filter (fun (scope, _) -> scope = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private secrets () =
    let store = InMemorySecretStore() :> ISecretStore

    store.SetSecret("_platform", $"peers/{callerPeer.PeerId}/signing-key", signingKey)
    |> Async.RunSynchronously
    |> ignore

    store

/// The token the drivers present, minted by the live signing helper and
/// addressed to the receiver — so a foreign runtime crosses the same
/// `JwtPeerAuthProvider` issue / validate pair the F# initiator does.
let mintToken () : string =
    let issuer = JwtPeerAuthProvider(secrets ()) :> IPeerAuthProvider

    match
        issuer.IssuePeerToken(callerPeer, receiverPeer, Anonymous)
        |> Async.RunSynchronously
    with
    | Ok token -> token
    | Error e -> failwithf "the harness could not mint a peer token: %A" e

// ─── The scripted receiver ───────────────────────────────────────────

/// The live receiver: a real `JsonRpcPeerHost.contract` binding behind a
/// real `DefaultPlatformPeer`, dispatched exactly as the Giraffe handler
/// dispatches it.
let private platformPeer () =
    let host =
        JsonRpcPeerHost.contract<OrdersContract> contractId contractVersions None implementation

    let peer = DefaultPlatformPeer(receiverPeer.PeerId) :> IPlatformPeer
    peer.RegisterContract host.Registration
    peer

/// What a leg's response is drawn from. `LiveHost` runs the receiver;
/// `CorpusBytes` serves a committed fixture untouched.
type LegSource =
    | LiveHost
    | CorpusBytes

/// The nine legs, in the order both drivers call them. The drivers and
/// this list are two halves of one contract.
let legScript: (string * LegSource) list = [
    "capabilities", LiveHost
    "immediate", LiveHost
    "handlerError", LiveHost
    "corpusResult", CorpusBytes
    "corpusError", CorpusBytes
    "corpusMalformed", CorpusBytes
    "pollPending", CorpusBytes
    "pollCompleted", CorpusBytes
    "pollFailed", CorpusBytes
]

/// Answer one request. Live legs authenticate with the real provider and
/// dispatch through the real `IPlatformPeer.Handle`; corpus legs return
/// committed bytes and never look at the request at all.
let private answer (auth: IPeerAuthProvider) (peer: IPlatformPeer) (states: PeerJobStatus<string> list) request =
    let legName =
        legScript
        |> List.tryItem request.Ordinal
        |> Option.map fst
        |> Option.defaultValue "unscripted"

    match legName with
    | "corpusResult" -> {
        Status = 200
        Body = corpusSuccessResponse ()
      }
    | "corpusError" -> {
        Status = 200
        Body = corpusFirstFailure ()
      }
    | "corpusMalformed" -> {
        Status = 200
        Body = corpusMalformed ()
      }
    | "pollPending" -> {
        Status = 200
        Body = pollResponse states[0]
      }
    | "pollCompleted" -> {
        Status = 200
        Body = pollResponse states[1]
      }
    | "pollFailed" -> {
        Status = 200
        Body = pollResponse states[2]
      }
    | _ ->
        // A live leg. Bearer first — the receiver is fail-closed, and a
        // driver that forgot the header must be told so, not served.
        let token =
            request.Authorization
            |> Option.bind (fun header ->
                if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
                    Some(header.Substring 7)
                else
                    None)

        match token with
        | None -> {
            Status = 401
            Body = JsonRpc.serialize (JsonRpc.failure "" (PeerUnauthorized "missing bearer token"))
          }
        | Some token ->
            match auth.ValidatePeerToken token |> Async.RunSynchronously with
            | Error e -> {
                Status = 401
                Body = JsonRpc.serialize (JsonRpc.failure "" e)
              }
            | Ok principal ->
                if legName = "capabilities" then
                    let capabilities = peer.Capabilities() |> Async.RunSynchronously

                    {
                        Status = 200
                        Body = JsonRpc.serialize capabilities
                    }
                else
                    let envelope = JsonRpc.deserialize<JsonRpcRequest> request.Body
                    let payload = JsonRpc.deserialize<PeerWirePayload> envelope.Params

                    // The receiver rebuilds the context from the VALIDATED
                    // principal rather than the wire body, exactly as the
                    // Giraffe handler does — the self-asserted caller is
                    // never trusted.
                    let context = {
                        payload.Context with
                            Peer = principal.Caller
                            Route = [ principal.Caller.PeerId ]
                    }

                    let outcome =
                        peer.Handle(contractId, context, envelope.Method, payload.Arguments)
                        |> Async.RunSynchronously

                    let response =
                        match outcome with
                        | Ok result -> {
                            JsonRpc = JsonRpc.version
                            Result = Some result
                            Error = None
                            Id = envelope.Id
                          }
                        | Error e -> JsonRpc.failure envelope.Id e

                    {
                        Status = 200
                        Body = JsonRpc.serialize response
                    }

// ─── Running a runtime's leg ─────────────────────────────────────────

/// One leg as the driver reported it. `Ok = false` is not a failure of
/// the harness — three legs are *supposed* to raise.
type ReportedLeg = {
    Name: string
    Ok: bool
    Value: JsonElement option
    Error: string option
}

/// Everything one runtime's execution produced.
type RuntimeRun = {
    Runtime: string
    Launch: RuntimeLaunch
    /// The generated client source, as the live generator emitted it.
    GeneratedClient: string
    /// Every request the driver put on the wire, in order.
    Requests: CapturedRequest list
    Legs: ReportedLeg list
    RawReport: string
}

let private parseReport (raw: string) : string * ReportedLeg list =
    use document = JsonDocument.Parse raw
    let root = document.RootElement

    let legs =
        root.GetProperty("legs").EnumerateArray()
        |> Seq.map (fun leg ->
            let ok = leg.GetProperty("ok").GetBoolean()

            {
                Name = leg.GetProperty("name").GetString()
                Ok = ok
                Value =
                    match leg.TryGetProperty "value" with
                    | true, value -> Some(value.Clone())
                    | _ -> None
                Error =
                    match leg.TryGetProperty "error" with
                    | true, value -> Some(value.GetString())
                    | _ -> None
            })
        |> List.ofSeq

    root.GetProperty("runtime").GetString(), legs

/// Emit the client, start the receiver, run the driver, collect
/// everything. The `mutate` hook rewrites the generated source before it
/// is written — the negative control the whole harness rests on: without
/// proving a corrupted client makes this go red, a green run and a run
/// that quietly stopped comparing look identical.
let execute
    (runtime: string)
    (launch: RuntimeLaunch)
    (driverFile: string)
    (clientFile: string)
    (emitClient: PeerContractSchema -> string)
    (mutate: string -> string)
    : Result<RuntimeRun, string> =
    let dir = scratchDir runtime
    let generated = emitClient schema
    File.WriteAllText(Path.Combine(dir, clientFile), mutate generated)
    File.Copy(Path.Combine(driverDir (), driverFile), Path.Combine(dir, driverFile))

    let auth = JwtPeerAuthProvider(secrets (), receiverPeer.PeerId) :> IPeerAuthProvider
    let peer = platformPeer ()
    let states = pollStates ()

    use receiver = new LoopbackReceiver(answer auth peer states)

    let outcome =
        runProcess launch [ driverFile; receiver.BaseUrl; mintToken (); callerPeer.PeerId ] dir

    if outcome.TimedOut then
        Error $"the {runtime} driver did not exit within the harness timeout"
    elif outcome.ExitCode <> 0 || outcome.StdOut.Trim() = "" then
        Error
            $"the {runtime} driver exited {outcome.ExitCode} without a report.\nstdout: {outcome.StdOut}\nstderr: {outcome.StdErr}"
    else
        try
            let reported, legs = parseReport (outcome.StdOut.Trim())

            Ok {
                Runtime = reported
                Launch = launch
                GeneratedClient = generated
                Requests = receiver.Captured
                Legs = legs
                RawReport = outcome.StdOut.Trim()
            }
        with ex ->
            Error $"the {runtime} driver's report did not parse ({ex.Message}): {outcome.StdOut}"

/// The Node leg.
let runNode (launch: RuntimeLaunch) (mutate: string -> string) =
    execute "node" launch "driver.mjs" "client.ts" TypeScriptClientGen.emit mutate

/// The Python leg.
let runPython (launch: RuntimeLaunch) (mutate: string -> string) =
    execute "python" launch "driver.py" "client.py" PythonClientGen.emit mutate

/// No mutation — the ordinary run.
let asEmitted (source: string) = source