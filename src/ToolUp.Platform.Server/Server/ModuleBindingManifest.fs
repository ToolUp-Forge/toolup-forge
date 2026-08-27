// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.IO
open System.Text.Json

// ─── Phase 166 — detachable module-binding manifest ─────────────────────
//
// A module's binding is a *deployment* property, not a build-time property
// of the module. The Phase 165 gate verifies a `ModuleBindingStamp` a
// module presents via `ServerModule.BindingStamp`; this manifest is where
// that stamp comes from at deploy time. It is a signed sidecar
// (`module-bindings.json`) mapping `moduleId → stamp`, read at startup and
// applied to the matching modules just before `addModule`. The same module
// artefact can therefore ship unbound, bound to deployment A, or re-bound
// to deployment B with no rebuild — re-keying is just re-stamping the
// manifest (the `toolup stamp` CLI command writes it).
//
// **GP 13 byte-identical:** a deployment with no manifest file loads an
// empty map; `applyTo` is then the identity on every module, so an
// unstamped/unconfigured deployment is unchanged.
//
// **No crypto here.** The manifest only *carries* the opaque stamp strings;
// verification stays in the `IModuleBindingVerifier` the gate consults. The
// stamp is signed over the module's identifier bytes, so the `moduleId` key
// is also what the gate recomputes the canonical bytes from — a stamp
// filed under module A cannot bind module B.

