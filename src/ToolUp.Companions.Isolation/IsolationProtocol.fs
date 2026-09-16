// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Companions.Isolation

open System
open System.Buffers.Binary
open System.IO
open System.Text

// ─── Phase 687 — the pipe ─────────────────────────────────────────────
//
// Two frames, one each way, over the worker's stdin / stdout. Binary
// and length-prefixed rather than JSON, deliberately: the payload IS
// untrusted bytes, and the one thing a framing must never do is
// interpret them. A four-byte magic on each frame is what lets the host
// tell "the worker answered" from "something else was started" —
// `IsolationRefusal.ProtocolViolation` is the typed form of the latter.
//
// Little-endian throughout; lengths are unsigned 32-bit, so one frame
// carries at most 4 GiB, which is already far above any sensible
// `IsolationLimits.MemoryCap`.

/// What the worker answered with. `Answered` is the entry point's
/// bytes; the other two are the worker's own typed rejections, carried
/// so the host can distinguish "the companion refused the input" from
/// "the worker could not even find the entry point".
[<RequireQualifiedAccess>]
type WorkerResponse =
    /// The entry point returned `Ok bytes`.
    | Answered of byte[]
    /// The entry point returned `Error message` or raised.
    | Rejected of message: string
    /// The worker could not instantiate the named entry point.
    | Unresolvable of reason: string

/// The frame codec. Pure over streams; used by both ends.
[<RequireQualifiedAccess>]
module IsolationProtocol =
    /// Protocol version carried on every frame. Bump on any change to
    /// the frame layout — the two ends ship in one package, but a
    /// launcher override can start a worker from another build.
    [<Literal>]
    let Version = 1uy

    let private requestMagic = "TUIR"B
    let private responseMagic = "TUIA"B

    let private writeU32 (stream: Stream) (value: int) =
        let buffer = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(), uint32 value)
        stream.Write(buffer, 0, 4)

    let private writeBytes (stream: Stream) (bytes: byte[]) =
        writeU32 stream bytes.Length
        stream.Write(bytes, 0, bytes.Length)

    let private writeString (stream: Stream) (value: string) =
        writeBytes stream (Encoding.UTF8.GetBytes value)

    let private readExactly (stream: Stream) (count: int) : byte[] =
        let buffer = Array.zeroCreate<byte> count
        stream.ReadExactly(buffer, 0, count)
        buffer

    let private readU32 (stream: Stream) : int =
        let value =
            BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(readExactly stream 4))

        if value > uint32 Int32.MaxValue then
            raise (InvalidDataException $"frame length {value} exceeds the addressable range")

        int value

    let private readBytes (stream: Stream) : byte[] =
        let length = readU32 stream
        readExactly stream length

    let private readString (stream: Stream) : string =
        Encoding.UTF8.GetString(readBytes stream)

    let private expectMagic (stream: Stream) (expected: byte[]) (what: string) =
        let actual = readExactly stream expected.Length

        if actual <> expected then
            raise (InvalidDataException $"expected a {what} frame; the first bytes were not its magic")

        let version = (readExactly stream 1)[0]

        if version <> Version then
            raise (InvalidDataException $"{what} frame is protocol version {version}; this build speaks {Version}")

    /// Write a request frame: the entry point's assembly-qualified name,
    /// its args, the input bytes.
    let writeRequest (stream: Stream) (entryTypeName: string) (request: IsolatedRequest) : unit =
        stream.Write(requestMagic, 0, requestMagic.Length)
        stream.WriteByte Version
        writeString stream entryTypeName
        writeU32 stream request.Args.Length

        for arg in request.Args do
            writeString stream arg

        writeBytes stream request.Input
        stream.Flush()

    /// Read a request frame. Raises `InvalidDataException` on a frame
    /// that is not a request, and `EndOfStreamException` on truncation.
    let readRequest (stream: Stream) : string * IsolatedRequest =
        expectMagic stream requestMagic "request"
        let entryTypeName = readString stream
        let argc = readU32 stream
        let args = List.init argc (fun _ -> readString stream)
        let input = readBytes stream
        entryTypeName, { Args = args; Input = input }

    /// Write a response frame.
    let writeResponse (stream: Stream) (response: WorkerResponse) : unit =
        stream.Write(responseMagic, 0, responseMagic.Length)
        stream.WriteByte Version

        match response with
        | WorkerResponse.Answered bytes ->
            stream.WriteByte 0uy
            writeBytes stream bytes
        | WorkerResponse.Rejected message ->
            stream.WriteByte 1uy
            writeString stream message
        | WorkerResponse.Unresolvable reason ->
            stream.WriteByte 2uy
            writeString stream reason

        stream.Flush()

    /// Read a response frame. Raises `InvalidDataException` on a frame
    /// that is not a response, and `EndOfStreamException` on truncation.
    let readResponse (stream: Stream) : WorkerResponse =
        expectMagic stream responseMagic "response"

        match (readExactly stream 1)[0] with
        | 0uy -> WorkerResponse.Answered(readBytes stream)
        | 1uy -> WorkerResponse.Rejected(readString stream)
        | 2uy -> WorkerResponse.Unresolvable(readString stream)
        | other -> raise (InvalidDataException $"response frame carries unknown status {other}")