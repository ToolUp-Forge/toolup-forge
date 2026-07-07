module ToolUp.Platform.Tests.InProcess.TreePatchChannelTests

open System.IO
open Expecto
open ToolUp.Platform

// ─── Phase 271 — neutral tree-patch transport envelope ─────────────────
//
// The reliability contract layered over the Phase 112 ILiveChannel. Five
// concerns:
//   1. Envelope wire round-trip — an opaque payload (delimiters and all)
//      survives encode → frame → decode byte-identically.
//   2. The pure gap-detector (`TreePatchReceiver.classify`) — in-order
//      accept, duplicate ignore, gap detection, snapshot re-base.
//   3. Ordered incremental delivery over a channel — monotonic Seq,
//      BaseSeq = previous, frames reach the subscriber in push order.
//   4. Gap → resync → resume end-to-end — a client that misses a frame
//      detects the gap, requests a snapshot, and resumes incremental.
//   5. GP 4 scope isolation — a patch to scope A is provably unseen by a
//      scope-B subscriber (inherited from the wrapped channel). Plus the
//      GP 13 not-composed shape and the OSS grep-guard.

let private run a = a |> Async.RunSynchronously

let private scope (id: string) : StorageScope = {
    ScopeId = id
    Container = $"test-%s{id}"
    Persist = false
}

/// A recording `ILiveChannel` — captures every framed patch, so a test
/// can assert what reached the wire without an SSE endpoint.
let private recordingChannel (sink: ResizeArray<string>) : ILiveChannel =
    { new ILiveChannel with
        member _.PushFrame payload = async { sink.Add payload }
    }

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

// ─── 1. Envelope wire round-trip ──────────────────────────────────────

let private envelopeTests =
    testList "Phase 271 — envelope wire round-trip" [
        testCase "an opaque payload with delimiters survives encode → decode"
        <| fun _ ->
            // A payload that embeds JSON, quotes, braces, and newlines —
            // the framing must not corrupt it (it is opaque, GP 1).
            let payload = """{"ops":[{"insert":"a\"b"},{"move":3}]}"""

            let env = {
                Seq = 7L
                BaseSeq = Some 6L
                Payload = payload
            }

            let decoded = env |> TreePatchEnvelope.encode |> TreePatchEnvelope.decode

            Expect.equal decoded.Seq 7L "Seq round-trips"
            Expect.equal decoded.BaseSeq (Some 6L) "BaseSeq round-trips"
            Expect.equal decoded.Payload payload "opaque payload survives verbatim"

        testCase "a snapshot envelope (BaseSeq None) reports isSnapshot"
        <| fun _ ->
            let snap = {
                Seq = 3L
                BaseSeq = None
                Payload = "full"
            }

            Expect.isTrue (TreePatchEnvelope.isSnapshot snap) "BaseSeq None ⇒ snapshot"

            Expect.isFalse (TreePatchEnvelope.isSnapshot { snap with BaseSeq = Some 2L }) "BaseSeq Some ⇒ incremental"
    ]

// ─── 2. Pure gap-detector ─────────────────────────────────────────────

let private incremental (s: int64) : TreePatchEnvelope = {
    Seq = s
    BaseSeq = Some(s - 1L)
    Payload = $"patch-{s}"
}

let private classifyTests =
    testList "Phase 271 — TreePatchReceiver.classify (pure, GP 12 rule 4)" [
        testCase "the first incremental after 0 is accepted"
        <| fun _ ->
            Expect.equal (TreePatchReceiver.classify 0L (incremental 1L)) (TreePatchReceipt.Accept 1L) "Seq 1 = 0 + 1"

        testCase "consecutive increments advance the watermark"
        <| fun _ ->
            Expect.equal (TreePatchReceiver.classify 5L (incremental 6L)) (TreePatchReceipt.Accept 6L) "6 = 5 + 1"

        testCase "an already-seen Seq is a Duplicate, not an error"
        <| fun _ ->
            Expect.equal
                (TreePatchReceiver.classify 6L (incremental 6L))
                TreePatchReceipt.Duplicate
                "redelivery ignored"

            Expect.equal (TreePatchReceiver.classify 6L (incremental 4L)) TreePatchReceipt.Duplicate "older ignored"

        testCase "a forward jump is a Gap naming expected vs got"
        <| fun _ ->
            Expect.equal
                (TreePatchReceiver.classify 5L (incremental 8L))
                (TreePatchReceipt.Gap(6L, 8L))
                "expected 6, got 8"

        testCase "a snapshot is always accepted — it re-bases the stream"
        <| fun _ ->
            let snap = {
                Seq = 42L
                BaseSeq = None
                Payload = "full"
            }

            // Even a snapshot whose Seq is far ahead of the watermark is
            // accepted (it is a resync reply, never a gap).
            Expect.equal
                (TreePatchReceiver.classify 5L snap)
                (TreePatchReceipt.Accept 42L)
                "snapshot re-bases to its Seq"
    ]

