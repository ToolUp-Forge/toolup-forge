// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.CompositionInspectorHandler

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 593 — composition inspector handler ───────────────────────
//
// Serves `ICompositionInspectorApi` over a snapshot of the deployment's
// own governance declarations. Read-only, Owner/Admin, caller-scope
// agnostic — every panel describes THE DEPLOYMENT, not the caller's
// data, so unlike the audit trail there is nothing here that one tenant
// could read about another. The gate is still the audit trail's, for the
// reason below.
//
// ─── Why the views are SNAPSHOTTED at compose time ───────────────────
//
// The composition manifest is a projection of the live `ServerApp`
// registry (`ServerApp.compositionManifest`), and `ServerApp` is a
// build-time record that no request-time DI container holds. So the
// projection happens once, where the record exists — at `ServerApp.run`,
// beside the Phase 281 line that already computes the same manifest for
// the well-formedness validator — and lands here as a registered
// singleton. Two consequences worth stating plainly:
//
//   * The panels describe the composition AS BOOTED. That is the correct
//     answer, not a limitation: composition is fixed at boot, and a
//     re-derivation per request could only ever return the same value
//     more expensively.
//   * The rule verdict in `RulesView` is the boot preflight's, evaluated
//     over the same manifest with the same rule list. `CapturedAtUtc`
//     names the boot it describes, so an exported artifact cannot be
//     mistaken for a later one.
//
// ─── Why this handler is NOT mounted by `buildRouteHandlers` ─────────
//
// Every other admin surface in the SDK mounts its route there. This one
// cannot: `CompositionManifest.fs`, `GroundingEnvelopeSeal.fs` and
// `CompositionValidator.fs` all compile AFTER `SDK.Server.fs` (and after
// `Compose/BuildRouteHandlers.fs`), so a handler over them is not in
// scope at the composition root. This is the same ordering that made
// Phase 281 build the composition validator in `ServerApp.run` rather
// than in `compose` — see the comment there. `ServerApp.run` appends the
// route to the handler list it hands `compose`, which is an existing
// append-only seam and needs no change to `compose`'s signature.
//
// The visible consequence: a deployment composed through the raw
// `compose` entry point (rather than the `ServerApp` fluent API the SDK
// documents) mounts no inspector route. That is honest — such a
// deployment also registers no snapshot, so an inspector there would
// have nothing to show.
//
// ─── The Surfaces panel, and why it is empty ─────────────────────────
//
// The shard asked this panel to render the surface-descriptor family:
// `ModuleSurface` (Phase 581), `HostEnvelope` (588), `PeerSurface`
// (590). None of the three is reachable from a request-time reader, and
// the reasons are structural rather than incidental:
//
//   1. `ModuleSurface.describeWith` and `HostEnvelope.describeWith` both
//      require the `ServerModule` RECORDS. `ServerApp.addModule` fans
//      each one into the app's accumulators and keeps no registration
//      record — `HostEnvelope.describeWith`'s own doc comment says so,
//      and names the Phase 431 / 433 / 438 lenses that take a
//      `ServerModule list` for the same reason. By `run`, where the
//      snapshot is taken, the modules are gone.
//   2. Retaining them would mean holding every composed module — its
//      handlers, closures, data types and vectorisation handlers — for
//      the process lifetime on every deployment, so that an admin panel
//      nobody may open can render. That is precisely the cost GP 13
//      exists to refuse, and changing `ServerApp`'s retention is a
//      durable architectural decision rather than a phase detail.
//   3. `PeerSurface` lives in the `InterPlatform` project, which
//      PROJECT-REFERENCES `ToolUp.Platform.Server`. The dependency runs
//      the other way, so this file cannot see that type at all — no
//      amount of capture would help.
//
// So `SurfacesView.Descriptors` is empty and `SurfacesView.Note` says
// why, in the operator's own terms. The list is on the wire rather than
// the panel being omitted because the shape is right and the capture
// point is the only thing missing: a composition that later captures
// descriptors fills the list with no wire change and no client change.
// An empty panel with no note would have read as "this deployment
// composes no modules", which is false.

// ─── The snapshot ─────────────────────────────────────────────────────

