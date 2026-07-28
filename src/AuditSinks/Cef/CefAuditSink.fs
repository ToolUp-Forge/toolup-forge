module ToolUp.Platform.AuditSinks.Cef

open System
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.CefFormat
open ToolUp.Platform.AuditSinks.CefSyslog
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 9g.A CEF `IAuditSink` companion. Renders every audit envelope as
// a Common Event Format line and ships it to a syslog collector, so
// audit traffic reaches the SIEMs that predate JSON log intake —
// ArcSight, IBM QRadar, LogRhythm, McAfee ESM. Slots alongside the
// Phase 9g `SplunkHec` / `DatadogLogs` / `S3Archive` companions; a
// deployment can compose several at once (the replicator keys cursors
// per sink `Name`).
//
// **DISTRIBUTED-READY.** The sink holds no per-delivery state: the
// endpoint is read from `ISecretStore` on every `Deliver`, the device
// identity is read from the config blob on every `Deliver`, and the
// transport connects and closes inside the call. Two processes running
// this sink behave identically, and a restart loses nothing beyond the
// replicator's own cursor (portability rule 4). It is NOT dev-only.
//
// **No vendor SDK** (GP 1) — BCL sockets and `System.Text.Json` only.
//
// **Configuration lives in two places, deliberately.**
//   * The *endpoint* (`host:port`) is a secret-store value, matching
//     every other Phase 9g companion: `SplunkHec` reads its HEC token and
//     `DatadogLogs` its API key from `ISecretStore` on each `Deliver`, so
//     a rotation takes effect on the next batch with no redeploy. The
//     endpoint gets the same treatment because a SOC migration is
//     operationally identical to a credential rotation.
//   * The *device identity* (Vendor / Product / DeviceVersion — CEF
//     header fields 1–3) comes from a `_platform/audit/cef.json` blob.
//     These are not secrets; they are the strings a customer's
//     correlation rules match on, and they are handed over by the
//     customer's SOC rather than chosen by the deployment. Reading them
//     from the platform blob container keeps them editable by an
//     operator without a code change, and the read is per-`Deliver` for
//     the same reason the secrets are.
//
// **Failure posture.** A missing or malformed config blob is NOT an
// error: the sink falls back to `CefSinkSettings.Identity` and delivers.
// Refusing to ship audit events because an optional cosmetic override is
// absent would trade a labelling problem for an audit gap. A missing or
// malformed *endpoint* secret IS an error — there is nowhere to send.
//
// **Batch idempotency.** Every line carries `externalId`, a SHA-256 over
// the envelope's schema version, timestamp, scope, subject kind, event
// type, and serialised payload. The dispatcher retries whole batches and
// the catch-up sweep can re-deliver after a restart, so a redelivered
// envelope must present the same id — a random per-emission id (the
// shape `SplunkHec` uses) would make each retry look like a fresh event
// to a SIEM whose dedup window keys on it.
//
// **Strip-imports.** Removing the `<ProjectReference>` and the
// `ServerApp.withAuditSink` line returns the deployment to no external
// replication: `ComposeAudit.buildAuditReplicatorSubsystem` returns
// `None` on an empty sink list, so the replicator's background service,
// bounded channel, and event-store decorator are never constructed
// (GP 13).

/// Deployment settings for the CEF sink. Everything here has a default;
/// a deployment typically overrides `Protocol` (to TLS) and the framing
/// hostname, and leaves the identity to the config blob.
type CefSinkSettings = {
    /// Fallback CEF header identity, used when `ConfigBlobName` is
    /// absent or unparseable. The blob overrides field-by-field.
    Identity: CefDeviceIdentity
    /// Syslog wire protocol. `CefUdpSyslog` is the default because it is
    /// the one every SIEM accepts without configuration; deployments
    /// crossing an untrusted network should set `CefTlsSyslog`.
    Protocol: CefSyslogProtocol
    /// RFC 3164 framing parameters (facility, hostname, tag).
    Framing: CefSyslogFraming
    /// Blob container holding the device-identity config. `_platform` is
    /// the SDK's reserved platform container.
    ConfigContainer: string
    /// Blob name of the device-identity config, relative to
    /// `ConfigContainer`. Default `audit/cef.json`.
    ConfigBlobName: string
    /// Optional replacement for the built-in event-kind → CEF-severity
    /// classification. `None` uses `CefFormat.severityOf`. A deployment
    /// whose SOC has its own severity policy supplies a function here
    /// rather than forking the companion; out-of-range results are
    /// clamped into 0–10.
    SeverityOverride: (AuditEvent -> int) option
}

module CefSinkSettings =
    /// UDP syslog, `local0` framing, identity from
    /// `_platform/audit/cef.json` with `CefDeviceIdentity.defaults` as
    /// the fallback, built-in severity classification.
    let defaults: CefSinkSettings = {
        Identity = CefDeviceIdentity.defaults
        Protocol = CefUdpSyslog
        Framing = CefSyslogFraming.defaults
        ConfigContainer = "_platform"
        ConfigBlobName = "audit/cef.json"
        SeverityOverride = None
    }

