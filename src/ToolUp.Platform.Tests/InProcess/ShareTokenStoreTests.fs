module ToolUp.Platform.Tests.InProcess.ShareTokenStoreTests

open System
open System.Collections.Concurrent
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

// ─── BlobShareTokenStore — IShareTokenStore contract binding ─────────
//
// Binds the `IShareTokenStore` contract pack to the Phase 21b default
// blob-backed impl over `LocalFileStorage` rooted in a fresh temp
// directory per factory call. The signing key is auto-generated and
// persisted into an `InMemorySecretStore` defined locally — no
// dependency on `FileSecretStore` to keep the test setup minimal.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Trivial in-memory `ISecretStore` for the share-token signing key.
/// Production deployments use `FileSecretStore` / `EncryptedSecretStore`
/// / cloud-vault companions.
type InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let tests =
    let factory () =
        let root =
            Path.Combine(Path.GetTempPath(), "toolup-sharetoken-tests-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore
        let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
        let secrets = InMemorySecretStore() :> ISecretStore

        let store = ShareTokenStore.create storage secrets None silentLogger

        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    IShareTokenStoreContract.tests "BlobShareTokenStore" factory


// ─── Read path: absence vs storage failure ────────────────────────
//
// `readClaim` used to collapse EVERY failed `storage.Download` into
// `ShareTokenError.NotFound`, so a transient IO error, a permissions
// problem or a misconfigured container reached the caller as "no such
// token" — and `MarkUsed` / `Revoke`, which both route through that
// read and short-circuit on its `Error`, named the wrong subsystem.
// It now probes `Exists` on the error path, so a blob that is THERE
// and unreadable reports `StorageFailed` carrying the store's own
// message.
//
// The pack is built to discriminate in both directions. The first
// three cases arm a read failure on a blob that genuinely exists, so
// the pre-fix implementation fails them; the last pins that a
// genuinely absent blob is still `NotFound`, which an implementation
// that simply renamed every error to `StorageFailed` would fail.
// Neither case passes on its own.

/// Fails `Download` for blobs matching `shouldFail`, passing every
/// other operation — `Exists` included — through to `inner`. The
/// passthrough is the whole point: the blob really is present, which
/// is the condition `readClaim` now detects.
type private ReadFailingBlobStorage(inner: IBlobStorage, shouldFail: string -> bool, message: string) =
    interface IBlobStorage with
        // Phase 741 — no bounded multi-part commit primitive here; callers assemble through memory.
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Download(container, blobName) =
            if shouldFail blobName then
                async { return Error message }
            else
                inner.Download(container, blobName)

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

let private readFailureMessage = "simulated transient read failure"

/// A fresh `LocalFileStorage` + secret store, shared between the
/// issuing store and the faulted one so both resolve the same signing
/// key and a token issued by the first validates against the second.
let private harness () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-sharetoken-readfail-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let backing = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let secrets = InMemorySecretStore() :> ISecretStore
    backing, secrets

let private storeOver (storage: IBlobStorage) (secrets: ISecretStore) =
    ShareTokenStore.create storage secrets None silentLogger

let private request scopeId : ShareTokenIssueRequest = {
    ScopeId = scopeId
    ResourceKind = "report"
    ResourceId = "r-1"
    AttributedHandle = None
    IssuedBy = "alice"
    ExpiresAt = None
    UseLimit = Some(Some 5)
    RateLimit = None
}

let private claimBlob (claim: ShareTokenClaim) =
    $"share-tokens/{claim.ScopeId}/{claim.TokenId}.json"

/// Issue a token against the unfaulted store, then hand back a store
/// whose reads of that token's claim blob fail while `Exists` still
/// answers truthfully.
let private issueThenFaultRead scopeId = async {
    let backing, secrets = harness ()
    let issuing = storeOver backing secrets
    let! issued = issuing.Issue(request scopeId)

    let token =
        match issued with
        | Ok t -> t
        | Error e -> failtestf "Issue should succeed: %A" e

    let blob = claimBlob token.Claim

    let faulted =
        ReadFailingBlobStorage(backing, (fun n -> n = blob), readFailureMessage) :> IBlobStorage

    return token, storeOver faulted secrets
}

let readPathTests =
    testList "BlobShareTokenStore — read path names absence apart from failure" [

        testCaseAsync "Validate reports StorageFailed, not NotFound, when the claim blob is present but unreadable"
        <| async {
            let! token, store = issueThenFaultRead "team-validate-fail"
            let! result = store.Validate token.Token

            match result with
            | Error(ShareTokenError.StorageFailed msg) ->
                Expect.stringContains msg readFailureMessage "the store's own error is carried, not discarded"
            | other -> failtestf "expected StorageFailed, got %A" other
        }

        testCaseAsync "MarkUsed reports StorageFailed, not NotFound, on an unreadable claim blob"
        <| async {
            let! token, store = issueThenFaultRead "team-markused-fail"
            let! result = store.MarkUsed(token.Claim.ScopeId, token.Claim.TokenId)

            match result with
            | Error(ShareTokenError.StorageFailed msg) ->
                Expect.stringContains msg readFailureMessage "the store's own error is carried, not discarded"
            | other -> failtestf "expected StorageFailed, got %A" other
        }

        testCaseAsync "Revoke reports StorageFailed, not NotFound, on an unreadable claim blob"
        <| async {
            let! token, store = issueThenFaultRead "team-revoke-fail"
            let! result = store.Revoke(token.Claim.ScopeId, token.Claim.TokenId, "alice")

            match result with
            | Error(ShareTokenError.StorageFailed msg) ->
                Expect.stringContains msg readFailureMessage "the store's own error is carried, not discarded"
            | other -> failtestf "expected StorageFailed, got %A" other
        }

        // The other direction. Without this, renaming every read error
        // to `StorageFailed` would pass the three cases above — and
        // would be a different defect in the mirror position.
        testCaseAsync "a genuinely absent claim blob is still NotFound"
        <| async {
            let backing, secrets = harness ()
            let store = storeOver backing secrets
            let! issued = store.Issue(request "team-absent")

            let token =
                match issued with
                | Ok t -> t
                | Error e -> failtestf "Issue should succeed: %A" e

            let! deleted = backing.Delete(ShareTokenStore.platformContainer, claimBlob token.Claim)

            match deleted with
            | Ok() -> ()
            | Error e -> failtestf "claim blob delete should succeed: %s" e

            let! validated = store.Validate token.Token

            match validated with
            | Error ShareTokenError.NotFound -> ()
            | other -> failtestf "expected NotFound, got %A" other

            let! marked = store.MarkUsed(token.Claim.ScopeId, token.Claim.TokenId)

            match marked with
            | Error ShareTokenError.NotFound -> ()
            | other -> failtestf "expected NotFound from MarkUsed, got %A" other
        }
    ]