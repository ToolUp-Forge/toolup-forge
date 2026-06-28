// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text.Json
open System.Security.Cryptography
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.IDataExporter

// ─── Phase 187 — compliance evidence-pack generator (server) ────────────
//
// Assembles the tamper-evident audit pack an MRM / compliance team reads,
// by *composing* already-shipped substrate — no new infrastructure:
//   * IEventStore.ReadBySource    — Phase 9 audit slices (per source module)
//   * IDataExporter.Export        — Phase 9h.A DSR (Article-15-shape) records
//   * IFieldClassifier.Classify   — Phase 41 classification sidecar
//   * IExportEnvelopeSigner       — Phase 40 detached-JWS over the manifest
//                                   (the neutral seam Phase 162 added; the
//                                   SDK core stays signer-free, GP 1)
//
// Opt-in (GP 13): the default is `NoEvidencePackGenerator` (in Core), which
// produces nothing. A deployment that never registers a real generator
// mounts no route and no service — boot path byte-for-byte unchanged.

/// Internal helpers — one shared serializer + content-addressing.
module private EvidencePackInternals =
    /// F#-aware STJ options (Option / DU / record / list / Map). Property
    /// order is reflection-stable, so a fixed manifest value serialises to
    /// fixed bytes — the determinism the signature relies on.
    let jsonOptions = FableConverters.create ()

    let serialize (value: 'T) : byte[] =
        JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions)

    let sha256Hex (bytes: byte[]) : string =
        use sha = SHA256.Create()

        sha.ComputeHash bytes
        |> Array.map (fun b -> b.ToString("x2"))
        |> System.String.Concat

/// Phase 187 — default `IEvidencePackGenerator`. Pure orchestration over
/// the audit / classification / DSR / signing seams. The `clock` is
/// injected so the manifest's `GeneratedAt` is reproducible (a fixed clock
/// + fixed request + fixed data ⇒ identical manifest bytes ⇒ identical
/// signature).
type DefaultEvidencePackGenerator
    (
        eventStore: IEventStore,
        classifier: IFieldClassifier,
        exporters: IDataExporter list,
        signer: IExportEnvelopeSigner option,
        clock: unit -> System.DateTimeOffset
    ) =

    interface IEvidencePackGenerator with
        member _.Generate(request: EvidencePackRequest) : Async<Result<EvidencePackResult, EvidencePackError>> = async {
            try
                // 1. Audit slices — one JSON segment per requested source module.
                let mutable auditSegments = []

                for sourceModule in request.AuditSourceModules do
                    let! events = eventStore.ReadBySource(request.ScopeId, sourceModule)

                    auditSegments <-
                        {
                            Name = $"audit/{sourceModule}.json"
                            MimeType = "application/json"
                            Body = EvidencePackInternals.serialize events
                        }
                        :: auditSegments

                // 2. DSR (Article-15-shape) export segments — only when a
                //    subject is named, gathered from registered exporters.
                let mutable dsrSegments = []

                match request.SubjectUserId with
                | Some subjectId when not (Erasure.isBlankSubject subjectId) ->
                    for exporter in exporters |> List.sortBy _.Name do
                        let! segments = exporter.Export(request.ScopeId, subjectId)

                        for s in segments do
                            dsrSegments <- { s with Name = $"dsr/{s.Name}" } :: dsrSegments
                | _ -> ()

                // 3. Classification sidecar — Phase 41 tags for each entity.
                let mutable sidecar = []

                for entityName in request.EntityNames do
                    let! classifications = classifier.Classify entityName

                    for c in classifications do
                        sidecar <-
                            {
                                EntityName = c.EntityName
                                FieldPath = c.FieldPath
                                Level = ClassificationLevel.name c.Level
                            }
                            :: sidecar

                let classifications = sidecar |> List.sortBy (fun c -> c.EntityName, c.FieldPath)

                let sidecarSegment =
                    if List.isEmpty classifications then
                        []
                    else
                        [
                            {
                                Name = "classification/sidecar.json"
                                MimeType = "application/json"
                                Body = EvidencePackInternals.serialize classifications
                            }
                        ]

                // 4. All segments, sorted by Name ⇒ deterministic bytes.
                let segments =
                    List.concat [ auditSegments; dsrSegments; sidecarSegment ] |> List.sortBy _.Name

                // 5. Content-addressed manifest entries.
                let entries =
                    segments
                    |> List.map (fun s -> {
                        Name = s.Name
                        MimeType = s.MimeType
                        Sha256 = EvidencePackInternals.sha256Hex s.Body
                        SizeBytes = s.Body.Length
                    })

                // 6. Manifest + the exact canonical bytes the signature covers.
                let manifest = {
                    ScopeId = request.ScopeId
                    GeneratedAt = clock ()
                    RequestedBy = request.RequestedBy
                    Reason = request.Reason
                    Entries = entries
                    Classifications = classifications
                }

                let manifestBytes = EvidencePackInternals.serialize manifest

                // 7. Sign the manifest bytes when a signer is composed.
                match signer with
                | None ->
                    return
                        Ok {
                            Manifest = manifest
                            ManifestBytes = manifestBytes
                            Segments = segments
                            Signature = None
                        }
                | Some s ->
                    match! s.SignEnvelope manifestBytes with
                    | Error e -> return Error(SigningFailed e)
                    | Ok signature ->
                        return
                            Ok {
                                Manifest = manifest
                                ManifestBytes = manifestBytes
                                Segments = segments
                                Signature = Some signature
                            }
            with ex ->
                return Error(AssemblyFailed ex.Message)
        }

