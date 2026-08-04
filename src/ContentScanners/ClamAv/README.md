# ToolUp.ContentScanners.ClamAv

`IContentScanner` over a [ClamAV](https://www.clamav.net/) `clamd` daemon, for deployments that
must scan uploaded bytes **inside** the platform — with the verdict in the platform's own audit
trail — rather than bolting scanning on outside and losing the linkage between a refusal and the
subject, scope and file that caused it.

Server-only companion. **Zero third-party dependencies**: clamd's `INSTREAM` protocol is a
command byte-string, length-prefixed chunks and a NUL-terminated reply line, so this package
speaks it directly over BCL `TcpClient`. There is no NuGet client library to track, and nothing
vendor-shaped reaches `ToolUp.Platform.*` (GP 1).

## Composing it

```fsharp
open ToolUp.Platform
open ToolUp.Platform.ContentScanners.ClamAv

let scanner = ClamAvContentScanner.create "clamav.internal"

app
|> KnowledgeBase.Server.withContentScanning scanner ContentScanPolicy.failClosed
```

`ContentScanPolicy` decides only what a **`ScanUnavailable`** verdict means — a daemon that is
down, unreachable or that failed mid-stream:

| Policy | On `ScanUnavailable` |
|---|---|
| `ContentScanPolicy.failClosed` (default) | refuse the upload |
| `ContentScanPolicy.failOpen` | admit the upload |

A `ScanRejected` verdict is always a refusal, and no policy softens it.

Tuning beyond the defaults (port `3310`, 64 KiB frames, a 30s ceiling):

```fsharp
let scanner =
    ClamAvOptions.create "clamav.internal"
    |> ClamAvOptions.withPort 3310
    |> ClamAvOptions.withTimeout (System.TimeSpan.FromSeconds 60.0)
    |> ClamAvContentScanner.createWith
```

## Health

`ClamAvHealthCheck` PINGs the daemon and reports on `/ready`. Its verdict follows the composed
`ContentScanPolicy`, because an unreachable daemon costs a fail-closed deployment every upload
(`Unhealthy` — take the replica out of rotation) and costs a fail-open deployment only a control
(`Degraded`, with the message saying out loud that uploads are being admitted unscanned).

```fsharp
ServerApp.withHealthCheck (ClamAvHealthCheck(scanner, policy) :> IHealthCheck) app
```

## Verdict mapping

Only an explicit clamd `OK` is `ScanClean`; only an explicit `FOUND` is `ScanRejected` (the
signature name is carried through into the audit row and the refusal message). Everything
else — an `ERROR` reply, a truncated reply, a refused connection, a timeout, `StreamMaxLength`
exceeded — is `ScanUnavailable`. The honest statement in those cases is "this payload was not
scanned", and mapping any of them to `ScanClean` here would quietly convert every deployment to
fail-open regardless of the policy it configured.

`Scan` never raises: socket failures are reported as `ScanUnavailable` with the underlying
message as the reason.

## Operating clamd

Any reachable `clamd` works — a container, a sidecar, or a host daemon. TCP must be enabled
(`TCPSocket 3310` in `clamd.conf`), and `StreamMaxLength` must be at least as large as the
deployment's `MaxUploadBytes`, or oversized uploads come back `ScanUnavailable` rather than
being scanned.

ClamAV is GPL-licensed, and this companion neither links nor redistributes it — it talks to a
daemon the operator runs, over a socket. Nothing ClamAV-licensed ships in this package.

## Testing

`ToolUp.Platform.Tests` carries two arms. The **structural** arm is always on and covers the
reply grammar, the frame encoding, the seam's no-op default and the fail-open/closed policy
split — no daemon, no Docker. The **live** arm is gated on `TOOLUP_CLAMAV_HOST` (optionally
`TOOLUP_CLAMAV_PORT`) and scans the real EICAR test string through a real daemon; it reports
`Pending` when the variable is unset, so a fresh checkout and CI are green without provisioning
anything.
