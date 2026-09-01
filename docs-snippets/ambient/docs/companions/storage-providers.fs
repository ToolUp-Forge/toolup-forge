// Ambient context for `docs/companions/storage-providers.md`.
//
// The page is a cross-cutting tour of the shipped `IBlobStorage`
// companions, so every wiring block is an excerpt from a composition
// root it never shows in full: the deployment's `config`, the
// already-composed `cloudStorage` the encryption decorator is layered
// over, and the three substrate dependencies `PerScopeKeyResolver`
// takes (`secretStore` for the key material, `memoryCache` for the
// per-scope key cache, `auditLog` for the key-lifecycle trail).
// Declared here so the blocks compile exactly as a reader would copy
// them, with no `open`-ceremony added to the markdown.
//
// The companions' own `open` lines stay in the markdown, because which
// module a companion lives in is what this page teaches — and it is
// where the drift was: the AWS block opened `ToolUp.Storage.AwsS3`,
// which does not exist.
open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets

[<AutoOpen>]
module PageAmbient =

    let config: ServerConfig = failwith "ambient"

    /// The cloud companion composed two sections earlier — what the
    /// encryption-at-rest decorator is layered on top of.
    let cloudStorage: IBlobStorage = failwith "ambient"

    // ─── What `PerScopeKeyResolver` is constructed over ───────────

    /// Where the per-scope data encryption keys live. NOT an
    /// `IBlobStorage`: the resolver reads key material through the
    /// secret backend and caches it in memory.
    let secretStore: ISecretStore = failwith "ambient"

    let memoryCache: IMemoryCache = failwith "ambient"

    /// Where `EncryptionKeyCreated` / key-destruction events land. The
    /// constructor takes it as `IAuditLog option`, so the block passes
    /// `Some auditLog`.
    let auditLog: IAuditLog = failwith "ambient"