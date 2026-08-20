// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open System.Text.Json.Nodes
open System.Threading
open ToolUp.Platform.Secrets

/// Default `ISigningKeyLedger`, persisting the append-only event
/// sequence into the deployment's own `ISecretStore` — the same
/// substrate the signing keys themselves live in, under the reserved
/// `_platform` scope. No new substrate dependency: a deployment that can
/// hold a signing key can hold the record of what it did with it.
///
/// The ledger is stored as one JSON array under a single key rather than
/// one key per event, because the read path always wants the whole
/// history and `ISecretStore` makes no ordering promise across keys.
///
/// **Concurrency.** Appends are read-modify-write, so two writers racing
/// on the same store can lose one event. An in-process semaphore
/// serialises appends within a deployment, which covers the ordinary
/// case (lifecycle transitions are operator-paced, not request-paced).
/// Across processes there is no compare-and-swap on `ISecretStore` to
/// build on; a deployment that rotates from several hosts at once should
/// implement `ISigningKeyLedger` over a store that has one. This is
/// stated rather than hidden because a silently-dropped revocation is
/// the one failure this type must not have.
type SecretStoreSigningKeyLedger(secrets: ISecretStore) =

    /// Reserved `ISecretStore` key holding the ledger, under the same
    /// `_platform` scope as the signing keys.
    static let ledgerKey = "signing/key-ledger"

    // Documented mutable exception (GP 5): instance-scoped append lock.
    let gate = new SemaphoreSlim(1, 1)

    let toNode (e: SigningKeyEvent) : JsonObject =
        let o = JsonObject()
        o["keyId"] <- JsonValue.Create e.KeyId
        o["kind"] <- JsonValue.Create(SigningKeyEventKind.name e.Kind)
        o["at"] <- JsonValue.Create(e.At.ToString "O")
        o["actor"] <- JsonValue.Create e.Actor

        match e.Reason with
        | Some r -> o["reason"] <- JsonValue.Create r
        | None -> ()

        o

    let ofNode (n: JsonNode) : SigningKeyEvent option =
        try
            match SigningKeyEventKind.tryParse (n["kind"].GetValue<string>()) with
            | None -> None
            | Some kind ->
                Some {
                    KeyId = n["keyId"].GetValue<string>()
                    Kind = kind
                    At = DateTimeOffset.Parse(n["at"].GetValue<string>())
                    Actor = n["actor"].GetValue<string>()
                    Reason =
                        match n["reason"] with
                        | null -> None
                        | r -> Some(r.GetValue<string>())
                }
        with _ ->
            None

    let readEvents () : Async<SigningKeyEvent list> = async {
        let! raw = secrets.GetSecret(SigningKeyMaterial.SecretScope, ledgerKey)

        match raw with
        | None -> return []
        | Some json ->
            try
                match JsonNode.Parse json with
                | :? JsonArray as arr -> return arr |> Seq.choose ofNode |> List.ofSeq
                | _ -> return []
            with _ ->
                // An unparseable ledger blob is reported as empty rather
                // than thrown: the caller's next `Record` re-seeds it,
                // and a verification path must never fail closed on a
                // malformed record it can neither read nor repair.
                return []
    }

    /// Reserved `ISecretStore` key the ledger is persisted under.
    static member LedgerKey = ledgerKey

    interface ISigningKeyLedger with

        member _.Record(event: SigningKeyEvent) : Async<Result<unit, string>> = async {
            do! gate.WaitAsync() |> Async.AwaitTask

            try
                let! existing = readEvents ()
                let arr = JsonArray()

                for e in existing do
                    arr.Add(toNode e)

                arr.Add(toNode event)

                return! secrets.SetSecret(SigningKeyMaterial.SecretScope, ledgerKey, arr.ToJsonString())
            finally
                gate.Release() |> ignore
        }

        member _.History() : Async<SigningKeyHistory> = async {
            let! events = readEvents ()
            return SigningKeyHistory.ofEvents events
        }

module SecretStoreSigningKeyLedger =
    /// Construct the default `ISecretStore`-backed signing-key ledger.
    let create (secrets: ISecretStore) : ISigningKeyLedger =
        SecretStoreSigningKeyLedger(secrets) :> ISigningKeyLedger

/// An `ISigningKeyLedger` that records nothing and reports an empty
/// history — the value an application signer composed WITHOUT a ledger
/// behaves as. Verification then rests on the signature bytes alone,
/// exactly as it did before this surface existed (GP 11): no key is
/// revoked, because no revocation has been recorded.
type EmptySigningKeyLedger() =
    interface ISigningKeyLedger with
        member _.Record(_: SigningKeyEvent) = async { return Ok() }
        member _.History() = async { return { Entries = [] } }

module EmptySigningKeyLedger =
    let create () : ISigningKeyLedger =
        EmptySigningKeyLedger() :> ISigningKeyLedger