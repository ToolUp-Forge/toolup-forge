// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 850 — the runtime the Elmish extractions need, for the Fable host.
///
/// The F# backend of the F* extractor ships no runtime library; the
/// `Prims` beside this directory (`../Prims.fs`) is the one the .NET
/// oracle compiles against, and its `Prims.int` is `BigInteger` because
/// the decoder model genuinely needs the `uint64` ceiling. The Elmish
/// models need nothing of the kind — every integer in `ElmishRing.fst`
/// and `ElmishLoop.fst` is an index, a length, a count or the fuel,
/// bounded by the ring's capacity or by the differential's script — and
/// `BigInteger` is not something the browser's copy of the runtime should
/// carry. This is the same shim over machine integers: the same five
/// names the extractions reference, with `int` and `nat` as
/// `System.Int32`, which Fable transpiles to a plain JavaScript number.
///
/// **Which extractions it serves.** The ring's (`../ElmishRing.fs`, Phase
/// 850) and, since Phase 884, the dispatch loop's (`../ElmishLoop.fs`,
/// which opens the ring and adds counters — `started`, `held`, `renders`
/// — and the fuel; Phase 900 added nothing it references). NOT the
/// subscription diff's (`../ElmishSub.fs`): that extraction is compiled
/// on .NET only, and the Fable host holds the transpiled `Sub.fs` to the
/// verdicts the .NET host recorded for it (`tests/elmish-proof-corpus/`)
/// rather than running the model live. Compiling it here would need
/// nothing this shim lacks; it is not done because nothing runs it.
///
/// **This is a stated assumption, not a proof step.** The theorems in
/// `ElmishRing.fst` and `ElmishLoop.fst` are about mathematical integers;
/// this shim holds them exactly as long as no index, length or count
/// exceeds `Int32.MaxValue`, which no ring or loop in any client ever
/// approaches (the grow step doubles a backing array of a few dozen
/// slots; the counters count starts and paints). It is recorded on the
/// claims ladder as such (`proofs/README.md`, the Phase 850 section), and
/// the Fable host holds the two shims to each other: the corpus the .NET
/// host wrote with the `BigInteger` shim must be what this shim computes
/// on the same ring sequences, and the loop model's verdicts over the
/// loop campaign are pinned by fingerprint on both hosts.
///
/// Compiled by `ToolUp.Remoting.Proofs.Oracle.Fable.fsproj` beside this
/// file, ahead of the normalised `../ElmishRing.fs` and
/// `../ElmishLoop.fs`; built on .NET by `proofs/check.ps1` (the same
/// source compiles on both hosts) and by Fable through
/// `ToolUp.AI.Client.Tests`.
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