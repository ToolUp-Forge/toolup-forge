// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 955 — the Fable host's stand-in for Custard's support library.
///
/// Custard's F# backend writes a support library, `FStarCustard.fs`,
/// beside every module it extracts, and every extracted module opens it
/// (`../custard/FStarCustard.fs` is the one it wrote for the array ring;
/// the .NET oracle compiles it as generated). Fable refuses that file:
/// it holds `System.Int128` / `System.UInt128` arithmetic, console
/// writers, `exit` and a strict UTF-8 decoder, none of which Fable
/// carries, and it is one module, so the refusal is of the whole file
/// whatever the extraction in hand uses.
///
/// The array ring (`../custard/ElmishRingArray.fs`) uses NOTHING from it.
/// Its extraction is arithmetic on `uint64`, an array and `ref` cells,
/// all the target's own. So on this host the module it opens is this
/// one, and it is empty on purpose: an extraction that came to need a
/// name from the support library would fail to compile here, naming the
/// name, rather than run against a second implementation of it that
/// nothing holds to the first.
///
/// That is the difference from `Prims.fs` beside this file, which
/// re-implements five names over machine integers and says so on the
/// claims ladder. Nothing is re-implemented here.
///
/// Compiled by `ToolUp.Remoting.Proofs.Oracle.Fable.fsproj`, ahead of the
/// generated `../custard/ElmishRingArray.fs`.
module FStarCustard

/// F# does not accept a module with no declarations; this is the one.
let supportLibraryNamesCarried = 0