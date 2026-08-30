// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBase.ServerBulkImport

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Threading
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open SharedTypes

// NOTE: `ToolUp.Platform.ConfigValidation` is referenced QUALIFIED below
// rather than opened. Its `ValidationResult` carries `Ok` / `Error`
// cases, which shadow `Result`'s — and this file is built on
// `Result<Uri, string>` (`classifyUrl`) and `Result<UrlFetchResponse,
// string>` (`IUrlContentFetcher.Fetch`). Opening it turns every one of
// those into a type error. Same posture as `AICompose.fs`.

// ─── Phase 511 — bulk import source expansion ─────────────────────
//
// Everything in this file turns *a source* into *items*. Nothing here
// persists, chunks, embeds, dedups or checks a quota — the caller hands
// each surviving item to the ordinary single-item upload path, which
// owns all of that. Keeping expansion and admission apart is what lets
// the batch surface exist without a second copy of the upload boundary.
//
// Two hostile inputs are handled here, and only here:
//   * an **archive**, whose entry names and declared sizes are both
//     attacker-controlled;
//   * a **URL**, which by default must not be fetched at all.

/// One expanded item, ready for the single-item upload path.
type ExpandedItem = {
    /// Human-legible origin for the report line.
    Source: string
    /// Name to upload under. Already leaf-only; the upload boundary
    /// sanitises it again regardless (defence in depth, not delegation).
    FileName: string
    Content: byte[]
}

/// Expansion produced either an item or a classified refusal. Both carry
/// a `Source` so a refusal still names what was refused.
type ExpansionOutcome =
    | Expanded of ExpandedItem
    | Rejected of source: string * fileName: string * reason: string

// ─── Archive entry names ──────────────────────────────────────────

/// `true` when an archive entry name would escape the directory it is
/// expanded into — the zip-slip family.
///
/// Checked on the RAW entry name, before anything is flattened. That
/// ordering is the point: `uploadDocument` already reduces any name to
/// its leaf under a server-controlled `knowledge/{docId}/` key, so a
/// traversal name could not actually write outside the container even if
/// it reached there. What silently flattening would cost is the SIGNAL —
/// an archive carrying `../../../etc/passwd` would import as `passwd`
/// and read in the report as an ordinary success. A hostile archive
/// should be visibly refused, not quietly normalised, so an operator
/// looking at a migration report can tell the two apart.
///
/// Rooted names, drive/UNC qualifiers and `..` segments are all refused;
/// an ordinary `docs/report.pdf` is not (its directory component is
/// flattened by the caller, which is the neutralisation the phase asks
/// for).
let isTraversalEntryName (entryName: string) : bool =
    if String.IsNullOrWhiteSpace entryName then
        true
    else
        let normalised = entryName.Replace('\\', '/')

        normalised.StartsWith "/"
        // `C:/x` and `\\server\share` alike — a colon can never appear in
        // a legitimate relative zip entry path.
        || normalised.Contains ":"
        || normalised.Split('/') |> Array.exists (fun segment -> segment = "..")

/// The leaf name an entry imports under. Separator-agnostic (a zip
/// written on Windows may carry `\`), so the directory component is gone
/// whichever the archive used.
let entryLeafName (entryName: string) : string =
    entryName.Replace('\\', '/').Split('/') |> Array.last

// ─── Archive expansion ────────────────────────────────────────────

/// Read at most `limit + 1` bytes from `stream`. Returns `None` when the
/// stream produced more than `limit`.
///
/// **This is the guard that actually holds.** `ZipArchiveEntry.Length` is
/// a number the archive declares about itself; a crafted archive can
/// declare 1 KB and stream gigabytes. So every declared-size check below
/// is a cheap pre-filter and this bounded read is the enforcement — the
/// stream is never copied wholesale into memory and is abandoned the
/// moment it exceeds what the policy permits.
let private readBounded (stream: Stream) (limit: int64) : byte[] option =
    use buffer = new MemoryStream()
    let chunk = Array.zeroCreate<byte> 81920
    let mutable total = 0L
    let mutable overrun = false
    let mutable finished = false

    while not finished do
        let read = stream.Read(chunk, 0, chunk.Length)

        if read <= 0 then
            finished <- true
        else
            total <- total + int64 read

            if total > limit then
                overrun <- true
                finished <- true
            else
                buffer.Write(chunk, 0, read)

    if overrun then None else Some(buffer.ToArray())