/// Composition helpers for the evidence-pack generator.
module DefaultEvidencePackGenerator =
    /// Build a generator with an explicit clock. Tests inject a fixed clock
    /// to assert manifest determinism.
    let createWithClock
        (eventStore: IEventStore)
        (classifier: IFieldClassifier)
        (exporters: IDataExporter list)
        (signer: IExportEnvelopeSigner option)
        (clock: unit -> System.DateTimeOffset)
        : IEvidencePackGenerator =
        DefaultEvidencePackGenerator(eventStore, classifier, exporters, signer, clock) :> IEvidencePackGenerator

    /// Build a generator with the wall-clock. The ergonomic production
    /// default.
    let create
        (eventStore: IEventStore)
        (classifier: IFieldClassifier)
        (exporters: IDataExporter list)
        (signer: IExportEnvelopeSigner option)
        : IEvidencePackGenerator =
        createWithClock eventStore classifier exporters signer (fun () -> System.DateTimeOffset.UtcNow)

/// DI registration — mirrors `ToolUp.ArtefactSigning.SignedExportBundle.register`.
/// Calling this is the opt-in: the SDK default registers nothing, so a
/// deployment that never calls it is byte-for-byte unchanged (GP 13).
module EvidencePackGenerator =
    let register (services: IServiceCollection) (generator: IEvidencePackGenerator) : IServiceCollection =
        services.AddSingleton<IEvidencePackGenerator>(generator)

/// Phase 187 — opt-in admin route for the evidence pack:
///
///   POST /_platform/evidence-pack   (body: EvidencePackRequest JSON)
///
/// Mounts only when a non-`No` `IEvidencePackGenerator` is registered in
/// DI; when absent or `NoEvidencePackGenerator`, returns 404 "not enabled"
/// so an accidentally-composed handler stays inert. Compose into the
/// deployment's admin route group (behind the same auth as other
/// `_platform` admin endpoints):
///   `choose [ EvidencePackHandler.routes; ...existing... ]`
///
/// Gated in-handler on `canModifyPlatformConfig` (platform admin) — the
/// pack carries audit + classification evidence and is never anonymous.
module EvidencePackHandler =

    [<Literal>]
    let Route = "/_platform/evidence-pack"

    let private resolveAccessContext (ctx: HttpContext) : AccessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            AccessContext.unrestricted (AnonymousSession userId)

    /// The composed generator, unless it is the `No` default (treated as
    /// "not enabled" so the route is inert without a real generator).
    let private resolveGenerator (ctx: HttpContext) : IEvidencePackGenerator option =
        match ctx.RequestServices.GetService(typeof<IEvidencePackGenerator>) with
        | :? NoEvidencePackGenerator -> None
        | :? IEvidencePackGenerator as g -> Some g
        | _ -> None

    let private handler: HttpHandler =
        fun _next (ctx: HttpContext) -> task {
            match resolveGenerator ctx with
            | None ->
                ctx.Response.StatusCode <- 404
                return! ctx.WriteTextAsync "evidence-pack generator not enabled"
            | Some generator ->
                if not (AccessContext.canModifyPlatformConfig (resolveAccessContext ctx)) then
                    ctx.Response.StatusCode <- 403
                    return! ctx.WriteTextAsync "platform admin role required"
                else
                    let! request = ctx.BindJsonAsync<EvidencePackRequest>()
                    let! result = generator.Generate request |> Async.StartImmediateAsTask

                    match result with
                    | Ok pack ->
                        ctx.Response.StatusCode <- 200

                        return!
                            ctx.WriteJsonAsync {|
                                manifest = pack.Manifest
                                signature = pack.Signature
                            |}
                    | Error e ->
                        ctx.Response.StatusCode <- 500
                        return! ctx.WriteTextAsync(EvidencePackError.describe e)
        }

    /// Giraffe routes for the evidence-pack endpoint.
    let routes: HttpHandler = POST >=> route Route >=> handler