// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 433 — component data-footprint manifest ────────────────────
//
// What a composed component STORES, and where: per `ComponentId`, the
// data classes it reads, writes, and persists — entities behind
// `IDataObjectStore`, blobs behind `IBlobStorage`, audit records behind
// an audit sink, event streams behind `IEventStore`, credential material
// behind `ISecretStore` — each carrying a `ContainsPii` marker. Joined
// across a composition the way Phase 296 joins *effects*, so
// "what does this composition store, and where?" is a static property
// rather than a source-reading exercise.
//
// This is the compliance face of the effect-join. Phase 296 answers
// "can this composition touch the world?"; the footprint answers "which
// of the world's stores does it leave data in, and is any of it
// personal?" — the input a data-subject-request (DSR) / offboarding
// pipeline needs to be PROVABLY complete rather than
// convention-complete, and the composition-level widening of Phase 8a's
// object-level lineage.
//
// **Why a sidecar map rather than a fourth field on
// `CompanionCapability`.** Adding a field to an F# record changes its
// constructor signature — a break for every consumer that constructs
// one, and a removal under the public-API baseline gate. This is the
// call Phase 585 made for `ClassifiedCompositionRule` and Phase 432 for
// `RequirementsSignature`. `CapabilitySignature`, `RequirementsSignature`
// and `FootprintSignature` are three parallel `Map<ComponentId, _>`
// projections of the same component set; a tool reads them as one
// descriptor by joining on the id they all key against.
//
// **Derived where the seam already knows (433.B).** A registered
// `DataType` names its own producer — the store knows which module
// registered which type — so its footprint entry derives with zero
// per-module effort (`DataFootprintDerivation.ofModules`, Server tier).
// The residual half is what no registration can see: the classes a
// COMPANION persists behind its own backing store, and the `ContainsPii`
// judgement, which is a property of the data's meaning rather than of
// its registration. Those are DECLARED, merged on top of the derived
// half, declaration winning on a collision.
//
// **Zero cost when undeclared (GP 11 / GP 13).** The identity is the
// EMPTY footprint: a component that declares nothing stores nothing as
// far as the join is concerned, an empty signature composes to the empty
// surface, and the Server-tier preflight registers no validator at all.
// A deployment that never asks is byte-for-byte what it was.
//
// **Generic substrate (GP 1) + Fable-safe.** Only `ComponentId`, small
// closed DUs, strings and booleans — no vendor type, no domain type, no
// BCL surface beyond `System.String`. A scaffolding / authoring tool on
// either tier reads the same shapes.

/// The store seam a data class lives behind — which SDK interface a
/// deployment would have to look inside to find the bytes. Closed for
/// the seams the SDK itself owns, with `ForeignStoreSeam` the honest
/// escape for a companion's own backing store (a vendor database, a
/// search index) that no SDK interface fronts.
type DataStoreSeam =
    /// Structured objects behind `IDataObjectStore` (the seam registered
    /// `DataType`s land in).
    | DataObjectStoreSeam
    /// Binary content behind `IBlobStorage`.
    | BlobStorageSeam
    /// Append-only module events behind `IEventStore`.
    | EventStoreSeam
    /// Audit records behind `IAuditLog` / a replicating `IAuditSink`.
    | AuditSinkSeam
    /// Credential material behind `ISecretStore`.
    | SecretStoreSeam
    /// A companion's own backing store, named by the companion. Never a
    /// default chosen to avoid classifying — use it when the data
    /// genuinely does not sit behind an SDK store interface.
    | ForeignStoreSeam of storeName: string

/// What KIND of data a class is, independent of the seam it sits behind.
/// Seam and kind are orthogonal on purpose: an entity can be archived to
/// blob storage, and an audit record can be replicated to a foreign
/// store, without either fact changing what the data IS.
type DataClassKind =
    /// A structured domain record (a registered `DataType`, an entity).
    | EntityClass
    /// Binary content — an upload, an export bundle, a rendered artefact.
    | BlobClass
    /// An audit record: who did what, when.
    | AuditRecordClass
    /// An append-only event stream entry.
    | EventStreamClass
    /// Credential material — keys, tokens, connection strings.
    | SecretMaterialClass