/// The deployment's governance declarations, projected once at compose
/// time and registered as a singleton for the handler to read.
///
/// The already-PROJECTED wire views rather than the server-tier sources,
/// deliberately: it makes the snapshot a value with no reference back
/// into the composition machinery, and it puts the projection cost on
/// the boot that has the data rather than on the first reviewer to open
/// a panel.
type CompositionInspectorSnapshot = {
    Composition: CompositionView
    Surfaces: SurfacesView
    Rules: RulesView
    Disclosure: DisclosureView
}

/// The note `SurfacesView` carries on every deployment the SDK composes
/// today. See this file's header for the three reasons behind it.
[<Literal>]
let SurfacesUnavailableNote =
    "No surface descriptors were captured at compose time. The module and host descriptors are derived from the ServerModule records, which composition fans into the application's accumulators and does not retain; the peer descriptor is published by the federation layer, which sits downstream of this assembly. This panel is not reporting that the deployment composes no modules — see the Composition panel for those."

module CompositionInspectorSnapshot =

    /// The manifest's kind label for a component entry. Matches the
    /// `ComponentId` slot prefixes (`module:` / `companion:` /
    /// `datatype:` / `tool:` / …) so the rendered kind and the rendered
    /// id agree on the page.
    ///
    /// Written here rather than reused because the two existing
    /// renderings (`CompositionValidator.kindLabel`,
    /// `HostEnvelope.kindLabel`) are both `private` to their modules and
    /// both serve a different audience — a defect message and a
    /// capability-layer name. Making one of them public to share it
    /// would widen a published surface for a display string.
    let private kindLabel (kind: ComponentKind) : string =
        match kind with
        | ModuleComponent -> "module"
        | CompanionComponent -> "companion"
        | DataTypeComponent -> "datatype"
        | ToolComponent -> "tool"
        | MetricComponent -> "metric"
        | SubjectComponent -> "subject"
        | PurposeComponent -> "purpose"

    let private component_ (entry: ComponentEntry) : InspectedComponent = {
        Id = entry.Id.Value
        Kind = kindLabel entry.Kind
        Label = entry.Label
        Impl = entry.Impl
    }

    let private knob (k: ConfigKnob) : InspectedKnob = { Name = k.Name; Value = k.Value }

    let private severityLabel (severity: CompositionDefectSeverity) : string =
        match severity with
        | DefectError -> "error"
        | DefectWarning -> "warning"

    let private ruleClassLabel (cls: CompositionRuleClass) : string =
        match cls with
        | StructuralRule -> "structural"
        | ExternalProbeRule -> "external-probe"

    /// Project the composition manifest.
    ///
    /// `SchemaVersion` is read through `effectiveSchemaVersion`, not off
    /// the field: a manifest projected before Phase 694 carries no
    /// version and arrives as `0`, and rendering that as "schema 0"
    /// would tell a reviewer something untrue about a perfectly valid
    /// pre-694 composition.
    let composition (manifest: CompositionManifest) : CompositionView = {
        SchemaVersion = CompositionManifest.effectiveSchemaVersion manifest
        Modules = manifest.Modules |> List.map component_
        CompanionSlots = manifest.CompanionSlots |> List.map component_
        DataTypes = manifest.DataTypes |> List.map component_
        Tools = manifest.Tools |> List.map component_
        Metrics = manifest.Metrics |> List.map component_
        Subjects = manifest.Subjects |> List.map component_
        Purposes = manifest.Purposes |> List.map component_
        ConfigKnobs = manifest.ConfigKnobs |> List.map knob
        CanonicalMethods =
            // Through `canonicalMethods`, not the raw field, so a
            // pre-694 manifest reads as "declares none" rather than
            // throwing a version distinction at the panel.
            CompositionManifest.canonicalMethods manifest
            |> List.map (fun m -> {
                MetricId = m.MetricId
                Selector = m.Selector
            })
    }

    /// Project the rule manifest and the deployment's verdict over it.
    ///
    /// `classifiedRuleManifest` and `checkWith` read the SAME declared
    /// rule list, so the rules a reviewer sees listed are exactly the
    /// rules whose verdict sits beside them — the property Phase 294
    /// exported the manifest for in the first place.
    let rules (manifest: CompositionManifest) (refs: CompositionReferences) (capturedAtUtc: DateTime) : RulesView = {
        Rules =
            CompositionValidator.classifiedRuleManifest
            |> List.map (fun r -> {
                Code = r.Code
                Severity = severityLabel r.Severity
                RuleClass = ruleClassLabel r.Class
                Description = r.Description
            })
        Defects =
            CompositionValidator.checkWith refs manifest
            |> List.map (fun d -> {
                Code = d.RuleCode
                Severity = severityLabel d.Severity
                Message = d.Message
            })
        CapturedAtUtc = capturedAtUtc
    }

    /// Project the grounding / disclosure envelope.
    ///
    /// `GroundingEnvelope.ofManifest` is the one derivation of these
    /// declarations (Phase 694 collapsed the second), and `digest` is the
    /// value a boot seal binds — so an exported Disclosure panel can be
    /// matched against a recorded seal without re-deriving anything.
    let disclosure (manifest: CompositionManifest) : DisclosureView =
        let envelope = GroundingEnvelope.ofManifest manifest

        {
            SchemaVersion = envelope.SchemaVersion
            Declarations =
                envelope
                |> GroundingEnvelope.canonicalDeclarations
                |> List.map (fun d -> {
                    Facet = GroundingFacet.label d.Facet
                    Subject = d.Subject
                    Value = d.Value
                })
            Digest = GroundingEnvelope.digest envelope
        }

    /// The surfaces panel as every SDK-composed deployment renders it
    /// today: empty, with the structural reason attached.
    let surfaces: SurfacesView = {
        Descriptors = []
        Note = Some SurfacesUnavailableNote
    }

    /// Take the snapshot. Called once, from `ServerApp.run`, with the
    /// manifest and reference set that boot's preflight already derived.
    ///
    /// `capturedAtUtc` is a PARAMETER rather than a `DateTime.UtcNow`
    /// read inside, because the DI registration is a lazy singleton
    /// factory (GP 13 — a deployment that never opens the inspector
    /// never projects). Reading the clock in here would stamp the
    /// artifact with the first reviewer's page load instead of the boot
    /// it describes, and would make the projection untestable.
    let ofComposition
        (manifest: CompositionManifest)
        (refs: CompositionReferences)
        (capturedAtUtc: DateTime)
        : CompositionInspectorSnapshot =
        {
            Composition = composition manifest
            Surfaces = surfaces
            Rules = rules manifest refs capturedAtUtc
            Disclosure = disclosure manifest
        }

