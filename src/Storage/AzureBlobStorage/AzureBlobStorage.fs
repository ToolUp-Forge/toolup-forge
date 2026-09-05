module ToolUp.Storage.AzureBlobStorage

open System
open System.IO
open System.Runtime.ExceptionServices
open Azure
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
// Phase 741 — `GetBlockBlobClient` (the block-list compose primitive)
// is an extension on `BlobContainerClient` in the Specialized namespace.
open Azure.Storage.Blobs.Specialized
open Azure.Storage.Sas
open ToolUp.Platform.BlobStorage

// ─── Configuration ───────────────────────────────────────────────────

/// Configuration for `AzureBlobStorage`. Construct from a connection
/// string read out of `TOOLUP_AZURE_STORAGE_CONNECTION_STRING` (or
/// wherever the deployment keeps it) and choose a root container name.
///
/// Why a single root container: Azure Blob containers must follow a
/// naming convention — 3-63 characters, lowercase letters, digits,
/// hyphens, start with a letter or digit — which rules out ToolUp's
/// `_platform` scope and risks collisions with user-supplied scope
/// identifiers. A single Azure container holds every ToolUp scope as
/// a blob-name prefix (`{toolupContainer}/{blobName}`), mapping cleanly
/// onto Azure portal's virtual-directory view without leaking
/// naming-constraint complexity to callers. Also stays well under
/// Azure's per-account container quota at any realistic scale.
type AzureBlobStorageConfig = {
    /// Azure Storage connection string. `DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;`
    /// or the shorthand `UseDevelopmentStorage=true` for Azurite.
    ConnectionString: string
    /// Single Azure container that holds all ToolUp scopes. Defaults
    /// to `"toolup"`. Must satisfy Azure naming rules; the class
    /// creates it on construction if absent.
    RootContainer: string
    /// Phase 2c — optional per-call connection-string provider for
    /// out-of-band credential rotation. `None` (default) preserves
    /// today's behaviour: the static `ConnectionString` above is used
    /// and the `BlobServiceClient` is built once for the process
    /// lifetime. `Some f` calls `f ()` on each operation and rebuilds
    /// the client *only when the resolved connection string changes*
    /// (change-detection cache — not a per-call reconstruction), so a
    /// rotated AccountKey / regenerated SAS is picked up without a
    /// restart. The closure typically closes over an
    /// `ISecretStore.GetSecret` read, keeping this companion free of a
    /// direct dependency on the secret backend (GP 12).
    ConnectionStringProvider: (unit -> string) option
    /// Phase 2c — optional audit sink for credential rejections. `None`
    /// (default) emits nothing and costs nothing (GP 11 + GP 13);
    /// `Some log` records one `BlobStorageAuthFailed` row per Azure call
    /// rejected `401` / `403`, under the `_platform` scope.
    ///
    /// Complements `ConnectionStringProvider` rather than duplicating it:
    /// the provider is how a rotated AccountKey is PICKED UP, this is how
    /// an operator learns that the one currently held has stopped being
    /// accepted. A deployment on a static connection string — which
    /// cannot recover without a restart — is the one that most needs the
    /// row.
    AuditLog: ToolUp.Platform.IAuditLog option
}

module AzureBlobStorageConfig =
    let defaults = {
        ConnectionString = ""
        RootContainer = "toolup"
        ConnectionStringProvider = None
        AuditLog = None
    }

// ─── Helpers ─────────────────────────────────────────────────────────

let private blobKey (toolupContainer: string) (blobName: string) = $"{toolupContainer}/{blobName}"

/// Azure SDK exceptions can surface at this companion's `with` handlers
/// wrapped in `AggregateException`, so a direct `:? RequestFailedException`
/// test never fires. Measured by the armed cloud-parity run (Phase 733,
/// 2026-08-27): the `Status = 416` arm of `DownloadRange` sat dead and a
/// fully-past-EOF range returned `Error "One or more errors occurred. …"`
/// instead of the contract's `Ok [||]`. The 404 arms were unaffected in
/// EFFECT only because their fall-through is also an `Error` — the
/// semantic 416 arm is the one where being unreachable changes an answer.
///
/// Match through the wrapper: flatten and take the single inner exception
/// a one-Task await carries; a bare exception passes through unchanged, so
/// an unmatched case still rethrows the original. Mirrors the pattern
/// `ToolUp.Storage.GoogleCloudStorage` carries for the same class.
let private (|Unwrapped|) (ex: exn) =
    match ex with
    | :? AggregateException as aggregate ->
        match Seq.tryHead (aggregate.Flatten().InnerExceptions) with
        | Some inner -> inner
        | None -> ex
    | _ -> ex

