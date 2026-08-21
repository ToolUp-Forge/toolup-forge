// Ambient context for `docs/entity-store/relationships.md`.
//
// The page introduces `Order` and its `BelongsToCustomer` relationship in
// its first block and then reads both from later blocks, the way a reader
// who scrolled past them would. Declared here so those later blocks
// compile as written; a block that declares its own `Order` shadows this
// one, which is why the declarations sit in an auto-opened module.
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore

[<AutoOpen>]
module PageAmbient =

    /// The store and scope every query example on the page runs against —
    /// resolved from DI by the deployment, never constructed in a doc.
    let store: IEntityStore = failwith "ambient"

    let scope: string = failwith "ambient"

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

    /// The many-to-many worked example: the join entity, the relationship
    /// declared through it, and the target the second leg loads. The page
    /// shows the first two in full; `Course` it only ever names.
    type Enrollment = {
        Id: EntityId
        Type: string
        Version: int
        StudentId: string
        CourseId: string
    }

    type Course = {
        Id: EntityId
        Type: string
        Version: int
        Title: string
    }

    let enrolledInCourses: Relationship = {
        Name = "EnrolledInCourses"
        Target = "Course"
        Cardinality = Cardinality.ManyToMany
        ForeignKeyField = "StudentId"
        Direction = RelationshipDirection.Outgoing
        JoinEntity =
            Some {
                EntityType = "Enrollment"
                TargetKeyField = "CourseId"
            }
    }

    let orderRegistration: EntityRegistration<Order> =
        EntityRegistration.create<Order> "Order"
        |> EntityRegistration.withRelationship belongsToCustomer