// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 817 — the cross-host contract of the `ProcessedData` summary
/// envelope, pinned as data.
///
/// The envelope's JSON is written on the server by `ProcessedDataCodec`
/// (System.Text.Json + `FableConverters`) and read in the browser by
/// `DataTypeDisplay.typed` (`Fable.SimpleJson`). Neither host can run the
/// other's codec, so the agreement is held the way the wire corpus holds
/// its bytes: ONE literal, asserted from both sides. The .NET pack holds
/// the encoder to this text; the Fable pack holds the decoder to the
/// value. Compiled into both packs (the `WireCorpus.fs` precedent), so
/// the sample type and the literal cannot drift apart.
///
/// The sample carries the three shapes a module summary plausibly has and
/// the two converter sets could plausibly disagree on: a primitive, an
/// option (`Some` is the bare value, `None` is `null`) and a list.
module ToolUp.Platform.Tests.Remoting.ProcessedDataEnvelopeFixture

type SampleSummary = {
    Rows: int
    Header: string
    Note: string option
    Tags: string list
}

let sample: SampleSummary = {
    Rows = 4
    Header = "header_x,header_y"
    Note = Some "first upload"
    Tags = [ "csv"; "ingested" ]
}

/// A second value, with the option absent — the arm that reads as `null`.
let sampleWithoutNote: SampleSummary = { sample with Note = None; Tags = [] }

/// What `ProcessedDataCodec.encode sample` writes, byte for byte.
[<Literal>]
let SamplePayload =
    """{"Rows":4,"Header":"header_x,header_y","Note":"first upload","Tags":["csv","ingested"]}"""

/// What `ProcessedDataCodec.encode sampleWithoutNote` writes.
[<Literal>]
let SampleWithoutNotePayload =
    """{"Rows":4,"Header":"header_x,header_y","Note":null,"Tags":[]}"""

/// The envelope as the client sees it. `TypeName` is a routing tag the
/// decode never compares, so the .NET spelling is fine on both hosts.
let envelope (payload: string) : ProcessedDataTypes.ProcessedData = {
    TypeName = "ToolUp.Platform.Tests.Remoting.ProcessedDataEnvelopeFixture+SampleSummary"
    Payload = payload
}