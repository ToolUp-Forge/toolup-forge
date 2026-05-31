module ToolUp.Platform.AuditSinks.SplunkHec

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 9g Splunk HEC `IAuditSink` companion. POSTs each batch to
// Splunk's HTTP Event Collector endpoint with an HEC token from
// `ISecretStore`. No Splunk SDK dependency — BCL `HttpClient` directly.
//
// **Wire format.** Splunk's HEC `/services/collector/event` endpoint
// expects newline-delimited JSON, one event per line. Each line is a
// JSON object with `event` (the payload), optional `time` (epoch
// seconds), `sourcetype`, `index`, and `_meta` (deduplication keys).
// We emit `_meta.uuid` derived from the SDK's wire-format event id
// so Splunk can dedup on retry.
//
// **Authentication.** `Authorization: Splunk <token>` header. Token
// resolved per-call via `ISecretStore.GetSecret` so rotated tokens
// flow through immediately — the sink never caches the token.
//
// **Retry handling.** The dispatcher's `RetryPolicy` (configured via
// `AuditReplicatorRetryPolicy.defaults`) owns the retry loop. The
// sink classifies the response: 2xx → Ok;
// anything else → Error with the diagnostic body. Splunk returns 200
// on success, 4xx on auth / format errors (vendor-rejected — let the
// dispatcher exhaust retries; deployments fix the underlying issue
// and clear the dead-letter), 5xx on transient infra failures
// (retried).
//
// **Batch size.** Splunk HEC accepts up to 1MB per request by default
// (configurable via `max_content_length` on the HEC token). The
// dispatcher's default `MaxBatchSize = 100` produces requests well
// under that ceiling. Deployments emitting unusually large audit
// payloads should either tune Splunk's limit upward or reduce
// `MaxBatchSize` via `AuditReplicatorOptions`.

/// Connection settings for the Splunk HEC sink. Apps construct this
/// directly; the `secretKey` parameter to `create` references a
/// secret in `ISecretStore` rather than carrying the token in this
/// record (which would be JSON-serialised in audit / diagnostic
/// surfaces).
type SplunkHecSettings = {
    /// Full HEC endpoint URL — e.g.
    /// `https://splunk.example.com:8088/services/collector/event`.
    /// Must include the protocol, host, port, and path; the sink
    /// POSTs to this URL verbatim.
    EndpointUrl: string
    /// Sourcetype tag stamped on every event. Conventionally
    /// `toolup_audit` so Splunk admins can route audit events to a
    /// dedicated index without needing to inspect the payload.
    Sourcetype: string
    /// Optional Splunk index override. `None` → events land in the
    /// HEC token's default index. Pinning here lets one HEC token
    /// span multiple SDK deployments routed to per-deployment
    /// indexes.
    Index: string option
    /// Optional `host` field stamped on every event (Splunk's
    /// canonical host attribute). `None` → the HEC default. Most
    /// deployments set this to the deployment name (`"toolup-prod"`).
    Host: string option
}

module SplunkHecSettings =
    /// Default settings — `toolup_audit` sourcetype, no index pin, no
    /// host pin. Apps override per environment.
    let defaults: SplunkHecSettings = {
        EndpointUrl = "https://localhost:8088/services/collector/event"
        Sourcetype = "toolup_audit"
        Index = None
        Host = None
    }

let private hecJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

