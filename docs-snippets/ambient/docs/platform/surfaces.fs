// Ambient context for `docs/platform/surfaces.md`.
//
// The mixed-mode composition excerpts show the `Surfaces` list and the
// pipeline around it, not the substrate construction above them — so
// they read the auth provider, the module list, and the optional
// per-module subject migrator a composition root would already hold.
open ToolUp.Platform

[<AutoOpen>]
module PageAmbient =

    let authProvider: IAuthProvider = failwith "ambient"

    let modules: ServerModule list = []

    /// An illustrative per-module migrator — the Forms tier's guest-
    /// draft carry-over, named in the page's prose. Consumers write
    /// their own against `IAnonymousSessionMigrator`.
    module FormDraftMigrator =
        let instance: IAnonymousSessionMigrator = failwith "ambient"