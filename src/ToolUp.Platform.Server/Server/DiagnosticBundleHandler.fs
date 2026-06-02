module ToolUp.Platform.DiagnosticBundleHandler

open System
open System.Formats.Tar
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.ConfigValidatorAggregator

// ─── Phase 9n — /dev/bundle diagnostic-support archive ──────────────
//
// One-shot operator endpoint that produces a tar containing every
// `/dev/inspect` section + a redacted `ServerConfig` + the recent audit
// tail + last preflight outcome + live health-check probes + companion
// versions + dependency graph + a manifest describing the bundle's
// composition. Operator runs one curl, attaches the output to a
// support ticket. Halves time-to-diagnose for incidents involving
// cross-companion behaviour.
//
// **Activation gate.** Same shape as `/dev/inspect`:
// `ServerConfig.EnableDevEndpoints = true`. Default `false` keeps
// the endpoint off in production. Per the Phase 11.B de-gating, the
// runtime flag is the sole gate — no compile-time `#if DEBUG` belt-
// and-suspenders; the bundle handler ships in Release builds and
// returns 404 by default config, matching `/dev/inspect`.
//
// **Privileged action.** Every bundle download emits a
// `DiagnosticBundleAccessed` audit event under `_platform` scope with
// `SourceModule = "_platform.diagnostics"`. The download is itself
// privileged; operators inspecting the audit trail can see who
// extracted a bundle from this deployment, when, and whether the
// 50 MB cap forced truncation.
//
// **Secrets.** Every byte that goes into the tar passes through a
// JSON-tree walk against the same property-name suffix allowlist
// `ConfigDriftDetector`'s snapshot pass uses (`apikey`, `token`,
// `secret`, `password`, case-insensitive). The allowlist intentionally
// duplicates the drift detector's inline copy — extracting it into a
// shared module when there are only two consumers does not earn its
// keep, and a future "add a new suffix" touch is a two-edit operation,
// not a refactor. `ServerConfig` itself does not currently carry
// secrets (those live in `ISecretStore`); the redaction is defence-in-
// depth against future fields and any caller-supplied string that
// surfaces in `inspect.json` / `audit-tail.jsonl`.
//
// **Size cap.** Hard 50 MB ceiling, applied per-section in priority
// order. `audit-tail.jsonl` is the only unbounded section; if the
// running tally crosses the cap while building it, the event count
// is reduced until the rest of the bundle fits and the truncation is
// recorded in `manifest.json`. The bundle always produces — a
// truncated bundle is more useful for a support ticket than a 0-byte
// failure.

// ─── Constants ───────────────────────────────────────────────────────

[<Literal>]
let private bundleSizeCapBytes = 52_428_800L // 50 MiB

[<Literal>]
let private auditTailMaxEvents = 1000

[<Literal>]
let private bundleSchema = 1

[<Literal>]
let private platformScopeId = "_platform"

let private anonymousSentinel = "_anonymous"

// ─── Redaction walk (duplicate of `ConfigDriftDetector`'s allowlist;
//      see file header for the "don't extract" rationale). ──────────

let private redactionSuffixes = [ "apikey"; "token"; "secret"; "password" ]

let private shouldRedact (propName: string) =
    let lower = propName.ToLowerInvariant()
    redactionSuffixes |> List.exists lower.EndsWith

let private redactedString (length: int) = sprintf "<redacted:length=%d>" length

let rec private redact (node: JsonNode) : unit =
    if isNull node then
        ()
    else
        match node with
        | :? JsonObject as obj ->
            // Snapshot keys before mutating — JsonObject raises
            // InvalidOperationException if mutated during enumeration.
            let names = obj |> Seq.map (fun kvp -> kvp.Key) |> Seq.toArray

            for name in names do
                let child = obj.[name]

                if shouldRedact name then
                    if isNull child then
                        ()
                    else
                        match child.GetValueKind() with
                        | JsonValueKind.Null -> ()
                        | JsonValueKind.String ->
                            let s = child.GetValue<string>()
                            obj.[name] <- JsonValue.Create(redactedString s.Length) :> JsonNode
                        | _ ->
                            let serialised = child.ToJsonString()
                            obj.[name] <- JsonValue.Create(redactedString serialised.Length) :> JsonNode
                else
                    redact child
        | :? JsonArray as arr ->
            for child in arr do
                redact child
        | _ -> ()