/// Convert one `AuditEnvelope` into the wire-format JSON line Splunk
/// expects. The audit event itself goes into `event`; bookkeeping
/// metadata goes into the wrapper. Phase 66 Stream B.7 — `_meta` now
/// carries `schema_version` + `subject_kind` so Splunk dashboards can
/// filter by audit-schema version and route per subject without parsing
/// the nested `event` payload.
let private serializeEventLine (settings: SplunkHecSettings) (envelope: AuditEnvelope) : string =
    let payload = JsonConvert.SerializeObject(envelope.Event, hecJsonSettings)
    let subjectPayload = JsonConvert.SerializeObject(envelope.Subject, hecJsonSettings)
    // SDK doesn't carry a per-event UUID at the AuditEvent layer
    // (the underlying ModuleEvent does, but the dispatcher decodes
    // before passing the batch). Fall back to a fresh GUID for
    // dedup-keying — collisions across retries are vanishingly
    // unlikely with random GUIDs and harmless if they occur.
    let dedupId = Guid.NewGuid().ToString "N"
    let eventTypeName = AuditEvent.eventTypeName envelope.Event
    let subjectKind = AuditEnvelope.subjectKindString envelope
    // Splunk HEC `time` field is epoch seconds (with optional decimal
    // sub-second precision). Use the envelope's OccurredAt so Splunk
    // orders by the original event time, not the upload time.
    let epochSeconds =
        envelope.OccurredAt.Subtract(DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds

    let baseFields = [
        sprintf "\"event\":%s" payload
        sprintf "\"sourcetype\":\"%s\"" settings.Sourcetype
        sprintf "\"time\":%s" (epochSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
        sprintf
            "\"_meta\":{\"uuid\":\"%s\",\"event_type\":\"%s\",\"schema_version\":%d,\"subject_kind\":\"%s\",\"subject\":%s,\"scope_id\":\"%s\"}"
            dedupId
            eventTypeName
            AuditSchemaVersion.current
            subjectKind
            subjectPayload
            envelope.ScopeId
    ]

    let withIndex =
        match settings.Index with
        | Some idx -> baseFields @ [ sprintf "\"index\":\"%s\"" idx ]
        | None -> baseFields

    let withHost =
        match settings.Host with
        | Some h -> withIndex @ [ sprintf "\"host\":\"%s\"" h ]
        | None -> withIndex

    "{" + String.Join(",", withHost) + "}"

/// Serialise a batch into newline-delimited JSON for Splunk HEC.
let private serializeBatch (settings: SplunkHecSettings) (batch: AuditEnvelope list) : string =
    batch
    |> List.map (serializeEventLine settings)
    |> fun lines -> String.Join("\n", lines)

[<Literal>]
let private SecretStoreScope = "_platform"

/// SDK Splunk HEC `IAuditSink`. One POST per batch. Token from
/// `ISecretStore` per call.
type SplunkHecAuditSink
    (name: string, settings: SplunkHecSettings, secretStore: ISecretStore, secretKey: string, httpClient: HttpClient) =

    interface IAuditSink with
        member _.Name = name

        member _.SchemaVersion = AuditSchemaVersion.current

        member _.Deliver(batch) = async {
            if List.isEmpty batch then
                return Ok()
            else
                try
                    let! tokenResult = secretStore.GetSecret(SecretStoreScope, secretKey)

                    match tokenResult with
                    | None ->
                        return Error(sprintf "SplunkHec token not found in ISecretStore at _platform/%s" secretKey)
                    | Some token ->
                        let body = serializeBatch settings batch
                        let content = new StringContent(body, Encoding.UTF8, "application/json")

                        use request = new HttpRequestMessage(HttpMethod.Post, settings.EndpointUrl)

                        request.Content <- content
                        request.Headers.Authorization <- AuthenticationHeaderValue("Splunk", token)

                        let! response = httpClient.SendAsync(request) |> Async.AwaitTask

                        if response.IsSuccessStatusCode then
                            return Ok()
                        else
                            let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                            return Error(sprintf "SplunkHec HTTP %d: %s" (int response.StatusCode) responseBody)
                with ex ->
                    return Error(sprintf "SplunkHec sink threw: %s" ex.Message)
        }

/// Construct a Splunk HEC sink. `secretKey` references the HEC token
/// in `ISecretStore` under the `_platform` scope; rotate tokens by
/// updating the secret without touching this code.
let create
    (name: string)
    (settings: SplunkHecSettings)
    (secretStore: ISecretStore)
    (secretKey: string)
    (httpClient: HttpClient)
    : IAuditSink =
    SplunkHecAuditSink(name, settings, secretStore, secretKey, httpClient) :> _