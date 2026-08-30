// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup tenants list | preview | offboard` — a scripted surface over
/// the deployment's admin tenant-lifecycle API.
///
/// The lifecycle substrate has been callable for a while, but only from
/// the admin UI or hand-rolled HTTP: bulk cleanup of stray scopes, a
/// scheduled offboard sweep, or a CI-driven environment reset all meant
/// writing the request by hand. These verbs map 1:1 onto the admin API
/// and add nothing of their own.
///
/// **Thin client, no new behaviour.** Every decision — who may call, what
/// a preview counts, whether an offboard needs a confirmation token, what
/// the actor id is — is made server-side and unchanged here. In
/// particular the CLI **never mints a confirmation token**: under a
/// confirmation mode a token-less offboard is refused by the server, the
/// refusal banner is surfaced verbatim, and the process exits non-zero.
/// Minting is a deliberate second-operator act (`RequestDeprovisionToken`
/// on the admin surface, which under the two-person rule must be a
/// *different* admin), so automating it here would dissolve exactly the
/// control the gate exists to impose.
///
/// **Pure BCL** (the base CLI carries no SDK dependency): the request
/// bodies and response reads are hand-written against the admin API's
/// documented wire shape — a JSON array of the method's arguments in,
/// the serialised return value out, with an F# `Result` rendered as
/// `{"Ok": …}` / `{"Error": "…"}`. The transport is a record of one
/// function so a test can substitute a mock without a socket.
module ToolUp.Cli.Commands.Tenants

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open ToolUp.Cli.Dispatch

// ── Admin transport (550.C) ─────────────────────────────────────────
//
// The same authenticated-admin shape the admin UI uses: a POST per
// method under the API's route prefix, the arguments as a JSON array in
// the body, and the caller's identity in an `Authorization: Bearer`
// header. The server pins the acting user to the authenticated caller
// and gates every method on the Platform-Admin role, so the credential
// is the whole of the CLI's authority — there is no CLI-side gate to
// weaken or bypass.

/// One admin call: a route (`/api/…`) and a JSON array of arguments,
/// yielding the raw response body or a transport-level failure. A record
/// of one function rather than an interface so a test substitutes a
/// literal without a socket or a listening port.
type Transport = {
    Post: string -> string -> Result<string, string>
}

/// Where to reach the deployment, and with what credential.
type AdminEndpoint = {
    /// Origin (and optional path prefix) of the deployment, no trailing
    /// slash — e.g. `https://app.example.com`.
    BaseUrl: string
    /// Bearer credential of a Platform Admin.
    Token: string
}

/// Advisory wire actor. The server pins the real actor to the
/// authenticated caller (the wire value is forward-compat shape only), so
/// this is a provenance marker in request logs, never an identity claim.
[<Literal>]
let WireActor = "toolup-cli"

/// Route prefix of the tenant-lifecycle admin API.
let tenantRoute (methodName: string) =
    sprintf "/api/_platform/tenants/%s" methodName

/// Route of the team admin API (the deployment-wide team read).
let teamRoute (methodName: string) = sprintf "/api/TeamApi/%s" methodName

/// Resolve where to call and with what, from `--endpoint` and
/// `--token-file`.
///
/// **Neither is an environment variable, and that is deliberate.**
/// `TOOLUP_*` is the *deployment's* configuration namespace — centrally
/// registered, dumped by `--print-config`, documented in the config
/// reference — and this CLI configures a client process, not a
/// deployment. The natural name was already taken and means something
/// else entirely: `TOOLUP_ADMIN_TOKEN` is the deployment's shared
/// crypto-shred secret, replayed as an `X-Admin-Token` header, not an
/// admin's bearer identity. A CLI silently picking that up on a box
/// where the server also runs would send the wrong secret under the
/// wrong scheme, and the resulting 401 would look like a login problem.
///
/// **And the credential is read from a FILE rather than argv or the
/// environment.** A token in argv lands in shell history and in every
/// process listing on the machine; a token in the environment is
/// inherited by every child process and shows up in crash dumps. A file
/// carries filesystem permissions, and it is the shape a container
/// secret mount already has.
let resolveEndpoint (baseUrl: string option) (tokenFile: string option) : Result<AdminEndpoint, string> =
    match baseUrl, tokenFile with
    | None, _ -> Error "no deployment endpoint — pass --endpoint <url>"
    | _, None -> Error "no admin credential — pass --token-file <path> holding a Platform-Admin bearer token"
    | Some url, Some path ->
        try
            match File.ReadAllText(path).Trim() with
            | "" -> Error(sprintf "the token file '%s' is empty" path)
            | token ->
                Ok {
                    BaseUrl = url.Trim().TrimEnd '/'
                    Token = token
                }
        with ex ->
            Error(sprintf "could not read the token file '%s': %s" path ex.Message)

