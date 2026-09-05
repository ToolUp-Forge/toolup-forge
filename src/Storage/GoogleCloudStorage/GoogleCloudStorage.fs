module ToolUp.Storage.GoogleCloudStorage

// `GoogleCredential.FromJson` is marked deprecated in favour of the
// newer `CredentialFactory` API. For single-call credential
// construction from inline JSON, `FromJson` remains the simplest
// surface and carries no security risk in this use case (caller
// supplies a trusted service-account JSON read from config).
#nowarn "44"

open System
open System.Globalization
open System.IO
open System.Net
open System.Runtime.ExceptionServices
open Google
open Google.Apis.Auth.OAuth2
open Google.Cloud.Storage.V1
open ToolUp.Platform.BlobStorage

// ─── Configuration ───────────────────────────────────────────────────

/// Configuration for `GoogleCloudStorage`. Takes the bucket name plus
/// optional service-account JSON credentials. When `CredentialsJson`
/// is `None`, the GCS SDK follows its Application Default Credentials
/// chain (env var `GOOGLE_APPLICATION_CREDENTIALS`, gcloud-login
/// user creds, GCE metadata server, GKE workload identity) — the
/// standard GCP deployment path.
///
/// Like Azure and S3, ToolUp's logical containers map to object-name
/// prefixes inside a single GCS bucket — bucket names are globally
/// unique across all GCP projects, so one-per-scope is impractical,
/// and prefixes let a deployment use one bucket for all data.
type GoogleCloudStorageConfig = {
    /// Existing GCS bucket. The class does NOT create the bucket —
    /// provisioning is the deployment's responsibility, and the
    /// service account's IAM role usually restricts access to a
    /// specific bucket.
    BucketName: string
    /// Service-account JSON credentials string. `None` uses the ADC
    /// resolution chain (`GOOGLE_APPLICATION_CREDENTIALS` path, gcloud
    /// auth, metadata server, workload identity).
    CredentialsJson: string option
    /// Phase 2c — optional per-call service-account-JSON provider for
    /// out-of-band credential rotation. `None` (default) preserves
    /// today's behaviour: the `StorageClient` is built once from
    /// `CredentialsJson` (or the ADC chain when that is `None`) for the
    /// process lifetime. `Some f` calls `f ()` on each operation and
    /// rebuilds the client *only when the resolved JSON changes*
    /// (change-detection cache — not a per-call reconstruction), so a
    /// rolled service-account key is picked up without a restart. The
    /// closure typically closes over an `ISecretStore.GetSecret` read.
    /// Note: ADC-based deployments (`CredentialsJson = None`,
    /// `CredentialsJsonProvider = None`) are already rotation-transparent
    /// — the metadata server / workload-identity chain refreshes tokens
    /// itself — so the provider is only needed for the inline-JSON path.
    CredentialsJsonProvider: (unit -> string) option
    /// Optional GCS-compatible endpoint override — the mirror of
    /// `AwsS3StorageConfig.EndpointUrl`, and the seam an emulator needs.
    /// `None` (default) preserves today's behaviour exactly: the client is
    /// built by `StorageClient.Create`, which resolves the real
    /// `storage.googleapis.com` endpoint and honours the ADC chain.
    ///
    /// `Some uri` builds through `StorageClientBuilder` with `BaseUri` set,
    /// which is the only surface in this SDK that can be pointed elsewhere
    /// — `StorageClient.Create` does NOT consult `STORAGE_EMULATOR_HOST`
    /// (verified: with that variable set it still walks the ADC chain and
    /// throws "Your default credentials were not found"). When an override
    /// is set and no credentials are supplied, the builder is put into
    /// unauthenticated mode, because an emulator has no credentials to
    /// present and the ADC chain would otherwise fail before the first
    /// call. Supplying `CredentialsJson` alongside an override keeps the
    /// credential, for an authenticating GCS-compatible endpoint.
    EndpointUrl: string option
    /// Phase 2c — optional audit sink for credential rejections. `None`
    /// (default) emits nothing and costs nothing (GP 11 + GP 13);
    /// `Some log` records one `BlobStorageAuthFailed` row per GCS call
    /// rejected `401` / `403`, under the `_platform` scope.
    ///
    /// Complements `CredentialsJsonProvider` rather than duplicating it:
    /// the provider is how a rolled service-account key is PICKED UP,
    /// this is how an operator learns that the one currently held has
    /// stopped being accepted. Worth composing even on an ADC deployment,
    /// which is rotation-transparent but can still lose an IAM binding.
    AuditLog: ToolUp.Platform.IAuditLog option
}

