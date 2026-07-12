module ToolUp.Platform.Tests.InProcess.RelationshipTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore

// ─── Phase 19c — declarative relationship edges ─────────────────────
//
// Builder + validation (foreign-key-field existence, duplicate-name
// rejection, ManyToMany join requirement), auto-index (a declared
// relationship registers a BlobIndex on the FK field, idempotent with an
// explicit withIndex), relationship-aware queries through the existing
// Phase 19a executor (many-to-one + many-to-many-via-join), backward-
// compat (zero relationships → byte-identical registration), and the
// relationships read accessor (the Phase 68d projection seam).

// ── Foreign-key domain (Order → Customer) ──
type Customer = {
    Id: EntityId
    Type: string
    Version: int
    Name: string
}

type Order = {
    Id: EntityId
    Type: string
    Version: int
    CustomerId: string
    Total: string
}

// ── ManyToMany domain (Student ↔ Course via Enrollment) ──
type Student = {
    Id: EntityId
    Type: string
    Version: int
    Name: string
}

type Course = {
    Id: EntityId
    Type: string
    Version: int
    Title: string
}

type Enrollment = {
    Id: EntityId
    Type: string
    Version: int
    StudentId: string
    CourseId: string
}

let private belongsToCustomer: Relationship = {
    Name = "BelongsToCustomer"
    Target = "Customer"
    Cardinality = Cardinality.ManyToOne
    ForeignKeyField = "CustomerId"
    Direction = RelationshipDirection.Outgoing
    JoinEntity = None
}

let private enrolledInCourses: Relationship = {
    Name = "EnrolledInCourses"
    Target = "Course"
    Cardinality = Cardinality.ManyToMany
    // For ManyToMany the FK lives on the join entity — the field
    // referencing the source (Student) side.
    ForeignKeyField = "StudentId"
    Direction = RelationshipDirection.Outgoing
    JoinEntity =
        Some {
            EntityType = "Enrollment"
            TargetKeyField = "CourseId"
        }
}

let private mkStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-rel-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()

    // Order declares its relationship — auto-indexes CustomerId.
    registry.Register<Order>(
        EntityRegistration.create<Order> "Order"
        |> EntityRegistration.withRelationship belongsToCustomer
    )

    registry.Register<Customer>(EntityRegistration.create<Customer> "Customer")

    // The join entity indexes both foreign keys on its own registration
    // (ManyToMany does not auto-index from the Student side).
    registry.Register<Enrollment>(
        EntityRegistration.create<Enrollment> "Enrollment"
        |> EntityRegistration.withIndex "StudentId" _.StudentId
        |> EntityRegistration.withIndex "CourseId" _.CourseId
    )

    registry.Register<Course>(EntityRegistration.create<Course> "Course")

    let store = BlobEntityStore(dos, blob, registry, None) :> IEntityStore
    let scope = "team-rel-" + Guid.NewGuid().ToString("N").Substring(0, 6)
    store, scope

