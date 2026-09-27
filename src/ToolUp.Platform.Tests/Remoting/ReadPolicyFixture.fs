// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 854 — one API record declared both ways, compiled by BOTH packs:
/// the attributes on the record (what the .NET host reads with
/// `ReadPolicies.ofAttributes`) and the same declaration as data (what a
/// Fable client registers, since its reflection carries no attributes).
/// The .NET pack pins the two equal; the Fable pack registers the data and
/// drives the real proxy with it. This file is the shape a consumer copies
/// to keep its own two declarations from drifting.
module ToolUp.Platform.Tests.Remoting.ReadPolicyFixture

open ToolUp.Platform
open ToolUp.Remoting.Client

type ReadCatalogApi = {
    /// Shared while in flight, and served for a minute.
    [<Cacheable(60)>]
    GetCount: string -> Async<int>
    /// Shared while in flight, never cached.
    [<Cacheable(0)>]
    GetShared: string -> Async<int>
    /// Undeclared: never shared, never cached.
    GetUndeclared: string -> Async<int>
    /// A mutation: a success makes `GetCount` stale.
    [<Invalidates("GetCount")>]
    Bump: string -> Async<int>
}

/// The declaration a Fable client registers for `ReadCatalogApi`.
let declarations: (string * ReadPolicy) list = [
    "GetCount", ReadPolicy.cacheable 60
    "GetShared", ReadPolicy.cacheable 0
    "Bump", ReadPolicy.invalidates [ "GetCount" ]
]