// ─── 3. Ordered incremental delivery ──────────────────────────────────

let private deliveryTests =
    testList "Phase 271 — ordered incremental delivery" [
        testCase "Push assigns a monotonic Seq with BaseSeq = previous"
        <| fun _ ->
            let frames = ResizeArray<string>()

            let ch =
                TreePatchChannel.createInMemory (recordingChannel frames) (fun () -> "snap")

            let e1 = ch.Push "p1" |> run
            let e2 = ch.Push "p2" |> run
            let e3 = ch.Push "p3" |> run

            Expect.equal (e1.Seq, e2.Seq, e3.Seq) (1L, 2L, 3L) "sequences are monotonic 1,2,3"
            Expect.equal e1.BaseSeq (Some 0L) "first patch diffs against the initial empty state"
            Expect.equal e2.BaseSeq (Some 1L) "second diffs against the first"
            Expect.equal e3.BaseSeq (Some 2L) "third diffs against the second"
            Expect.equal ch.LastSeq 3L "LastSeq tracks the highest pushed"

        testCase "framed patches reach the subscriber in push order and decode to the payload"
        <| fun _ ->
            let frames = ResizeArray<string>()

            let ch =
                TreePatchChannel.createInMemory (recordingChannel frames) (fun () -> "snap")

            ch.Push "alpha" |> run |> ignore
            ch.Push "beta" |> run |> ignore

            let decoded = frames |> Seq.map TreePatchEnvelope.decode |> List.ofSeq

            Expect.equal (decoded |> List.map _.Seq) [ 1L; 2L ] "frames arrive in push order"
            Expect.equal (decoded |> List.map _.Payload) [ "alpha"; "beta" ] "payloads delivered verbatim"

        testCase "Ack advances the watermark monotonically"
        <| fun _ ->
            let ch =
                TreePatchChannel.createInMemory (recordingChannel (ResizeArray())) (fun () -> "snap")

            ch.Ack 3L |> run
            Expect.equal ch.AckedThrough 3L "watermark advances to 3"
            ch.Ack 2L |> run
            Expect.equal ch.AckedThrough 3L "a lower ack does not regress the watermark"
            ch.Ack 5L |> run
            Expect.equal ch.AckedThrough 5L "a higher ack advances it"
    ]

// ─── 4. Gap → resync → resume, end-to-end ─────────────────────────────