/// Expand a zip archive into items under `policy`.
///
/// Refusals come in two shapes, and the distinction is deliberate:
///   * a **whole-archive** refusal (unreadable, too many entries, total
///     size or compression ratio over the cap) returns a single
///     `Rejected` — the archive is a bomb and there is nothing in it
///     worth admitting;
///   * a **per-entry** refusal (hostile name, oversized entry, an entry
///     that streamed more than it declared) is one `Rejected` line
///     amongst the archive's other, admitted entries — one bad file must
///     not fail the batch (511.A).
///
/// Directory entries are skipped silently: they are structure, not
/// content, and reporting a refusal for every folder in a corpus would
/// bury the real findings.
let expandArchive (policy: ArchiveImportPolicy) (archiveName: string) (archiveBytes: byte[]) : ExpansionOutcome list =
    let label (entryName: string) = sprintf "%s → %s" archiveName entryName

    try
        use stream = new MemoryStream(archiveBytes, writable = false)
        use archive = new ZipArchive(stream, ZipArchiveMode.Read)

        // Directory entries carry an empty `Name` (the path ends in a
        // separator); everything else is content.
        let entries =
            archive.Entries
            |> Seq.filter (fun e -> not (String.IsNullOrEmpty e.Name))
            |> List.ofSeq

        let declaredTotal = entries |> List.sumBy _.Length

        let archiveRefusal =
            match policy.MaxEntries with
            | Some maxEntries when List.length entries > maxEntries ->
                Some(
                    sprintf
                        "archive declares %d entries, exceeding the %d-entry bulk-import limit"
                        (List.length entries)
                        maxEntries
                )
            | _ ->
                match policy.MaxTotalUncompressedBytes with
                | Some maxTotal when declaredTotal > maxTotal ->
                    Some(
                        sprintf
                            "archive declares %d uncompressed bytes, exceeding the %d-byte bulk-import limit"
                            declaredTotal
                            maxTotal
                    )
                | _ ->
                    match policy.MaxCompressionRatio with
                    | Some maxRatio when archiveBytes.LongLength > 0L ->
                        let ratio = float declaredTotal / float archiveBytes.LongLength

                        if ratio > maxRatio then
                            Some(
                                sprintf
                                    "archive expands %.1f:1 (%d bytes into %d), exceeding the %.1f:1 decompression-ratio limit — refused as a decompression bomb"
                                    ratio
                                    archiveBytes.LongLength
                                    declaredTotal
                                    maxRatio
                            )
                        else
                            None
                    | _ -> None

        match archiveRefusal with
        | Some reason -> [ Rejected(archiveName, archiveName, reason) ]
        | None ->
            // Running total of ACTUALLY decompressed bytes. The declared
            // total above can lie; this cannot.
            let mutable actualTotal = 0L
            let mutable exhausted = false

            [
                for entry in entries do
                    if exhausted then
                        yield
                            Rejected(
                                label entry.FullName,
                                entryLeafName entry.FullName,
                                "archive exceeded its total-uncompressed-bytes budget before this entry was reached"
                            )
                    elif isTraversalEntryName entry.FullName then
                        yield
                            Rejected(
                                label entry.FullName,
                                entryLeafName entry.FullName,
                                "archive entry name escapes the archive root (path traversal) — refused rather than silently renamed"
                            )
                    else
                        let entryLimit =
                            [ policy.MaxEntryBytes; policy.MaxTotalUncompressedBytes ]
                            |> List.choose id
                            |> function
                                | [] -> Int64.MaxValue
                                | limits -> List.min limits

                        match policy.MaxEntryBytes with
                        | Some maxEntry when entry.Length > maxEntry ->
                            yield
                                Rejected(
                                    label entry.FullName,
                                    entryLeafName entry.FullName,
                                    sprintf
                                        "archive entry declares %d uncompressed bytes, exceeding the %d-byte per-entry limit"
                                        entry.Length
                                        maxEntry
                                )
                        | _ ->
                            use entryStream = entry.Open()

                            match readBounded entryStream entryLimit with
                            | None ->
                                yield
                                    Rejected(
                                        label entry.FullName,
                                        entryLeafName entry.FullName,
                                        sprintf
                                            "archive entry streamed more than its %d-byte limit (the declared size understated the real content) — refused as a decompression bomb"
                                            entryLimit
                                    )
                            | Some content ->
                                actualTotal <- actualTotal + content.LongLength

                                match policy.MaxTotalUncompressedBytes with
                                | Some maxTotal when actualTotal > maxTotal ->
                                    exhausted <- true

                                    yield
                                        Rejected(
                                            label entry.FullName,
                                            entryLeafName entry.FullName,
                                            sprintf
                                                "archive exceeded its %d-byte total-uncompressed-bytes limit while expanding (the declared sizes understated the real content) — refused as a decompression bomb"
                                                maxTotal
                                        )
                                | _ ->
                                    yield
                                        Expanded {
                                            Source = label entry.FullName
                                            FileName = entryLeafName entry.FullName
                                            Content = content
                                        }
            ]
    with ex -> [
        Rejected(archiveName, archiveName, sprintf "archive could not be read as a zip: %s" ex.Message)
    ]

