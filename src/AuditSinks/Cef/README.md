# CEF audit sink

Phase 9g.A `IAuditSink` companion. Renders every audit envelope as a **Common Event Format** line and ships it to a syslog collector, so audit traffic reaches the SIEMs that consume CEF rather than JSON — ArcSight, IBM QRadar, LogRhythm, McAfee ESM.

No vendor SDK and no third-party syslog library: BCL sockets (`UdpClient` / `TcpClient` / `SslStream`) and `System.Text.Json` only (GP 1).

**Distributed-ready.** The sink holds no state between deliveries — endpoint, device identity, and socket are all resolved inside `Deliver`. Two processes running it behave identically (portability rule 4). It is not dev-only.

## How to enable

1. Add a `<ProjectReference>` to `ToolUp.AuditSinks.Cef.fsproj` from the consuming server project.

2. Store the collector endpoint in `ISecretStore` under the `_platform` scope, in `host:port` form:

   ```fsharp
   do! secretStore.SetSecret("_platform", "cef_syslog_endpoint", "siem.example.com:514")
   ```

   Or via `EnvironmentSecretStore` — `TOOLUP_SECRET_PLATFORM_CEF_SYSLOG_ENDPOINT=siem.example.com:514`.
   IPv6 literals use bracket form: `[2001:db8::1]:6514`.

3. Write the device identity the customer's SOC expects to `_platform/audit/cef.json` (optional — see below).

4. Construct the sink and register it:

   ```fsharp
   open ToolUp.Platform.AuditSinks.Cef
   open ToolUp.Platform.AuditSinks.CefSyslog

   let settings = {
       CefSinkSettings.defaults with
           Protocol = CefTlsSyslog
           Framing = { CefSyslogFraming.defaults with Hostname = "toolup-prod-01" }
   }

   let sink = create "cef-soc-prod" settings secretStore "cef_syslog_endpoint" blobStorage

   ServerApp.empty
   |> ServerApp.withAuditSink sink
   |> ServerApp.run
   ```

## Wire format

```
<134>May 28 12:00:00 toolup-prod-01 ToolUpAudit: CEF:0|Contoso|ContosoAnalytics|2.4.0|UserLoggedIn|UserLoggedIn|2|rt=1780315200000 externalId=9f2c… cat=UserLoggedIn suser=u123 cs1Label=ScopeId cs1=team-42 cs2Label=SubjectKind cs2=team cs4Label=AuditSchemaVersion cs4=2 cs3Label=TeamId cs3=42 msg={"Case":"UserLoggedIn",…}
```

The RFC 3164 syslog header (`<PRI>MMM dd HH:mm:ss HOSTNAME TAG:`) is what the collector's syslog listener parses before handing the remainder to its CEF decoder. `PRI` is `facility * 8 + syslogSeverity`, where the syslog severity is derived from the CEF severity — note the two scales run in **opposite** directions (CEF 10 = most severe, syslog 0 = most severe).

The seven pipe-delimited header fields are positional. `SignatureID` and `Name` both carry the audit event-type name (`UserLoggedIn`, `PermissionChanged`, …) — the SDK's event type *is* the signature.

| Extension | Meaning |
|---|---|
| `rt` | Event receipt time, epoch milliseconds, from `AuditEnvelope.OccurredAt` (not upload time) |
| `externalId` | Deterministic dedup key — see below |
| `cat` | Audit event type name |
| `suser` | Subject actor: user id, session id, or the claim's attributed handle |
| `cs1` / `cs1Label` | `ScopeId` |
| `cs2` / `cs2Label` | `SubjectKind` (`anonymous` / `user` / `team` / `claim`) |
| `cs3` / `cs3Label` | `TeamId`, present only for team subjects |
| `cs4` / `cs4Label` | `AuditSchemaVersion` |
| `msg` | The `AuditEvent` payload as JSON, serialised via the SDK's canonical `FableConverters` set |
| `cefTruncated` | `true` when the 1023-byte cap forced a drop — absent otherwise |

## Escaping

CEF escapes the header and the extension differently, and getting this wrong is the usual cause of an unparseable line:

