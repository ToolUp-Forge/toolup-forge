# Phase 108 — time-bound direct-download URLs for KB originals

**Applies to:** `ToolUp.Platform.Core`, `ToolUp.KnowledgeBase.{Core,Server}`,
`ToolUp.Storage.{AzureBlob,AwsS3,GoogleCloud}`.
**Breaking:** one member added to `IOriginalSourceResolver` (see below). Everything
else is additive and defaults to prior behaviour (GP 11).

## What changes

Serving a multi-MB original by streaming it through the app tier on every citation
click is wasteful once a deployment sits on object storage that can mint its own
time-bounded URL. Phase 108 adds that capability and wires it into the Knowledge
Base, opt-in and off by default.

1. **`ISignedUrlBlobStorage`** (`ToolUp.Platform.Core`, `Shared/Interfaces/IBlobStorage.fs`)
   — an optional capability interface implemented *alongside* `IBlobStorage`, probed
   by type test, exactly like Phase 600's `IConditionalBlobStorage`. Not a member on
   `IBlobStorage`: thirteen in-tree implementers would break, and signing is not merely
   unimplemented on the local filesystem — it is meaningless there. Shipped implementations:
   Azure (service SAS), S3 (presigned GET), GCS (V4-signed URL).
2. **`BlobStorage.trySignedUrl`** — the probe helper. `Ok (Some url)` / `Ok None`
   ("cannot sign; proxy instead") / `Error msg` ("tried and failed; do NOT proxy").
3. **`IOriginalSourceResolver.ResolveMetadata`** — the same per-`KnowledgeSource`
   resolution decision answered from blob *metadata*, returning an `OriginalLocation`
   (signable blob name + file name + content type + size). This is the fix Phase 200
   recorded and could not make from where it sat.
4. **`KnowledgeBase.Server.withSignedOriginalUrls`** — the compose-time opt-in.
5. **`KnowledgeApi.GetOriginalDelivery`** — a delivery-mode-aware fetch returning
   `PreviewContent` (Phase 200's DU). `GetOriginalDocument` is untouched.

## If you implement `IOriginalSourceResolver` — required, one line

This is the only breaking change. Add the new member:

```diff
  { new IOriginalSourceResolver with
        member _.Resolve(storage, container, doc) = async { ... }
+
+       member this.ResolveMetadata(storage, container, doc) =
+           OriginalSourceResolver.locationViaResolve this storage container doc
  }
```

`locationViaResolve` satisfies the contract by downloading and describing what it got.
It is **correct but not cheap**, and its `BlobName` is `None`, so signed-URL delivery
falls back to proxying rather than signing a blob it cannot name. If your resolver
knows its blob names, implement the member properly instead and get the byte-light
path:

```fsharp
member _.ResolveMetadata(storage, container, doc) = async {
    let blobName = sprintf "custom/%s/%s" doc.Id doc.FileName
    match! storage.GetMetadata(container, blobName) with
    | Ok meta ->
        return Some {
            BlobName = Some blobName
            FileName = doc.FileName
            ContentType = "application/pdf"
            SizeBytes = meta.Size
        }
    | Error _ -> return None
}
```

**Contract:** `ResolveMetadata` must return `None` exactly when `Resolve` would.
A caller that branches on it must not be able to serve a link to something `Resolve`
calls absent.

## If you want signed originals — opt in

```fsharp
open KnowledgeBase.Server
open KnowledgeBase.ServerOriginalPreviewSeam

app
|> withSignedOriginalUrls { PreviewSignedUrlOptions.defaults with Ttl = TimeSpan.FromMinutes 15.0 }
```

Then call `GetOriginalDelivery` instead of `GetOriginalDocument` and branch on the result:

```fsharp
match! knowledgeApi.GetOriginalDelivery docId with
| Ok (PreviewContent.SignedUrl (url, expiresAt)) -> // fetch the bytes directly from storage
| Ok (PreviewContent.Inline original) -> // the bytes arrived inline, as before
| Error e -> // NotInScope / NoOriginalAvailable / OriginalRetrievalFailed
```

With a custom resolver, compose the seam explicitly so *your* resolution branches run:

```fsharp
app |> withOriginalPreviewSeam (createBlobSignedUrl myResolver options)
```

## What you get on each backend

| Composed `IBlobStorage` | `GetOriginalDelivery` returns |
|---|---|
| Azure / S3 / GCS, credentials able to sign | `SignedUrl` — bytes never touch the app tier |
| GCS on Application Default Credentials | `Inline` — no signable private key in-process |
| Azure built from a SAS-token connection string | `Inline` — no account key to sign with |
| Local filesystem, in-memory | `Inline` |
| **Anything wrapped by `EncryptedBlobStorage`** | `Inline` — the decorator does not implement the capability, so nobody is ever handed a URL to ciphertext |
| A signing backend that errors mid-mint | `Error (OriginalRetrievalFailed …)` — deliberately NOT a silent fall back to shipping the megabytes you excluded |

## Verification

- `GetOriginalDocument` is byte-for-byte the Phase 102 path with or without the opt-in.
- Without `withSignedOriginalUrls`, `GetOriginalDelivery` returns `Inline` carrying
  exactly what `GetOriginalDocument` returns — pinned by test.
- The scope check runs **before** any mint (GP 4). A signed URL is a bearer token, so
  ordering *is* the access control; the test asserts an out-of-scope fetch produces
  `NotInScope` **and zero mints**, because asserting the refusal alone would still pass
  if a URL had quietly been issued.

## Rollback

Remove the `withSignedOriginalUrls` call. Nothing else needs reverting — no seam
registered means no behaviour change anywhere.