// ─── JSON helpers ────────────────────────────────────────────────────

let private jsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o

let private renderJson (value: obj) : string =
    JsonSerializer.Serialize(value, jsonOptions)

let private utf8 (s: string) = Encoding.UTF8.GetBytes s

// ─── Per-section builders ────────────────────────────────────────────

/// `inspect.json` — full `/dev/inspect` payload, redacted. The redaction
/// walk also runs over the inspect tree as a defence-in-depth pass (the
/// `Caller` block may carry a userId-shaped header name that future
/// schemas could promote to a secret slot).
let private buildInspectJson
    (config: ServerConfig)
    (capture: DevDiagnosticsHandler.DevDiagnosticsCapture)
    (ctx: HttpContext)
    : Async<byte[]> =
    async {
        let! report = DevDiagnosticsHandler.buildReport config capture ctx
        let raw = JsonSerializer.Serialize(report, jsonOptions)
        let parsed = JsonNode.Parse raw
        redact parsed
        return parsed.ToJsonString(jsonOptions) |> utf8
    }

/// `config.json` — resolved `ServerConfig` serialised + redacted via
/// the same suffix allowlist `ConfigDriftDetector` uses for its
/// persisted startup snapshot.
let private buildConfigJson (config: ServerConfig) : byte[] =
    let raw = JsonSerializer.Serialize(config, jsonOptions)
    let parsed = JsonNode.Parse raw
    redact parsed
    parsed.ToJsonString(jsonOptions) |> utf8

/// `validators.json` — `IConfigValidator` preflight snapshot
/// (Phase 9m). Empty when no validators registered or
/// `SkipPreflight = true`. Snapshot reflects the most recent
/// startup; the bundle does NOT re-run validators on access (heavier
/// than health probes; sentinel writes / DNS resolution are too
/// expensive for an operator-pulled support file).
let private buildValidatorsJson (ctx: HttpContext) : byte[] =
    let snapshot =
        match ctx.RequestServices.GetService(typeof<IPreflightSnapshot>) with
        | :? IPreflightSnapshot as snap ->
            snap.LastRun
            |> List.map (fun o -> {|
                Name = o.Name
                Status = ConfigValidation.ValidationResult.status o.Result
                Message = ConfigValidation.ValidationResult.message o.Result
                ElapsedMs = o.ElapsedMs
            |})
        | _ -> []

    renderJson snapshot |> utf8

/// `health.json` — fan-out across every registered `IHealthCheck`
/// (Phase 9k). Probes invoked in parallel with their declared
/// per-probe timeout, same as `/dev/inspect`'s "Health checks"
/// panel and the BCL aggregator behind `/ready`. The bundle
/// captures a fresh probe run rather than a cached state so the
/// support archive reflects "what was true at the moment of
/// extraction" — exactly what an operator filing a ticket wants.
let private buildHealthJson (ctx: HttpContext) : Async<byte[]> = async {
    let probes = ctx.RequestServices.GetServices<IHealthCheck>() |> List.ofSeq

    let! rows =
        if probes.IsEmpty then
            async.Return [||]
        else
            probes |> List.map HealthCheckRunner.runOne |> Async.Parallel

    let payload =
        rows
        |> Array.map (fun r -> {|
            Name = r.Name
            Kind = HealthKind.toString r.Kind
            TimeoutMs = r.TimeoutMs
            Status = r.Status
            Message = r.Message
            ElapsedMs = r.ElapsedMs
        |})

    return renderJson payload |> utf8
}

/// `version.json` — companion + framework assembly versions. Sourced
/// the same way `ConfigDriftDetector` computes its companion-set
/// hash; the bundle additionally surfaces the BCL + ASP.NET Core
/// framework versions so a support ticket can see whether the
/// deployment is on a patched runtime.
let private buildVersionJson () : byte[] =
    let assemblies =
        AppDomain.CurrentDomain.GetAssemblies()
        |> Array.choose (fun a ->
            let name = a.GetName()
            let simple = name.Name |> Option.ofObj |> Option.defaultValue ""

            if simple.StartsWith("ToolUp.", StringComparison.Ordinal) then
                Some(simple, string name.Version)
            else
                None)
        |> Array.distinct
        |> Array.sortBy fst
        |> Array.map (fun (n, v) -> {| Name = n; Version = v |})

    let payload = {|
        Generated = DateTime.UtcNow.ToString("o")
        FrameworkDescription = Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        OSDescription = Runtime.InteropServices.RuntimeInformation.OSDescription
        ProcessArchitecture = string Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
        Companions = assemblies
    |}

    renderJson payload |> utf8

