module ToolUp.Platform.IImageEmbedder

/// Optional companion for multimodal embedding. Encodes images and
/// free-text queries into a shared vector space so a user can ask
/// "find the slide with the spend curve" and retrieve image regions
/// (charts, figures, scanned diagrams) the text-embedding path cannot
/// reach.
///
/// Off by default. No no-op default ships in `ToolUp.RAG` because there
/// is no honest no-op for "embed an image as a 512-dim vector": the
/// answer is either real CLIP-style embeddings or none at all. Deployments
/// that need image retrieval ship a companion under
/// `src/ImageEmbedders/<Name>/` (CLIP via ONNX, OpenAI image embeddings).
/// Without a companion registered, the KB extractor never produces
/// `ImageRegion` chunks and the index has no image content.
///
/// Image vectors do not share a vector space with text vectors unless the
/// model is genuinely multimodal (CLIP, SigLIP). Even then, image and
/// text chunks have different `Dimensions` from any given text embedder.
/// The composing application MUST route `ImageRegion` chunks (those whose
/// `Metadata["dataTypeId"] = "ImageRegion"`) to a separate `IVectorStore`
/// instance keyed on `(IImageEmbedder.ProviderId, ModelId, Dimensions)`,
/// or to a different `VectorScope` partition that no text query crosses.
/// Mixing dimensions in one store breaks cosine similarity silently.
///
/// Phase 9c portability:
/// 1. Identity by value — inputs are `byte[]` (image), `string`
///    (mime type, text query). No file handles, no streams.
/// 2. Async at every boundary.
/// 3. No callback / supervision hooks.
/// 4. Stateless between calls.
/// 5. No cross-call ordering promises.
/// 6. Precision: vectors are model-dependent; the contract promises only
///    that two calls with the same `(image, modelId)` return the same
///    vector (within numerical tolerance of the underlying inference).
type IImageEmbedder =
    /// Stable identifier for the provider implementation — e.g. `"clip-onnx"`,
    /// `"openai-clip"`. Stamped on every chunk via `Metadata["_imageProvider"]`
    /// so the re-embedding service can detect provider swaps. Never
    /// serialised on the wire to clients.
    abstract ProviderId: string

    /// Stable identifier for the specific model (provider + variant +
    /// version) — e.g. `"openai-clip-vit-l-14"`, `"siglip-base-patch16-224"`.
    /// Stamped on every chunk via `Metadata["_imageModel"]`.
    abstract ModelId: string

    /// Output vector dimensionality. Must match across all calls within a
    /// single provider's lifetime. Stamped on every chunk via
    /// `Metadata["_imageDim"]` so the re-embedding service can detect
    /// dimension changes (rare, but does happen on a model upgrade).
    abstract Dimensions: int

    /// Embed an image into the model's vector space. Implementations are
    /// responsible for resizing / normalising the input to the model's
    /// expected resolution.
    abstract EmbedImage: imageBytes: byte[] -> mimeType: string -> Async<float[]>

    /// Embed a free-text query into the same vector space as `EmbedImage`,
    /// for cross-modal retrieval (text query → image results). Multimodal
    /// models such as CLIP support this natively; image-only models throw.
    abstract EmbedQuery: query: string -> Async<float[]>

/// Reserved `dataTypeId` for image-region chunks produced by an
/// `IImageEmbedder`-backed extraction path. Populated in
/// `Metadata["dataTypeId"]` so the composing app can route these chunks
/// to a dimension-isolated `IVectorStore`.
[<Literal>]
let ImageRegionDataTypeId = "ImageRegion"

/// Reserved metadata keys for image-embed provenance. Symmetric with the
/// text-side `EmbeddingVersion` keys in `IEmbeddingProvider.fs`.
module ImageEmbeddingMetadata =
    [<Literal>]
    let ProviderKey = "_imageProvider"

    [<Literal>]
    let ModelKey = "_imageModel"

    [<Literal>]
    let DimensionsKey = "_imageDim"