// ─── URL ingestion gate ───────────────────────────────────────────

/// Whether a URL may be fetched under `policy`. **Pure, total, and the
/// only place the decision is made** — the fetcher below calls it for
/// the submitted URL AND for every redirect target, so there is one
/// gate rather than one per code path.
///
/// Refusals, in the order they are evaluated:
///   * **ingestion inert** — no host allowlisted, so nothing is
///     fetchable. Checked first so an uncomposed deployment does not
///     even parse the URL (GP 13);
///   * **unparseable / relative** — a URL that is not absolute;
///   * **scheme** — only `http` and `https`. `file:`, `ftp:`, `gopher:`
///     and friends are classic SSRF pivots and none of them is a
///     document source;
///   * **embedded credentials** — `https://allowed.example@evil.test/`
///     is a parser-confusion vector; a URL carrying userinfo is refused
///     outright rather than trusted to have been parsed the way the
///     reader expects;
///   * **literal IP** — refused unconditionally, whatever the address.
///     This is deliberately blunter than a private-range blocklist:
///     enumerating link-local (169.254.0.0/16, the cloud metadata
///     endpoint), loopback, RFC1918, unique-local and every IPv6
///     equivalent is a list that is wrong the moment it is written, and
///     an allowlist is by hostname anyway, so a literal address has no
///     legitimate reason to appear;
///   * **host not allowlisted** — exact, lower-cased equality (see
///     `UrlIngestionPolicy` on why not a suffix test).
let classifyUrl (policy: UrlIngestionPolicy) (url: string) : Result<Uri, string> =
    if UrlIngestionPolicy.isInert policy then
        Error
            "URL ingestion is not enabled for this deployment — no host allowlist is composed, so no URL is fetched. Enable it deliberately with KnowledgeBase.Server.withUrlIngestion."
    else

        match Uri.TryCreate(url, UriKind.Absolute) with
        | false, _ -> Error(sprintf "'%s' is not an absolute URL" url)
        | true, uri ->
            if uri.Scheme <> Uri.UriSchemeHttp && uri.Scheme <> Uri.UriSchemeHttps then
                Error(sprintf "URL scheme '%s' is not fetchable — only http and https are" uri.Scheme)
            elif not (String.IsNullOrEmpty uri.UserInfo) then
                Error "URL carries embedded credentials, which are refused at the ingestion gate"
            elif fst (IPAddress.TryParse uri.Host) then
                Error(
                    sprintf
                        "URL host '%s' is a literal IP address; URL ingestion allowlists hostnames only, and literal addresses are refused"
                        uri.Host
                )
            elif not (UrlIngestionPolicy.allowsHost uri.Host policy) then
                Error(sprintf "URL host '%s' is not in this deployment's URL-ingestion allowlist" uri.Host)
            else
                Ok uri