/// Phase 2c — the companion id this storage backend records under, the
/// SAME spelling its health probe uses (`blob_storage:azure`), so the
/// probe's `Unhealthy` reading and this companion's audit rows join on
/// one key rather than on two.
[<Literal>]
let internal companionId = "azure"

/// Phase 2c — classify an SDK exception as a credential rejection,
/// returning the rejecting status when it is one.
///
/// Reuses the Phase 733 `Unwrapped` pattern rather than adding a second
/// matching path: an Azure exception reaches a `with` handler wrapped in
/// `AggregateException`, and a direct type test on the wrapper never
/// fires — the defect that left this file's `416` arm dead until it was
/// measured.
///
/// `401` and `403` are kept distinct rather than folded into one
/// verdict because they answer different operator questions: `401` means
/// no usable credential was presented (an expired SAS reaches here), and
/// `403` means one WAS presented and refused (a rotated AccountKey, or a
/// container ACL change). Everything else, `404` and `412` included, is
/// not a credential fact and returns `None`.
let internal authFailureStatus (ex: exn) : int option =
    match ex with
    | Unwrapped(:? RequestFailedException as failure) when failure.Status = 401 || failure.Status = 403 ->
        Some failure.Status
    | _ -> None

// ─── IBlobStorage implementation ─────────────────────────────────────