/// One class of data a component touches: a stable name, what kind it is,
/// which store seam it lives behind, and whether it carries personal
/// data.
///
/// `ContainsPii` is a DECLARED judgement, never inferred. Nothing in a
/// registration can tell whether a column holds an order quantity or a
/// home address, so a derived class defaults to `false` and a declaration
/// raises it. That default is the conservative one for BEHAVIOUR (an
/// undeclared class adds no preflight finding, so a pre-433 deployment is
/// unchanged); it is deliberately NOT a claim that the data is
/// PII-free — an unclassified class is unclassified, which is what the
/// coverage report says about it.
type DataClass = {
    /// The class's stable name — a `DataType.Id`, a blob container role,
    /// an event type, a secret family. Never a display label.
    ClassName: string
    ClassKind: DataClassKind
    /// The store seam the class lives behind.
    Seam: DataStoreSeam
    /// Whether this class carries personal data. Declared, never
    /// inferred.
    ContainsPii: bool
}

/// Which way a component touches a class. Ordered from least to most
/// consequential for a data-at-rest question: reading leaves nothing
/// behind, writing may be transient, persisting is what a DSR has to
/// reach.
type DataAccessMode =
    /// The component reads the class but writes none of it.
    | ReadAccess
    /// The component writes the class (which may or may not outlive the
    /// request — see `PersistAccess`).
    | WriteAccess
    /// The component leaves the class AT REST in its store: the access
    /// mode a data-subject request has to be able to reach.
    | PersistAccess

/// One component's data footprint: what it reads, what it writes, and
/// what it leaves at rest. Sets rather than lists, so a repeated
/// declaration is idempotent and ordering is irrelevant.
///
/// `Persists` is deliberately not implied by `Writes`: a component can
/// write to a stream it does not own the retention of (a metric, a
/// transient cache), and the DSR question is only ever about what is
/// still there afterwards. A component that both writes and persists
/// declares both.
type DataFootprint = {
    /// The component this footprint belongs to.
    Component: ComponentId
    Reads: Set<DataClass>
    Writes: Set<DataClass>
    Persists: Set<DataClass>
}

/// Every composed component's data footprint, keyed by stable
/// `ComponentId` — the parallel of Phase 282's `CapabilitySignature` and
/// Phase 432's `RequirementsSignature` over the same id space. An ABSENT
/// id has the empty footprint (the join identity), so an undeclared
/// component never changes the join (GP 11).
type FootprintSignature = Map<ComponentId, DataFootprint>

/// The whole composition's data-at-rest surface — the join of every
/// component's footprint, with the per-component attribution collapsed.
/// The answer to "what does this composition store, and where?" in one
/// value.
///
/// Field names are deliberately distinct from `DataFootprint`'s: two
/// records sharing a full field-name set make every unannotated
/// construction ambiguous under F#'s last-declared-wins inference, which
/// is the hazard recorded on `EventTopology.Participants`.
type CompositionDataFootprint = {
    ReadClasses: Set<DataClass>
    WrittenClasses: Set<DataClass>
    PersistedClasses: Set<DataClass>
}

/// A declarative claim that one data class is reachable by the
/// deployment's data-subject-request paths: which composed
/// `IDataExporter`s export it and which `IErasureHandler`s erase it, or
/// why it needs neither.
///
/// **A coverage CLAIM, not a behavioural test.** Nothing here executes a
/// handler or inspects a store; the check is that every persisted
/// PII-flagged class is claimed, and that each claimed path name matches
/// a DSR path this deployment actually composed. Whether the named
/// handler erases everything it should is a question for the handler's
/// own tests — this rules out the failure that has no test at all: a
/// class nobody remembered to wire up.
type DsrCoverage = {
    /// The class this claim covers. Matched to a footprint class by
    /// IDENTITY (`DataClass.identity` — name + seam), not by structural
    /// equality, so a claim stays valid when the class is later marked
    /// PII.
    CoveredClass: DataClass
    /// Names of the composed `IDataExporter`s that export this class.
    Exporters: string list
    /// Names of the composed `IErasureHandler`s that erase this class.
    Erasers: string list
    /// Why this class needs no DSR path — anonymised at rest, retained
    /// under legal hold, or genuinely not subject-linked. `Some` makes
    /// the claim complete without naming a path; the reason is reported,
    /// so an exemption is a visible decision rather than a silent gap.
    Exemption: string option
}

/// A named gap in DSR coverage — one persisted PII-flagged class with no
/// adequate claim, or a claim naming a path this deployment does not
/// compose. Data, not a string, so a report / dashboard / CI gate can
/// group it however it likes.
type DsrCoverageGap = {
    /// The class the gap is about.
    GapClass: DataClass
    /// The components that persist it, in id order.
    PersistedBy: ComponentId list
    /// The claimed-but-unresolved path names, empty when the class is
    /// unclaimed altogether.
    UnresolvedPaths: string list
    /// One line naming what is missing.
    Detail: string
}

