// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── The work provenance seam ────────────────────────────────────────
//
// The wire records this seam speaks in live in `ToolUp.Platform.Core`
// (`WorkProvenanceWireTypes.fs`), together with the reasoning about the
// three properties they carry. This file is the seam itself: the
// interface a source system's adapter fills, the default that composes
// nothing, and the shipped decorator that makes the declared caps
// binding rather than advisory.
//
// **A seam, not a dependency — and that IS the design.** The platform
// takes no reference on whatever system produced its sources. The
// upstream half arrives as wire data through this interface and nothing
// else: no package reference, no shared type, no vocabulary the platform
// has to keep up with. An authoring system this SDK has never heard of
// activates the join by implementing one interface, with no substrate
// change.
//
// **Nothing composes this by default.** `NoWorkProvenanceSource` is the
// default mode, and it is not a null object that answers emptily — it
// carries no implementation at all, so an uncomposed deployment
// registers nothing: no DI singleton, no middleware, no route, no
// background work, no allocation (GP 13). Its deploy records are
// byte-for-byte what they were before this surface existed (GP 11), and
// its work reading is `SourceAbsent` — the honest statement that nobody
// was asked, which is a different fact from "asked, and nothing was
// found".
//
// **Six portability rules (GP 12).** Identity by value — refs and
// records of strings in, records of strings out; no live handles.
// Async at every boundary. Failure as data (`Result` over a typed error
// or a reason string, never an exception). Stateless between calls, so a
// source-system refresh takes effect on the next query with nothing to
// invalidate. No cross-shard ordering promise. No timing-precision
// boundary.

open System.Collections.Generic

/// Read-only access to a source system's upstream work records.
///
/// **Every member is a query.** There is no member that writes and none
/// that answers with `unit` — a `unit` answer is the shape a mutation
/// takes, so its absence is what a shipped test asserts over this
/// interface's methods. A deployment therefore cannot expose a work
/// record write path by composing this seam, whatever it wires behind
/// it.
type IWorkProvenanceSource =
    /// The name of the source system this adapter answers from, for
    /// diagnostics and operator-facing display. Opaque to the platform.
    abstract SourceSystem: unit -> string

    /// The bounds this source declares. Cheap and constant — a caller
    /// reads it once and sizes its walks, instead of discovering the
    /// limit as a refusal.
    abstract GetCaps: unit -> Async<WorkProvenanceCaps>

    /// One record by ref: `Found`, `Withheld`, or `Absent`. The three
    /// are distinct answers and an implementation must not collapse
    /// them.
    abstract GetRecord: reference: WorkRecordRef -> Async<WorkRecordAnswer>

    /// A bounded walk back through a record's ancestors. An
    /// implementation MUST refuse — never truncate — a request or an
    /// answer that exceeds its declared caps; `WorkProvenanceSource.bounded`
    /// wraps any source so that holds whether or not the implementation
    /// remembered.
    abstract GetAncestors: request: WorkAncestorRequest -> Async<Result<WorkAncestorPage, WorkProvenanceError>>

    /// Which work record covers the sources named by an opaque upstream
    /// provenance reference — the head of the chain, or the honest
    /// statement that there is none.
    ///
    /// The reference is the deploy record's own uninterpreted slot value,
    /// passed through verbatim. The platform does not know what it means
    /// and does not need to: the source system that fills the slot is the
    /// one being asked about it.
    abstract Covering: upstreamReference: string -> Async<Result<WorkCoverage, string>>

/// Whether a deployment composes a work provenance source.
///
/// The default is `NoWorkProvenanceSource`, which carries no
/// implementation — so there is nothing to register, nothing to start,
/// and nothing to pay for (GP 13). Composing a source is the deliberate
/// act; not composing one is free and stays honest about it.
type WorkProvenanceMode =
    /// The default. No source system is consulted, nothing is
    /// registered, and every work reading records `SourceAbsent`.
    | NoWorkProvenanceSource
    /// A composed source. Wrap it with `WorkProvenanceSource.bounded`
    /// unless the implementation is itself the thing under test.
    | ComposedWorkProvenanceSource of IWorkProvenanceSource