// ─── Canonical JSON ───────────────────────────────────────────────────

/// Export options: the SDK's canonical converter set, indented.
///
/// Indented because the artifact's whole purpose is to be read by a
/// person — a reviewer who was handed a single-line JSON blob would
/// reformat it, and then what they hold is not byte-identical to what
/// the deployment emitted. The converter set is the same one
/// `HostEnvelope.toJson` uses, so a panel's JSON deserialises with the
/// SDK's own reader.
let private exportOptions =
    lazy
        (let opts = FableConverters.create ()
         opts.WriteIndented <- true
         opts)

/// Serialise one projected view. Generic over the view type so every
/// panel's export takes the identical path — an export that rendered
/// per-panel could drift from the getter it claims to mirror.
let private toCanonicalJson (view: 'view) : string =
    JsonSerializer.Serialize(view, exportOptions.Value)

// ─── Handler ──────────────────────────────────────────────────────────

/// Resolve the caller's `AccessContext`, falling back to an anonymous
/// context — which the gate then refuses. `AuditViewApiHandler`'s
/// fallback, and it fails CLOSED here for the same reason.
let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

/// True when a service of type `'svc` is registered.
let private isComposed<'svc> (ctx: HttpContext) : bool =
    match ctx.RequestServices.GetService(typeof<'svc>) with
    | :? 'svc -> true
    | _ -> false

/// The message a caller sees when the route is mounted but no snapshot
/// was registered. Not an empty view: an empty composition would read as
/// "this deployment composes nothing", which is a false statement about
/// the deployment rather than an honest absence.
[<Literal>]
let SnapshotMissingMessage =
    "This deployment did not record a composition snapshot at startup. The inspector is composed by ServerApp.run; a host composed through the lower-level compose entry point registers no snapshot and has nothing to inspect."

/// Build the `ICompositionInspectorApi` ToolUp.Remoting handler.
///
/// The snapshot is resolved per request rather than captured in the
/// closure so a test can register one after the handler factory is
/// built, and so the missing-snapshot path is reachable and assertable
/// rather than a compose-time impossibility nobody has exercised.
let compositionInspectorApi (ctx: HttpContext) : ICompositionInspectorApi =

    let accessContext = resolveAccessContext ctx

    let snapshot: CompositionInspectorSnapshot option =
        match ctx.RequestServices.GetService(typeof<CompositionInspectorSnapshot>) with
        | :? CompositionInspectorSnapshot as s -> Some s
        | _ -> None

    /// Owner/Admin read gate. `AuditViewApiHandler.ensureReadAllowed`'s
    /// shape, and the same predicate (`TeamRoles.canWriteTeamConfig`)
    /// the shard names.
    ///
    /// The same gate as the audit trail even though the content is
    /// different in kind — this panel set names the deployment's
    /// companions, its resolved config knobs, and the purposes it may
    /// disclose data under. That is a map of the attack surface, and a
    /// second, laxer predicate for it would be a second thing to keep in
    /// step with the first.
    let ensureReadAllowed () : Async<Result<unit, string>> = async {
        match accessContext.Subject with
        | AnonymousSession _ -> return Error "The composition inspector is not available in this mode."
        | TeamMember(userId, teamId) ->
            match ctx.RequestServices.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as ts ->
                let! role = ts.GetMemberRole(teamId, userId)

                match role with
                | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                | Some r ->
                    return
                        Error
                            $"Only team owners and admins can view the composition inspector. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | AuthenticatedUser _
        | ClaimBearer _ -> return Ok()
    }

    let withGate (f: unit -> Async<Result<'T, string>>) : Async<Result<'T, string>> = async {
        match! ensureReadAllowed () with
        | Error msg -> return Error msg
        | Ok() -> return! f ()
    }

    /// A panel read: gated, then answered off the snapshot, or refused
    /// with the missing-snapshot message.
    let panel (project: CompositionInspectorSnapshot -> 'T) : Async<Result<'T, string>> =
        withGate (fun () -> async {
            return
                match snapshot with
                | Some s -> Ok(project s)
                | None -> Error SnapshotMissingMessage
        })

    /// The provenance report is the one panel resolved per request
    /// rather than snapshotted: it reports DI registrations, which the
    /// container answers directly and which the compose-time snapshot
    /// would only be able to mirror.
    let provenanceView () : ProvenanceView = {
        GraphComposed = isComposed<IProvenanceGraph> ctx
        FactEvidenceComposed = isComposed<IFactEvidenceSource> ctx
        ArtifactProvenanceComposed = isComposed<IArtifactProvenanceSource> ctx
    }

    {
        GetComposition = fun () -> panel _.Composition
        GetSurfaces = fun () -> panel _.Surfaces
        GetRules = fun () -> panel _.Rules
        GetDisclosure = fun () -> panel _.Disclosure

        GetProvenance = fun () -> withGate (fun () -> async { return Ok(provenanceView ()) })

        ExportPanel =
            fun requested ->
                withGate (fun () -> async {
                    // Every arm serialises the SAME value its getter
                    // returns, through the same serialiser — so the
                    // round-trip property is structural rather than a
                    // pair of renderings that happen to agree today.
                    match requested, snapshot with
                    | ProvenancePanel, _ -> return Ok(toCanonicalJson (provenanceView ()))
                    | _, None -> return Error SnapshotMissingMessage
                    | CompositionPanel, Some s -> return Ok(toCanonicalJson s.Composition)
                    | SurfacesPanel, Some s -> return Ok(toCanonicalJson s.Surfaces)
                    | RulesPanel, Some s -> return Ok(toCanonicalJson s.Rules)
                    | DisclosurePanel, Some s -> return Ok(toCanonicalJson s.Disclosure)
                })
    }