namespace ToolUp.PublicRendering

open ToolUp.Platform.IEntityStore

/// Default `IPublicContentApi` impl composing a file-backed
/// `MarkdownContentLoader` with an optional `IEntityStore` overlay for
/// runtime-edited content. File entries win on collision (the
/// loader's in-memory map is consulted first); the entity store is
/// consulted only when the file set has no match.
///
/// **Overlay scope.** v1 falls through to the entity store on
/// `GetPage` only. `ListPages` / `GetCollection` stay file-only until
/// a clear use case demands a server-side index — adding either is
/// additive (declare an index on `PublicPageEntity.registration`,
/// then route the call through `entityStore.FindByIndex`).
///
/// **Constructor surface.** `entityStore` is optional so deployments
/// that have no runtime-edited content pay zero overhead. The default
/// SDK always registers `IEntityStore` in DI, so `PublicRenderingCompose`
/// can pass `Some` unconditionally; passing `None` from a custom
/// composition root short-circuits the fallthrough at no runtime cost.
type PublicContentApiImpl(loader: MarkdownContentLoader, entityStore: IEntityStore option) =

    interface IPublicContentApi with

        member _.GetPage(slug: string) : Async<PublicPage option> = async {
            match loader.GetPage slug with
            | Some page -> return Some page
            | None ->
                match entityStore with
                | None -> return None
                | Some store ->
                    let! result =
                        store.Get<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, slug)

                    return
                        match result with
                        | Ok envelope -> Some envelope.Page
                        | Error _ -> None
        }

        member _.ListPages(prefix: string) : Async<PublicPage list> = async { return loader.ListPages prefix }

        member _.GetCollection(collectionId: string) : Async<PublicPage list> = async {
            return loader.GetCollection collectionId
        }

module PublicContentApiImpl =

    let create (loader: MarkdownContentLoader) (entityStore: IEntityStore option) : IPublicContentApi =
        PublicContentApiImpl(loader, entityStore) :> IPublicContentApi