- **Header fields** — `\` → `\\`, `|` → `\|`. A raw pipe would shift every later field one position. CR/LF are replaced with a space.
- **Extension values** — `\` → `\\`, `=` → `\=`, CR → `\r`, LF → `\n`. A raw `=` would read as the start of the next key. The pipe is *not* escaped here; it is only structural in the header.
- **Extension keys** — alphanumeric by spec. Anything else is dropped rather than escaped, because a receiver splits the extension on unescaped `=` and whitespace, so a malformed key is unrecoverable rather than merely ugly.

## The 1023-byte cap

CEF receivers truncate silently past 1023 bytes, which corrupts the trailing key/value pair — and because the cut can land inside a `\=`, the remainder of the line can reparse as a different field. This sink truncates explicitly instead:

- Extension pairs are packed against a byte budget, most-valuable first (`rt`, `externalId`, `cat`, `suser`, the custom strings), with the unbounded `msg` payload last so it is the field that shortens.
- A key is either emitted whole with a value or not emitted at all — never a bare key.
- An oversized value is trimmed on its **raw** text and escaped afterwards, so a cut can never split a `\\` or `\=` pair. Trimming also never leaves a lone UTF-16 high surrogate.
- When anything was dropped or shortened, the line ends with `cefTruncated=true`, and the budget reserves room for that marker so appending it cannot push the line back over the cap.

The result parses cleanly at every truncation point, and the loss is visible downstream rather than inferred.

## Severity

The SDK's `AuditEvent` is a DU of ~130 cases and carries no severity field, so this companion derives one. `CefSeverityBand` classifies by event kind and projects onto CEF's 0–10 scale:

| Band | Score | Examples |
|---|---|---|
| `CefCritical` | 10 | `DataStoreReset`, `EncryptionKeyDestroyed`, `PlatformAdminAssigned`, `TeamDeleted`, `TenantDeprovisioned` |
| `CefHigh` | 8 | `AuthorizationDenied`, `PermissionChanged`, `EgressBlocked`, `SurfaceDenied`, `AuditSinkDeadLettered` |
| `CefMedium` | 6 | `FileDeleted`, `MemberRemoved`, `ShareTokenRevoked`, `NotificationDeliveryFailed` |
| `CefLow` | 4 | everything unclassified — ordinary state changes |
| `CefInformational` | 2 | `UserLoggedIn`, `AnalysisRun`, `NotificationSent`, `ShareTokenUsed` |

Classification is a name lookup rather than an exhaustive `match`: the audit DU is append-only and grows most releases, and an exhaustive match would turn every new audit case into a compile break inside a sink. New cases land in `CefLow` until classified.

A deployment whose SOC has its own severity policy sets `CefSinkSettings.SeverityOverride` to an `AuditEvent -> int` of its own; results are clamped into 0–10.

## Device identity — `_platform/audit/cef.json`

CEF header fields 1–3 (Vendor / Product / DeviceVersion) are the strings a customer's correlation rules match on. They are handed over by the customer's SOC rather than chosen by the deployment, so they live in a blob an operator can edit without a code change:

```json
{
  "vendor": "Contoso",
  "product": "ContosoAnalytics",
  "deviceVersion": "2.4.0"
}
```

Read from `IBlobStorage` on every `Deliver` (same reasoning as the per-`Deliver` secret read: an edit takes effect on the next batch). Property names match case-insensitively. Missing keys keep the corresponding `CefSinkSettings.Identity` value.

**An absent or malformed blob is not an error** — the sink falls back to `CefSinkSettings.Identity` and delivers. Refusing to ship audit events because an optional cosmetic override is missing would trade a labelling problem for an audit gap. A missing or malformed **endpoint secret** *is* an error: there is nowhere to send.

## Transports

| Protocol | Framing | Notes |
|---|---|---|
| `CefUdpSyslog` (default) | one datagram per line (RFC 5426) | Every SIEM accepts it. Fire-and-forget — `Ok` means the datagram left this host, not that the SIEM has it. The 1023-byte cap keeps a framed line inside one datagram. |
| `CefTcpSyslog` | LF-terminated lines (RFC 6587 non-transparent) | Confirms the collector's socket accepted the bytes. |
| `CefTlsSyslog` | TCP framing inside a TLS session | Full server-certificate validation. Registered port 6514. |

All three connect, write, and close inside a single `Send`. That costs a handshake per batch on the stream transports, which is the right trade for an audit path delivering tens of batches a minute — a pooled connection would trade it for a silent failure mode where a half-open socket swallows a batch.

There is deliberately **no certificate-validation bypass switch**. A deployment reaching for TLS here is doing so to keep audit payloads off the wire in clear, and a bypass flag is how that gets silently undone.

## Batch idempotency

`externalId` is a SHA-256 over the envelope's schema version, UTC timestamp, scope id, subject kind, event type, and serialised payload — **deterministic**, so a redelivered envelope presents the same id and the SIEM's dedup window collapses it.

This differs from `SplunkHec`, which mints a fresh GUID per emission. That is right for Splunk, whose `_meta.uuid` mostly guards against index-side duplication; it is wrong here, because syslog cannot report partial acceptance, so the dispatcher's whole-batch retry and the catch-up sweep both genuinely re-present events that were already delivered.

## Failure handling

| Condition | Sink result | Dispatcher behaviour |
|---|---|---|
| Endpoint secret missing | `Error` | Retried per `RetryPolicy`, then dead-lettered — operators provision the secret |
| Endpoint secret not `host:port` | `Error` | Same |
| Config blob missing / malformed | `Ok` (fallback identity used) | Delivery proceeds |
| Socket / TLS / DNS failure | `Error` | Retried per `RetryPolicy` |
| Empty batch | `Ok` | No write |

`Deliver` never throws — every failure surfaces as `Result.Error` so the dispatcher's retry loop owns it.

## Strip-imports

Removing the `<ProjectReference>` and the `ServerApp.withAuditSink` line returns the deployment to no external replication. `ComposeAudit.buildAuditReplicatorSubsystem` returns `None` on an empty sink list, so the replicator's background service, bounded channel, and event-store decorator are never constructed — zero cost when unused (GP 13).

## Single-instance limitation

Same as every Phase 9g companion — the replicator is in-process. Multi-instance deployments running the same sink double-deliver until the distributed lock lands. The deterministic `externalId` mitigates this at the SIEM's dedup layer, which is a mitigation rather than a fix: run the audit-emitting tier as `replicas: 1` where the compliance posture demands exactly-once.