/// The structural difference between two footprint signatures, keyed by
/// `(ComponentId, DataAccessMode, DataClass)` — never by list position,
/// so re-ordering declarations diffs to empty. Feeds the composition
/// golden-file CI gate: a component that starts persisting a PII class
/// is a reviewable change rather than a silent one.
///
/// A separate delta type rather than fields grown onto `CompositionDelta`
/// (Phase 286), for the reason recorded on `ClassifiedCompositionRule`
/// and `EventTopologyDelta`: growing a shipped F# record breaks its
/// constructor. The deltas join on `ComponentId`, which they all carry.
type DataFootprintDelta = {
    FootprintComponentsAdded: ComponentId list
    FootprintComponentsRemoved: ComponentId list
    AccessAdded: (ComponentId * DataAccessMode * DataClass) list
    AccessRemoved: (ComponentId * DataAccessMode * DataClass) list
}

/// A data class projected to plain strings — the shape a golden file or
/// an external tool persists, so neither depends on the F# union
/// representations round-tripping through a serialiser.
type DataClassWire = {
    Name: string
    KindToken: string
    SeamToken: string
    Pii: bool
}

/// One component's footprint projected to plain strings. Field names are
/// distinct from both `DataFootprint`'s and `CompositionDataFootprint`'s
/// for the field-inference reason recorded above.
type DataFootprintWireEntry = {
    FootprintComponent: string
    Read: DataClassWire list
    Written: DataClassWire list
    Persisted: DataClassWire list
}

[<RequireQualifiedAccess>]
module DataStoreSeam =

    /// A stable, human-readable token for a seam — for reports,
    /// manifests, golden files, and authoring tools. Never positional;
    /// matches the DU case.
    let toWireString (seam: DataStoreSeam) : string =
        match seam with
        | DataObjectStoreSeam -> "IDataObjectStore"
        | BlobStorageSeam -> "IBlobStorage"
        | EventStoreSeam -> "IEventStore"
        | AuditSinkSeam -> "IAuditSink"
        | SecretStoreSeam -> "ISecretStore"
        | ForeignStoreSeam name -> "foreign:" + name

    /// Read a persisted seam token back. An unrecognised token
    /// round-trips as a `ForeignStoreSeam`, which is the honest reading:
    /// a store this build does not know about is exactly a foreign one.
    let ofWireString (token: string) : DataStoreSeam =
        match token with
        | "IDataObjectStore" -> DataObjectStoreSeam
        | "IBlobStorage" -> BlobStorageSeam
        | "IEventStore" -> EventStoreSeam
        | "IAuditSink" -> AuditSinkSeam
        | "ISecretStore" -> SecretStoreSeam
        | other when other.StartsWith "foreign:" -> ForeignStoreSeam(other.Substring "foreign:".Length)
        | other -> ForeignStoreSeam other

[<RequireQualifiedAccess>]
module DataClassKind =

    /// A stable, human-readable token for a data-class kind.
    let toWireString (kind: DataClassKind) : string =
        match kind with
        | EntityClass -> "entity"
        | BlobClass -> "blob"
        | AuditRecordClass -> "audit-record"
        | EventStreamClass -> "event-stream"
        | SecretMaterialClass -> "secret-material"

    /// Read a persisted kind token back. Unrecognised input raises: a
    /// kind is a closed vocabulary, and silently coercing an unknown one
    /// to `EntityClass` would fabricate a classification.
    let ofWireString (token: string) : DataClassKind =
        match token with
        | "entity" -> EntityClass
        | "blob" -> BlobClass
        | "audit-record" -> AuditRecordClass
        | "event-stream" -> EventStreamClass
        | "secret-material" -> SecretMaterialClass
        | other -> invalidArg "token" ("Unknown data-class kind token '" + other + "'.")

