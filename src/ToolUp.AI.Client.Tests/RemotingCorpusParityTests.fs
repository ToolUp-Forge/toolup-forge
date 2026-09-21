// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 784.C — the Fable half of the remoting wire differential corpus.
///
/// The .NET suites in `ToolUp.Platform.Tests/Remoting/` assert that the
/// committed `tests/remoting-corpus/*.msgpack` fixtures decode to their
/// declared values. This pack asserts the SAME fixtures decode to the
/// SAME values through the MsgPack reader as Fable transpiles it — which
/// is the reader the browser client actually runs (`Platform.Client`'s
/// `Remoting.fs` calls `MsgPack.Read.Reader(response).Read returnType`).
/// Two readers, one corpus, one set of expected values.
///
/// ─── Why the expected values are not a file ──────────────────────────
///
/// `WireCorpus.fs` is COMPILED INTO this project rather than transcribed,
/// so the value a fixture must decode to is one F# declaration that both
/// hosts read. This is the `WireFixtures.fs` precedent — the shared
/// fixture set compiled into both host packs so the golden bytes are one
/// source of truth — and it is stronger than a canonical-JSON oracle:
/// a second rendering of the expected value is free to drift from the
/// first, and drift in an oracle is invisible by construction.
///
/// ─── Why this pack and not `ToolUp.AI.Wire.Fable.Tests` ──────────────
///
/// The phase shard guessed at that project. Checked and refuted twice
/// over: it compiles `ToolUp.AI.Wire` and three provider wire mappers and
/// nothing from `ToolUp.Platform.Core`, so the MsgPack reader is not in
/// it — and `Build.fs`'s `VerifyFable` target drives
/// `src/ToolUp.AI.Client.Tests` and only that, so a pack added there
/// would be reached by no gate at all. This project's own header calls it
/// "the estate's ONE Fable-tier test harness"; it pulls
/// `Platform.Client` + `Platform.Core` through Fable, so `Read.fs` is
/// already transpiled here and this pack costs no new project reference.
///
/// ─── What it does NOT claim ──────────────────────────────────────────
///
/// The comparison is at the declared value, not at the CLR type: under
/// Fable there are no CLR runtime types to compare (`int64` is a JS
/// object, `int32` a JS number), so `WireCorpus`'s type-exactness check
/// is a .NET-side claim and says so. What survives here is structural
/// equality against the declaration, which is the claim a parity leg
/// should make.
///
/// Cases outside `WireCorpus.crossHostCases` are MEASURED divergences
/// between the two readers, each carrying its reason; they are printed on
/// every run rather than skipped silently.
module ToolUp.AI.Client.Tests.RemotingCorpusParityTests

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform.Tests.Remoting

[<Import("readFileSync", from = "node:fs")>]
let private readFileSync (path: obj) : obj = jsNative

[<Import("existsSync", from = "node:fs")>]
let private existsSync (path: obj) : bool = jsNative

/// Resolve a path relative to THIS MODULE rather than to the process cwd
/// — the Phase 613 precedent in `SidebarRailShapeSnapshotTests`. The
/// transpiled module sits in `output/`, so the repository root is three
/// levels up. A cwd-relative path would silently read nothing when the
/// harness is invoked from elsewhere, and "the corpus was empty" is the
/// one failure a parity gate must never report as success.
[<Emit("new URL($0, import.meta.url)")>]
let private beside (relative: string) : obj = jsNative

/// Node's `readFileSync` hands back a `Buffer`; the reader wants the
/// `byte[]` Fable represents as a `Uint8Array`. A `Buffer` IS a
/// `Uint8Array`, but it is a VIEW into a larger pooled allocation, so
/// wrapping it afresh is what keeps `byteOffset` out of the picture.
[<Emit("new Uint8Array($0.buffer, $0.byteOffset, $0.byteLength)")>]
let private toBytes (buffer: obj) : byte[] = jsNative

[<Literal>]
let private CorpusDir = "../../../tests/remoting-corpus/"

let private fixturePath (name: string) = beside (CorpusDir + name + ".msgpack")

let private cases = WireCorpus.crossHostCases