module GoogleCloudStorageConfig =
    let defaults = {
        BucketName = ""
        CredentialsJson = None
        CredentialsJsonProvider = None
        EndpointUrl = None
        AuditLog = None
    }

// ─── Helpers ─────────────────────────────────────────────────────────

let private blobKey (toolupContainer: string) (blobName: string) = $"{toolupContainer}/{blobName}"

/// Phase 741 — GCS composes at most 32 source objects per request. A
/// hard service constraint, not a tuning knob: it is why `ComposeFrom`
/// folds in batches rather than issuing one call.
let private composeBatchSize = 32

/// GCS SDK exceptions can surface at this companion's `with` handlers
/// wrapped in `AggregateException` — proven by the first armed
/// cloud-parity run (2026-08-27): `Delete`'s direct
/// `:? GoogleApiException` NotFound test sat dead, breaking delete
/// idempotency on missing blobs. Match through the wrapper: flatten and
/// take the single inner exception a one-Task await carries; a bare
/// exception passes through unchanged, so an unmatched case still
/// rethrows the original.
let private (|Unwrapped|) (ex: exn) =
    match ex with
    | :? AggregateException as aggregate ->
        match Seq.tryHead (aggregate.Flatten().InnerExceptions) with
        | Some inner -> inner
        | None -> ex
    | _ -> ex

/// Phase 2c — the companion id this storage backend records under, the
/// SAME spelling its health probe uses (`blob_storage:gcs`), so the
/// probe's `Unhealthy` reading and this companion's audit rows join on
/// one key rather than on two.
[<Literal>]
let internal companionId = "gcs"

/// Phase 2c — classify an SDK exception as a credential rejection,
/// returning the rejecting status when it is one.
///
/// Reuses the `Unwrapped` pattern rather than adding a second matching
/// path: a GCS exception reaches a `with` handler wrapped in
/// `AggregateException`, and a direct type test on the wrapper never
/// fires — the defect that broke `Delete`'s idempotency until the first
/// armed cloud-parity run measured it.
///
/// `401` and `403` are kept distinct rather than folded into one verdict
/// because they answer different operator questions: `401` means no
/// usable credential was presented (an ADC chain that resolved nothing,
/// an expired token), and `403` means one WAS presented and refused — a
/// rolled service-account key, or an IAM binding removed from the
/// bucket. Everything else, `404` and `412` included, is not a
/// credential fact and returns `None`.
let internal authFailureStatus (ex: exn) : int option =
    match ex with
    | Unwrapped(:? GoogleApiException as failure) ->
        match failure.HttpStatusCode with
        | HttpStatusCode.Unauthorized -> Some 401
        | HttpStatusCode.Forbidden -> Some 403
        | _ -> None
    | _ -> None

let private buildClientFor (endpointUrl: string option) (credentialsJson: string option) : StorageClient =
    match endpointUrl with
    | None ->
        // Unchanged pre-existing path (GP 11) — no builder, no behaviour
        // difference for any deployment that does not set an override.
        match credentialsJson with
        | Some json ->
            let credential = GoogleCredential.FromJson json
            StorageClient.Create credential
        | None ->
            // Application Default Credentials — the SDK resolves env /
            // metadata server / workload identity itself.
            StorageClient.Create()
    | Some uri ->
        let builder = StorageClientBuilder(BaseUri = uri)

        match credentialsJson with
        | Some json -> builder.Credential <- GoogleCredential.FromJson json
        | None ->
            // No credential to present, and an emulator wants none — say so
            // explicitly rather than letting the ADC chain throw first.
            builder.UnauthenticatedAccess <- true

        builder.Build()


// ─── IBlobStorage implementation ─────────────────────────────────────

