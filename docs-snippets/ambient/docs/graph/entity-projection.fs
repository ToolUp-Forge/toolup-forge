// Ambient context for `docs/graph/entity-projection.md`.
//
// The composition block enrols two of the consumer's own entity types —
// the `Book` / `Author` pair the page's tables use throughout. They are
// the reader's domain, not SDK surface, so they are declared here rather
// than in the block, which stays about the wiring.
open ToolUp.Platform.EntityTypes

[<AutoOpen>]
module PageAmbient =

    type Book = {
        Id: EntityId
        Type: string
        Version: int
        Title: string
        AuthorId: string
    }

    type Author = {
        Id: EntityId
        Type: string
        Version: int
        Name: string
    }

    let bookRegistration: EntityRegistration<Book> =
        EntityRegistration.create<Book> "Book"

    let authorRegistration: EntityRegistration<Author> =
        EntityRegistration.create<Author> "Author"