[<RequireQualifiedAccess>]
module DataClass =

    /// A data class with the given name, kind and seam, carrying no
    /// personal data. The default an automatic derivation produces —
    /// see the `ContainsPii` note on `DataClass`.
    let create (name: string) (kind: DataClassKind) (seam: DataStoreSeam) : DataClass =
        if System.String.IsNullOrWhiteSpace name then
            invalidArg "name" "A data class requires a non-empty ClassName."

        {
            ClassName = name.Trim()
            ClassKind = kind
            Seam = seam
            ContainsPii = false
        }

    /// The same class marked as carrying personal data — the DECLARED
    /// judgement the derivation cannot make.
    let withPii (cls: DataClass) : DataClass = { cls with ContainsPii = true }

    /// A PII-carrying class in one call.
    let pii (name: string) (kind: DataClassKind) (seam: DataStoreSeam) : DataClass = create name kind seam |> withPii

    /// The class's IDENTITY: name + seam. Two declarations of the same
    /// name behind the same store are the same class even when one of
    /// them has since been marked PII — which is what lets a coverage
    /// claim survive that marking, and what stops a PII re-classification
    /// from reading as a brand-new uncovered class.
    let identity (cls: DataClass) : string * string =
        cls.ClassName, DataStoreSeam.toWireString cls.Seam

    /// Render a class as `name (kind @ seam)`, with a `PII` marker when
    /// it carries personal data. Every report path renders through this.
    let describe (cls: DataClass) : string =
        let marker = if cls.ContainsPii then ", PII" else ""

        cls.ClassName
        + " ("
        + DataClassKind.toWireString cls.ClassKind
        + " @ "
        + DataStoreSeam.toWireString cls.Seam
        + marker
        + ")"

    /// A deterministic sort key: name, then seam, then kind. Used
    /// everywhere a class list is rendered or persisted, so two runs
    /// produce byte-identical output.
    let sortKey (cls: DataClass) : string * string * string =
        cls.ClassName, DataStoreSeam.toWireString cls.Seam, DataClassKind.toWireString cls.ClassKind

    /// Project a class to its plain-string wire form.
    let toWire (cls: DataClass) : DataClassWire = {
        Name = cls.ClassName
        KindToken = DataClassKind.toWireString cls.ClassKind
        SeamToken = DataStoreSeam.toWireString cls.Seam
        Pii = cls.ContainsPii
    }

    /// Read a plain-string wire form back into a class. Round-trips
    /// `toWire` exactly.
    let ofWire (wire: DataClassWire) : DataClass = {
        ClassName = wire.Name
        ClassKind = DataClassKind.ofWireString wire.KindToken
        Seam = DataStoreSeam.ofWireString wire.SeamToken
        ContainsPii = wire.Pii
    }

