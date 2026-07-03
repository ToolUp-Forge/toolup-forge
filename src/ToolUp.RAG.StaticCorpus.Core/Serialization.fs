// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.RAG.StaticCorpus

// ─── Static-corpus binary (de)serialisation ──────────────────────
//
// MessagePack binary format for `StaticCorpus`. Embeddings dominate the
// on-disk size (k ≈ 5,000 chunks × d ≈ 1,536 float32 ≈ tens of MB); JSON is
// ~3× larger and slow to parse, so the index is a compact MessagePack blob.
//
// Written with the low-level `MessagePackWriter` / `MessagePackReader`
// rather than attribute-annotated DTOs so the byte layout is fully under our
// control — a determinism requirement (Phase 63.G: pack twice over an
// unchanged corpus ⇒ byte-identical `.scidx`). The field order is fixed and
// `Metadata` maps are written in sorted-key order, so nothing depends on
// hash-map enumeration order. No reflection, no resolver, no vendor
// attributes on the domain types.
//
// .NET-only (`#if !FABLE_COMPILER`): MessagePack-CSharp is not a Fable
// library. A Fable-side loader would need a Fable-native decoder — deferred;
// the `Types` above already compile Fable-side so a client can model a
// corpus it received over the wire.

#if !FABLE_COMPILER
open System
open System.Buffers
open MessagePack

module Serialization =

    /// Wire-format version. Bumped only on a breaking layout change so a
    /// reader can refuse an incompatible blob loudly rather than mis-parse.
    [<Literal>]
    let FormatVersion = 1

    // ── Write ──────────────────────────────────────────────────────

    let private writeChunk (writer: byref<MessagePackWriter>) (c: DocChunk) =
        // Fixed 7-element array per chunk.
        writer.WriteArrayHeader 7
        writer.Write c.Id
        writer.Write c.Source

        let headings = List.toArray c.HeadingPath
        writer.WriteArrayHeader headings.Length

        for h in headings do
            writer.Write h

        writer.Write c.Body

        writer.WriteArrayHeader c.Embedding.Length

        for e in c.Embedding do
            writer.Write e

        // Deterministic metadata: sorted by key, written as a map.
        let md = c.Metadata |> Map.toArray |> Array.sortBy fst
        writer.WriteMapHeader md.Length

        for (k, v) in md do
            writer.Write k
            writer.Write v

    /// Serialise a corpus to a MessagePack byte array. Deterministic: the
    /// same corpus value always produces byte-identical output.
    let serialize (corpus: StaticCorpus) : byte[] =
        let buffer = ArrayBufferWriter<byte>()
        let mutable writer = MessagePackWriter buffer

        // Top-level 6-element array: version + 4 scalars + chunk array.
        writer.WriteArrayHeader 6
        writer.Write FormatVersion
        writer.Write corpus.EmbeddingModel
        writer.Write corpus.EmbeddingDimensions
        writer.Write corpus.BuiltUtc.Ticks
        writer.Write corpus.PackerVersion

        writer.WriteArrayHeader corpus.Chunks.Length

        for c in corpus.Chunks do
            writeChunk &writer c

        writer.Flush()
        buffer.WrittenSpan.ToArray()

    // ── Read ───────────────────────────────────────────────────────

    let private readChunk (reader: byref<MessagePackReader>) : DocChunk =
        let n = reader.ReadArrayHeader()

        if n <> 7 then
            failwithf "StaticCorpus: malformed chunk (expected 7 fields, got %d)" n

        let id = reader.ReadString()
        let source = reader.ReadString()

        // Plain `for` loops (not list comprehensions / `Array.init`) — a byref
        // reader cannot be captured by a closure (FS0406).
        let headingCount = reader.ReadArrayHeader()
        let headings = ResizeArray<string> headingCount

        for _ in 1..headingCount do
            headings.Add(reader.ReadString())

        let headingPath = List.ofSeq headings

        let body = reader.ReadString()

        let embLen = reader.ReadArrayHeader()
        let embedding = Array.zeroCreate<float32> embLen

        for i in 0 .. embLen - 1 do
            embedding[i] <- reader.ReadSingle()

        let mdCount = reader.ReadMapHeader()
        let mutable metadata = Map.empty

        for _ in 1..mdCount do
            let k = reader.ReadString()
            let v = reader.ReadString()
            metadata <- Map.add k v metadata

        {
            Id = id
            Source = source
            HeadingPath = headingPath
            Body = body
            Embedding = embedding
            Metadata = metadata
        }

    /// Deserialise a corpus from a MessagePack byte array produced by
    /// `serialize`. Raises on a truncated blob or an unrecognised
    /// `FormatVersion`.
    let deserialize (bytes: byte[]) : StaticCorpus =
        let mutable reader = MessagePackReader(ReadOnlyMemory bytes)

        let top = reader.ReadArrayHeader()

        if top <> 6 then
            failwithf "StaticCorpus: malformed header (expected 6 fields, got %d)" top

        let version = reader.ReadInt32()

        if version <> FormatVersion then
            failwithf
                "StaticCorpus: unsupported format version %d (this build reads version %d — repack with a matching packer)"
                version
                FormatVersion

        let embeddingModel = reader.ReadString()
        let embeddingDimensions = reader.ReadInt32()
        let builtUtcTicks = reader.ReadInt64()
        let packerVersion = reader.ReadString()

        let chunkCount = reader.ReadArrayHeader()
        let chunks = Array.zeroCreate<DocChunk> chunkCount

        for i in 0 .. chunkCount - 1 do
            chunks[i] <- readChunk &reader

        {
            Chunks = chunks
            EmbeddingModel = embeddingModel
            EmbeddingDimensions = embeddingDimensions
            BuiltUtc = DateTime(builtUtcTicks, DateTimeKind.Utc)
            PackerVersion = packerVersion
        }
#endif