// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 687 — the contract-pack bindings that make the two seams this
/// phase touches PROVEN replaceable (GP 12: a portable interface is not
/// proven until a second implementation runs the same pack):
///
///   * `ICompanionIsolationContract` over both implementations of the
///     seam — in-process and out-of-process — so the promise that a
///     caller cannot tell them apart on a well-behaved entry point is a
///     law, not a description;
///   * `IDerivativeRendererContract` over the isolated Skia renderer,
///     whose in-process twin binds the same pack from the Platform pack.
module ToolUp.Companions.Isolation.Tests.ContractBindings

open System
open Expecto
open ToolUp.AssetStore
open ToolUp.Companions.Isolation
open ToolUp.Platform.Tests.Contracts

let private generous = {
    Timeout = TimeSpan.FromSeconds 60.0
    MemoryCap = Some(512L * 1024L * 1024L)
}

let tests =
    testList "Phase 687 — contract-pack bindings" [
        ICompanionIsolationContract.tests "InProcessIsolation" (fun () -> InProcessIsolation.instance)
        ICompanionIsolationContract.tests "ProcessIsolation" (fun () -> ProcessIsolation.create generous)
        IDerivativeRendererContract.tests "IsolatedSkiaDerivativeRenderer (through the worker)" (fun () ->
            IsolatedSkiaDerivativeRenderer.create (ProcessIsolation.create generous))
    ]