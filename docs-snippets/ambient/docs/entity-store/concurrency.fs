// Ambient context for `docs/entity-store/concurrency.md`.
//
// The page shows a module reading, editing and saving one of its own
// entity records through the store, so every block is an excerpt of a
// handler the page never shows in full: the entity record itself, the
// DI-resolved `IEntityStore`, and the caller's resolved scope. Declared
// here so the blocks compile exactly as a reader would copy them.
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore

[<AutoOpen>]
module PageAmbient =

    /// The page's example entity — the three required fields plus the
    /// one the user edits.
    type Note = {
        Id: EntityId
        Type: string
        Version: int
        Body: string
    }

    /// The DI-resolved store a handler receives.
    let store: IEntityStore = failwith "ambient"

    /// The caller's server-resolved storage scope.
    let scopeId: string = failwith "ambient"

    /// The id of the note the user is editing.
    let noteId: EntityId = failwith "ambient"

    /// What the user typed.
    let editedBody: string = failwith "ambient"

    /// The caller the handler resolved — the `AccessContext` whose
    /// `UserId` becomes the `EntityPrincipal` every mutating member has
    /// taken since Phase 806 (renamed from `EntityActor` by Phase 814).
    let caller: AccessContext = failwith "ambient"