/// Construction, joining, querying, and diffing of data footprints + the
/// `FootprintSignature` that keys them by `ComponentId`. Every function
/// here is pure — nothing is built until a caller asks (GP 13).
module DataFootprint =

    let private sorted (classes: Set<DataClass>) : DataClass list =
        classes |> Set.toList |> List.sortBy DataClass.sortKey

    // ── construction ──────────────────────────────────────────────────

    /// The identity: a component that stores nothing. An UNDECLARED
    /// component contributes this, so an empty signature composes to the
    /// empty surface and a pre-433 deployment is unchanged (GP 11).
    let none (componentId: ComponentId) : DataFootprint = {
        Component = componentId
        Reads = Set.empty
        Writes = Set.empty
        Persists = Set.empty
    }

    /// A footprint with the given read / write / persist class lists.
    let create
        (componentId: ComponentId)
        (reads: DataClass list)
        (writes: DataClass list)
        (persists: DataClass list)
        : DataFootprint =
        {
            Component = componentId
            Reads = Set.ofList reads
            Writes = Set.ofList writes
            Persists = Set.ofList persists
        }

    /// Record one access of a class (fluent declaration helper).
    let withAccess (mode: DataAccessMode) (cls: DataClass) (footprint: DataFootprint) : DataFootprint =
        match mode with
        | ReadAccess -> {
            footprint with
                Reads = footprint.Reads.Add cls
          }
        | WriteAccess -> {
            footprint with
                Writes = footprint.Writes.Add cls
          }
        | PersistAccess -> {
            footprint with
                Persists = footprint.Persists.Add cls
          }

    /// Record a read of a class.
    let withRead (cls: DataClass) (footprint: DataFootprint) : DataFootprint = withAccess ReadAccess cls footprint

    /// Record a write of a class.
    let withWrite (cls: DataClass) (footprint: DataFootprint) : DataFootprint = withAccess WriteAccess cls footprint

    /// Record that a class is left AT REST by this component — the
    /// access mode a data-subject request has to reach.
    let withPersist (cls: DataClass) (footprint: DataFootprint) : DataFootprint = withAccess PersistAccess cls footprint

    /// Whether this component touches no data at all — the identity.
    let isEmpty (footprint: DataFootprint) : bool =
        Set.isEmpty footprint.Reads
        && Set.isEmpty footprint.Writes
        && Set.isEmpty footprint.Persists

    /// Every class this component touches in any mode.
    let touched (footprint: DataFootprint) : Set<DataClass> =
        Set.union footprint.Reads footprint.Writes |> Set.union footprint.Persists

    /// The PII-flagged classes this component leaves at rest — the ones a
    /// DSR path has to reach.
    let persistedPii (footprint: DataFootprint) : DataClass list =
        footprint.Persists |> Set.filter _.ContainsPii |> sorted

    /// Flatten a footprint to its `(mode, class)` accesses — the inverse
    /// of the constructors, used by `join` and the diff.
    let accesses (footprint: DataFootprint) : (DataAccessMode * DataClass) list = [
        for cls in footprint.Reads -> ReadAccess, cls
        for cls in footprint.Writes -> WriteAccess, cls
        for cls in footprint.Persists -> PersistAccess, cls
    ]

    // ── the join (433.C, the Phase 296 shape) ─────────────────────────

    /// Componentwise join of two footprints: set union per access mode.
    /// Associative, commutative and idempotent (set union in each
    /// coordinate), so a fold order never changes the result — exactly
    /// what Phase 296's effect-join gives for capabilities.
    ///
    /// The result keeps the FIRST argument's `Component`; callers key by
    /// id (`addTo` / `mergeSignature`), so joining two different
    /// components' footprints does not arise on the signature paths.
    let join (a: DataFootprint) (b: DataFootprint) : DataFootprint = {
        Component = a.Component
        Reads = Set.union a.Reads b.Reads
        Writes = Set.union a.Writes b.Writes
        Persists = Set.union a.Persists b.Persists
    }

    /// The join of a sequence of one component's footprints, folded from
    /// its empty footprint. An empty sequence joins to the identity.
    let joinAll (componentId: ComponentId) (footprints: DataFootprint seq) : DataFootprint =
        Seq.fold join (none componentId) footprints

    // ── the signature ─────────────────────────────────────────────────

    /// The empty signature — no component stores anything. Every
    /// derived surface degrades to empty against it (GP 13).
    let emptySignature: FootprintSignature = Map.empty

    /// Add / join one component's footprint into a signature.
    let addTo (footprint: DataFootprint) (signature: FootprintSignature) : FootprintSignature =
        match Map.tryFind footprint.Component signature with
        | Some existing -> Map.add footprint.Component (join existing footprint) signature
        | None -> Map.add footprint.Component footprint signature

    /// Build a signature from a footprint list, joining any two entries
    /// that share a `ComponentId`.
    let signatureOf (entries: DataFootprint seq) : FootprintSignature =
        entries |> Seq.fold (fun acc footprint -> addTo footprint acc) Map.empty

    /// Join a whole declared signature onto a derived one — the 433.B
    /// fold: derivation first, declaration on top. Because the join is a
    /// set union, a declaration ADDS to what was derived rather than
    /// replacing it; a declared class that re-states a derived one with
    /// `ContainsPii = true` is a distinct member of the set, so
    /// `reclassify` exists for the case where the declaration is meant to
    /// SUPERSEDE the derived classification.
    let mergeSignature (derived: FootprintSignature) (declared: FootprintSignature) : FootprintSignature =
        declared
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.fold (fun acc footprint -> addTo footprint acc) derived

    /// Re-classify every occurrence of a class IDENTITY (name + seam)
    /// across a whole signature — the declaration path for "this derived
    /// class actually carries personal data". Applied after
    /// `mergeSignature`, it collapses the derived (`ContainsPii = false`)
    /// and declared (`true`) members of the same identity into one, so
    /// the joined surface reports the class once, correctly.
    let reclassify (declaredClasses: DataClass list) (signature: FootprintSignature) : FootprintSignature =
        let byIdentity =
            declaredClasses
            |> List.map (fun cls -> DataClass.identity cls, cls)
            |> Map.ofList

        if Map.isEmpty byIdentity then
            signature
        else
            let apply (classes: Set<DataClass>) =
                classes
                |> Set.map (fun cls ->
                    match Map.tryFind (DataClass.identity cls) byIdentity with
                    | Some declared -> declared
                    | None -> cls)

            signature
            |> Map.map (fun _ footprint -> {
                Component = footprint.Component
                Reads = apply footprint.Reads
                Writes = apply footprint.Writes
                Persists = apply footprint.Persists
            })

    /// Look a component's footprint up, falling back to the identity for
    /// an undeclared component — so an absent id never changes a join
    /// (GP 11).
    let resolve (signature: FootprintSignature) (componentId: ComponentId) : DataFootprint =
        signature |> Map.tryFind componentId |> Option.defaultValue (none componentId)

    /// Every footprint in the signature, ordered by `ComponentId` so
    /// reports are deterministic.
    let all (signature: FootprintSignature) : DataFootprint list =
        signature
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun f -> ComponentId.value f.Component)

    /// Whether any component in the signature declares any access at
    /// all — the gate on registering the preflight validator (GP 13).
    let anyDeclared (signature: FootprintSignature) : bool =
        signature |> Map.exists (fun _ footprint -> not (isEmpty footprint))

    /// Restrict a signature to a set of component ids — the projection
    /// that keeps a stale declaration for an UNCOMPOSED component out of
    /// the composition surface. An empty id set restricts to nothing.
    let restrictTo (composed: Set<ComponentId>) (signature: FootprintSignature) : FootprintSignature =
        signature |> Map.filter (fun componentId _ -> composed.Contains componentId)

    // ── the composed surface ──────────────────────────────────────────

    /// The composition's whole data-at-rest surface: the join of every
    /// component's footprint with the per-component attribution
    /// collapsed. An empty signature composes to the empty surface.
    let compose (signature: FootprintSignature) : CompositionDataFootprint =
        let fold (pick: DataFootprint -> Set<DataClass>) =
            signature |> Map.toSeq |> Seq.map (snd >> pick) |> Seq.fold Set.union Set.empty

        {
            ReadClasses = fold _.Reads
            WrittenClasses = fold _.Writes
            PersistedClasses = fold _.Persists
        }

    /// The empty composed surface — what a deployment with no declared
    /// footprint projects to.
    let emptyComposition: CompositionDataFootprint = {
        ReadClasses = Set.empty
        WrittenClasses = Set.empty
        PersistedClasses = Set.empty
    }

    /// Every class the composition touches in any mode, in deterministic
    /// order.
    let allClasses (composed: CompositionDataFootprint) : DataClass list =
        Set.union composed.ReadClasses composed.WrittenClasses
        |> Set.union composed.PersistedClasses
        |> sorted

    /// The classes the composition leaves at rest, in deterministic
    /// order — the "what is stored" half of the acceptance question.
    let persistedClasses (composed: CompositionDataFootprint) : DataClass list = sorted composed.PersistedClasses

    /// The persisted classes that carry personal data.
    let persistedPiiClasses (composed: CompositionDataFootprint) : DataClass list =
        composed.PersistedClasses |> Set.filter _.ContainsPii |> sorted

    // ── queries (433.C) ───────────────────────────────────────────────

    /// The classes touched behind one store seam — the "where" half of
    /// the acceptance question.
    let classesBySeam (seam: DataStoreSeam) (composed: CompositionDataFootprint) : DataClass list =
        allClasses composed |> List.filter (fun cls -> cls.Seam = seam)

    /// The classes of one kind, regardless of seam.
    let classesOfKind (kind: DataClassKind) (composed: CompositionDataFootprint) : DataClass list =
        allClasses composed |> List.filter (fun cls -> cls.ClassKind = kind)

    /// Every store seam this composition touches, in deterministic
    /// order — the shortest answer to "where does this app keep data?".
    let seams (composed: CompositionDataFootprint) : DataStoreSeam list =
        allClasses composed
        |> List.map _.Seam
        |> List.distinct
        |> List.sortBy DataStoreSeam.toWireString

    /// The components accessing a given class identity in a given mode,
    /// in id order. Matched by identity (name + seam), so a PII
    /// re-classification does not change the answer.
    let componentsAccessing (mode: DataAccessMode) (cls: DataClass) (signature: FootprintSignature) : ComponentId list =
        let wanted = DataClass.identity cls

        all signature
        |> List.filter (fun footprint ->
            accesses footprint
            |> List.exists (fun (m, c) -> m = mode && DataClass.identity c = wanted))
        |> List.map _.Component

    /// The components that leave a given class at rest — the ones whose
    /// stores a DSR has to reach.
    let componentsPersisting (cls: DataClass) (signature: FootprintSignature) : ComponentId list =
        componentsAccessing PersistAccess cls signature

    // ── diff (433.C, the Phase 286 projection) ────────────────────────

    /// The empty delta — what an identical pair (or a signature diffed
    /// against itself) projects to.
    let emptyDelta: DataFootprintDelta = {
        FootprintComponentsAdded = []
        FootprintComponentsRemoved = []
        AccessAdded = []
        AccessRemoved = []
    }

    let private accessTriples (signature: FootprintSignature) : (ComponentId * DataAccessMode * DataClass) list = [
        for footprint in all signature do
            for mode, cls in accesses footprint -> footprint.Component, mode, cls
    ]

    let private modeRank (mode: DataAccessMode) : int =
        match mode with
        | ReadAccess -> 0
        | WriteAccess -> 1
        | PersistAccess -> 2

    let private sortTriples (triples: (ComponentId * DataAccessMode * DataClass) list) =
        triples
        |> List.sortBy (fun (componentId, mode, cls) ->
            ComponentId.value componentId, modeRank mode, DataClass.sortKey cls)

    /// Structurally diff two footprint signatures. `before` is the
    /// reference (or prior) composition, `after` the candidate — so
    /// "added" means present in `after` and not in `before`. Keyed
    /// throughout by `(ComponentId, DataAccessMode, DataClass)`, so the
    /// result never depends on declaration order. A component that starts
    /// persisting a PII class surfaces as an added access whose class
    /// carries the marker.
    let diff (before: FootprintSignature) (after: FootprintSignature) : DataFootprintDelta =
        let idsOf (signature: FootprintSignature) =
            signature |> Map.toList |> List.map fst |> Set.ofList

        let beforeIds = idsOf before
        let afterIds = idsOf after

        let sortedIds (ids: Set<ComponentId>) =
            ids |> Set.toList |> List.sortBy ComponentId.value

        let beforeAccess = accessTriples before |> Set.ofList
        let afterAccess = accessTriples after |> Set.ofList

        {
            FootprintComponentsAdded = sortedIds (Set.difference afterIds beforeIds)
            FootprintComponentsRemoved = sortedIds (Set.difference beforeIds afterIds)
            AccessAdded = sortTriples (Set.difference afterAccess beforeAccess |> Set.toList)
            AccessRemoved = sortTriples (Set.difference beforeAccess afterAccess |> Set.toList)
        }

    /// `true` when two signatures are structurally identical.
    let isEmptyDelta (d: DataFootprintDelta) : bool =
        List.isEmpty d.FootprintComponentsAdded
        && List.isEmpty d.FootprintComponentsRemoved
        && List.isEmpty d.AccessAdded
        && List.isEmpty d.AccessRemoved

    /// A deterministic, human-readable rendering of a delta — the message
    /// a composition golden-file CI gate prints on a footprint mismatch.
    /// A PII class renders with its marker, so "a component started
    /// persisting personal data" is legible in the failure itself.
    let renderDelta (d: DataFootprintDelta) : string =
        let accessLines marker (triples: (ComponentId * DataAccessMode * DataClass) list) =
            triples
            |> List.map (fun (componentId, mode, cls) ->
                let modeToken =
                    match mode with
                    | ReadAccess -> "reads"
                    | WriteAccess -> "writes"
                    | PersistAccess -> "persists"

                "  "
                + marker
                + " "
                + ComponentId.value componentId
                + " "
                + modeToken
                + " "
                + DataClass.describe cls)

        let section title (added: string list) (removed: string list) =
            if List.isEmpty added && List.isEmpty removed then
                []
            else
                [ yield title + ":"; yield! added; yield! removed ]

        if isEmptyDelta d then
            "(no data-footprint differences)"
        else
            [
                yield!
                    section
                        "Components"
                        (d.FootprintComponentsAdded |> List.map (fun c -> "  + " + ComponentId.value c))
                        (d.FootprintComponentsRemoved |> List.map (fun c -> "  - " + ComponentId.value c))
                yield! section "Access" (accessLines "+" d.AccessAdded) (accessLines "-" d.AccessRemoved)
            ]
            |> String.concat "\n"

    // ── wire projection ───────────────────────────────────────────────

    /// Project a signature to plain strings for persistence (a golden
    /// file, an external viewer). Deterministic — components in id order,
    /// classes in `DataClass.sortKey` order within each set.
    let toWire (signature: FootprintSignature) : DataFootprintWireEntry list =
        all signature
        |> List.map (fun footprint -> {
            FootprintComponent = ComponentId.value footprint.Component
            Read = sorted footprint.Reads |> List.map DataClass.toWire
            Written = sorted footprint.Writes |> List.map DataClass.toWire
            Persisted = sorted footprint.Persists |> List.map DataClass.toWire
        })

    /// Read a persisted wire projection back into a signature.
    /// Round-trips `toWire` exactly, which is what lets a golden-file
    /// gate compare a committed baseline against a live derivation
    /// through `diff`.
    let ofWire (entries: DataFootprintWireEntry list) : FootprintSignature =
        entries
        |> List.map (fun entry ->
            let componentId = ComponentId.create entry.FootprintComponent

            {
                Component = componentId
                Reads = entry.Read |> List.map DataClass.ofWire |> Set.ofList
                Writes = entry.Written |> List.map DataClass.ofWire |> Set.ofList
                Persists = entry.Persisted |> List.map DataClass.ofWire |> Set.ofList
            })
        |> signatureOf