/// The file name a fetched URL imports under: its last path segment, or
/// the host when the path carries none. Sanitised again at the upload
/// boundary regardless.
let fileNameOfUrl (uri: Uri) : string =
    let leaf = uri.AbsolutePath.TrimEnd('/').Split('/') |> Array.last

    if String.IsNullOrWhiteSpace leaf then
        uri.Host
    else
        Uri.UnescapeDataString leaf

/// Fetches the bytes behind an already-gated URL. A seam rather than a
/// direct `HttpClient` call so the gate and the redirect walk are
/// testable with no network, and so a deployment behind an egress proxy
/// can substitute its own transport.
///
/// Identity by value, async at the boundary, stateless between calls
/// (GP 12 rules 1/2/4) — the implementation holds nothing about a
/// previous fetch.
type IUrlContentFetcher =
    /// Fetch `uri`, returning the response body and the `Location`
    /// header when the response was a redirect. Returning the redirect
    /// target rather than following it is what keeps the allowlist
    /// decision with `classifyUrl` instead of inside a transport.
    abstract Fetch: uri: Uri * maxBytes: int64 * timeoutSeconds: int -> Async<Result<UrlFetchResponse, string>>

/// One hop's outcome. `Redirect` carries the raw target so the caller
/// re-gates it; `Body` is a terminal 2xx response.
and UrlFetchResponse =
    | Body of content: byte[]
    | Redirect of location: string

/// BCL `HttpClient` fetcher — no vendor dependency (GP 1). Redirects are
/// **not** followed by the transport (`AllowAutoRedirect = false`): the
/// handler must re-gate each hop, and an auto-following client would
/// have already left the allowlist by the time anyone could ask.
type HttpUrlContentFetcher(client: HttpClient) =
    /// The shared-client default. A single `HttpClient` per process is
    /// the documented BCL pattern (a per-request client exhausts
    /// sockets); the per-request timeout rides a `CancellationToken`
    /// instead of the client's own, so one policy value cannot pin the
    /// shared instance.
    static let shared =
        lazy
            (let handler = new HttpClientHandler(AllowAutoRedirect = false)
             new HttpClient(handler, Timeout = Timeout.InfiniteTimeSpan))

    new() = HttpUrlContentFetcher(shared.Value)

    interface IUrlContentFetcher with
        member _.Fetch(uri, maxBytes, timeoutSeconds) = async {
            try
                use cts = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
                use request = new HttpRequestMessage(HttpMethod.Get, uri)

                let! response =
                    client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    |> Async.AwaitTask

                use response = response

                let status = int response.StatusCode

                if status >= 300 && status < 400 then
                    match response.Headers.Location with
                    | null -> return Error(sprintf "URL returned %d with no Location header" status)
                    | location ->
                        // Resolve a relative Location against the current
                        // hop before handing it back, so the gate sees an
                        // absolute URL rather than refusing every
                        // relative redirect as unparseable.
                        let absolute =
                            if location.IsAbsoluteUri then
                                location
                            else
                                Uri(uri, location)

                        return Ok(Redirect(absolute.ToString()))
                elif not response.IsSuccessStatusCode then
                    return Error(sprintf "URL returned HTTP %d" status)
                else
                    let! stream = response.Content.ReadAsStreamAsync(cts.Token) |> Async.AwaitTask
                    use stream = stream

                    match readBounded stream maxBytes with
                    | None -> return Error(sprintf "URL response exceeded the %d-byte fetch limit" maxBytes)
                    | Some content -> return Ok(Body content)
            with
            | :? OperationCanceledException -> return Error(sprintf "URL fetch timed out after %ds" timeoutSeconds)
            | ex -> return Error(sprintf "URL fetch failed: %s" ex.Message)
        }