/// `dependency-graph.json` — DI services + the constructor parameter
/// types of their implementations (each parameter type itself tagged
/// `Registered: true|false` so a reader can see whether an SDK-resolved
/// dependency is a known registration or an external/BCL type).
/// Reflection failures (open generics, factory-only registrations,
/// instance-only registrations) record an empty `Dependencies` list
/// rather than failing the bundle. The graph extends `/dev/inspect`'s
/// "DI services" panel with the inter-service edges operators trace
/// when answering "what does X depend on?".
let private buildDependencyGraphJson (capture: DevDiagnosticsHandler.DevDiagnosticsCapture) : byte[] =
    let registeredTypeNames =
        capture.Services |> List.map _.ServiceTypeName |> Set.ofList

    let dependenciesFor
        (impl: string option)
        : {|
              ParamType: string
              Registered: bool
          |} list
        =
        match impl with
        | None
        | Some "<factory>" -> []
        | Some implTypeName ->
            let resolved =
                AppDomain.CurrentDomain.GetAssemblies()
                |> Array.tryPick (fun a ->
                    try
                        a.GetType(implTypeName, false) |> Option.ofObj
                    with _ ->
                        None)

            match resolved with
            | None -> []
            | Some t ->
                try
                    let ctors = t.GetConstructors()

                    if ctors.Length = 0 then
                        []
                    else
                        // Greedy DI convention: the public ctor with the
                        // most parameters is the one the resolver wires.
                        // Mirrors `ActivatorUtilities`'s tie-break.
                        let preferred = ctors |> Array.maxBy _.GetParameters().Length

                        preferred.GetParameters()
                        |> Array.map (fun p ->
                            let typeName =
                                if isNull p.ParameterType.FullName then
                                    p.ParameterType.Name
                                else
                                    p.ParameterType.FullName

                            {|
                                ParamType = typeName
                                Registered = Set.contains typeName registeredTypeNames
                            |})
                        |> Array.toList
                with _ -> []

    let nodes =
        capture.Services
        |> List.map (fun s -> {|
            ServiceType = s.ServiceTypeName
            Lifetime = s.Lifetime
            Implementation = s.ImplementationTypeName
            Dependencies = dependenciesFor s.ImplementationTypeName
        |})

    renderJson nodes |> utf8

/// Caller summary for the audit-trail query + audit event payload.
/// Mirrors `DevDiagnosticsHandler.buildCallerSummary`'s posture:
/// best-effort identity / scope resolution that never fails the
/// endpoint — if scope resolution throws, the bundle still produces
/// and the audit trail narrows to `_platform` scope.
let private resolveCaller (ctx: HttpContext) : Async<string * string option> = async {
    try
        let authProv =
            ctx.RequestServices.GetService(typeof<IAuthProvider>) :?> IAuthProvider

        let resolver =
            ctx.RequestServices.GetService(typeof<IStorageScopeResolver>) :?> IStorageScopeResolver

        let! request = ScopeRequestExtractor.fromHttpContext ctx authProv
        let user = request.User |> Option.defaultValue AuthenticatedUser.anonymous

        let userId =
            if AuthenticatedUser.isAnonymous user then
                anonymousSentinel
            else
                user.UserId

        let! scopeResult = resolver.Resolve request

        let scopeId =
            match scopeResult with
            | Ok scope -> Some scope.ScopeId
            | Error _ -> None

        return userId, scopeId
    with _ ->
        return anonymousSentinel, None
}