/// The real transport. One-shot per call: a CLI process makes a handful
/// of requests, so a pooled client would buy nothing.
let httpTransport (endpoint: AdminEndpoint) : Transport = {
    Post =
        fun route argsJson ->
            try
                use client = new HttpClient()
                // An inline offboard runs every hook to completion and can
                // legitimately take tens of minutes on a large tenant; the
                // default 100s would abandon a destructive call mid-flight
                // and report a failure that did not happen.
                client.Timeout <- TimeSpan.FromMinutes 60.0
                use request = new HttpRequestMessage(HttpMethod.Post, endpoint.BaseUrl + route)
                request.Content <- new StringContent(argsJson, Encoding.UTF8, "application/json")

                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + endpoint.Token)
                |> ignore

                use response = client.SendAsync request |> Async.AwaitTask |> Async.RunSynchronously

                let body =
                    response.Content.ReadAsStringAsync()
                    |> Async.AwaitTask
                    |> Async.RunSynchronously

                if response.IsSuccessStatusCode then
                    Ok body
                else
                    Error(sprintf "HTTP %d — %s" (int response.StatusCode) (body.Trim()))
            with ex ->
                Error ex.Message
}

// ── Wire reading ────────────────────────────────────────────────────
//
// The admin API returns an F# `Result<'T, string>`; the JSON converter
// set renders a single-field union case as `{"<Case>": <field>}` and
// also accepts the string-prefixed array form. Both are read here so a
// serialiser-shape change on the server does not silently become an
// "unrecognised response" at the operator's terminal.

let private property (el: JsonElement) (name: string) =
    match el.TryGetProperty name with
    | true, v -> Some v
    | _ -> None

let private caseNamed (el: JsonElement) (name: string) =
    match property el name with
    | Some v -> Some v
    | None -> property el (name.ToLowerInvariant())

/// Read the `Result` envelope, yielding the `Ok` payload or the server's
/// error text **verbatim** — the refusal banner an operator needs to see
/// unedited (`offboard confirmation required`, `platform admin role
/// required`) is the server's wording, not ours.
let resultPayload (el: JsonElement) : Result<JsonElement, string> =
    let errorText (v: JsonElement) =
        if v.ValueKind = JsonValueKind.String then
            v.GetString()
        else
            v.ToString()

    match el.ValueKind with
    | JsonValueKind.Object ->
        match caseNamed el "Ok", caseNamed el "Error" with
        | Some ok, _ -> Ok ok
        | _, Some err -> Error(errorText err)
        | None, None -> Error(sprintf "unrecognised response shape: %s" (el.ToString()))
    | JsonValueKind.Array ->
        // `["Ok", <payload>]` / `["Error", "<text>"]`.
        let items = el.EnumerateArray() |> Seq.toList

        match items with
        | head :: rest when head.ValueKind = JsonValueKind.String ->
            match head.GetString(), rest with
            | "Ok", [ payload ] -> Ok payload
            | "Error", [ err ] -> Error(errorText err)
            | _ -> Error(sprintf "unrecognised response shape: %s" (el.ToString()))
        | _ -> Error(sprintf "unrecognised response shape: %s" (el.ToString()))
    | _ -> Error(sprintf "unrecognised response shape: %s" (el.ToString()))

