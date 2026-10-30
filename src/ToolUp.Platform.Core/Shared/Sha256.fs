// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 1089 — one digest helper and one canonical frame ─────────
//
// Every content id in the grounding plane is a SHA-256 over a canonical
// field list. Before this file each recipe joined its fields its own way
// (`"\n"`, `"|"`, `sprintf`), and each carried its own private hex helper.
// A join on a delimiter is not injective — `["a|b"; "c"]` and
// `["a"; "b|c"]` join to the same text — so two distinct identities could
// share an id. The frame below is the length-prefixed framing
// `ProvenanceFraming.frame` already gives the deployment evidence chain:
// each field is written `<length>:<field>`, the length counted in UTF-16
// code units, so no field value can be re-cut into a different sequence.
//
// The frame's TEXT is pure string building and compiles everywhere, so a
// Fable host can reproduce the bytes an id is taken over. The hash is
// server-side: `System.Security.Cryptography` does not compile under
// Fable.

#if !FABLE_COMPILER
/// The one SHA-256 helper — lowercase hex, the estate's digest spelling.
[<RequireQualifiedAccess>]
module Sha256 =

    /// Lowercase-hex SHA-256 of `bytes`.
    let hex (bytes: byte[]) : string =
        System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData bytes)

    /// Lowercase-hex SHA-256 of the UTF-8 bytes of `text`.
    let hexOfUtf8 (text: string) : string =
        hex (System.Text.Encoding.UTF8.GetBytes text)
#endif

/// The length-prefixed canonical frame every content id is taken over.
///
/// `text ["ab"; "c"]` is `"2:ab1:c"`; `text ["a"; "bc"]` is `"1:a2:bc"`.
/// A field list maps to exactly one text and the text to exactly one
/// field list, so a digest over it names one identity. A `null` field
/// frames as the empty string, as `ProvenanceFraming.frame` does.
[<RequireQualifiedAccess>]
module CanonicalFrame =

    /// The framed text of `fields`, in order.
    let text (fields: string list) : string =
        let builder = System.Text.StringBuilder()
        fields |> List.iter (ProvenanceFraming.frame builder)
        builder.ToString()

#if !FABLE_COMPILER
    /// The UTF-8 bytes of the framed text — what a digest covers.
    let frame (fields: string list) : byte[] =
        System.Text.Encoding.UTF8.GetBytes(text fields)

    /// The content id of `fields`: lowercase-hex SHA-256 over the frame.
    let digest (fields: string list) : string = Sha256.hex (frame fields)
#endif