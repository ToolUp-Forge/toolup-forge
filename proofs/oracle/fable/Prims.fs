// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 850 — the runtime the Elmish extractions need, for the Fable host.
///
/// The F# backend of the F* extractor ships no runtime library; the
/// `Prims` beside this directory (`../Prims.fs`) is the one the .NET
/// oracle compiles against, and its `Prims.int` is `BigInteger` because
/// the decoder model genuinely needs the `uint64` ceiling. The Elmish
/// models need nothing of the kind — every integer in `ElmishRing.fst` is
/// an index or a length, bounded by the ring's capacity — and `BigInteger`
/// is not something the browser's copy of the runtime should carry. This
/// is the same shim over machine integers: the same five names the ring
/// extraction references, with `int` and `nat` as `System.Int32`, which
/// Fable transpiles to a plain JavaScript number.
///
/// **This is a stated assumption, not a proof step.** The theorems in
/// `ElmishRing.fst` are about mathematical integers; this shim holds them
/// exactly as long as no index or length exceeds `Int32.MaxValue`, which
/// no ring in any client ever approaches (the grow step doubles a
/// backing array of a few dozen slots). It is recorded on the claims
/// ladder as such (`proofs/README.md`, the Phase 850 section), and the
/// Fable host's first case holds the two shims to each other: the corpus
/// the .NET host wrote with the `BigInteger` shim must be what this shim
/// computes on the same sequences.
///
/// Compiled by `ToolUp.Remoting.Proofs.Oracle.Fable.fsproj` beside this
/// file, ahead of the normalised `../ElmishRing.fs`; built on .NET by
/// `proofs/check.ps1` (the same source compiles on both hosts) and by
/// Fable through `ToolUp.AI.Client.Tests`.
module Prims

type int = System.Int32
type nat = System.Int32
type bool = System.Boolean
type string = System.String
type list<'a> = Microsoft.FSharp.Collections.List<'a>

/// Integer literals reach the extraction as decimal text.
let parse_int (text: System.String) : int = System.Int32.Parse text

/// Structural equality. Used on integers and on strings.
let op_Equals (left: 'a) (right: 'a) : bool = left = right

let strcat (left: System.String) (right: System.String) : System.String = left + right

let string_of_int (value: int) : System.String = value.ToString()