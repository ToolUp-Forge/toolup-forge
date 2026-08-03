# Phase 186 — `IAssetStore` upload-validation seam (consumer adoption)

**What changes.** The asset upload path gains a content-validation seam. Before this phase
`UploadRequest.create` validated only what the *caller asserted* — the declared `Content-Type`
against `AcceptedMimeTypes`, the filename, the alt text, the byte count. Nothing inspected the
bytes, and there was no hook for a malware/AV scan. Phase 186 adds:

- `IUploadValidator` — `Name` + `Validate: byte[] * string -> Async<Result<unit, UploadRejection>>`.
- `UploadRejection` — `MimeMismatch of declared * sniffed` | `MalwareDetected of detail` |
  `ValidationUnavailable of reason`, surfaced through the new
  `AssetUploadError.ValidationRejected`.
- `AssetStoreOptions.UploadValidation` — `NoUploadValidator` (default) | `EnabledUploadValidator v`.
- `SniffingUploadValidator` — an in-tree, **vendor-free**, **opt-in** reference validator that
  cross-checks the declared type against `MagicBytes.sniff` and refuses raster-image polyglots.
- `AssetUploadHandler.readCapped` — the upload's byte cap is now enforced **during** the read.

The scan backend never reaches this package (GP 1): a ClamAV / cloud-scan / ICAP companion
implements `IUploadValidator` and is composed by the deployment, exactly as `IAuditSink` and
`INotificationChannel` keep their vendors out of the core.

## Is this a breaking change?

**Behaviourally: no, by default.** `AssetStoreOptions.defaults` carries `NoUploadValidator`, and the
seam short-circuits without touching the bytes (GP 11 / GP 13). A deployment that configures nothing
runs the same checks it ran before.

**At the API surface: one widened constructor.** `AssetStoreOptions` gained a field, so its positional
constructor changed arity. Every in-tree and documented usage is `{ AssetStoreOptions.defaults with … }`,
which is unaffected. A consumer constructing the record **positionally** or **field-by-field without a
`defaults` base** adds `UploadValidation = NoUploadValidator`.

**One ordering change worth knowing about.** The size check now runs *before* the alt-text /
filename / MIME checks, because refusing an oversized upload should not require materialising it
first. An upload that is both oversized *and* missing its alt text now returns `FileTooLarge` where
it previously returned `AltTextRequired`. Both were 400s; only the error case changed.

## ⚠️ Turning validation ON can refuse uploads that previously succeeded

This is the whole point of the seam, and it is worth stating plainly: `SniffingUploadValidator`
**fails closed**. With it composed, an upload is refused when

- the declared `Content-Type` and the actual bytes disagree (`MimeMismatch`) — including the common
  benign case of a client that mislabels a JPEG as `image/png`;
- the bytes match nothing in the magic-byte table (`MimeMismatch(declared, "application/octet-stream")`)
  — this catches SVG, plain text, and any format the table does not cover;
- a raster image carries executable markup in its first 1 KiB (`MimeMismatch(declared, "text/html")`).

Likewise, with **any** validator composed, a validator that cannot reach a verdict — backend down,
credential expired, or the validator itself raising — refuses the upload with
`ValidationUnavailable`. That is deliberate: a scanner answering "clean" because it could not run
turns an outage into a silent admission.

### Rollout order

1. **Ship with `NoUploadValidator`** (the default). Nothing changes; confirm the release is clean.
2. **Shadow-measure before enforcing.** Compose a validator that delegates to
   `SniffingUploadValidator`, logs the verdict, and returns `Ok()` regardless. Run it over real
   traffic for a full upload cycle and read the log: a corpus of legitimately-mislabelled uploads is
   the norm, not the exception, and this is where you find yours.
3. **Widen the accept-list or the sniff options to match what you found** — set
   `AllowUnrecognisedBytes = true` if the deployment legitimately accepts SVG or another
   unsniffable type, and remove types from `AcceptedMimeTypes` you never intended to allow.
4. **Enforce** — return the real verdict.
5. **Add a scan backend last**, once the MIME cross-check is quiet, so a new class of refusal is
   never confused with the previous one.
6. **Alert on `ValidationUnavailable` separately from `MalwareDetected`.** They are different
   incidents: one is an outage, one is a hit. The DU keeps them distinct — keep them distinct
   downstream too.

**Rollback is one field.** Set `UploadValidation = NoUploadValidator` (or drop the
`withUploadValidator` call) and redeploy. No storage migration, no data change; refused uploads were
never persisted.

## Opt-in adoption

```fsharp
open ToolUp.AssetStore

// Byte-level cross-check only, no scan backend.
let options =
    AssetStoreOptions.defaults
    |> AssetStoreOptions.withUploadValidator (SniffingUploadValidator())

// Or tune the sniffing posture.
let lenient =
    AssetStoreOptions.defaults
    |> AssetStoreOptions.withUploadValidator (
        SniffingUploadValidator { MimeSniffOptions.defaults with AllowUnrecognisedBytes = true }
    )

app |> AssetStoreServerApp.withOptions options
```

A scan backend is your own `IUploadValidator`:

```fsharp
type ClamAvValidator(client: MyClamClient) =
    interface IUploadValidator with
        member _.Name = "clamav"

        member _.Validate(bytes, _declaredMime) = async {
            match! client.Scan bytes with
            | Clean -> return Ok()
            | Infected signature -> return Error(MalwareDetected signature)
            | Unreachable reason -> return Error(ValidationUnavailable reason)
        }
```

Composing both checks is your composition — an `IUploadValidator` that calls the sniffer first and
the scanner second. The SDK deliberately does not own that ordering, because whether a mismatch
should short-circuit the (expensive) scan is a deployment policy.

## Verification

- `dotnet build ToolUp.Forge.sln`
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — the
  `SniffingUploadValidator — IUploadValidator contract` list and the
  `Phase 186 — IAssetStore upload-validation seam` list (38 cases). Every refusal case is paired
  with a control asserting a legitimate upload still succeeds; the size probe *measures* bytes
  pulled through a counting stream rather than inferring the cap from the returned error.
- After composing a validator, confirm a deliberately mislabelled upload returns 400 with
  `ValidationRejected (MimeMismatch …)` and that the asset record was not created.

## See also

- [Phase 39 — `IAssetStore` companion](39-iassetstore-companion.md) — the substrate this extends.
- `src/InterPlatform/Server/JsonRpcPeerHost.fs` (`PeerWireLimits`) — the bounded-read idiom
  `readCapped` follows.
- `src/InterPlatform/Server/PeerTokenPolicy.fs` (`IPeerReplayGuard`) — the in-repo precedent for a
  distinct "unavailable" verdict that is never collapsed into a pass.
