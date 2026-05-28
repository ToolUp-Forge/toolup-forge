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
                use ms = new MemoryStream()
                do! file.CopyToAsync ms
                let bytes = ms.ToArray()

                let altText = formField form "altText" |> Option.defaultValue ""
                let caption = formField form "caption" |> Option.filter (fun s -> s.Length > 0)

                let profile =
                    formField form "profile"
                    |> Option.map DerivativeProfileId
                    |> Option.defaultValue DerivativeProfileId.webDefault

                let validation =
                    UploadRequest.create options bytes file.FileName file.ContentType altText caption userId profile

                match validation with
                | Error err ->
                    ctx.SetStatusCode 400
                    return! ctx.WriteJsonAsync err
                | Ok request ->
                    let! result = store.Upload(container, request) |> Async.StartAsTask

                    match result with
                    | Error err ->
                        ctx.SetStatusCode 400
                        return! ctx.WriteJsonAsync err
                    | Ok record ->
                        ctx.SetStatusCode 201
                        return! ctx.WriteJsonAsync record
    }