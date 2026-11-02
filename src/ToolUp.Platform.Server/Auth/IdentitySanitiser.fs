module ToolUp.Platform.Config.Auth.IdentitySanitiser

open ToolUp.Platform
open ToolUp.Platform.Auth

open System

// ─── Phase 6l.H — identity / scope-id sanitisation ──────────────────
//
// User identifiers flow into two sensitive places:
//   1. `StorageScope.Container` — used as a path segment in
//      `LocalFileStorage` (`{baseDir}/{container}/{blob}`) and as a
//      blob/container name in cloud backends (Azure / S3 / GCS).
//   2. `FileSecretStore.writePathFor` — used to construct
//      `secrets-{scopeId}.json`.
// A request carrying `X-User-Id: ../../etc/passwd` would let those
// path concatenations resolve outside the intended root. Cloud
// backends mostly tolerate odd characters but the local backend is
// vulnerable. OIDC providers normally validate `sub` but a self-issued
// or misconfigured IdP can deliver a malicious claim.
//
// Phase 6l.H defines a single sanitiser applied at every identity
// boundary. The check is conservative: identifiers must look like
// the ones that legitimate auth systems produce — alphanumerics,
// hyphen, underscore, period, length 1..256. Anything else is
// rejected. Defence-in-depth — there is no legitimate use case for
// `..` / `/` / `\` / NUL / control characters in a user id.

/// The maximum length of an accepted identity string. Real OIDC
/// `sub` claims rarely exceed 64 chars (UUIDs, short DB ids); 256
/// gives generous headroom while preventing DoS via 1MB JWT subjects.
[<Literal>]
let MaxLength = 256

/// Windows reserved device names. Even on Unix deployments, these
/// strings are blocked because file paths can flow through Windows
/// downstream (backup tools, dev environments mounted via Samba).
let private windowsReserved = [
    "CON"
    "PRN"
    "AUX"
    "NUL"
    "COM1"
    "COM2"
    "COM3"
    "COM4"
    "COM5"
    "COM6"
    "COM7"
    "COM8"
    "COM9"
    "LPT1"
    "LPT2"
    "LPT3"
    "LPT4"
    "LPT5"
    "LPT6"
    "LPT7"
    "LPT8"
    "LPT9"
]

/// Phase 6l.H — sanitise a user-supplied identity / scope-id. Returns
/// `Ok value` for valid identifiers, `Error reason` otherwise. The
/// reason string is suitable for audit logging but MUST NOT be
/// echoed back to the caller: it can carry the rejected value.
let sanitiseScopeId (value: string) : Result<string, string> =
    if isNull value then
        Error "null"
    elif value = "" then
        Error "empty"
    elif value.Length > MaxLength then
        Error(sprintf "length %d exceeds maximum %d" value.Length MaxLength)
    elif value.StartsWith "." then
        Error "leading period (Unix dotfile / Windows reserved)"
    else
        let invalid =
            value
            |> Seq.tryFind (fun ch ->
                // Allowed: alphanumerics, hyphen, underscore, period.
                // Period is needed for email-shaped subjects (some OIDC
                // providers); the leading-period check above handles
                // the dotfile risk.
                not (Char.IsLetterOrDigit ch || ch = '-' || ch = '_' || ch = '.'))

        match invalid with
        | Some ch ->
            // Don't echo the offending char in the error reason — the
            // value can flow into logs and we don't need an attacker-
            // controlled string in there. Categorise instead.
            let category =
                if ch = '/' || ch = '\\' then "path separator"
                elif ch = '\u0000' then "NUL byte"
                elif Char.IsControl ch then "control character"
                elif ch = ' ' then "whitespace"
                else "disallowed character"

            Error(sprintf "contains %s" category)
        | None ->
            // Final check: Windows reserved device names. Compare
            // case-insensitively; either bare name or with extension.
            let bareName =
                let dotIdx = value.IndexOf '.'
                if dotIdx >= 0 then value.Substring(0, dotIdx) else value

            if
                List.exists (fun (r: string) -> r.Equals(bareName, StringComparison.OrdinalIgnoreCase)) windowsReserved
            then
                Error "Windows reserved device name"
            else
                Ok value