/// `audit-tail.jsonl` — most-recent audit events under
/// `SourceModule = "_platform.audit"`. Queries `_platform` (always)
/// plus the caller's resolved scope (when available); cross-scope
/// reads are structurally impossible per `IEventStore`'s contract,
/// so the bundle settles for the two scopes most likely to hold
/// load-bearing rows — `_platform` for SDK-level events (drift,
/// encryption-key ops, platform-admin grants, audit-sink lifecycle,
/// rate-limit refusals) and the caller's scope for team-shaped events
/// (file ops, permission changes, entity lifecycle).
///
/// Each line is one JSON record carrying the wrapping `ModuleEvent`
/// metadata (`OccurredAt`, `ScopeId`, `EventType`, `Id`) plus the
/// pre-redacted payload. Lines are written newest-first by
/// `OccurredAt`. Returns `(bytes, eventsIncluded, truncated)`; the
/// caller passes `truncated` into `manifest.json`.
let private buildAuditTailJsonl
    (callerScopeId: string option)
    (budgetBytes: int64)
    (ctx: HttpContext)
    : Async<byte[] * int * bool> =
    async {
        let eventStoreOpt =
            match ctx.RequestServices.GetService(typeof<IEventStore>) with
            | :? IEventStore as s -> Some s
            | _ -> None

        match eventStoreOpt with
        | None -> return Array.empty, 0, false
        | Some store ->
            let scopesToRead =
                match callerScopeId with
                | Some sid when sid <> platformScopeId -> [ platformScopeId; sid ]
                | _ -> [ platformScopeId ]

            let! perScope =
                scopesToRead
                |> List.map (fun s -> async {
                    try
                        return! store.ReadBySource(s, AuditSourceModule.value)
                    with _ ->
                        return []
                })
                |> Async.Parallel

            let merged =
                perScope
                |> Array.toList
                |> List.collect id
                |> List.sortByDescending _.OccurredAt
                |> List.truncate auditTailMaxEvents

            let buffer = StringBuilder()
            let mutable byteCount = 0L
            let mutable included = 0
            let mutable truncated = false

            for evt in merged do
                if not truncated then
                    let payloadNode =
                        try
                            JsonNode.Parse evt.Payload
                        with _ ->
                            JsonValue.Create(evt.Payload) :> JsonNode

                    redact payloadNode

                    let line =
                        let obj = JsonObject()
                        obj.["Id"] <- JsonValue.Create(string evt.Id)
                        obj.["OccurredAt"] <- JsonValue.Create(evt.OccurredAt.ToUniversalTime().ToString("o"))
                        obj.["ScopeId"] <- JsonValue.Create(evt.ScopeId)
                        obj.["SourceModule"] <- JsonValue.Create(evt.SourceModule)
                        obj.["EventType"] <- JsonValue.Create(evt.EventType)
                        obj.["Payload"] <- payloadNode

                        obj.ToJsonString()

                    let lineBytes = Encoding.UTF8.GetByteCount(line + "\n") |> int64

                    if byteCount + lineBytes > budgetBytes then
                        truncated <- true
                    else
                        buffer.Append(line).Append('\n') |> ignore
                        byteCount <- byteCount + lineBytes
                        included <- included + 1

            return buffer.ToString() |> utf8, included, truncated || included < merged.Length
    }

// ─── Manifest + tar assembly ─────────────────────────────────────────

let private buildManifest
    (config: ServerConfig)
    (capture: DevDiagnosticsHandler.DevDiagnosticsCapture)
    (callerUserId: string)
    (callerScopeId: string option)
    (auditEventsIncluded: int)
    (auditTruncated: bool)
    (sectionSizes: (string * int64) list)
    : byte[] =
    let payload = {|
        Schema = bundleSchema
        Generated = DateTime.UtcNow.ToString("o")
        Surfaces = DeploymentConfig.surfacesLabel config
        Caller = {|
            UserId = callerUserId
            ScopeId = callerScopeId
        |}
        Sections = sectionSizes |> List.map (fun (n, sz) -> {| Name = n; SizeBytes = sz |})
        AuditTail = {|
            EventsIncluded = auditEventsIncluded
            MaxEvents = auditTailMaxEvents
            Truncated = auditTruncated
        |}
        BundleSizeCapBytes = bundleSizeCapBytes
        ModuleCount = capture.Modules.Length
        ServiceCount = capture.Services.Length
    |}

    renderJson payload |> utf8

let private addTarEntry (writer: TarWriter) (name: string) (bytes: byte[]) =
    let entry = PaxTarEntry(TarEntryType.RegularFile, name)
    entry.Mode <- UnixFileMode.UserRead ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead
    entry.DataStream <- new MemoryStream(bytes)
    writer.WriteEntry entry