let tests =
    testList "EntityRelationships" [

        // ── 19c.A — builder + validation ──

        testCase "withRelationship rejects a foreign-key field that doesn't exist on the entity"
        <| fun () ->
            Expect.throws
                (fun () ->
                    EntityRegistration.create<Order> "Order"
                    |> EntityRegistration.withRelationship {
                        belongsToCustomer with
                            ForeignKeyField = "CustmerId" // typo
                    }
                    |> ignore)
                "a typo'd ForeignKeyField must be rejected at registration time"

        testCase "withRelationship rejects a duplicate relationship name"
        <| fun () ->
            Expect.throws
                (fun () ->
                    EntityRegistration.create<Order> "Order"
                    |> EntityRegistration.withRelationship belongsToCustomer
                    |> EntityRegistration.withRelationship belongsToCustomer
                    |> ignore)
                "declaring the same relationship name twice must be rejected"

        testCase "ManyToMany without a JoinEntity is rejected"
        <| fun () ->
            Expect.throws
                (fun () ->
                    EntityRegistration.create<Student> "Student"
                    |> EntityRegistration.withRelationship {
                        enrolledInCourses with
                            JoinEntity = None
                    }
                    |> ignore)
                "a ManyToMany relationship requires a join entity"

        testCase "a non-ManyToMany relationship with a JoinEntity is rejected"
        <| fun () ->
            Expect.throws
                (fun () ->
                    EntityRegistration.create<Order> "Order"
                    |> EntityRegistration.withRelationship {
                        belongsToCustomer with
                            JoinEntity =
                                Some {
                                    EntityType = "X"
                                    TargetKeyField = "Y"
                                }
                    }
                    |> ignore)
                "a join entity only resolves a ManyToMany link"

        testCase "ManyToMany with a JoinEntity validates (FK lives on the join, not the source)"
        <| fun () ->
            // Should NOT throw — StudentId is not a field on Student, but
            // ManyToMany does not reflect the FK on the source entity.
            let reg =
                EntityRegistration.create<Student> "Student"
                |> EntityRegistration.withRelationship enrolledInCourses

            Expect.equal (EntityRegistration.relationships reg |> List.length) 1 "relationship declared"

        // ── 19c.B — auto-index ──

        testCase "declaring a foreign-key relationship auto-registers an index on the FK field"
        <| fun () ->
            let reg =
                EntityRegistration.create<Order> "Order"
                |> EntityRegistration.withRelationship belongsToCustomer

            Expect.isSome (EntityRegistration.tryFindIndex "CustomerId" reg) "CustomerId is auto-indexed"

        testCase "auto-index is idempotent with an explicit withIndex on the same field"
        <| fun () ->
            let reg =
                EntityRegistration.create<Order> "Order"
                |> EntityRegistration.withIndex "CustomerId" _.CustomerId
                |> EntityRegistration.withRelationship belongsToCustomer

            let customerIdIndexes = reg.Indexes |> List.filter (fun i -> i.Name = "CustomerId")
            Expect.hasLength customerIdIndexes 1 "no duplicate CustomerId index"

        testCase "ManyToMany does not auto-index the source entity"
        <| fun () ->
            let reg =
                EntityRegistration.create<Student> "Student"
                |> EntityRegistration.withRelationship enrolledInCourses

            Expect.isEmpty reg.Indexes "no index added on the source side for ManyToMany"

        // ── 19c.D — accessor ──

        testCase "relationships accessor returns the declared list"
        <| fun () ->
            let reg =
                EntityRegistration.create<Order> "Order"
                |> EntityRegistration.withRelationship belongsToCustomer

            Expect.equal (EntityRegistration.relationships reg) [ belongsToCustomer ] "declared relationship returned"

        // ── GP 11 — backward compat ──

        testCase "an entity with no declared relationships is byte-identical to a pre-19c registration"
        <| fun () ->
            let reg =
                EntityRegistration.create<Order> "Order"
                |> EntityRegistration.withIndex "CustomerId" _.CustomerId

            Expect.isEmpty reg.Relationships "no relationships"
            Expect.hasLength reg.Indexes 1 "only the explicit index"
            Expect.equal reg.Indexes[0].Name "CustomerId" "the explicit index"

        // ── 19c.C — relationship-aware queries ──

        testCaseAsync "relatedTo returns the correct many-to-one set through the existing executor"
        <| async {
            let store, scope = mkStore ()

            for c in [ "c-1"; "c-2" ] do
                let! _ =
                    store.Save<Customer>(
                        scope,
                        {
                            Id = c
                            Type = "Customer"
                            Version = 0
                            Name = c
                        }
                    )

                ()

            let orders = [ "o-1", "c-1"; "o-2", "c-1"; "o-3", "c-2" ]

            for id, cust in orders do
                let! _ =
                    store.Save<Order>(
                        scope,
                        {
                            Id = id
                            Type = "Order"
                            Version = 0
                            CustomerId = cust
                            Total = "10"
                        }
                    )

                ()

            let q =
                EntityQuery.forType<Order> "Order"
                |> EntityQuery.where (EntityQuery.relatedTo belongsToCustomer "c-1")

            match! store.Query<Order>(scope, q) with
            | Result.Ok found ->
                let ids = found |> List.map _.Id |> List.sort
                Expect.equal ids [ "o-1"; "o-2" ] "c-1's orders, via the declared relationship"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "ManyToMany resolves through the join entity in two indexed lookups"
        <| async {
            let store, scope = mkStore ()

            let courses = [ "course-fs", "F#"; "course-ml", "ML"; "course-db", "Databases" ]

            for id, title in courses do
                let! _ =
                    store.Save<Course>(
                        scope,
                        {
                            Id = id
                            Type = "Course"
                            Version = 0
                            Title = title
                        }
                    )

                ()

            // s-1 enrolled in F# + Databases; s-2 in ML.
            let enrollments = [
                "e-1", "s-1", "course-fs"
                "e-2", "s-1", "course-db"
                "e-3", "s-2", "course-ml"
            ]

            for id, student, course in enrollments do
                let! _ =
                    store.Save<Enrollment>(
                        scope,
                        {
                            Id = id
                            Type = "Enrollment"
                            Version = 0
                            StudentId = student
                            CourseId = course
                        }
                    )

                ()

            // Leg 1 — query the join entity by the source key.
            let joinQuery =
                EntityQuery.forType<Enrollment> "Enrollment"
                |> EntityQuery.where (EntityQuery.relatedTo enrolledInCourses "s-1")

            let! joinResult = store.Query<Enrollment>(scope, joinQuery)

            let courseIds =
                match joinResult with
                | Result.Ok rows -> rows |> List.map _.CourseId
                | Result.Error e -> failwithf "join query failed: %A" e

            // Leg 2 — load the targets by their extracted ids.
            let! loaded =
                courseIds
                |> List.map (fun cid -> store.Get<Course>(scope, "Course", cid))
                |> Async.Sequential

            let titles =
                loaded
                |> Array.toList
                |> List.choose (function
                    | Result.Ok c -> Some c.Title
                    | Result.Error _ -> None)
                |> List.sort

            Expect.equal titles [ "Databases"; "F#" ] "s-1's courses resolved via the join entity"
        }
    ]