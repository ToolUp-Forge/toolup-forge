// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ResilientSecretStore

open ToolUp.Platform.Secrets
open ToolUp.Platform.TransientFault

// ─── Phase 176 — ISecretStore transient-fault decorator ──────────────
//
// The `ResilientBlobStorage` shape over `ISecretStore`. Every method is
// routed through the same shared `TransientFaultRunner`, so a cloud
// secret-manager companion (Key Vault / Secrets Manager / Secret Manager
// / Vault) inherits one consistent retry/breaker/timeout policy instead
// of whatever its vendor SDK happens to default to.
//
// The interface's idempotency + deterministic-error contracts are
// preserved by construction: a `DeleteSecret` of an absent key returns
// `Ok` (a value — never retried), and a `SetSecret` that returns
// `Error "read-only"` is a deterministic domain outcome (also a value),
// so the retry classifier — which only ever sees *thrown exceptions* —
// cannot retry it. Only genuinely-transient thrown faults (transient
// network/timeout exceptions from the underlying companion) retry.

/// `ISecretStore` decorator applying a `TransientFaultPolicy` to every
/// method. With `TransientFaultPolicy.identity` it is a pure pass-through
/// (GP 11/13).
type ResilientSecretStore(inner: ISecretStore, policy: TransientFaultPolicy) =
    let runner = TransientFaultRunner policy

    interface ISecretStore with
        member _.GetSecret(scopeId, key) =
            runner.Run(fun () -> inner.GetSecret(scopeId, key))

        member _.SetSecret(scopeId, key, value) =
            runner.Run(fun () -> inner.SetSecret(scopeId, key, value))

        member _.DeleteSecret(scopeId, key) =
            runner.Run(fun () -> inner.DeleteSecret(scopeId, key))

        member _.ListKeys(scopeId) =
            runner.Run(fun () -> inner.ListKeys scopeId)

    /// Phase 457 — a decorator changes retry behaviour, not what the inner
    /// store does to the bytes, so the posture is the inner store's. It is
    /// DELEGATED rather than asserted: a wrapper that answered for itself
    /// would report a KMS-backed companion as plaintext the moment a
    /// deployment opted into resilience, which is a refusal manufactured by
    /// a retry policy.
    ///
    /// An inner store that declares nothing yields `UnknownAtRest` naming
    /// the wrapper, not `PlaintextAtRest` — the decorator has no more
    /// standing to make that claim than the reader does.
    interface ISecretStoreAtRestPosture with
        member _.AtRestPosture =
            match box inner with
            | :? ISecretStoreAtRestPosture as declared -> declared.AtRestPosture
            | _ ->
                UnknownAtRest
                    "the store wrapped by ResilientSecretStore declares no at-rest posture (ISecretStoreAtRestPosture)"

/// Apply the resilience decorator when the deployment opted in.
/// `NoResilience` returns `inner` un-wrapped — byte-for-byte identical to
/// the bare store (GP 13). Mirrors `applyStorageResilience`.
let applySecretResilience (mode: ResilienceMode) (inner: ISecretStore) : ISecretStore =
    match mode with
    | NoResilience -> inner
    | WithResiliencePolicy policy -> ResilientSecretStore(inner, policy) :> ISecretStore