/// Azure Blob Storage implementation of `IBlobStorage`. Maps ToolUp's
/// per-scope logical containers onto blob-name prefixes inside a single
/// Azure container. Thread-safe via the underlying Azure SDK clients
/// (designed for reuse).
type AzureBlobStorage(config: AzureBlobStorageConfig) =
    // Change-detection cache (Phase 2c). The container client is
    // (re)built whenever the resolved connection string differs from the
    // cached one — for the static (`ConnectionStringProvider = None`)
    // path that is exactly once, reproducing the original build-once
    // behaviour; for the provider path it rebuilds only on rotation, not
    // per call, since `BlobServiceClient` construction parses the
    // connection string and builds an HTTP pipeline. Instance-level
    // `mutable` guarded by `gate` — justified by the caching intent.
    let gate = obj ()
    let mutable cachedConnStr = ""
    let mutable containerClient: BlobContainerClient = null

    let resolveConnStr () =
        match config.ConnectionStringProvider with
        | Some provider -> provider ()
        | None -> config.ConnectionString

    // Return the current container client, rebuilding on a connection-
    // string change. One-time eager creation of the container itself is
    // preserved: Azure bills per-container only for content, not
    // existence, so `CreateIfNotExists` on (re)build costs nothing.
    let container () =
        let resolved = resolveConnStr ()

        lock gate (fun () ->
            if isNull containerClient || resolved <> cachedConnStr then
                let serviceClient = BlobServiceClient(resolved)
                let cc = serviceClient.GetBlobContainerClient(config.RootContainer)
                cc.CreateIfNotExists() |> ignore
                cachedConnStr <- resolved
                containerClient <- cc

            containerClient)

    // Eager build at construction — preserves the original fail-fast on a
    // malformed connection string / unreachable account at startup.
    do container () |> ignore

    // Phase 2c — the credential-rejection trail. Called from a `with`
    // handler with the exception that is about to become the caller's
    // `Error`, immediately BEFORE that `Error` is returned: nothing is
    // swallowed, no message changes, and a non-auth failure records
    // nothing. `BlobStorageAuthAudit.record` never throws, so this
    // cannot turn a storage failure into a different one.
    let noteAuthFailure (operation: string) (ex: exn) : Async<unit> =
        match authFailureStatus ex with
        | Some status ->
            ToolUp.Platform.BlobStorageAuthAudit.record
                config.AuditLog
                companionId
                config.RootContainer
                operation
                status
                ex.Message
        | None -> async { () }

    // Phase 600 follow-up — live-etag disclosure read for a refused
    // conditional write. `Ok None` = the blob is absent (the only case
    // the seam may report `ETagMismatch None`); `Error` = the
    // disclosure read itself failed, surfaced as
    // `ConditionalWriteFailure` rather than a fabricated verdict.
    //
    // Deliberately NOT audited for credential rejection (Phase 2c): this
    // read is reached only after a `412` / `409` on the same call, which
    // means the credential had already authenticated moments earlier.
    let currentETag (key: string) : Async<Result<string option, string>> = async {
        try
            let blob = (container ()).GetBlobClient key
            let! response = blob.GetPropertiesAsync() |> Async.AwaitTask
            return Ok(Some(response.Value.ETag.ToString()))
        with
        | Unwrapped(:? RequestFailedException as ex) when ex.Status = 404 -> return Ok None
        | Unwrapped ex -> return Error ex.Message
    }

    interface IBlobStorage with
        member this.Erase(container, prefix, policy, dryRun) =
            ToolUp.Platform.BlobStorage.eraseByPrefix
                (this :> ToolUp.Platform.BlobStorage.IBlobStorage)
                container
                prefix
                policy
                dryRun

        member _.Upload(toolupContainer, blobName, content) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                use ms = new MemoryStream(content)
                let! _ = blob.UploadAsync(ms, overwrite = true) |> Async.AwaitTask
                return Ok(blob.Uri.ToString())
            with Unwrapped ex ->
                do! noteAuthFailure "Upload" ex
                return Error ex.Message
        }

        member _.Download(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.DownloadContentAsync() |> Async.AwaitTask
                return Ok(response.Value.Content.ToArray())
            with
            | Unwrapped(:? RequestFailedException as ex) when ex.Status = 404 ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | Unwrapped ex ->
                do! noteAuthFailure "Download" ex
                return Error ex.Message
        }

        member _.DownloadRange(toolupContainer, blobName, offset, length) = async {
            if offset < 0L then
                return Error "DownloadRange: offset must be non-negative"
            elif length <= 0 then
                return Error "DownloadRange: length must be positive"
            else
                try
                    let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                    // Native range request — Azure clamps a range that
                    // overshoots EOF and 416s a range starting past it.
                    let options = BlobDownloadOptions(Range = HttpRange(offset, int64 length))
                    let! response = blob.DownloadContentAsync options |> Async.AwaitTask
                    return Ok(response.Value.Content.ToArray())
                with
                | Unwrapped(:? RequestFailedException as ex) when ex.Status = 404 ->
                    return Error $"Blob not found: {toolupContainer}/{blobName}"
                | Unwrapped(:? RequestFailedException as ex) when ex.Status = 416 ->
                    // Fully past EOF → `Ok [||]` per the interface
                    // contract. Matched through `Unwrapped` because
                    // Azure's 416 arrives wrapped; see the pattern's
                    // doc-comment for what that cost.
                    return Ok Array.empty
                | Unwrapped ex ->
                    do! noteAuthFailure "DownloadRange" ex
                    return Error ex.Message
        }

        // Phase 741 — Azure's native multi-part commit is the BLOCK
        // LIST: stage each part as its own block, then commit the list
        // in one call. Nothing is visible at the target name until the
        // commit, and an abandoned staging set is garbage-collected by
        // the service after a week, so a failed compose leaves no
        // half-object.
        //
        // Each part is pulled into memory once and staged from there, so
        // the peak is ONE part — not the object — which is the member's
        // whole contract. `StageBlockFromUri` would keep the bytes
        // entirely service-side, but it needs a source URI the service
        // can itself read: that means minting a SAS, which this
        // companion cannot do under every credential shape it accepts
        // (a SAS-token connection string carries no account key, and
        // Azurite's shape differs again). A primitive that works for
        // some deployments and silently degrades for others is worse
        // here than one bounded relay that works for all of them.
        member _.CanComposeFrom = true

        member _.ComposeFrom(toolupContainer, targetBlobName, sourceBlobNames) = async {
            if List.isEmpty sourceBlobNames then
                return Error(ComposeRefusal.ComposeFailed "ComposeFrom: at least one source blob is required")
            else
                try
                    let cc = container ()
                    let target = cc.GetBlockBlobClient(blobKey toolupContainer targetBlobName)

                    let mutable total = 0L
                    let blockIds = ResizeArray<string>()

                    for index, source in List.indexed sourceBlobNames do
                        let sourceBlob = cc.GetBlobClient(blobKey toolupContainer source)
                        let! part = sourceBlob.DownloadContentAsync() |> Async.AwaitTask
                        let bytes = part.Value.Content.ToArray()

                        // Block ids must be equal-length base64 within
                        // one blob — a fixed-width ordinal satisfies
                        // that and keeps the committed order explicit.
                        let blockId =
                            Convert.ToBase64String(Text.Encoding.UTF8.GetBytes(index.ToString("D10")))

                        use ms = new MemoryStream(bytes)
                        let! _ = target.StageBlockAsync(blockId, ms) |> Async.AwaitTask
                        blockIds.Add blockId
                        total <- total + int64 bytes.Length

                    let! _ = target.CommitBlockListAsync blockIds |> Async.AwaitTask
                    return Ok total
                with
                | Unwrapped(:? RequestFailedException as ex) when ex.Status = 404 ->
                    return Error(ComposeRefusal.ComposeFailed $"Compose source not found in {toolupContainer}")
                | Unwrapped ex ->
                    do! noteAuthFailure "ComposeFrom" ex
                    return Error(ComposeRefusal.ComposeFailed ex.Message)
        }

        member _.Delete(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                // `DeleteIfExists` is idempotent by design — matches
                // the contract's promise that deleting a missing
                // blob succeeds.
                let! _ = blob.DeleteIfExistsAsync() |> Async.AwaitTask
                return Ok()
            with Unwrapped ex ->
                do! noteAuthFailure "Delete" ex
                return Error ex.Message
        }

        // `List` deliberately has no error mapping — an auth failure
        // propagates as an exception, which is exactly what the Phase 2c
        // health probe relies on to read `Unhealthy` rather than the
        // `Exists`-shaped false "absent". The outer handler added here
        // records the trail and then RETHROWS THE ORIGINAL through
        // `ExceptionDispatchInfo`, preserving both its identity and its
        // stack, so the probe's `Unhealthy ex.Message` is unchanged. It
        // wraps the client resolution too, because `container ()` calls
        // `CreateIfNotExists` and can itself be refused.
        member _.List(toolupContainer, prefix) = async {
            let fullPrefix = blobKey toolupContainer prefix
            let stripLen = (toolupContainer + "/").Length
            let results = System.Collections.Generic.List<string>()

            try
                // All four positional args — F# can't resolve the C#-
                // style optional-params overload from a subset, so we
                // pass the default CancellationToken explicitly.
                let enumerable: AsyncPageable<BlobItem> =
                    (container ())
                        .GetBlobsAsync(
                            BlobTraits.None,
                            BlobStates.None,
                            fullPrefix,
                            System.Threading.CancellationToken()
                        )

                let enumerator = enumerable.GetAsyncEnumerator()

                try
                    let mutable keepGoing = true

                    while keepGoing do
                        let! hasNext = enumerator.MoveNextAsync().AsTask() |> Async.AwaitTask

                        if hasNext then
                            results.Add(enumerator.Current.Name.Substring stripLen)
                        else
                            keepGoing <- false
                finally
                    enumerator.DisposeAsync().AsTask().Wait()

                return results |> List.ofSeq
            with ex ->
                do! noteAuthFailure "List" ex
                ExceptionDispatchInfo.Capture(ex).Throw()
                // Unreachable — `Throw()` always throws. F# still needs a
                // branch value; `reraise` is not available inside a
                // computation-expression handler.
                return []
        }

        // `Exists` swallows every failure and answers `false`, which is
        // the contract — but it means a `403` from a rejected credential
        // used to be completely silent here. Recording it does not change
        // the answer (Phase 2c moved the health probe off `Exists` for
        // exactly this reason); it stops the rejection being invisible.
        member _.Exists(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.ExistsAsync() |> Async.AwaitTask
                return response.Value
            with ex ->
                do! noteAuthFailure "Exists" ex
                return false
        }

        member _.GetMetadata(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.GetPropertiesAsync() |> Async.AwaitTask
                let props = response.Value

                let contentType =
                    if String.IsNullOrEmpty props.ContentType then
                        None
                    else
                        Some props.ContentType

                return
                    Ok {
                        Size = props.ContentLength
                        LastModified = props.LastModified.UtcDateTime
                        ContentType = contentType
                    }
            with
            | Unwrapped(:? RequestFailedException as ex) when ex.Status = 404 ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | Unwrapped ex ->
                do! noteAuthFailure "GetMetadata" ex
                return Error ex.Message
        }

    // ─── Phase 600 follow-up — conditional writes (the ETag seam) ────
    //
    // Azure-native ETag preconditions: `BlobRequestConditions.IfMatch`
    // for the read-modify-write guard, `IfNoneMatch = ETag.All` (the
    // wire's `If-None-Match: *`) for create-only. The etag token is the
    // native Azure ETag, opaque per the seam contract. A refused
    // `If-Match` surfaces as 412 ConditionNotMet (including against an
    // absent blob); a losing `If-None-Match: *` create surfaces as 409
    // BlobAlreadyExists — both map to `ETagMismatch`, with the live
    // etag recovered by a follow-up properties read (`None` only when
    // the blob is absent).
    interface IConditionalBlobStorage with
        member _.DownloadWithETag(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.DownloadContentAsync() |> Async.AwaitTask
                let result = response.Value
                return Ok(result.Content.ToArray(), result.Details.ETag.ToString())
            with
            | Unwrapped(:? RequestFailedException as ex) when ex.Status = 404 ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | Unwrapped ex ->
                do! noteAuthFailure "DownloadWithETag" ex
                return Error ex.Message
        }

        member _.UploadWithETag(toolupContainer, blobName, content, condition) = async {
            let key = blobKey toolupContainer blobName

            try
                let blob = (container ()).GetBlobClient key
                let conditions = BlobRequestConditions()

                match condition with
                | IfMatch etag -> conditions.IfMatch <- Nullable(ETag etag)
                | IfAbsent -> conditions.IfNoneMatch <- Nullable ETag.All

                let options = BlobUploadOptions(Conditions = conditions)
                use ms = new MemoryStream(content)
                let! response = blob.UploadAsync(ms, options) |> Async.AwaitTask
                return Ok(response.Value.ETag.ToString())
            with
            | Unwrapped(:? RequestFailedException as ex) when ex.Status = 412 || ex.Status = 409 ->
                match! currentETag key with
                | Ok current -> return Error(ETagMismatch current)
                | Error msg ->
                    return Error(ConditionalWriteFailure $"precondition refused; etag disclosure read failed: {msg}")
            | Unwrapped ex ->
                do! noteAuthFailure "UploadWithETag" ex
                return Error(ConditionalWriteFailure ex.Message)
        }

    // ─── Phase 108 — time-bound direct-download URLs ─────────────────
    //
    // A service SAS minted from the account key: read permission on one
    // blob, expiring at `now + ttl`. Purely local — `GenerateSasUri`
    // signs with the credential already held, issuing no request — so a
    // caller that established existence via `GetMetadata` pays nothing
    // extra here.
    //
    // `CanGenerateSasUri` is false when the `BlobServiceClient` was
    // built from a connection string carrying a SAS token rather than
    // an `AccountKey` (or from a token credential): the client can read
    // and write, but holds no key to sign WITH. That is a legitimate
    // deployment shape, not a fault, so it reports `NotConfigured` and
    // the caller falls back to proxying.
    //
    // No credential-rejection row here (Phase 2c): SAS minting issues no
    // request, so there is no status to classify. A URL minted with a
    // rotated key fails at the FETCHER, which is not this process.
    interface ISignedUrlBlobStorage with
        member _.SignedUrl(toolupContainer, blobName, ttl) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)

                if not blob.CanGenerateSasUri then
                    return
                        Error(
                            SignedUrlRefusal.NotConfigured
                                "the Azure client holds no account key to sign with (the connection string carries a SAS token or a token credential, not an AccountKey)"
                        )
                else
                    let uri =
                        blob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add ttl)

                    return Ok(uri.ToString())
            with Unwrapped ex ->
                return Error(SignedUrlRefusal.SigningFailed ex.Message)
        }

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `IBlobStorage` from an `AzureBlobStorageConfig`. One-
/// time startup work: verifies the root container exists (creates it
/// if absent). Thread-safe thereafter — reuse the same instance for
/// every request.
let create (config: AzureBlobStorageConfig) : IBlobStorage = AzureBlobStorage config :> IBlobStorage

/// Read the connection string from `TOOLUP_AZURE_STORAGE_CONNECTION_STRING`
/// and construct an `AzureBlobStorage`. Returns `None` when the env var
/// is unset — the deployment falls back to `LocalFileStorage` or
/// whatever it has wired. `rootContainer` overrides the default
/// `"toolup"` name when `Some`.
let fromEnv (rootContainer: string option) : IBlobStorage option =
    match Environment.GetEnvironmentVariable ToolUp.Platform.ConfigKeys.Names.azureStorageConnectionString with
    | null
    | "" -> None
    | connectionString ->
        Some(
            create {
                ConnectionString = connectionString
                RootContainer = rootContainer |> Option.defaultValue "toolup"
                // `fromEnv` reads the connection string once. Deployments
                // that rotate the AccountKey out of band construct via
                // `create` with `ConnectionStringProvider = Some f`
                // (Phase 2c) to survive rotation without a restart.
                ConnectionStringProvider = None
                // Env-built instances compose no audit sink, there being
                // no env var that could name one. A deployment wanting
                // the credential-rejection trail builds the config itself.
                AuditLog = None
            }
        )