/// Google Cloud Storage implementation of `IBlobStorage`. Single
/// bucket, ToolUp logical scopes encoded as object-name prefixes.
/// Thread-safe — `StorageClient` is documented reusable across
/// concurrent calls.
type GoogleCloudStorage(config: GoogleCloudStorageConfig) =
    // Change-detection cache (Phase 2c). The `StorageClient` is (re)built
    // when the resolved service-account JSON differs from the cached one —
    // for the static (`CredentialsJsonProvider = None`) path that is
    // exactly once, reproducing the original build-once behaviour; for the
    // provider path it rebuilds only on key rotation, not per call.
    // Instance-level `mutable` guarded by `gate` — justified by the
    // caching intent.
    let gate = obj ()
    let mutable cachedJson: string = null
    let mutable cachedClient: StorageClient = null

    let client () =
        match config.CredentialsJsonProvider with
        | None ->
            // Static / ADC path: build once, never rebuild.
            lock gate (fun () ->
                if isNull cachedClient then
                    cachedClient <- buildClientFor config.EndpointUrl config.CredentialsJson

                cachedClient)
        | Some provider ->
            let resolved = provider ()

            lock gate (fun () ->
                if isNull cachedClient || resolved <> cachedJson then
                    cachedJson <- resolved
                    cachedClient <- buildClientFor config.EndpointUrl (Some resolved)

                cachedClient)

    // Eager build at construction — preserves the original fail-fast on a
    // malformed service-account JSON / credential-chain failure at startup.
    do client () |> ignore

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
                config.BucketName
                operation
                status
                ex.Message
        | None -> async { () }

    // Phase 600 follow-up — live-generation disclosure read for a
    // refused conditional write. `Ok None` = the object is absent (the
    // only case the seam may report `ETagMismatch None`); `Error` = the
    // disclosure read itself failed, surfaced as
    // `ConditionalWriteFailure` rather than a fabricated verdict.
    //
    // Deliberately NOT audited for credential rejection (Phase 2c): this
    // read is reached only after a `412` on the same call, which means
    // the credential had already authenticated moments earlier.
    let currentGeneration (key: string) : Async<Result<string option, string>> = async {
        try
            let! obj = (client ()).GetObjectAsync(config.BucketName, key) |> Async.AwaitTask

            match Option.ofNullable obj.Generation with
            | Some g -> return Ok(Some(string g))
            | None -> return Error "GCS returned an object without a generation"
        with
        | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound -> return Ok None
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
                use ms = new MemoryStream(content)
                let key = blobKey toolupContainer blobName
                // ContentType `null` lets GCS sniff from the object
                // name; we don't force octet-stream because the
                // platform doesn't track MIME per blob.
                let! obj =
                    (client ()).UploadObjectAsync(config.BucketName, key, null, ms)
                    |> Async.AwaitTask

                return Ok $"gs://{obj.Bucket}/{obj.Name}"
            with Unwrapped ex ->
                do! noteAuthFailure "Upload" ex
                return Error ex.Message
        }

        member _.Download(toolupContainer, blobName) = async {
            try
                use ms = new MemoryStream()
                let key = blobKey toolupContainer blobName
                let! _ = (client ()).DownloadObjectAsync(config.BucketName, key, ms) |> Async.AwaitTask
                return Ok(ms.ToArray())
            with
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound ->
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
                    use ms = new MemoryStream()
                    let key = blobKey toolupContainer blobName
                    // Native range request; RangeHeaderValue end is
                    // inclusive. GCS clamps a range that overshoots EOF
                    // and 416s a range starting past it.
                    let options =
                        DownloadObjectOptions(Range = Http.Headers.RangeHeaderValue(offset, offset + int64 length - 1L))

                    let! _ =
                        (client ()).DownloadObjectAsync(config.BucketName, key, ms, options)
                        |> Async.AwaitTask

                    return Ok(ms.ToArray())
                with
                | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                    return Error $"Blob not found: {toolupContainer}/{blobName}"
                | Unwrapped(:? GoogleApiException as ex) when
                    ex.HttpStatusCode = HttpStatusCode.RequestedRangeNotSatisfiable
                    ->
                    // Past-EOF clamp per the interface contract.
                    return Ok Array.empty
                | Unwrapped ex ->
                    do! noteAuthFailure "DownloadRange" ex
                    return Error ex.Message
        }

        // Phase 741 — GCS is the one companion whose compose is FULLY
        // server-side: `Objects.compose` concatenates stored objects in
        // the bucket and not one byte passes through this process, so
        // the memory bound the member promises is trivially met and the
        // network cost is nil.
        //
        // The one constraint is the 32-source-per-request cap, met by
        // composing in batches into intermediates and composing those —
        // a fold, repeated until one object remains. Intermediates are
        // named under a reserved suffix on the target and deleted after
        // the final compose; a crash between the two leaves objects
        // that are discoverable by their names and cost storage, which
        // is the honest trade for never materialising the object.
        member _.CanComposeFrom = true

        member _.ComposeFrom(toolupContainer, targetBlobName, sourceBlobNames) = async {
            if List.isEmpty sourceBlobNames then
                return Error(ComposeRefusal.ComposeFailed "ComposeFrom: at least one source blob is required")
            else
                let targetKey = blobKey toolupContainer targetBlobName
                let intermediates = System.Collections.Generic.List<string>()

                // One `Objects.compose` call: `sources` (already full
                // object keys, at most `composeBatchSize` of them) into
                // `destination`.
                let composeOnce (sources: string list) (destination: string) = async {
                    let body = Google.Apis.Storage.v1.Data.ComposeRequest()

                    body.SourceObjects <-
                        sources
                        |> List.map (fun name ->
                            let s = Google.Apis.Storage.v1.Data.ComposeRequest.SourceObjectsData()
                            s.Name <- name
                            s)
                        |> System.Collections.Generic.List

                    let request =
                        (client ()).Service.Objects.Compose(body, config.BucketName, destination)

                    return! request.ExecuteAsync() |> Async.AwaitTask
                }

                try
                    let mutable level = 0
                    let mutable current = sourceBlobNames |> List.map (blobKey toolupContainer)

                    // Fold down to at most `composeBatchSize` sources.
                    while List.length current > composeBatchSize do
                        let batches = List.chunkBySize composeBatchSize current
                        let next = System.Collections.Generic.List<string>()

                        for index, batch in List.indexed batches do
                            if List.length batch = 1 then
                                // Nothing to compose — carry it forward
                                // rather than minting an intermediate
                                // that is a copy of one object.
                                next.Add(List.head batch)
                            else
                                let name = sprintf "%s.__compose/%d/%d" targetKey level index
                                let! _ = composeOnce batch name
                                intermediates.Add name
                                next.Add name

                        current <- List.ofSeq next
                        level <- level + 1

                    let! composed = composeOnce current targetKey

                    for name in intermediates do
                        try
                            do! (client ()).DeleteObjectAsync(config.BucketName, name) |> Async.AwaitTask
                        with _ ->
                            ()

                    return
                        Ok(
                            if composed.Size.HasValue then
                                int64 composed.Size.Value
                            else
                                0L
                        )
                with
                | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                    return Error(ComposeRefusal.ComposeFailed $"Compose source not found in {toolupContainer}")
                | Unwrapped ex ->
                    do! noteAuthFailure "ComposeFrom" ex
                    return Error(ComposeRefusal.ComposeFailed ex.Message)
        }

        member _.Delete(toolupContainer, blobName) = async {
            // GCS DeleteObject raises 404 for missing objects; we
            // swallow that to keep the idempotent contract. Any
            // other error surfaces as `Error`.
            try
                let key = blobKey toolupContainer blobName
                do! (client ()).DeleteObjectAsync(config.BucketName, key) |> Async.AwaitTask
                return Ok()
            with
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound -> return Ok()
            | Unwrapped ex ->
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
        // wraps the client resolution too, because `client ()` can itself
        // fail on a credential the ADC chain can no longer resolve.
        member _.List(toolupContainer, prefix) = async {
            let fullPrefix = blobKey toolupContainer prefix
            let stripLen = (toolupContainer + "/").Length
            let results = System.Collections.Generic.List<string>()

            try
                // `fullPrefix` is the 2nd positional arg; no options
                // needed for a simple prefix list.
                let enumerable = (client ()).ListObjectsAsync(config.BucketName, fullPrefix)
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
                let key = blobKey toolupContainer blobName
                let! _ = (client ()).GetObjectAsync(config.BucketName, key) |> Async.AwaitTask
                return true
            with
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound -> return false
            | ex ->
                do! noteAuthFailure "Exists" ex
                return false
        }

        member _.GetMetadata(toolupContainer, blobName) = async {
            try
                let key = blobKey toolupContainer blobName
                let! obj = (client ()).GetObjectAsync(config.BucketName, key) |> Async.AwaitTask

                let size = if obj.Size.HasValue then int64 obj.Size.Value else 0L

                // The Google.Apis `UpdatedDateTimeOffset` getter re-parses the
                // raw RFC3339 `updated` wire string with an exact format on
                // every read, so even the `.HasValue` probe throws
                // FormatException on variants a GCS-compatible emulator can
                // emit (fake-gcs-server sometimes trims fractional seconds).
                // Real GCS always emits the exact shape; a lenient re-parse of
                // the raw string degrades the timestamp, not the whole
                // GetMetadata call.
                let lastModified =
                    try
                        if obj.UpdatedDateTimeOffset.HasValue then
                            obj.UpdatedDateTimeOffset.Value.UtcDateTime
                        else
                            DateTime.UtcNow
                    with :? FormatException ->
                        match
                            DateTimeOffset.TryParse(
                                obj.UpdatedRaw,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal
                            )
                        with
                        | true, parsed -> parsed.UtcDateTime
                        | false, _ -> DateTime.UtcNow

                let contentType =
                    if String.IsNullOrEmpty obj.ContentType then
                        None
                    else
                        Some obj.ContentType

                return
                    Ok {
                        Size = size
                        LastModified = lastModified
                        ContentType = contentType
                    }
            with
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | Unwrapped ex ->
                do! noteAuthFailure "GetMetadata" ex
                return Error ex.Message
        }

    // ─── Phase 600 follow-up — conditional writes (the ETag seam) ────
    //
    // GCS optimistic concurrency rides object GENERATIONS, not HTTP
    // ETags (a GCS ETag is not a stable comparison token across
    // transcoding). The seam's etag token is therefore the decimal
    // generation number, surfaced as an opaque string — legal because
    // the contract makes tokens opaque per-provider: callers only
    // round-trip them, never parse them. `IfAbsent` maps to
    // `ifGenerationMatch=0`, the documented create-only precondition;
    // a refused precondition surfaces as HTTP 412, with the live
    // generation recovered by a follow-up metadata read (`None` only
    // when the object is absent). A token this backend never minted
    // (non-numeric) can match no generation and is refused without a
    // write round-trip.
    interface IConditionalBlobStorage with
        member _.DownloadWithETag(toolupContainer, blobName) = async {
            try
                let key = blobKey toolupContainer blobName
                // Two calls made coherent by generation pinning: read
                // the live generation, then download exactly that
                // generation — a concurrent overwrite between the two
                // calls cannot tear the (content, token) pair.
                let! obj = (client ()).GetObjectAsync(config.BucketName, key) |> Async.AwaitTask

                match Option.ofNullable obj.Generation with
                | None -> return Error $"GCS returned no generation for {toolupContainer}/{blobName}"
                | Some gen ->
                    use ms = new MemoryStream()
                    let options = DownloadObjectOptions(Generation = Nullable gen)

                    let! _ =
                        (client ()).DownloadObjectAsync(config.BucketName, key, ms, options)
                        |> Async.AwaitTask

                    return Ok(ms.ToArray(), string gen)
            with
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | Unwrapped ex ->
                do! noteAuthFailure "DownloadWithETag" ex
                return Error ex.Message
        }

        member _.UploadWithETag(toolupContainer, blobName, content, condition) = async {
            let key = blobKey toolupContainer blobName

            let requiredGeneration =
                match condition with
                | IfAbsent -> Some 0L
                | IfMatch token ->
                    match Int64.TryParse token with
                    | true, gen -> Some gen
                    | false, _ -> None

            match requiredGeneration with
            | None ->
                // A foreign token — refused as a mismatch, with the live
                // generation disclosed.
                match! currentGeneration key with
                | Ok current -> return Error(ETagMismatch current)
                | Error msg ->
                    return Error(ConditionalWriteFailure $"precondition refused; generation read failed: {msg}")
            | Some gen ->
                try
                    use ms = new MemoryStream(content)
                    let options = UploadObjectOptions(IfGenerationMatch = Nullable gen)

                    let! obj =
                        (client ()).UploadObjectAsync(config.BucketName, key, null, ms, options)
                        |> Async.AwaitTask

                    match Option.ofNullable obj.Generation with
                    | Some g -> return Ok(string g)
                    | None -> return Error(ConditionalWriteFailure "upload succeeded but GCS returned no generation")
                with
                | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.PreconditionFailed ->
                    match! currentGeneration key with
                    | Ok current -> return Error(ETagMismatch current)
                    | Error msg ->
                        return Error(ConditionalWriteFailure $"precondition refused; generation read failed: {msg}")
                | Unwrapped ex ->
                    do! noteAuthFailure "UploadWithETag" ex
                    return Error(ConditionalWriteFailure ex.Message)
        }

    // ─── Phase 108 — time-bound direct-download URLs ─────────────────
    //
    // A V4-signed GET URL, computed locally from the service-account
    // key — no request is issued and no existence check is made (a
    // signed URL for an absent object is well-formed and 404s on fetch,
    // per the seam contract).
    //
    // GCS signing needs an RSA private key, which only a service-account
    // credential carries. A deployment on the Application Default
    // Credentials chain (`CredentialsJson = None`) may be running under
    // a user credential, a workload identity, or the metadata server —
    // none of which expose a signable key to this process — so that
    // shape reports `NotConfigured` and the caller falls back to
    // proxying, rather than throwing on a path the deployment never
    // opted into. Deployments that want signed originals supply the
    // service-account JSON explicitly (`CredentialsJson` /
    // `CredentialsJsonProvider`).
    //
    // No credential-rejection row here (Phase 2c): signing issues no
    // request, so there is no status to classify. A URL signed with a
    // rolled key fails at the FETCHER, which is not this process.
    interface ISignedUrlBlobStorage with
        member _.SignedUrl(toolupContainer, blobName, ttl) = async {
            let resolvedJson =
                match config.CredentialsJsonProvider with
                | Some provider -> Some(provider ())
                | None -> config.CredentialsJson

            match resolvedJson with
            | None ->
                return
                    Error(
                        SignedUrlRefusal.NotConfigured
                            "GCS URL signing needs an explicit service-account key; this client resolved Application Default Credentials, which expose no signable private key to the process"
                    )
            | Some json ->
                try
                    let signer = UrlSigner.FromCredential(GoogleCredential.FromJson json)
                    let key = blobKey toolupContainer blobName
                    let! url = signer.SignAsync(config.BucketName, key, ttl) |> Async.AwaitTask
                    return Ok url
                with Unwrapped ex ->
                    return Error(SignedUrlRefusal.SigningFailed ex.Message)
        }

// ─── Public entry points ─────────────────────────────────────────────

let create (config: GoogleCloudStorageConfig) : IBlobStorage =
    GoogleCloudStorage config :> IBlobStorage

/// Read bucket + credentials from environment and construct a
/// `GoogleCloudStorage`. Returns `None` when `TOOLUP_GCS_BUCKET` is
/// unset — deployment falls back to whatever `compose` has wired.
///
/// Required: `TOOLUP_GCS_BUCKET`.
/// Optional: `TOOLUP_GCS_CREDENTIALS_JSON` (service-account JSON
/// inline). When unset, GCS SDK's Application Default Credentials
/// chain resolves automatically — typically via
/// `GOOGLE_APPLICATION_CREDENTIALS` pointing at a JSON file.
let fromEnv () : IBlobStorage option =
    let read name = Environment.GetEnvironmentVariable name

    match read ToolUp.Platform.ConfigKeys.Names.gcsBucket with
    | null
    | "" -> None
    | bucket ->
        let credsJson =
            match read ToolUp.Platform.ConfigKeys.Names.gcsCredentialsJson with
            | null
            | "" -> None
            | json -> Some json

        Some(
            create {
                BucketName = bucket
                CredentialsJson = credsJson
                // `fromEnv` reads the service-account JSON once. Deployments
                // that roll the key out of band construct via `create` with
                // `CredentialsJsonProvider = Some f` (Phase 2c) to survive
                // rotation without a restart; ADC deployments need neither.
                CredentialsJsonProvider = None
                // No env key for the endpoint override, deliberately. It
                // exists for emulator / GCS-compatible-endpoint use, which
                // is a `create` concern; a deployment that could set an env
                // var to redirect production storage is a footgun, not a
                // feature.
                EndpointUrl = None
                // Env-built instances compose no audit sink, there being
                // no env var that could name one. A deployment wanting
                // the credential-rejection trail builds the config itself.
                AuditLog = None
            }
        )