let tests =
    testList "Remoting wire corpus — Fable parity" [
        testCase "the corpus directory resolved and is not empty"
        <| fun () ->
            // The non-vacuity floor. Every case below would pass by
            // doing nothing if this list were empty or the path wrong,
            // which is the shape `VerifyFable`'s own TAP-count floor
            // exists to catch one level up.
            Expect.isTrue
                (List.length cases > 40)
                (sprintf
                    "only %d cross-host case(s) are declared; the parity leg is close to vacuous. Check WireCorpus.crossHostCases."
                    (List.length cases))

            let missing = cases |> List.filter (fun c -> not (existsSync (fixturePath c.Name)))

            Expect.equal
                (List.length missing)
                0
                (sprintf
                    "%d committed fixture(s) are unreachable from the Fable harness (first: %s). The path is resolved relative to the transpiled module in output/, so this is `%s` being wrong, not a missing checkout."
                    (List.length missing)
                    (match missing with
                     | c :: _ -> c.Name
                     | [] -> "-")
                    CorpusDir)

        testCase "recorded cross-host divergences"
        <| fun () ->
            for name, reason in WireCorpus.recordedDivergences do
                printfn "cross-host divergence — %s: %s" name reason

        // ─── Phase 803.B — the binary frames the corpus does not pin ──
        //
        // `binary-past-bin8` pins the `bin16` frame from a committed
        // fixture. The `bin32` frame needs a payload past 65,535 bytes,
        // and 64 KiB of committed bytes for one length header is the
        // wrong trade — so it is built here, in memory, from the same
        // format bytes the writer would emit, and decoded through the
        // same reader entry the corpus cases use. The two refusals
        // beside it are Phase 786's length-vs-remaining guard, asserted
        // under Fable for the two wide frames: a header that claims more
        // than the bytes present is refused BEFORE anything is allocated
        // from it, on this host as on .NET.
        testCase "decodes a bin32-framed byte array"
        <| fun () ->
            let payload = Array.init 65536 (fun i -> byte (i % 251))

            let framed = Array.concat [ [| 0xc6uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy |]; payload ]

            let decoded = ToolUp.Remoting.MsgPack.Read.Reader(framed).Read typeof<byte[]>

            Expect.equal
                (decoded :?> byte[])
                payload
                "the Fable reader decoded a `c6 00 01 00 00` frame to something other than its 65,536-byte payload"

        testCase "refuses a bin16 header that claims more than the bytes remaining"
        <| fun () ->
            // `c5 01 2c` over 300 bytes is `binary-past-bin8`; the same
            // header over 4 bytes claims 296 the payload does not have.
            let framed = [| 0xc5uy; 0x01uy; 0x2cuy; 1uy; 2uy; 3uy; 4uy |]

            match ToolUp.Remoting.MsgPack.Read.Reader(framed).TryRead typeof<byte[]> with
            | Ok _ -> failwith "a bin16 header claiming 300 bytes over a 4-byte payload was ACCEPTED"
            | Error refusal ->
                Expect.isTrue
                    (refusal.Found.Contains "300")
                    (sprintf "the refusal should name the 300 elements claimed; it said `%s`" refusal.Found)

        testCase "refuses a bin32 header that claims more than the bytes remaining"
        <| fun () ->
            let framed = [| 0xc6uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy; 1uy; 2uy; 3uy; 4uy |]

            match ToolUp.Remoting.MsgPack.Read.Reader(framed).TryRead typeof<byte[]> with
            | Ok _ -> failwith "a bin32 header claiming 65,536 bytes over a 4-byte payload was ACCEPTED"
            | Error refusal ->
                Expect.isTrue
                    (refusal.Found.Contains "65536")
                    (sprintf "the refusal should name the 65536 elements claimed; it said `%s`" refusal.Found)

        yield! [
            for c in cases ->
                testCase ("decodes " + c.Name)
                <| fun () ->
                    // Bind the Buffer ONCE. `toBytes` is an `[<Emit>]` that
                    // names `$0` three times, and Fable substitutes the
                    // argument EXPRESSION at each — so passing the read
                    // inline evaluated `readFileSync` three times and built
                    // the view from three different pooled Buffers (`.buffer`
                    // of one, `.byteOffset` of another). It held only while all
                    // three landed in one pool slab; one more module in the
                    // harness moved the slab boundary onto `binary-empty`
                    // (Phase 69c.D, 2026-09-15).
                    let buffer = readFileSync (fixturePath c.Name)
                    let bytes = toBytes buffer

                    let decoded = ToolUp.Remoting.MsgPack.Read.Reader(bytes).Read c.ClrType

                    match c.Compare decoded with
                    | Ok() -> ()
                    | Error problem ->
                        failwithf
                            "the Fable reader decoded `%s` to something other than the declared value: %s. The .NET suite asserts the same fixture against the same declaration, so a failure here is a divergence between the two readers — record it in WireCorpus with its reason, or fix the reader."
                            c.Name
                            problem
        ]
    ]