/// A source whose declared caps are enforced on the way in and on the
/// way out, whatever the wrapped implementation does.
///
/// The inner source is asked only for requests it declared it can
/// answer, and its answer is checked against the same declared cap
/// before it reaches the caller. An implementation that quietly trimmed
/// its walk is reported as `WorkAncestorsOverDeclaredCap` rather than
/// passed on — because the whole point of the bound is that a caller
/// cannot tell a trimmed chain from a complete one, so the only safe
/// thing to do with a suspect answer is refuse it.
type BoundedWorkProvenanceSource(inner: IWorkProvenanceSource) =

    interface IWorkProvenanceSource with
        member _.SourceSystem() = inner.SourceSystem()

        member _.GetCaps() = inner.GetCaps()

        member _.GetRecord reference = inner.GetRecord reference

        member _.Covering upstreamReference = inner.Covering upstreamReference

        member _.GetAncestors request = async {
            let! caps = inner.GetCaps()

            if request.Depth < 1 then
                return Result.Error(WorkDepthInvalid request.Depth)
            elif request.Depth > caps.MaxDepth then
                return Result.Error(WorkDepthExceedsCap(request.Depth, caps.MaxDepth))
            else
                match! inner.GetAncestors request with
                | Result.Error error -> return Result.Error error
                | Result.Ok page ->
                    let reached = WorkAncestorPage.size page

                    if reached > caps.MaxRecords then
                        return Result.Error(WorkAncestorsOverDeclaredCap(reached, caps.MaxRecords))
                    else
                        return Result.Ok page
        }

[<RequireQualifiedAccess>]
module WorkProvenanceSource =

    /// The source a mode carries, if any. `NoWorkProvenanceSource` has
    /// none — there is no implementation behind the default case, which
    /// is what makes "registers nothing" structural rather than a
    /// promise about an empty implementation's behaviour.
    let ofMode (mode: WorkProvenanceMode) : IWorkProvenanceSource option =
        match mode with
        | NoWorkProvenanceSource -> None
        | ComposedWorkProvenanceSource source -> Some source

    /// Wrap a source so its declared caps bind. Composition sites should
    /// prefer this to the bare implementation: the refusal contract is
    /// then a property of the substrate rather than of each adapter's
    /// diligence.
    let bounded (source: IWorkProvenanceSource) : IWorkProvenanceSource =
        BoundedWorkProvenanceSource(source) :> IWorkProvenanceSource

    /// Walk a record's ancestors breadth-first over `GetRecord`, for a
    /// source system that exposes single-record lookup and no native
    /// walk.
    ///
    /// Bounded on both axes and refusing on both: a depth outside the
    /// declared range is refused before anything is fetched, and a walk
    /// that reaches more records than the declared cap is refused whole
    /// once it has counted them. It never returns what it managed to
    /// collect — a partial ancestor chain reads as a complete one, which
    /// is the failure this contract exists to make impossible.
    ///
    /// A withheld ancestor stops the walk THROUGH that record and is
    /// recorded as a marker: its own parents are exactly the content the
    /// refusal sealed, so continuing past it would be inventing edges.
    ///
    /// An UNRESOLVABLE ancestor is recorded too, for the same reason and
    /// in the same shape. A parent ref a reached record named, which the
    /// source then holds nothing under, is a join the page itself
    /// asserted and could not follow; it crosses as a `SeveredWorkEdge`
    /// carrying the failing ref and the record that named it. The walk
    /// still returns every record it DID reach — one lost edge must not
    /// cost the caller the rest of the page, or severing an edge becomes
    /// the cheapest way to suppress the whole answer.
    ///
    /// **The ROOT is not an edge.** A root the source holds nothing under
    /// was named by the caller, not by this page, so its absence is the
    /// caller's own question coming back empty rather than a break inside
    /// a page; it is answered exactly as it was before this marker
    /// existed, with an empty page.
    let walkOverLookups
        (source: IWorkProvenanceSource)
        (request: WorkAncestorRequest)
        : Async<Result<WorkAncestorPage, WorkProvenanceError>> =
        async {
            let! caps = source.GetCaps()

            if request.Depth < 1 then
                return Result.Error(WorkDepthInvalid request.Depth)
            elif request.Depth > caps.MaxDepth then
                return Result.Error(WorkDepthExceedsCap(request.Depth, caps.MaxDepth))
            else
                let seen = HashSet<string * string>()
                let records = ResizeArray<WorkRecord>()
                let withheld = ResizeArray<WithheldWorkRecord>()
                let severed = ResizeArray<SeveredWorkEdge>()

                seen.Add(request.Root.SourceSystem, request.Root.RecordId) |> ignore

                // Each frontier entry carries the record that NAMED it,
                // where one did. The root carries none, which is what
                // keeps root-level absence distinguishable from a severed
                // edge inside the page rather than a special case checked
                // after the fact.
                let rec walkLevel (frontier: (WorkRecordRef * WorkRecordRef option) list) (hop: int) : Async<unit> = async {
                    if hop >= request.Depth || List.isEmpty frontier then
                        return ()
                    else
                        let next = ResizeArray<WorkRecordRef * WorkRecordRef option>()

                        for reference, namedBy in frontier do
                            match! source.GetRecord reference with
                            | WorkRecordAnswer.Found record ->
                                records.Add record

                                for parent in record.Parents do
                                    if seen.Add(parent.SourceSystem, parent.RecordId) then
                                        next.Add(parent, Some record.Ref)
                            | WorkRecordAnswer.Withheld marker -> withheld.Add marker
                            | WorkRecordAnswer.Absent ->
                                match namedBy with
                                | Some namer -> severed.Add { Ref = reference; NamedBy = namer }
                                | None -> ()

                        return! walkLevel (List.ofSeq next) (hop + 1)
                }

                do! walkLevel [ request.Root, None ] 0

                let reached = records.Count + withheld.Count

                if reached > caps.MaxRecords then
                    return Result.Error(WorkAncestorsExceedRecordCap(reached, caps.MaxRecords))
                else
                    return
                        Result.Ok {
                            Root = request.Root
                            Records = List.ofSeq records
                            Withheld = List.ofSeq withheld
                            Severed = List.ofSeq severed
                            Depth = request.Depth
                        }
        }