/// The on-disk manifest format:
///
/// ```json
/// {
///   "version": 1,
///   "bindings": {
///     "Sales":     { "kind": "mac", "keyId": "anchor-1", "tag": "<base64url>" },
///     "Inventory": { "kind": "jws", "detachedJws": "<header>..<sig>" }
///   }
/// }
/// ```
module ModuleBindingManifest =

    /// Conventional manifest file name (alongside the deployed binary).
    [<Literal>]
    let DefaultFileName = "module-bindings.json"

    /// Current manifest schema version. A reader rejects a higher major it
    /// does not understand rather than silently mis-binding.
    [<Literal>]
    let CurrentVersion = 1

    let private parseEntry (moduleId: string) (entry: JsonElement) : Result<ModuleBindingStamp, string> =
        let prop (name: string) =
            match entry.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> Ok(v.GetString())
            | _ -> Error(sprintf "binding for '%s' is missing the string field '%s'" moduleId name)

        match entry.TryGetProperty "kind" with
        | true, k when k.ValueKind = JsonValueKind.String ->
            match k.GetString() with
            | "mac" ->
                match prop "keyId", prop "tag" with
                | Ok keyId, Ok tag -> Ok(MacStamp(keyId, tag))
                | Error e, _
                | _, Error e -> Error e
            | "jws" -> prop "detachedJws" |> Result.map JwsStamp
            | other -> Error(sprintf "binding for '%s' has unknown kind '%s' (expected 'mac' or 'jws')" moduleId other)
        | _ -> Error(sprintf "binding for '%s' is missing the string field 'kind'" moduleId)

    /// Parse a manifest JSON document into a `moduleId → stamp` map.
    let parse (json: string) : Result<Map<string, ModuleBindingStamp>, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let versionOk =
                match root.TryGetProperty "version" with
                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() <= CurrentVersion
                | _ -> true // version is advisory; absent ⇒ assume current

            if not versionOk then
                Error(
                    sprintf "module-binding manifest version is newer than this SDK understands (max %d)" CurrentVersion
                )
            else
                match root.TryGetProperty "bindings" with
                | true, bindings when bindings.ValueKind = JsonValueKind.Object ->
                    (Ok Map.empty, bindings.EnumerateObject())
                    ||> Seq.fold (fun acc prop ->
                        match acc with
                        | Error _ -> acc
                        | Ok m ->
                            match parseEntry prop.Name prop.Value with
                            | Ok stamp -> Ok(Map.add prop.Name stamp m)
                            | Error e -> Error e)
                | true, _ -> Error "module-binding manifest 'bindings' must be a JSON object"
                | false, _ -> Ok Map.empty // no bindings ⇒ empty (valid)
        with ex ->
            Error(sprintf "module-binding manifest is not valid JSON: %s" ex.Message)

    // ─── Phase 216 — optional per-entry SBOM ────────────────────────────
    //
    // A manifest entry may additionally carry a signed SBOM describing what's
    // *inside* the bound module: a `sbom` object (`components`: name / version
    // / sha256) plus a `sbomSig` object of the SAME stamp shape (`kind` mac /
    // jws) as the module stamp, covering the SBOM's canonical bytes. The
    // reader only parses — verification (the crypto) lives in the
    // `DefaultModuleBindingVerifier` (`ToolUp.ArtefactSigning`), as it does
    // for the module stamp. An entry with no `sbom` section yields nothing
    // here, so a stamp-only manifest is byte-for-byte the Phase-166 reader.

    let private parseSbomComponent (moduleId: string) (el: JsonElement) : Result<ModuleSbomComponent, string> =
        if el.ValueKind <> JsonValueKind.Object then
            Error(sprintf "SBOM component for '%s' must be a JSON object" moduleId)
        else
            let str (name: string) =
                match el.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> "" // version / sha256 are optional per component

            match el.TryGetProperty "name" with
            | true, n when n.ValueKind = JsonValueKind.String ->
                Ok {
                    Name = n.GetString()
                    Version = str "version"
                    Sha256 = str "sha256"
                }
            | _ -> Error(sprintf "an SBOM component for '%s' is missing the string field 'name'" moduleId)

    /// Parse the optional SBOM section of one entry. `Ok None` when the entry
    /// carries no `sbom` (the stamp-only path); `Ok(Some _)` when both `sbom`
    /// and its `sbomSig` parse; `Error` when `sbom` is present but malformed
    /// or its signature is missing (fail-closed, never silently dropped).
    let private parseSbomEntry (moduleId: string) (entry: JsonElement) : Result<ModuleSbomStamp option, string> =
        match entry.TryGetProperty "sbom" with
        | false, _ -> Ok None
        | true, sbom when sbom.ValueKind <> JsonValueKind.Object ->
            Error(sprintf "binding for '%s' has a non-object 'sbom' section" moduleId)
        | true, sbom ->
            let components =
                match sbom.TryGetProperty "components" with
                | true, c when c.ValueKind = JsonValueKind.Array ->
                    (Ok [], c.EnumerateArray())
                    ||> Seq.fold (fun acc el ->
                        match acc with
                        | Error _ -> acc
                        | Ok xs -> parseSbomComponent moduleId el |> Result.map (fun comp -> xs @ [ comp ]))
                | true, _ -> Error(sprintf "SBOM 'components' for '%s' must be a JSON array" moduleId)
                | false, _ -> Ok [] // an SBOM with no components is valid (empty bill)

            match components with
            | Error e -> Error e
            | Ok comps ->
                match entry.TryGetProperty "sbomSig" with
                | true, sig' when sig'.ValueKind = JsonValueKind.Object ->
                    parseEntry moduleId sig'
                    |> Result.map (fun signature ->
                        Some {
                            Sbom = { Components = comps }
                            Signature = signature
                        })
                | _ -> Error(sprintf "binding for '%s' carries an 'sbom' but no 'sbomSig' signature object" moduleId)

    /// Parse a manifest document into the `moduleId → signed-SBOM` map. Only
    /// entries that carry an `sbom` section appear; a stamp-only manifest
    /// yields an empty map.
    let parseSboms (json: string) : Result<Map<string, ModuleSbomStamp>, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            match root.TryGetProperty "bindings" with
            | true, bindings when bindings.ValueKind = JsonValueKind.Object ->
                (Ok Map.empty, bindings.EnumerateObject())
                ||> Seq.fold (fun acc prop ->
                    match acc with
                    | Error _ -> acc
                    | Ok m ->
                        match parseSbomEntry prop.Name prop.Value with
                        | Ok None -> Ok m
                        | Ok(Some sbom) -> Ok(Map.add prop.Name sbom m)
                        | Error e -> Error e)
            | true, _ -> Error "module-binding manifest 'bindings' must be a JSON object"
            | false, _ -> Ok Map.empty
        with ex ->
            Error(sprintf "module-binding manifest is not valid JSON: %s" ex.Message)

    // ─── Phase 589 — optional per-entry certified surface ───────────────
    //
    // A manifest entry may additionally carry the module's CERTIFIED SURFACE:
    // a `certifiedSurface` object (`surfaceJson` — the canonical projection the
    // certifying run observed; `surfaceHash`; an optional `verdict` recording
    // that run's conformance-pack result) plus a `certifiedSurfaceSig` object
    // of the SAME stamp shape as the module stamp, covering the certification's
    // canonical bytes. As with the SBOM, the reader only PARSES — the crypto
    // and the live-vs-certified comparison live in the
    // `DefaultModuleBindingVerifier` (`ToolUp.ArtefactSigning`). An entry with
    // no `certifiedSurface` section yields nothing here, so a stamp-only
    // manifest is byte-for-byte the Phase-166 reader.

    let private parseLaw (moduleId: string) (el: JsonElement) : Result<ModuleLawOutcome, string> =
        if el.ValueKind <> JsonValueKind.Object then
            Error(sprintf "a conformance law result for '%s' must be a JSON object" moduleId)
        else
            let str (name: string) =
                match el.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> ""

            match el.TryGetProperty "law" with
            | true, n when n.ValueKind = JsonValueKind.String ->
                Ok {
                    Law = n.GetString()
                    Passed =
                        match el.TryGetProperty "passed" with
                        | true, p -> p.ValueKind = JsonValueKind.True
                        | _ -> false
                    Detail = str "detail"
                }
            | _ -> Error(sprintf "a conformance law result for '%s' is missing the string field 'law'" moduleId)

    /// Parse the optional `verdict` object of a certification. `Ok None` when
    /// absent (a certification may record the surface without a verdict).
    let private parseVerdict
        (moduleId: string)
        (certified: JsonElement)
        : Result<ModuleConformanceVerdict option, string> =
        match certified.TryGetProperty "verdict" with
        | false, _ -> Ok None
        | true, v when v.ValueKind <> JsonValueKind.Object ->
            Error(sprintf "the certification for '%s' has a non-object 'verdict'" moduleId)
        | true, v ->
            let str (name: string) =
                match v.TryGetProperty name with
                | true, x when x.ValueKind = JsonValueKind.String -> x.GetString()
                | _ -> ""

            let laws =
                match v.TryGetProperty "laws" with
                | true, l when l.ValueKind = JsonValueKind.Array ->
                    (Ok [], l.EnumerateArray())
                    ||> Seq.fold (fun acc el ->
                        match acc with
                        | Error _ -> acc
                        | Ok xs -> parseLaw moduleId el |> Result.map (fun law -> xs @ [ law ]))
                | true, _ -> Error(sprintf "the conformance verdict 'laws' for '%s' must be a JSON array" moduleId)
                | false, _ -> Ok [] // a verdict with no law results is valid (an empty run)

            laws
            |> Result.map (fun ls ->
                Some {
                    PackVersion = str "packVersion"
                    Laws = ls
                    RunStamp = str "runStamp"
                })

    /// Parse the optional certified-surface section of one entry. `Ok None`
    /// when the entry carries none (the uncertified path); `Error` when
    /// `certifiedSurface` is present but malformed or unsigned — fail-closed,
    /// never silently dropped.
    let private parseCertificationEntry
        (moduleId: string)
        (entry: JsonElement)
        : Result<ModuleCertificationStamp option, string> =
        match entry.TryGetProperty "certifiedSurface" with
        | false, _ -> Ok None
        | true, certified when certified.ValueKind <> JsonValueKind.Object ->
            Error(sprintf "binding for '%s' has a non-object 'certifiedSurface' section" moduleId)
        | true, certified ->
            let requiredString (name: string) =
                match certified.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.String -> Ok(v.GetString())
                | _ -> Error(sprintf "the certification for '%s' is missing the string field '%s'" moduleId name)

            match requiredString "surfaceJson", requiredString "surfaceHash" with
            | Error e, _
            | _, Error e -> Error e
            | Ok surfaceJson, Ok surfaceHash ->
                match parseVerdict moduleId certified with
                | Error e -> Error e
                | Ok verdict ->
                    match entry.TryGetProperty "certifiedSurfaceSig" with
                    | true, sig' when sig'.ValueKind = JsonValueKind.Object ->
                        parseEntry moduleId sig'
                        |> Result.map (fun signature ->
                            Some {
                                Certified = {
                                    SurfaceJson = surfaceJson
                                    SurfaceHash = surfaceHash
                                    Verdict = verdict
                                }
                                Signature = signature
                            })
                    | _ ->
                        Error(
                            sprintf
                                "binding for '%s' carries a 'certifiedSurface' but no 'certifiedSurfaceSig' signature object"
                                moduleId
                        )

    /// Parse a manifest document into the `moduleId → signed-certification`
    /// map. Only entries that carry a `certifiedSurface` section appear; a
    /// stamp-only manifest yields an empty map.
    let parseCertifications (json: string) : Result<Map<string, ModuleCertificationStamp>, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            match root.TryGetProperty "bindings" with
            | true, bindings when bindings.ValueKind = JsonValueKind.Object ->
                (Ok Map.empty, bindings.EnumerateObject())
                ||> Seq.fold (fun acc prop ->
                    match acc with
                    | Error _ -> acc
                    | Ok m ->
                        match parseCertificationEntry prop.Name prop.Value with
                        | Ok None -> Ok m
                        | Ok(Some certification) -> Ok(Map.add prop.Name certification m)
                        | Error e -> Error e)
            | true, _ -> Error "module-binding manifest 'bindings' must be a JSON object"
            | false, _ -> Ok Map.empty
        with ex ->
            Error(sprintf "module-binding manifest is not valid JSON: %s" ex.Message)

    /// Load a manifest from `path`. An absent file yields an empty map (the
    /// GP-13 "no manifest" path); a present-but-malformed file is an
    /// `Error` the caller fails closed on rather than silently ignoring.
    let load (path: string) : Result<Map<string, ModuleBindingStamp>, string> =
        if not (File.Exists path) then
            Ok Map.empty
        else
            try
                parse (File.ReadAllText path)
            with ex ->
                Error(sprintf "failed to read module-binding manifest '%s': %s" path ex.Message)

    /// Load the conventional `module-bindings.json` from a directory.
    let loadFromDir (dir: string) : Result<Map<string, ModuleBindingStamp>, string> =
        load (Path.Combine(dir, DefaultFileName))

    /// Load the `moduleId → signed-SBOM` map from `path` (Phase 216). An
    /// absent file yields an empty map; a present-but-malformed file is an
    /// `Error` the caller fails closed on.
    let loadSboms (path: string) : Result<Map<string, ModuleSbomStamp>, string> =
        if not (File.Exists path) then
            Ok Map.empty
        else
            try
                parseSboms (File.ReadAllText path)
            with ex ->
                Error(sprintf "failed to read module-binding manifest '%s': %s" path ex.Message)

    /// Load the SBOM map from the conventional `module-bindings.json` in a
    /// directory.
    let loadSbomsFromDir (dir: string) : Result<Map<string, ModuleSbomStamp>, string> =
        loadSboms (Path.Combine(dir, DefaultFileName))

    /// Load the `moduleId → signed-certification` map from `path` (Phase 589).
    /// An absent file yields an empty map; a present-but-malformed file is an
    /// `Error` the caller fails closed on.
    let loadCertifications (path: string) : Result<Map<string, ModuleCertificationStamp>, string> =
        if not (File.Exists path) then
            Ok Map.empty
        else
            try
                parseCertifications (File.ReadAllText path)
            with ex ->
                Error(sprintf "failed to read module-binding manifest '%s': %s" path ex.Message)

    /// Load the certification map from the conventional `module-bindings.json`
    /// in a directory.
    let loadCertificationsFromDir (dir: string) : Result<Map<string, ModuleCertificationStamp>, string> =
        loadCertifications (Path.Combine(dir, DefaultFileName))

    /// Apply a manifest to one module: attach the stamp filed under the
    /// module's name, or leave the module unchanged when it has no entry.
    let applyTo (manifest: Map<string, ModuleBindingStamp>) (m: ServerModule) : ServerModule =
        match Map.tryFind m.Name manifest with
        | Some stamp -> ServerModule.withBindingStamp stamp m
        | None -> m

    /// Apply a manifest across a module list (the composition root's
    /// `modules |> ModuleBindingManifest.applyToAll manifest` before
    /// `addModules`).
    let applyToAll (manifest: Map<string, ModuleBindingStamp>) (modules: ServerModule list) : ServerModule list =
        modules |> List.map (applyTo manifest)