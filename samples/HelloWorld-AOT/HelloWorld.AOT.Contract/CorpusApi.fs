// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace HelloWorld.AOT.Contract

open System
open ToolUp.Platform.Tests.Remoting.WireCorpus

// =============================================================================
// Phase 804.B - the API record the generator reads
// =============================================================================
//
// One method per wire TYPE the remoting corpus pins, each an echo
// (`'T -> Async<'T>`), so that a single API record carries every corpus
// shape on BOTH sides the generator emits for: the return side (decoders,
// the client's binary-response path) and the argument side (the typed
// server argument-parse table). The corpus declarations themselves are
// compiled in from the SDK's own test project - one declaration, two
// encodings, no transcription - which is why this contract project links
// `WireCorpus.fs` rather than copying its types.
//
// This is the consumer shape the generator's build-time target expects:
// the project whose assembly carries the API records declares what to emit,
// and the emitted source lands in the sibling that compiles it.

type ICorpusApi = {
    Bool: bool -> Async<bool>
    Int32: int -> Async<int>
    String: string -> Async<string>
    Char: char -> Async<char>
    Byte: byte -> Async<byte>
    SByte: sbyte -> Async<sbyte>
    Int16: int16 -> Async<int16>
    UInt16: uint16 -> Async<uint16>
    UInt32: uint32 -> Async<uint32>
    Int64: int64 -> Async<int64>
    UInt64: uint64 -> Async<uint64>
    Float: float -> Async<float>
    Float32: float32 -> Async<float32>
    Decimal: decimal -> Async<decimal>
    DateTime: DateTime -> Async<DateTime>
    DateTimeOffset: DateTimeOffset -> Async<DateTimeOffset>
    TimeSpan: TimeSpan -> Async<TimeSpan>
    DateOnly: DateOnly -> Async<DateOnly>
    TimeOnly: TimeOnly -> Async<TimeOnly>
    Guid: Guid -> Async<Guid>
    Bytes: byte[] -> Async<byte[]>
    OptionInt: int option -> Async<int option>
    OptionString: string option -> Async<string option>
    OptionAddress: Address option -> Async<Address option>
    ListInt: int list -> Async<int list>
    ListAddress: Address list -> Async<Address list>
    ArrayString: string[] -> Async<string[]>
    MapStringInt: Map<string, int> -> Async<Map<string, int>>
    MapIntString: Map<int, string> -> Async<Map<int, string>>
    SetString: Set<string> -> Async<Set<string>>
    SetInt: Set<int> -> Async<Set<int>>
    TuplePair: (int * string) -> Async<int * string>
    TupleTriple: (int * string * bool) -> Async<int * string * bool>
    Priority: Priority -> Async<Priority>
    Outcome: Outcome -> Async<Outcome>
    Address: Address -> Async<Address>
    Consignment: Consignment -> Async<Consignment>
    Customer: Customer -> Async<Customer>
    Envelope: ApiEnvelope -> Async<ApiEnvelope>
}