let private resyncTests =
    testList "Phase 271 — gap → resync → resume" [
        testCase "a client that misses a frame resyncs to a snapshot then resumes incremental"
        <| fun _ ->
            let frames = ResizeArray<string>()
            // The snapshot source yields the authoritative full-tree payload.
            let ch =
                TreePatchChannel.createInMemory (recordingChannel frames) (fun () -> "FULL-TREE")

            // Model the client's durable watermark; it applies each frame it
            // is actually handed via `classify`.
            let mutable lastSeen = 0L
            let applied = ResizeArray<string>()

            let deliver (env: TreePatchEnvelope) =
                match TreePatchReceiver.classify lastSeen env with
                | TreePatchReceipt.Accept newSeq ->
                    lastSeen <- newSeq
                    applied.Add env.Payload
                    None
                | TreePatchReceipt.Duplicate -> None
                | TreePatchReceipt.Gap(expected, got) -> Some(expected, got)

            // Frame 1 reaches the client — accepted.
            let e1 = ch.Push "p1" |> run
            Expect.isNone (deliver e1) "frame 1 accepted in order"

            // Frame 2 is pushed but LOST in transit (never handed to the client).
            ch.Push "p2" |> run |> ignore

            // Frame 3 reaches the client — a gap (expected 2, got 3).
            let e3 = ch.Push "p3" |> run

            match deliver e3 with
            | Some(expected, got) ->
                Expect.equal (expected, got) (2L, 3L) "the client detects the gap: expected 2, got 3"
            | None -> failtest "frame 3 must trip a gap after the lost frame 2"

            // The client requests a resync; the host replies with a snapshot,
            // which the client accepts unconditionally, re-basing the stream.
            let snap = ch.RequestResync() |> run
            Expect.isTrue (TreePatchEnvelope.isSnapshot snap) "resync reply is a snapshot"
            Expect.equal snap.Payload "FULL-TREE" "snapshot carries the full-tree payload"
            Expect.isNone (deliver snap) "the client accepts the snapshot and re-bases"
            Expect.equal lastSeen snap.Seq "the watermark jumps to the snapshot's Seq"

            // Incremental delivery resumes cleanly from the snapshot.
            let e5 = ch.Push "p5" |> run
            Expect.isNone (deliver e5) "the next incremental is accepted after resync"

            // The client applied p1, then the snapshot, then p5 — never the
            // lost p2 or the gapped p3 (which triggered the resync instead).
            Expect.equal (List.ofSeq applied) [ "p1"; "FULL-TREE"; "p5" ] "resume applies exactly the recovered stream"
    ]

// ─── 5. Scope isolation + GP 13 + OSS grep-guard ──────────────────────

let private isolationTests =
    testList "Phase 271 — scope isolation (GP 4) + GP 13 + OSS boundary" [
        testCase "a patch to scope A is provably unseen by a scope-B subscriber"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            let dA =
                match host.OpenSession(scope "a") |> run with
                | Ok d -> d
                | Error r -> failtestf "open A failed: %A" r

            let dB =
                match host.OpenSession(scope "b") |> run with
                | Ok d -> d
                | Error r -> failtestf "open B failed: %A" r

            let receivedA = ResizeArray<string>()
            let receivedB = ResizeArray<string>()
            host.Subscribe("a", dA.SessionId, receivedA.Add) |> run |> ignore
            host.Subscribe("b", dB.SessionId, receivedB.Add) |> run |> ignore

            // Resolve + wrap scope A's channel; push a patch through it.
            let chA =
                match TreePatchChannel.forSession host "a" dA.SessionId (fun () -> "snapA") |> run with
                | Some ch -> ch
                | None -> failtest "scope A's session must resolve a patch channel"

            chA.Push "for-A" |> run |> ignore

            Expect.equal receivedA.Count 1 "scope A's subscriber sees exactly its one patch"
            Expect.equal receivedB.Count 0 "scope B's subscriber sees nothing (structural isolation, GP 4)"

        testCase "cross-scope channel resolution is structurally None"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            let dA =
                match host.OpenSession(scope "a") |> run with
                | Ok d -> d
                | Error r -> failtestf "open failed: %A" r

            // Scope B cannot obtain a patch channel for scope A's session —
            // the wrapped Phase 112 resolution denies it (GP 4 / GP 13).
            let resolved =
                TreePatchChannel.forSession host "b" dA.SessionId (fun () -> "snap") |> run

            Expect.isNone resolved "a patch channel is never resolvable across scopes"

        testCase "the envelope source carries no banned OSS vocabulary"
        <| fun _ ->
            let path =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Notifications",
                    "TreePatchChannel.fs"
                )

            Expect.isTrue (File.Exists path) (sprintf "expected the seam file at %s" path)
            NeutralityTokens.assertNoBannedTokens path (File.ReadAllText path)
            NeutralityTokens.skipUnlessExternalSource ()
    ]

let tests =
    testList "TreePatchChannel (Phase 271)" [ envelopeTests; classifyTests; deliveryTests; resyncTests; isolationTests ]