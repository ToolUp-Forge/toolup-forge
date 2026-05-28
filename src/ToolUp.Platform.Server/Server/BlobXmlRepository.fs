// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Generic
open System.Text
open System.Xml.Linq
open Microsoft.AspNetCore.DataProtection.Repositories
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 9j — DataProtection key ring over IBlobStorage ────────────
//
// The stateless CSRF token (and any other DataProtection-sealed
// payload) must verify on every instance and survive restarts. ASP.NET
// DataProtection persists its key ring through an `IXmlRepository`;
// this adapter points that at the resolved `IBlobStorage` so the ring
// follows the same substrate as all other platform state:
//
//  * local file blob (dev / single box) — keys survive process restart
//  * cloud blob (S3 / Azure / GCS) — keys are shared across replicas
//
// That is what makes the hardened CSRF posture correct multi-instance
// with no session store and no sticky load balancer. Keys are written
// under the platform-reserved `_platform` container; DataProtection
// already encrypts key material at rest with the configured protector,
// and the blob store may add its own at-rest encryption on top.
//
// Key-ring access is infrequent (read once at startup + a periodic
// refresh; write only on key creation/rotation, ~quarterly), so the
// sync-over-async bridge on these `IBlobStorage` calls is acceptable —
// ASP.NET Core has no synchronization context, so `RunSynchronously`
// cannot deadlock here.

[<RequireQualifiedAccess>]
module private BlobDpKeyRing =
    [<Literal>]
    let Container = "_platform"

    [<Literal>]
    let Prefix = "dataprotection/"

/// `IXmlRepository` that persists the DataProtection key ring through
/// the resolved `IBlobStorage`.
type BlobXmlRepository(blob: IBlobStorage) =
    interface IXmlRepository with
        member _.GetAllElements() : IReadOnlyCollection<XElement> =
            let elements = ResizeArray<XElement>()

            let names =
                try
                    blob.List(BlobDpKeyRing.Container, BlobDpKeyRing.Prefix)
                    |> Async.RunSynchronously
                with _ -> []

            for name in names do
                try
                    match blob.Download(BlobDpKeyRing.Container, name) |> Async.RunSynchronously with
                    | Ok bytes -> elements.Add(XElement.Parse(Encoding.UTF8.GetString bytes))
                    | Error _ -> ()
                with _ ->
                    ()

            elements :> IReadOnlyCollection<XElement>

        member _.StoreElement(element: XElement, friendlyName: string) =
            let safeName =
                if String.IsNullOrWhiteSpace friendlyName then
                    Guid.NewGuid().ToString "N"
                else
                    friendlyName
                    |> String.filter (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_')

            let blobName = BlobDpKeyRing.Prefix + safeName + ".xml"
            let bytes = Encoding.UTF8.GetBytes(element.ToString SaveOptions.DisableFormatting)

            blob.Upload(BlobDpKeyRing.Container, blobName, bytes)
            |> Async.RunSynchronously
            |> ignore