/// Fetch one URL under `policy`, re-gating every redirect hop.
///
/// **A redirect is re-gated, never trusted.** An allowlisted host that
/// 302s to `169.254.169.254` is the canonical SSRF bypass, so each hop's
/// target runs through `classifyUrl` again — same scheme rule, same
/// literal-IP refusal, same allowlist. A redirect out of the allowlist
/// is a refusal, not a follow.
let fetchUrl
    (fetcher: IUrlContentFetcher)
    (policy: UrlIngestionPolicy)
    (url: string)
    : Async<Result<ExpandedItem, string>> =
    let rec hop (current: string) (remaining: int) = async {
        match classifyUrl policy current with
        | Error reason -> return Error reason
        | Ok uri ->
            match! fetcher.Fetch(uri, policy.MaxResponseBytes, policy.TimeoutSeconds) with
            | Error reason -> return Error reason
            | Ok(Body content) ->
                return
                    Ok {
                        Source = url
                        FileName = fileNameOfUrl uri
                        Content = content
                    }
            | Ok(Redirect location) ->
                if remaining <= 0 then
                    return Error(sprintf "URL exceeded the %d-redirect limit" policy.MaxRedirects)
                else
                    // The recursion re-enters `classifyUrl` at the top —
                    // that is the whole safety property of this loop.
                    return! hop location (remaining - 1)
    }

    hop url policy.MaxRedirects

// ─── Compose-time registration ────────────────────────────────────

/// Append a service registration onto the shared `ComposeExtensions`
/// seam — the same threading every other KB compose hook uses.
let private withServiceConfig (register: IServiceCollection -> IServiceCollection) (app: ServerApp) : ServerApp = {
    app with
        Extensions = {
            app.Extensions with
                ServiceConfig =
                    match app.Extensions.ServiceConfig with
                    | None -> Some register
                    | Some baseFn -> Some(fun s -> register (baseFn s))
        }
}

// ─── Phase 725.D — bulk-import preflight ──────────────────────────
//
// Every other KB policy ships an `IConfigValidator` that names a risky
// posture at compose time (upload: uncapped in Team mode; quota:
// unlimited in Team mode; content scan: fail-open). The two levers this
// file registers warned about nothing, which meant the ONE surface whose
// inputs are wholly attacker-controlled — an archive's declared sizes,
// and an egress allowlist — was also the one surface whose posture never
// appeared in a preflight report.
//
// Both validators are `Warning`, never `Error`, and never abort startup
// (GP 11): an unguarded expander and a broad allowlist are legitimate
// deliberate choices, and the finding's job is to make them *stated*
// rather than inherited. Both are registered only by their own `with*`
// call, so a deployment that composes neither pays nothing (GP 13) — and
// an uncomposed deployment is already the guarded default
// (`ArchiveImportPolicy.defaults`) and the inert one
// (`UrlIngestionPolicy.disabled`), so it has nothing to warn about.

type private ArchiveImportPolicyValidator(policy: ArchiveImportPolicy) =
    interface ConfigValidation.IConfigValidator with
        member _.Name = "knowledge-base:archive-import-policy"
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member _.Validate() = async {
            match ArchiveImportPolicy.unguardedLevers policy with
            | [] -> return ConfigValidation.ValidationResult.Ok
            | levers ->
                return
                    ConfigValidation.ValidationResult.Warning(
                        sprintf
                            "Knowledge Base archive import leaves %s unbounded (%s). An archive's declared entry sizes are attacker-controlled, so an unguarded expander admits a decompression bomb: the streaming read still refuses an entry that overruns its limit, but with no limit composed there is nothing for it to enforce. Restore the shipped caps with KnowledgeBase.Server.withArchiveImportPolicy ArchiveImportPolicy.defaults, or raise individual levers from defaults rather than clearing them."
                            (if List.length levers = 1 then
                                 "one guard"
                             else
                                 sprintf "%d guards" (List.length levers))
                            (String.concat ", " levers)
                    )
        }