[<RequireQualifiedAccess>]
module WorkProvenance =

    /// Read the upstream work half of a deploy record.
    ///
    /// **Never drops a deploy.** Every outcome is recorded: attested by
    /// reference, or unattested carrying which of the five reasons
    /// applies. A deploy whose sources no work record covers reports as
    /// unattested WITH the reason — an account that listed only its
    /// attested members would read as complete and would not be.
    ///
    /// The deploy record itself is untouched. This is a reading taken
    /// against the record's opaque upstream-provenance slot, echoed back
    /// in the companion so a reader can confirm which record it belongs
    /// to; the slot stays authoritative and unchanged, and the sealed
    /// bytes are exactly what they were.
    let attest (mode: WorkProvenanceMode) (record: DeployRecord) : Async<DeployWorkAttestation> = async {
        let upstream = (DeployProvenance.coerce record.Provenance).UpstreamProvenanceDigest

        let unattested source reason = {
            SchemaVersion = DeployWorkAttestation.SchemaVersion
            UpstreamReference = upstream
            Head = WorkAttestation.Unattested reason
            SourceSystem = source
        }

        match WorkProvenanceSource.ofMode mode with
        | None -> return unattested "" WorkUnattestedReason.SourceAbsent
        | Some source ->
            let system = source.SourceSystem()

            match upstream with
            | None -> return unattested system WorkUnattestedReason.UpstreamReferenceUnrecorded
            | Some reference ->
                match! source.Covering reference with
                | Result.Ok(WorkCoverage.Covered head) ->
                    return {
                        SchemaVersion = DeployWorkAttestation.SchemaVersion
                        UpstreamReference = upstream
                        Head = WorkAttestation.AttestedBy head
                        SourceSystem = system
                    }
                | Result.Ok WorkCoverage.NotTracked -> return unattested system WorkUnattestedReason.NotTracked
                | Result.Ok WorkCoverage.NoCoveringRecord ->
                    return unattested system WorkUnattestedReason.NoCoveringRecord
                | Result.Error reason -> return unattested system (WorkUnattestedReason.LookupFailed reason)
    }

    /// A deploy record together with its upstream-work reading.
    let attestRecord (mode: WorkProvenanceMode) (record: DeployRecord) : Async<WorkAttestedDeployRecord> = async {
        let! work = attest mode record
        return { Record = record; Work = work }
    }