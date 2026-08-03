// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AssetStore.AssetUploadHandler

open System.IO
open Microsoft.AspNetCore.Http
open Giraffe
open ToolUp.Platform

/// Multipart-form upload endpoint mounted at
/// `POST /api/assets/upload`. Reads the file part and the alt-
/// text / caption / profile form fields, validates via
/// `UploadRequest.create`, hands to `IAssetStore.Upload`, and
/// returns the `AssetRecord` JSON (or a typed error JSON).
///
/// Why not Fable.Remoting? Because raw `byte[]` payloads round-
/// tripped through MsgPack are awkward for browser file inputs
/// — Feliz's `<input type="file">` exposes a `File` blob the
/// browser will chunk-encode for us if we use a `FormData` POST.
/// One HTTP request, no double serialisation, native browser
/// upload-progress events.
///
/// Required form parts:
///   - `file` (single file part — binary)
///   - `altText` (string — required, non-empty)
///   - `profile` (string — `DerivativeProfileId` value)
///
/// Optional form parts:
///   - `caption` (string)

[<RequireQualifiedAccess>]
type UploadResponse =
    | Created of AssetRecord
    | Rejected of AssetUploadError
    | BadRequest of message: string

let private formField (form: IFormCollection) (key: string) =
    match form.TryGetValue key with
    | true, values when values.Count > 0 -> Some(values[0])
    | _ -> None

let private readFile (form: IFormCollection) =
    if form.Files.Count = 0 then
        None
    else
        let file = form.Files[0]

        if file.Length <= 0L then None else Some file

/// Phase 186 — copy `source` into memory under a hard ceiling,
/// stopping the moment the ceiling is passed.
///
/// The pre-186 handler did `file.CopyToAsync ms` and let
/// `UploadRequest.create` compare `bytes.Length` against `MaxBytes`
/// afterwards — which is a size check that has already paid the cost
/// it exists to avoid. A payload larger than the cap was materialised
/// in full (on the large-object heap, past 85 KB) and only then
/// refused. This reads forward once and abandons at the ceiling, so
/// the bytes past it are never copied anywhere and the ones before it
/// are dropped with the `MemoryStream`.
///
/// `Error total` carries the byte count actually pulled, which is
/// `cap + 1 .. cap + bufferSize` — deliberately NOT the payload's
/// true size, because learning that would mean reading all of it. The
/// caller reconciles it against the multipart section's own declared
/// length before reporting.
///
/// Exposed (rather than `private`) so a test can drive it with a
/// counting stream and MEASURE that the read stopped, instead of
/// inferring it from the returned error — the same reason the peer
/// substrate's wire-limit pack counts bytes pulled.
let readCapped (source: Stream) (cap: int64) : System.Threading.Tasks.Task<Result<byte[], int64>> = task {
    let buffer = Array.zeroCreate<byte> 16384
    use collected = new MemoryStream()
    let mutable total = 0L
    let mutable overflowed = false
    let mutable finished = false

    while not finished do
        let! read = source.ReadAsync(buffer, 0, buffer.Length)

        if read = 0 then
            finished <- true
        else
            total <- total + int64 read

            if total > cap then
                overflowed <- true
                finished <- true
            else
                collected.Write(buffer, 0, read)

    if overflowed then
        return Error total
    else
        return Ok(collected.ToArray())
}

