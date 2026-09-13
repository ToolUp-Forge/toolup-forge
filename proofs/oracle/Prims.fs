// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 787 — the entire runtime the extracted model needs.
///
/// **The F# backend of the F* extractor ships no runtime library.** The
/// OCaml backend has one; the F# backend emits code referencing a
/// `Prims` module and leaves producing it to the consumer. This is that
/// module, and its size is the point: nine names, no dependency beyond
/// `System.Numerics`, and nothing that could make the oracle agree with
/// production for a reason other than the model being right.
///
/// The nine are not chosen — they are exactly what
/// `proofs/RemotingDecode.fst`'s extraction references, and
/// `proofs/check.ps1` re-derives that set on every run: the extraction
/// is byte-compared against the committed `RemotingDecode.fs`, so a
/// model change that reached for a tenth name would fail the diff
/// rather than silently compile against a shim someone widened.
///
/// `Prims.int` is `BigInteger` rather than `int64` because the model
/// genuinely needs the range: `as_uint64`'s ceiling is
/// 18446744073709551615, which no signed 64-bit carrier holds. That is
/// also why the model can state the width rule honestly — it reasons
/// about the mathematical value, and the host's cast is applied only
/// where the rule has licensed it.
module Prims

open System
open System.Globalization
open System.Numerics

type int = BigInteger
type nat = BigInteger
type bool = Boolean
type string = String
type list<'a> = Microsoft.FSharp.Collections.List<'a>

/// Integer literals reach the extraction as decimal text.
let parse_int (text: String) : int =
    BigInteger.Parse(text, NumberFormatInfo.InvariantInfo)

/// Structural equality. Used on integers and on strings.
let op_Equals (left: 'a) (right: 'a) : bool = left = right

let strcat (left: String) (right: String) : String = left + right

/// Invariant decimal rendering — the same digits `Int64.ToString()` and
/// `UInt64.ToString()` produce for every value either can hold, which is
/// what lets the differential compare refusal messages verbatim.
let string_of_int (value: int) : String =
    value.ToString(NumberFormatInfo.InvariantInfo)