type private UrlIngestionValidator(serverConfig: ServerConfig, policy: UrlIngestionPolicy) =
    interface ConfigValidation.IConfigValidator with
        member _.Name = "knowledge-base:url-ingestion"
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member _.Validate() = async {
            let teamScoped = DeploymentConfig.hasTeamScope serverConfig
            let hostCount = Set.count policy.AllowedHosts

            // An inert policy is the default posture and is never worth a
            // line in a preflight report.
            if UrlIngestionPolicy.isInert policy then
                return ConfigValidation.ValidationResult.Ok
            else
                let findings = [
                    if UrlIngestionPolicy.isBroadAllowlist policy then
                        sprintf
                            "the allowlist names %d hosts (at or above the %d-host reviewability threshold), so a stale or mistaken entry is unlikely to be noticed on review"
                            hostCount
                            UrlIngestionPolicy.BroadAllowlistThreshold
                    if teamScoped then
                        "the deployment is Team / MultiTeam scoped, so every tenant can make the SERVER fetch from these hosts — the egress surface is shared, not per-tenant"
                ]

                match findings with
                | [] -> return ConfigValidation.ValidationResult.Ok
                | _ ->
                    return
                        ConfigValidation.ValidationResult.Warning(
                            sprintf
                                "Knowledge Base URL ingestion is enabled for %d host(s) and %s. Redirects are re-gated against this same allowlist and literal IP addresses are always refused, so this is a posture to confirm rather than a defect: narrow the list with KnowledgeBase.Server.withUrlIngestion (UrlIngestionPolicy.allowingHosts [ ... ]), or leave it uncomposed to keep ingestion inert."
                                hostCount
                                (String.concat "; and " findings)
                        )
        }

/// Phase 511 — override the archive-expansion resource guards. A
/// deployment that never calls this gets `ArchiveImportPolicy.defaults`,
/// which already carries real caps: bulk import is a new surface, and an
/// uncapped archive expander is not a defensible default (see the type's
/// remarks). The pre-511 single-file upload path is untouched either way
/// (GP 11).
///
/// Phase 725.D — also registers a preflight validator that warns when
/// this policy leaves any expansion guard unbounded (the posture
/// `ArchiveImportPolicy.unbounded` states in full).
let withArchiveImportPolicy (policy: ArchiveImportPolicy) (app: ServerApp) : ServerApp =
    let withSingleton =
        app |> withServiceConfig (fun s -> s.AddSingleton<ArchiveImportPolicy>(policy))

    ServerApp.withConfigValidator
        (ArchiveImportPolicyValidator(policy) :> ConfigValidation.IConfigValidator)
        withSingleton

/// Phase 511 — enable fetch-by-URL ingestion for an explicit host
/// allowlist, and register the transport that performs it.
///
/// **Not calling this leaves URL ingestion inert**, and that is the
/// default posture the phase requires: with no `UrlIngestionPolicy`
/// registered the handler resolves `UrlIngestionPolicy.disabled`, whose
/// allowlist is empty, and `classifyUrl` refuses before it even parses
/// the URL. There is no wildcard, no "allow all" constant, and no
/// enable-flag separate from the allowlist — the only way to reach the
/// network is to name a host.
///
/// Composing an EMPTY allowlist is therefore also inert, deliberately:
/// a deployment that wires this from configuration and gets an empty
/// list back fails closed rather than open.
///
/// Phase 725.D — also registers a preflight validator that names the
/// egress posture: a broad allowlist, or any allowlist at all in a
/// Team / MultiTeam deployment where the fetch surface is shared across
/// tenants. An empty (inert) allowlist warns about nothing.
let withUrlIngestion (policy: UrlIngestionPolicy) (app: ServerApp) : ServerApp =
    let withSingletons =
        app
        |> withServiceConfig (fun s ->
            s
                .AddSingleton<UrlIngestionPolicy>(policy)
                .AddSingleton<IUrlContentFetcher>(HttpUrlContentFetcher() :> IUrlContentFetcher))

    ServerApp.withConfigValidator
        (UrlIngestionValidator(app.Config, policy) :> ConfigValidation.IConfigValidator)
        withSingletons

/// Phase 511 — register a custom URL transport (an egress proxy, a
/// signed-fetch service) without changing the allowlist semantics. The
/// gate stays in `classifyUrl`; only the bytes arrive differently.
let withUrlContentFetcher (fetcher: IUrlContentFetcher) (app: ServerApp) : ServerApp =
    app |> withServiceConfig (fun s -> s.AddSingleton<IUrlContentFetcher>(fetcher))