let private writeTar (entries: (string * byte[]) list) : byte[] =
    use stream = new MemoryStream()

    // `leaveOpen = true` so the writer flushes its tail block but our
    // MemoryStream stays open for `ToArray`. Dispose explicitly so the
    // flush happens before we read the bytes back; `use` would extend
    // the binding to the end of this function and read before flush.
    let writer = new TarWriter(stream, TarEntryFormat.Pax, true)

    for (name, bytes) in entries do
        addTarEntry writer name bytes

    writer.Dispose()
    stream.ToArray()

// ─── Route handler ───────────────────────────────────────────────────

let private bundleHandler (config: ServerConfig) (capture: DevDiagnosticsHandler.DevDiagnosticsCapture) : HttpHandler =
    fun next ctx -> task {
        let! callerUserId, callerScopeId = resolveCaller ctx

        // Build the bounded sections first so the audit-tail can claim
        // whatever cap budget remains. Each section is built in
        // priority order; the audit tail receives the surplus.
        let! inspectBytes = buildInspectJson config capture ctx
        let configBytes = buildConfigJson config
        let validatorsBytes = buildValidatorsJson ctx
        let! healthBytes = buildHealthJson ctx
        let versionBytes = buildVersionJson ()
        let depGraphBytes = buildDependencyGraphJson capture

        // Reserve ~32 KiB for the manifest itself; the manifest never
        // gets near this in practice (a few hundred bytes), but the
        // headroom is cheap and avoids re-budgeting after manifest
        // assembly.
        let manifestReserveBytes = 32_768L

        let fixedSectionsBytes =
            int64 inspectBytes.Length
            + int64 configBytes.Length
            + int64 validatorsBytes.Length
            + int64 healthBytes.Length
            + int64 versionBytes.Length
            + int64 depGraphBytes.Length

        let auditBudget =
            max 0L (bundleSizeCapBytes - fixedSectionsBytes - manifestReserveBytes)

        let! auditTailBytes, eventsIncluded, auditTruncated = buildAuditTailJsonl callerScopeId auditBudget ctx

        let sectionSizes = [
            "inspect.json", int64 inspectBytes.Length
            "config.json", int64 configBytes.Length
            "audit-tail.jsonl", int64 auditTailBytes.Length
            "validators.json", int64 validatorsBytes.Length
            "health.json", int64 healthBytes.Length
            "version.json", int64 versionBytes.Length
            "dependency-graph.json", int64 depGraphBytes.Length
        ]

        let manifestBytes =
            buildManifest config capture callerUserId callerScopeId eventsIncluded auditTruncated sectionSizes

        let tarBytes =
            writeTar [
                "manifest.json", manifestBytes
                "inspect.json", inspectBytes
                "config.json", configBytes
                "audit-tail.jsonl", auditTailBytes
                "validators.json", validatorsBytes
                "health.json", healthBytes
                "version.json", versionBytes
                "dependency-graph.json", depGraphBytes
            ]

        // Audit emission is best-effort; a write failure must not
        // poison the response. The `IAuditLog` default already
        // swallows + logs exceptions internally.
        let auditLogOpt =
            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as log -> Some log
            | _ -> None

        match auditLogOpt with
        | Some log ->
            do!
                log.Record(
                    platformScopeId,
                    DiagnosticBundleAccessed {
                        UserId = callerUserId
                        ScopeId = callerScopeId
                        BundleSizeBytes = int64 tarBytes.Length
                        Truncated = auditTruncated
                    }
                )
        | None -> ()

        let stampUtc = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
        let filename = sprintf "toolup-diagnostic-bundle-%s.tar" stampUtc
        let contentDisposition = sprintf "attachment; filename=\"%s\"" filename

        ctx.Response.ContentType <- "application/x-tar"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        ctx.Response.Headers["Content-Disposition"] <- contentDisposition
        ctx.Response.ContentLength <- Nullable(int64 tarBytes.Length)

        do! ctx.Response.Body.WriteAsync(tarBytes, 0, tarBytes.Length)
        return! next ctx
    }

/// Single Giraffe route for `/dev/bundle`. The caller composes this
/// into the `devDiagnosticsRoutes` block in `SDK.Server.fs` so the
/// same `ServerConfig.EnableDevEndpoints` runtime gate covers both
/// `/dev/inspect` and `/dev/bundle`.
let route (config: ServerConfig) (capture: DevDiagnosticsHandler.DevDiagnosticsCapture) : HttpHandler =
    route "/dev/bundle" >=> bundleHandler config capture