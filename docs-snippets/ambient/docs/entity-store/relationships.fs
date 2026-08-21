// Ambient context for `docs/entity-store/relationships.md`.
//
// The page introduces `Order` and its `BelongsToCustomer` relationship in
// its first block and then reads both from later blocks, the way a reader
// who scrolled past them would. Declared here so those later blocks
// compile as written; a block that declares its own `Order` shadows this
// one, which is why the declarations sit in an auto-opened module.
open ToolUp.Platform.EntityTypes

[<AutoOpen>]
module PageAmbient =

    type Order = {
        Id: EntityId
        Type: string
        Version: int
        CustomerId: string
        Total: string
    }

    let belongsToCustomer: Relationship = {
        Name = "BelongsToCustomer"
        Target = "Customer"
        Cardinality = Cardinality.ManyToOne
        ForeignKeyField = "CustomerId"
        Direction = RelationshipDirection.Outgoing
        JoinEntity = None
    }