/// A record field, tolerant of the case-insensitive property matching the
/// wire converter allows.
let field (el: JsonElement) (name: string) : JsonElement option =
    match property el name with
    | Some v -> Some v
    | None ->
        el.EnumerateObject()
        |> Seq.tryFind (fun p -> String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map _.Value

let stringField (el: JsonElement) (name: string) : string =
    match field el name with
    | None -> ""
    | Some v ->
        match v.ValueKind with
        | JsonValueKind.String -> v.GetString()
        | JsonValueKind.Null -> ""
        | _ -> v.ToString()

let intField (el: JsonElement) (name: string) : int =
    match field el name with
    | Some v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt64() with
        | true, n -> int n
        | _ -> 0
    | _ -> 0

let boolField (el: JsonElement) (name: string) : bool =
    match field el name with
    | Some v -> v.ValueKind = JsonValueKind.True
    | None -> false

let arrayField (el: JsonElement) (name: string) : JsonElement list =
    match field el name with
    | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> List.ofSeq
    | _ -> []

// ── Table rendering ─────────────────────────────────────────────────

/// Left-align every column but the last, which runs free so a long
/// detail string does not pad every row to its width.
let renderTable (headers: string list) (rows: string list list) : string list =
    let all = headers :: rows
    let columns = List.length headers

    if columns = 0 then
        []
    else
        let widths =
            [ 0 .. columns - 2 ]
            |> List.map (fun i -> all |> List.map (fun r -> r[i].Length) |> List.max)

        let renderRow (r: string list) =
            let padded = widths |> List.mapi (fun i w -> r[i].PadRight w)
            String.concat "  " (padded @ [ r[columns - 1] ])

        all |> List.map renderRow

// ── Shared command plumbing ─────────────────────────────────────────

let private jsonArgs (values: string list) =
    let encoded = values |> List.map JsonSerializer.Serialize
    "[" + String.concat "," encoded + "]"

/// Invoke `route` and hand the `Ok` payload to `render`, which returns the
/// lines to print and the exit code. A transport failure or a server
/// refusal goes to stderr and exits non-zero.
let call (transport: Transport) (route: string) (argsJson: string) (render: JsonElement -> string list * int) : int =
    match transport.Post route argsJson with
    | Error message ->
        eprintfn "toolup: %s" message
        ExitRuntimeError
    | Ok body ->
        let parsed =
            try
                Ok(JsonDocument.Parse body)
            with ex ->
                Error(sprintf "could not read the response: %s" ex.Message)

        match parsed with
        | Error message ->
            eprintfn "toolup: %s" message
            ExitRuntimeError
        | Ok doc ->
            use doc = doc

            match resultPayload doc.RootElement with
            | Error banner ->
                // Verbatim: a confirmation-gate refusal is the server's
                // own wording, and an operator forwarding it to a
                // colleague must be forwarding what the server said.
                eprintfn "%s" banner
                ExitRuntimeError
            | Ok payload ->
                let lines, code = render payload
                lines |> List.iter (printfn "%s")
                code

// ── 550.A — `tenants list` ──────────────────────────────────────────

let renderTeams (payload: JsonElement) : string list * int =
    let teams =
        if payload.ValueKind = JsonValueKind.Array then
            payload.EnumerateArray() |> List.ofSeq
        else
            []

    if List.isEmpty teams then
        [ "no teams" ], ExitOk
    else
        let rows =
            teams
            |> List.map (fun t -> [
                stringField t "TeamId"
                stringField t "Name"
                string (intField t "MemberCount")
                (if boolField t "Archived" then "yes" else "no")
                (arrayField t "Owners"
                 |> List.map (fun o -> o.GetString())
                 |> function
                     | [] -> "-"
                     | os -> String.concat "," os)
            ])

        let table = renderTable [ "TEAM"; "NAME"; "MEMBERS"; "ARCHIVED"; "OWNERS" ] rows
        table @ [ ""; sprintf "%d team(s)" (List.length teams) ], ExitOk

let runList (transport: Transport) : int =
    call transport (teamRoute "ListAllTeams") "[]" renderTeams

// ── 550.A — `tenants preview` ───────────────────────────────────────

let renderPreview (payload: JsonElement) : string list * int =
    let items = arrayField payload "Items"

    let rows =
        items
        |> List.map (fun i -> [
            stringField i "HookName"
            (if boolField i "HasPreview" then "yes" else "no")
            string (intField i "WouldAffect")
            stringField i "Detail"
        ])

    let header =
        sprintf "offboard preview for %s (nothing is modified)" (stringField payload "ScopeId")

    let body =
        if List.isEmpty rows then
            [ "no lifecycle hooks are registered" ]
        else
            renderTable [ "HOOK"; "PREVIEW"; "WOULD-AFFECT"; "DETAIL" ] rows

    let opaque =
        items |> List.filter (fun i -> not (boolField i "HasPreview")) |> List.length

    let footer = [
        ""
        sprintf
            "%d record(s) would be affected across %d hook(s)"
            (intField payload "TotalWouldAffect")
            (List.length items)
        // An opted-out hook is a genuine gap in the projection, not a
        // zero — say so rather than letting the total read as complete.
        if opaque > 0 then
            sprintf "%d hook(s) offered no preview — their blast radius is NOT in that total" opaque
    ]

    (header :: "" :: body) @ footer, ExitOk

let runPreview (transport: Transport) (scopeId: string) : int =
    call transport (tenantRoute "PreviewDeprovision") (jsonArgs [ scopeId ]) renderPreview

// ── 550.A — `tenants offboard` ──────────────────────────────────────

let private hookResult (r: JsonElement) =
    // `LifecycleHookResult` — a no-field case is a bare string, a
    // single-field case an object keyed by the case name.
    match r.ValueKind with
    | JsonValueKind.String -> r.GetString(), ""
    | JsonValueKind.Object ->
        match r.EnumerateObject() |> Seq.tryHead with
        | Some p ->
            let detail =
                if p.Value.ValueKind = JsonValueKind.String then
                    p.Value.GetString()
                else
                    p.Value.ToString()

            p.Name, detail
        | None -> "?", ""
    | _ -> "?", ""

/// Render a `LifecycleSummary`. A failed hook does not abort the sweep
/// server-side (the rest of the erasure still runs), but it leaves the
/// tenant partially offboarded — so the exit code is non-zero and a
/// scripted sweep stops rather than reporting a clean run.
let renderSummary (summary: JsonElement) : string list * int =
    let outcomes = arrayField summary "Outcomes"

    let rows =
        outcomes
        |> List.map (fun o ->
            let status, detail = hookResult (field o "Result" |> Option.defaultValue o)

            [ stringField o "HookName"; status; string (intField o "ElapsedMs"); detail ])

    let counts =
        rows
        |> List.countBy (fun r -> r[1])
        |> List.sortBy fst
        |> List.map (fun (status, n) -> sprintf "%d %s" n (status.ToLowerInvariant()))

    let failed = rows |> List.filter (fun r -> r[1] = "Failed") |> List.length

    let body =
        if List.isEmpty rows then
            [ "no lifecycle hooks are registered — nothing ran" ]
        else
            renderTable [ "HOOK"; "RESULT"; "ELAPSED-MS"; "DETAIL" ] rows

    let footer = [
        ""
        sprintf
            "%s in %dms — %s"
            (stringField summary "ScopeId")
            (intField summary "TotalElapsedMs")
            (if List.isEmpty counts then
                 "no hooks"
             else
                 String.concat ", " counts)
        if failed > 0 then
            sprintf "%d hook(s) FAILED — the tenant is partially offboarded" failed
    ]

    body @ footer, (if failed > 0 then ExitRuntimeError else ExitOk)

/// `ExportThenDeprovision` returns the erasure summary bundled with the
/// archive reference the operator hands the departing customer.
let renderExportThenSummary (payload: JsonElement) : string list * int =
    let archiveLines =
        match field payload "Archive" with
        | None -> []
        | Some a -> [
            ""
            sprintf
                "export archive: %s/%s (%d segment(s), sha256 %s)"
                (stringField a "Container")
                (stringField a "BlobPath")
                (intField a "SegmentCount")
                (stringField a "ContentHash")
          ]

    match field payload "Summary" with
    | None -> renderSummary payload
    | Some summary ->
        let lines, code = renderSummary summary
        lines @ archiveLines, code

/// The offboard runner, shared with `toolup users offboard` (550.B).
/// Which admin method it drives is decided entirely by what the operator
/// supplied — and the token, when present, is replayed verbatim; the CLI
/// has no path that produces one.
let runOffboard
    (transport: Transport)
    (scopeId: string)
    (reason: string)
    (exportFirst: bool)
    (token: string option)
    : int =
    match exportFirst, token with
    | true, Some _ ->
        // Not a CLI restriction: the admin API has no confirmed
        // export-then-erase method, so this pairing has nothing to call.
        // Refusing here says so, rather than sending a request the server
        // would refuse for a reason that reads like a token problem.
        eprintfn "toolup tenants offboard: --export-first and --token cannot be combined —"
        eprintfn "  the confirmation-gated offboard has no export-then-erase form."
        ExitUsage
    | true, None ->
        call
            transport
            (tenantRoute "ExportThenDeprovision")
            (jsonArgs [ scopeId; WireActor; reason ])
            renderExportThenSummary
    | false, Some t ->
        call
            transport
            (tenantRoute "DeprovisionTenantConfirmed")
            (jsonArgs [ scopeId; WireActor; reason; t ])
            renderSummary
    | false, None ->
        call transport (tenantRoute "DeprovisionTenant") (jsonArgs [ scopeId; WireActor; reason ]) renderSummary

// ── Argument parsing ────────────────────────────────────────────────

type Options = {
    Endpoint: string option
    TokenFile: string option
    ScopeId: string option
    Reason: string
    ExportFirst: bool
    Token: string option
    TeamLess: bool
}

let defaultOptions = {
    Endpoint = None
    TokenFile = None
    ScopeId = None
    Reason = "offboard requested via the toolup CLI"
    ExportFirst = false
    Token = None
    TeamLess = false
}

/// Shared parser for every verb in this family. A verb that does not
/// accept a positional argument or `--team-less` rejects it at the
/// call site, so one parser serves all of them without any verb
/// silently accepting an option it ignores.
let rec parse (opts: Options) (args: string list) : Result<Options, string> =
    match args with
    | [] -> Ok opts
    | "--endpoint" :: v :: rest -> parse { opts with Endpoint = Some v } rest
    | [ "--endpoint" ] -> Error "missing value for --endpoint"
    | "--token-file" :: v :: rest -> parse { opts with TokenFile = Some v } rest
    | [ "--token-file" ] -> Error "missing value for --token-file"
    | "--reason" :: v :: rest -> parse { opts with Reason = v } rest
    | [ "--reason" ] -> Error "missing value for --reason"
    | "--token" :: v :: rest -> parse { opts with Token = Some v } rest
    | [ "--token" ] -> Error "missing value for --token"
    | "--export-first" :: rest -> parse { opts with ExportFirst = true } rest
    | "--team-less" :: rest -> parse { opts with TeamLess = true } rest
    | value :: rest when not (value.StartsWith "-") ->
        match opts.ScopeId with
        | Some existing -> Error(sprintf "unexpected second argument '%s' (already have '%s')" value existing)
        | None -> parse { opts with ScopeId = Some value } rest
    | unknown :: _ -> Error(sprintf "unrecognised argument: %s" unknown)

let usageError (verb: string) (help: string list) (message: string) =
    eprintfn "toolup %s: %s" verb message
    eprintfn ""
    help |> List.iter (eprintfn "%s")
    ExitUsage

// ── Help + registration ─────────────────────────────────────────────

let endpointHelp = [
    "Endpoint + credential (both required):"
    "  --endpoint <url>    Deployment origin, e.g. https://app.example.com."
    "  --token-file <path> File holding a Platform-Admin bearer token."
    ""
    "The token is read from a file rather than a flag or an environment"
    "variable: a credential in argv lands in shell history and in every process"
    "listing, and one in the environment is inherited by every child process. A"
    "file carries filesystem permissions, and is the shape a container secret"
    "mount already has. (Note TOOLUP_ADMIN_TOKEN is a DIFFERENT thing — the"
    "deployment's shared crypto-shred secret — and is not read here.)"
]

let listHelp =
    [
        "Usage: toolup tenants list --endpoint <url> --token-file <path>"
        ""
        "Lists every team on the deployment with its membership summary — the"
        "same deployment-wide admin read the Platform-Management table uses."
        "Platform-Admin only; the server enforces the gate."
        ""
    ]
    @ endpointHelp

let previewHelp =
    [
        "Usage: toolup tenants preview <scopeId> --endpoint <url> --token-file <path>"
        ""
        "Renders each lifecycle hook's would-affect projection for an offboard"
        "of <scopeId> WITHOUT modifying anything — the encryption key that"
        "would be destroyed, the jobs that would be cancelled, the records that"
        "would be erased. A hook that offers no preview is listed as such, and"
        "its blast radius is excluded from the total rather than counted as 0."
        ""
    ]
    @ endpointHelp

let offboardHelp =
    [
        "Usage: toolup tenants offboard <scopeId> [--export-first] [--token <t>]"
        "                               [--reason <text>] --endpoint <url>"
        "                               --token-file <path>"
        ""
        "Runs every registered deprovision hook for <scopeId>: crypto-shred,"
        "membership-cache eviction, scheduled-job cancellation, subject-data"
        "erasure. IRREVERSIBLE — run `toolup tenants preview <scopeId>` first."
        ""
        "Exits non-zero when any hook failed (the sweep still ran the rest, so"
        "the tenant is left partially offboarded and a scripted sweep should"
        "stop), and when the server refuses the call."
        ""
        "Options:"
        "  --export-first      Produce the tenant's data-export archive as a"
        "                      durable pre-step and erase only once it is"
        "                      written; a failed export aborts before any"
        "                      destruction. Prints the archive reference."
        "  --token <t>         Replay a confirmation token minted by another"
        "                      admin. Required when the deployment runs a"
        "                      confirmation mode; a token-less call is then"
        "                      refused by the server and the refusal is shown"
        "                      verbatim. This CLI never mints a token — under"
        "                      the two-person rule the minting admin must be a"
        "                      different person, which is not something a"
        "                      command-line flag can attest to."
        "  --reason <text>     Operator reason recorded in the audit trail."
        ""
    ]
    @ endpointHelp

/// Build a verb whose body needs a resolved transport. Endpoint
/// resolution failures are usage errors (nothing was sent).
let withTransport (verb: string) (help: string list) (opts: Options) (body: Transport -> int) =
    match resolveEndpoint opts.Endpoint opts.TokenFile with
    | Error message -> usageError verb help message
    | Ok endpoint -> body (httpTransport endpoint)

let listCommand = {
    Path = [ "tenants"; "list" ]
    Summary = "List every team on the deployment (Platform-Admin)."
    Help = listHelp
    Run =
        fun args ->
            match parse defaultOptions args with
            | Error message -> usageError "tenants list" listHelp message
            | Ok opts when opts.ScopeId.IsSome ->
                usageError "tenants list" listHelp (sprintf "unexpected argument: %s" opts.ScopeId.Value)
            | Ok opts -> withTransport "tenants list" listHelp opts runList
}

let previewCommand = {
    Path = [ "tenants"; "preview" ]
    Summary = "Preview an offboard's blast radius without modifying anything."
    Help = previewHelp
    Run =
        fun args ->
            match parse defaultOptions args with
            | Error message -> usageError "tenants preview" previewHelp message
            | Ok opts ->
                match opts.ScopeId with
                | None -> usageError "tenants preview" previewHelp "a scope id is required"
                | Some scopeId -> withTransport "tenants preview" previewHelp opts (fun t -> runPreview t scopeId)
}

let offboardCommand = {
    Path = [ "tenants"; "offboard" ]
    Summary = "Run every deprovision hook for a scope (IRREVERSIBLE)."
    Help = offboardHelp
    Run =
        fun args ->
            match parse defaultOptions args with
            | Error message -> usageError "tenants offboard" offboardHelp message
            | Ok opts ->
                match opts.ScopeId with
                | None -> usageError "tenants offboard" offboardHelp "a scope id is required"
                | Some scopeId ->
                    withTransport "tenants offboard" offboardHelp opts (fun t ->
                        runOffboard t scopeId opts.Reason opts.ExportFirst opts.Token)
}