/// Parse the `_platform/audit/cef.json` document into a
/// `CefDeviceIdentity`, overriding `fallback` field-by-field. Property
/// names are matched case-insensitively (`vendor` / `Vendor` both work) —
/// the file is hand-edited by operators, and rejecting it over a capital
/// letter would push a deployment onto the fallback identity silently.
///
/// Returns `None` when the document is not a JSON object or cannot be
/// parsed at all; a valid object with no recognised keys returns
/// `fallback` unchanged.
let parseIdentityJson (fallback: CefDeviceIdentity) (json: string) : CefDeviceIdentity option =
    if String.IsNullOrWhiteSpace json then
        None
    else
        try
            use document = JsonDocument.Parse json

            if document.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                let read (name: string) (current: string) =
                    let hit =
                        document.RootElement.EnumerateObject()
                        |> Seq.tryFind (fun property ->
                            String.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))

                    match hit with
                    | Some property when property.Value.ValueKind = JsonValueKind.String ->
                        let value = property.Value.GetString()
                        if String.IsNullOrWhiteSpace value then current else value
                    | _ -> current

                Some {
                    Vendor = read "vendor" fallback.Vendor
                    Product = read "product" fallback.Product
                    DeviceVersion = read "deviceVersion" fallback.DeviceVersion
                }
        with _ ->
            None

/// SDK CEF `IAuditSink`. One syslog write per batch; identity + endpoint
/// resolved per `Deliver`.
type CefAuditSink
    (
        name: string,
        settings: CefSinkSettings,
        secretStore: ISecretStore,
        endpointSecretKey: string,
        blobStorage: IBlobStorage,
        transport: ICefLineTransport
    ) =

    /// Read the device identity from the config blob, falling back to
    /// `settings.Identity` on any absence or malformation.
    member private _.ResolveIdentity() = async {
        try
            let! blob = blobStorage.Download(settings.ConfigContainer, settings.ConfigBlobName)

            match blob with
            | Ok bytes ->
                let json = Encoding.UTF8.GetString bytes

                return
                    parseIdentityJson settings.Identity json
                    |> Option.defaultValue settings.Identity
            | Error _ -> return settings.Identity
        with _ ->
            return settings.Identity
    }

    interface IAuditSink with
        member _.Name = name

        member _.SchemaVersion = AuditSchemaVersion.current

        member this.Deliver(batch) = async {
            if List.isEmpty batch then
                return Ok()
            else
                try
                    let! secret = secretStore.GetSecret("_platform", endpointSecretKey)

                    match secret with
                    | None ->
                        return
                            Error(
                                sprintf
                                    "CEF syslog endpoint not found in ISecretStore at _platform/%s (expected host:port)"
                                    endpointSecretKey
                            )
                    | Some raw ->
                        match CefSyslogEndpoint.parse raw with
                        | Error reason -> return Error(sprintf "CEF syslog endpoint invalid: %s" reason)
                        | Ok endpoint ->
                            let! identity = this.ResolveIdentity()

                            let records =
                                batch
                                |> List.map (fun envelope ->
                                    let severity =
                                        match settings.SeverityOverride with
                                        | Some scoreOf -> scoreOf envelope.Event |> max 0 |> min 10
                                        | None -> severityOf envelope.Event

                                    {
                                        CefLine = renderLineWith identity settings.SeverityOverride envelope
                                        Severity = severity
                                    })

                            return! transport.Send(endpoint, records)
                with ex ->
                    return Error(sprintf "CEF sink threw: %s" ex.Message)
        }

/// Construct a CEF sink over an explicit transport. The seam a
/// deployment reaches for when it needs a relay or an in-process
/// listener; `create` is the ordinary route.
let createWith
    (name: string)
    (settings: CefSinkSettings)
    (secretStore: ISecretStore)
    (endpointSecretKey: string)
    (blobStorage: IBlobStorage)
    (transport: ICefLineTransport)
    : IAuditSink =
    CefAuditSink(name, settings, secretStore, endpointSecretKey, blobStorage, transport) :> _

/// Construct a CEF sink with the syslog transport named by
/// `settings.Protocol`. `endpointSecretKey` references a `host:port`
/// value in `ISecretStore` under the `_platform` scope; the sink's `Name`
/// doubles as the replicator's cursor-key segment, so choose something
/// stable and deployment-unique (`"cef-soc-prod"`, not `"cef"`).
let create
    (name: string)
    (settings: CefSinkSettings)
    (secretStore: ISecretStore)
    (endpointSecretKey: string)
    (blobStorage: IBlobStorage)
    : IAuditSink =
    let transport = transportFor settings.Protocol settings.Framing
    createWith name settings secretStore endpointSecretKey blobStorage transport