module ToolUp.Platform.FileNameSanitiser

open System

// ─── Gap audit pass-2 #8 — file-name validation at upload boundary ──
//
// Every uploaded file's name flows from the HTTP request body into
// the SDK's blob ObjectId (`_processed_entry__{fileName}`) and from
// there into the underlying `IBlobStorage` impl's storage path
// (LocalFileStorage = filesystem path; cloud companions = blob key).
//
// Without sanitisation:
//   * Null bytes (`\0`) on Linux truncate filenames at the OS layer;
//     "report.csv\0../etc" and "report.csv" collide silently.
//   * CRLF / control characters in filenames pollute log lines and
//     can spoof structured-log entries (log injection).
//   * Path separators (`/`, `\\`) and parent-dir tokens (`..`) blow
//     past the per-scope blob prefix; LocalFileStorage rejects them
//     at the OS layer with surface-leaky error messages, and cloud
//     backends may accept them as part of object keys silently.
//   * Excessively long names (>255 bytes) break common filesystem
//     limits with non-obvious errors.
//
// This module makes the rule explicit at the SDK boundary so every
// `IBlobStorage` companion gets the same hardening.
//
// Validation rules (must all hold):
//   * Non-empty after trimming
//   * Length ≤ 255 bytes (UTF-8 byte count, not char count — common
//     filesystem ceiling)
//   * No null bytes (`\0`)
//   * No control characters (< 0x20) except tab is also forbidden
//     (no legitimate use in a filename)
//   * No path separators (`/`, `\\`)
//   * No parent-directory tokens (`..`)
//   * Not exactly `.` or `..`
//   * No leading/trailing whitespace
//
// We do NOT enforce a character allowlist — that would break legit
// non-ASCII filenames (Cyrillic, Han, etc.). The deny list catches
// the dangerous shapes; everything else passes through unchanged.

let private hasControlChars (s: string) : bool =
    s |> Seq.exists (fun c -> int c < 0x20 || c = '')

let private hasPathSeparators (s: string) : bool = s.Contains '/' || s.Contains '\\'

let private hasParentDirToken (s: string) : bool = s.Contains ".." || s = "." || s = ".."

let private byteCount (s: string) : int = Text.Encoding.UTF8.GetByteCount s

/// Validate a filename for use as a blob ObjectId / filesystem path
/// component. Returns `Ok ()` for safe names, `Error reason` with a
/// human-readable explanation for rejected ones.
let validate (fileName: string) : Result<unit, string> =
    if isNull fileName then
        Error "filename is null"
    elif String.IsNullOrEmpty fileName then
        Error "filename is empty"
    elif fileName <> fileName.Trim() then
        Error "filename has leading or trailing whitespace"
    elif String.IsNullOrWhiteSpace fileName then
        Error "filename is whitespace-only"
    elif byteCount fileName > 255 then
        Error(sprintf "filename is %d bytes (max 255)" (byteCount fileName))
    elif hasControlChars fileName then
        Error "filename contains control characters (including null bytes / CRLF)"
    elif hasPathSeparators fileName then
        Error "filename contains path separators ('/' or '\\\\')"
    elif hasParentDirToken fileName then
        Error "filename contains '..' parent-directory token"
    else
        Ok()