/// The DSR / offboarding completeness check (433.D) as a pure function
/// over claims. Declarative throughout: it asks whether every persisted
/// PII class is CLAIMED by a path this deployment composed, never whether
/// the path works.
[<RequireQualifiedAccess>]
module DsrCoverage =

    /// A claim that the named exporters + erasure handlers cover this
    /// class.
    let create (cls: DataClass) (exporters: string list) (erasers: string list) : DsrCoverage = {
        CoveredClass = cls
        Exporters = exporters
        Erasers = erasers
        Exemption = None
    }

    /// A claim that this class needs no DSR path, and why. The reason is
    /// reported, so an exemption is a visible decision rather than a
    /// silent gap.
    let exempt (cls: DataClass) (reason: string) : DsrCoverage = {
        CoveredClass = cls
        Exporters = []
        Erasers = []
        Exemption = Some reason
    }

    /// Every path name a claim relies on — exporters and erasure
    /// handlers together, in declaration order.
    let paths (claim: DsrCoverage) : string list = claim.Exporters @ claim.Erasers

    /// Whether a claim names anything at all (a path or an exemption). A
    /// claim naming neither is an empty claim, and reads as no claim.
    let isSubstantive (claim: DsrCoverage) : bool =
        not (List.isEmpty (paths claim)) || Option.isSome claim.Exemption

    /// The claims covering a class identity (name + seam), so a claim
    /// stays attached across a later PII re-classification.
    let forClass (cls: DataClass) (claims: DsrCoverage list) : DsrCoverage list =
        let wanted = DataClass.identity cls

        claims
        |> List.filter (fun claim -> DataClass.identity claim.CoveredClass = wanted)

    /// The path names a claim relies on that the deployment does not
    /// actually compose — a stale claim naming a renamed or removed
    /// `IDataExporter` / `IErasureHandler`.
    let unresolvedPaths (available: Set<string>) (claim: DsrCoverage) : string list =
        paths claim |> List.filter (available.Contains >> not) |> List.distinct

    /// The coverage gaps: every persisted PII-flagged class with no
    /// substantive claim, or whose every claimed path is unresolved
    /// against the composed DSR path names.
    ///
    /// `available` is the set of composed `IDataExporter.Name` /
    /// `IErasureHandler.Name` values. Passing the EMPTY set is the honest
    /// posture for a deployment that composes no DSR pipeline at all:
    /// every claim is then unresolved and every persisted PII class is a
    /// gap, which is the correct report.
    let gaps (available: Set<string>) (claims: DsrCoverage list) (signature: FootprintSignature) : DsrCoverageGap list =
        let composed = DataFootprint.compose signature

        DataFootprint.persistedPiiClasses composed
        |> List.choose (fun cls ->
            let relevant = forClass cls claims |> List.filter isSubstantive
            let persistedBy = DataFootprint.componentsPersisting cls signature

            match relevant with
            | [] ->
                Some {
                    GapClass = cls
                    PersistedBy = persistedBy
                    UnresolvedPaths = []
                    Detail =
                        "persisted personal data with no declared DSR coverage: no composed IDataExporter or IErasureHandler is claimed for it, and no exemption is declared"
                }
            | _ ->
                let exempt = relevant |> List.exists (fun claim -> Option.isSome claim.Exemption)

                if exempt then
                    None
                else
                    let unresolved =
                        relevant
                        |> List.collect (unresolvedPaths available)
                        |> List.distinct
                        |> List.sort

                    let resolvedAny =
                        relevant
                        |> List.exists (fun claim -> paths claim |> List.exists available.Contains)

                    if resolvedAny then
                        None
                    else
                        Some {
                            GapClass = cls
                            PersistedBy = persistedBy
                            UnresolvedPaths = unresolved
                            Detail =
                                "persisted personal data whose declared DSR coverage names no path this deployment composes: "
                                + String.concat ", " unresolved
                        })

    /// Claims naming a path the deployment does not compose, whether or
    /// not the class is otherwise covered — the "stale claim" report,
    /// separate from `gaps` because a claim can be stale while the class
    /// is still reachable by a second, live path.
    let staleClaims (available: Set<string>) (claims: DsrCoverage list) : (DsrCoverage * string list) list =
        claims
        |> List.choose (fun claim ->
            match unresolvedPaths available claim with
            | [] -> None
            | unresolved -> Some(claim, unresolved))