/// Build the upload handler. Resolves `IAssetStore` per-request
/// from DI, reads the scope container from
/// `ctx.Items["ToolUp.StorageScope"]`, parses the multipart
/// form, validates, uploads. Authentication is the SDK's
/// default — `AuthEnforcementMiddleware` already covered the
/// request before we get here (the route is not registered as
/// anonymous).
let uploadHandler (options: AssetStoreOptions) : HttpHandler =
    route "/api/assets/upload"
    >=> POST
    >=> fun next (ctx: HttpContext) -> task {
        if not ctx.Request.HasFormContentType then
            ctx.SetStatusCode 400

            return!
                ctx.WriteJsonAsync {|
                    error = "Expected multipart/form-data"
                |}
        else
            let! form = ctx.Request.ReadFormAsync()

            let scopeContainer =
                match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                | true, (:? StorageScope as s) -> Some s.Container
                | _ -> None

            let assetStore =
                match ctx.RequestServices.GetService(typeof<IAssetStore>) with
                | :? IAssetStore as s -> Some s
                | _ -> None

            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) when id <> "" -> id
                | _ -> "anonymous"

            match scopeContainer, assetStore, readFile form with
            | None, _, _ ->
                ctx.SetStatusCode 401
                return! ctx.WriteJsonAsync {| error = "No active scope" |}
            | _, None, _ ->
                ctx.SetStatusCode 500
                return! ctx.WriteJsonAsync {| error = "Asset store not enabled" |}
            | _, _, None ->
                ctx.SetStatusCode 400
                return! ctx.WriteJsonAsync {| error = "Missing file part" |}
            | Some container, Some store, Some file ->
                // Phase 186 — checks run cheapest-first, and nothing
                // is written to storage until every one of them has
                // passed:
                //
                //   1. declared section length vs `MaxBytes`  — refused
                //      without copying a byte into our buffer;
                //   2. the read itself, capped                — a
                //      section whose real length exceeds what it
                //      declared is abandoned at the ceiling;
                //   3. `UploadRequest.create`                 — alt
                //      text, filename, declared MIME accept-list
                //      (unchanged, and still the cheap string checks);
                //   4. `IUploadValidator`                     — the
                //      seam: byte inspection and/or a scan backend,
                //      the only step that may make a network call;
                //   5. `IAssetStore.Upload`.
                //
                // Step 1 does not make the framework's own buffering
                // disappear — `ReadFormAsync` above has already spooled
                // the multipart body per Kestrel's
                // `MultipartBodyLengthLimit`. What it bounds is the
                // managed `byte[]` this handler allocates and hands to
                // the store, which is the part the SDK controls.
                let cap = options.MaxBytes
                let declaredLength = file.Length

                if declaredLength > cap then
                    ctx.SetStatusCode 400
                    return! ctx.WriteJsonAsync(FileTooLarge(declaredLength, cap))
                else
                    use source = file.OpenReadStream()
                    let! read = readCapped source cap

                    match read with
                    | Error pulled ->
                        ctx.SetStatusCode 400
                        return! ctx.WriteJsonAsync(FileTooLarge(max declaredLength pulled, cap))
                    | Ok bytes ->
                        let altText = formField form "altText" |> Option.defaultValue ""
                        let caption = formField form "caption" |> Option.filter (fun s -> s.Length > 0)

                        let profile =
                            formField form "profile"
                            |> Option.map DerivativeProfileId
                            |> Option.defaultValue DerivativeProfileId.webDefault

                        let validation =
                            UploadRequest.create
                                options
                                bytes
                                file.FileName
                                file.ContentType
                                altText
                                caption
                                userId
                                profile

                        match validation with
                        | Error err ->
                            ctx.SetStatusCode 400
                            return! ctx.WriteJsonAsync err
                        | Ok request ->
                            // The seam. `NoUploadValidator` (the
                            // default) short-circuits here, so an
                            // existing deployment reaches `Upload` on
                            // exactly the same path it always did.
                            let! verdict =
                                UploadValidator.run options.UploadValidation request.Bytes request.MimeType
                                |> Async.StartAsTask

                            match verdict with
                            | Error rejection ->
                                // Refused before `Upload` — the bytes
                                // never reach blob storage, and a
                                // `ValidationUnavailable` verdict is a
                                // refusal like any other.
                                ctx.SetStatusCode 400
                                return! ctx.WriteJsonAsync(ValidationRejected rejection)
                            | Ok() ->
                                let! result = store.Upload(container, request) |> Async.StartAsTask

                                match result with
                                | Error err ->
                                    ctx.SetStatusCode 400
                                    return! ctx.WriteJsonAsync err
                                | Ok record ->
                                    ctx.SetStatusCode 201
                                    return! ctx.WriteJsonAsync record
    }