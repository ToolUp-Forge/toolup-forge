// Ambient context for `docs/companions/media-library.md`.
//
// The page teaches one companion end to end, so almost every block is an
// excerpt from a composition root it never shows in full: the substrate
// the enabling block composes (`blobStorage` / `authProvider` /
// `notifications`), the half-built `app` every later `withOptions`
// example pipes into, the resolved `mediaLibrary` a serving example
// probes, and the item / scope / window a range or signed-URL call is
// about. Declared here so the blocks compile exactly as a reader would
// copy them, with no `open`-ceremony added to the markdown.
open ToolUp.MediaLibrary
open ToolUp.MediaLibrary.MediaCompose
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Usage

[<AutoOpen>]
module PageAmbient =

    /// The substrate the "Enabling" block composes. A deployment resolves
    /// these from its own composition root.
    let blobStorage: IBlobStorage = failwith "ambient"

    let authProvider: IAuthProvider = failwith "ambient"

    let notifications: INotificationChannel = failwith "ambient"

    /// The partly-built app every later `withOptions` / `withTranscoder`
    /// example pipes into — the value the "Enabling" block is holding
    /// mid-pipeline.
    let app: MediaLibraryServerApp = failwith "ambient"

    /// The composed library, and the item / scope a serving example is
    /// about. `container` is the resolved `StorageScope.Container`.
    let mediaLibrary: IMediaLibrary = failwith "ambient"

    let mediaId: MediaId = failwith "ambient"

    let viewerScope: StorageScope = failwith "ambient"

    let container: string = failwith "ambient"

    /// A derived blob's path within the item's derived directory, and the
    /// window a player asked for.
    let relativePath: string = failwith "ambient"

    let range: ByteRange = failwith "ambient"

    /// The deployment's options record, and the upload an admin surface
    /// has in hand when it constructs a `MediaUploadRequest`.
    let options: MediaLibraryOptions = failwith "ambient"

    let bytes: byte[] = failwith "ambient"

    let filename: string = failwith "ambient"

    let mimeType: string = failwith "ambient"

    let uploadedBy: string = failwith "ambient"

    let caption: string option = failwith "ambient"

    /// What a cloud-transcode `submit` callback comes back with — the
    /// master manifest plus its segments, as the vendor rendered them.
    let producedFiles: TranscodedFile list = failwith "ambient"

    /// The read path the telemetry rollup example folds — the same proxy
    /// the usage dashboard already holds — and its date window.
    let usageQueryApi: IUsageQueryApi = failwith "ambient"

    let from: DateTime = failwith "ambient"

    let until: DateTime = failwith "ambient"