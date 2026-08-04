// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.ExternalCompute.Http

open System
open System.Globalization
open System.Text.Json

// ─── Phase 322 — the generic HTTP/REST compute backend, as config ─────
//
// `IExternalComputeDispatcher` (Phase 318) says nothing about HOW a
// backend is reached. This companion says "over HTTP", and everything
// that varies between one HTTP compute service and the next lives in
// this file as data: the three URLs, the auth seam, the request-body
// field names, and the selectors that read a job id / status / progress
// / result ref back out of whatever JSON the service answers with.
//
// **The hot zone is selector expressiveness, and the resolution is a
// dotted path — deliberately NOT a templating language.** A real status
// response is `{"state":"RUNNING"}`, or `{"job":{"status":"running",
// "progress":{"fraction":0.4}}}`, or `{"items":[{"phase":"Succeeded"}]}`.
// All three are reachable with property navigation plus array indexing,
// which is what `JsonPath` below is and all it is. The temptation is to
// keep going — filters, wildcards, expressions, a `$`-rooted JSONPath
// dialect — and every step buys a rarer response shape at the cost of a
// second language inside the config, with its own parser, its own error
// messages, its own semantics to document, and its own bugs. The line is
// drawn where the *grammar* would stop being describable in one
// sentence.
//
// The escape hatch for a response this cannot describe is not a bigger
// selector language: it is a companion. `IExternalComputeDispatcher` is
// twenty lines, and a service whose status lives behind a query
// expression is better served by an implementation that knows the
// service than by a config that pretends to be generic. Recording that
// here so the next reader extends the *set of companions* rather than
// the grammar.
//
// **Zero paid dependency (GP 2), zero vendor SDK (GP 1).** BCL
// `HttpClient` + `System.Text.Json`, per the companion-authoring guide's
// rule for HTTP-shaped companions.

/// One step in a `JsonPath`. `[<RequireQualifiedAccess>]` because
/// `Index` and `Property` are about as collision-prone as case names
/// get, and an unqualified one is how a call site silently binds the
/// union you did not mean.
[<RequireQualifiedAccess>]
type JsonPathSegment =
    /// Read a named property of the current object.
    | Property of name: string
    /// Read the nth element of the current array (zero-based).
    | Index of index: int

/// A dotted-path selector into a JSON response — `state`,
/// `job.status`, `items[0].phase`, `result.refs[1]`.
///
/// The whole grammar, in one sentence: **a `.`-separated sequence of
/// property names, each optionally followed by one or more `[n]` array
/// indices.** No wildcards, no filters, no expressions, no root
/// sigil. See the file header for why the line is drawn there.
///
/// `Text` is retained verbatim so a diagnostic can name the selector
/// the operator wrote rather than a re-rendering of it.
type JsonPath = {
    /// The parsed steps, in reading order.
    Segments: JsonPathSegment list
    /// The selector exactly as configured.
    Text: string
}

[<RequireQualifiedAccess>]
module JsonPath =

    let private isDelimiter (c: char) = c = '.' || c = '['

    let rec private scan (text: string) (at: int) (acc: JsonPathSegment list) : Result<JsonPathSegment list, string> =
        if at >= text.Length then
            Ok(List.rev acc)
        elif text[at] = '[' then
            match text.IndexOf(']', at) with
            | -1 -> Error $"selector '%s{text}' has an unclosed '['"
            | close ->
                let inner = text.Substring(at + 1, close - at - 1)

                match Int32.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, index -> scan text (close + 1) (JsonPathSegment.Index index :: acc)
                | _ -> Error $"selector '%s{text}' has '[%s{inner}]', which is not a non-negative array index"
        elif text[at] = '.' then
            // A '.' must separate two non-empty elements. Rejecting the
            // degenerate forms here is what stops `a..b` and `.a` and
            // `a.` from being silently accepted as `a.b` / `a` / `a` —
            // a selector that quietly means something other than what
            // was written is the worst outcome available.
            if at = 0 then
                Error $"selector '%s{text}' starts with '.'"
            elif at = text.Length - 1 then
                Error $"selector '%s{text}' ends with '.'"
            elif isDelimiter text[at + 1] then
                Error $"selector '%s{text}' has an empty path element"
            else
                scan text (at + 1) acc
        else
            let stop =
                seq { at .. text.Length - 1 }
                |> Seq.tryFind (fun i -> isDelimiter text[i])
                |> Option.defaultValue text.Length

            scan text stop (JsonPathSegment.Property(text.Substring(at, stop - at)) :: acc)

    /// Parse a dotted-path selector, or say precisely why it is not one.
    let parse (text: string) : Result<JsonPath, string> =
        if String.IsNullOrWhiteSpace text then
            Error "a JSON selector cannot be empty"
        else
            let trimmed = text.Trim()

            match scan trimmed 0 [] with
            | Error e -> Error e
            | Ok [] -> Error $"selector '%s{trimmed}' names no path element"
            | Ok segments -> Ok { Segments = segments; Text = trimmed }

    /// `parse`, raising on a malformed selector.
    ///
    /// For composition code holding literal selectors: the raise happens
    /// at compose, in front of the operator who wrote the literal, and
    /// never on a request path. Config arriving from the environment goes
    /// through `parse` and reports instead.
    let ofString (text: string) : JsonPath =
        match parse text with
        | Ok path -> path
        | Error e -> raise (ArgumentException(e, nameof text))

    /// Navigate `root` by `path`. `None` when any step is absent or the
    /// document's shape does not admit it — never an exception, because
    /// a backend answering with a shape the config did not predict is an
    /// ordinary operational event and the caller has a typed refusal to
    /// return.
    let select (path: JsonPath) (root: JsonElement) : JsonElement option =
        let rec walk (element: JsonElement) segments =
            match segments with
            | [] -> Some element
            | JsonPathSegment.Property name :: rest ->
                if element.ValueKind <> JsonValueKind.Object then
                    None
                else
                    match element.TryGetProperty name with
                    | true, child -> walk child rest
                    | _ -> None
            | JsonPathSegment.Index index :: rest ->
                if element.ValueKind <> JsonValueKind.Array || index >= element.GetArrayLength() then
                    None
                else
                    walk element[index] rest

        walk root path.Segments

    /// Select a scalar as text. A number is returned as its raw JSON
    /// text, so a backend that answers `{"id": 8812}` yields `"8812"` —
    /// the `NativeRef` is an opaque string either way and refusing a
    /// numeric id would refuse a common real shape for no gain.
    /// `null` / a missing step / a composite value yield `None`.
    let selectString (path: JsonPath) (root: JsonElement) : string option =
        select path root
        |> Option.bind (fun element ->
            match element.ValueKind with
            | JsonValueKind.String -> element.GetString() |> Option.ofObj
            | JsonValueKind.Number
            | JsonValueKind.True
            | JsonValueKind.False -> Some(element.GetRawText())
            | _ -> None)

    /// Select a number. Accepts a JSON number, and a string holding one
    /// — invariant-culture parsed, because a backend rendering
    /// `"0.42"` is common and a decimal comma is not something to guess
    /// at.
    let selectFloat (path: JsonPath) (root: JsonElement) : float option =
        select path root
        |> Option.bind (fun element ->
            match element.ValueKind with
            | JsonValueKind.Number ->
                match element.TryGetDouble() with
                | true, value -> Some value
                | _ -> None
            | JsonValueKind.String ->
                match Double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, value -> Some value
                | _ -> None
            | _ -> None)

    /// Select a boolean. Accepts a JSON boolean and the strings
    /// `"true"` / `"false"` (case-insensitive). A number is NOT read as
    /// a boolean: `0` meaning false is a convention, not a fact, and
    /// guessing it wrong on a retriability flag re-submits work forever.
    let selectBool (path: JsonPath) (root: JsonElement) : bool option =
        select path root
        |> Option.bind (fun element ->
            match element.ValueKind with
            | JsonValueKind.True -> Some true
            | JsonValueKind.False -> Some false
            | JsonValueKind.String ->
                match (element.GetString() |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
                | "true" -> Some true
                | "false" -> Some false
                | _ -> None
            | _ -> None)

/// Which of the five `ExternalOutcome` states a backend's own status
/// label means. The classification result, kept separate from
/// `ExternalOutcome` because the outcome needs a progress fraction /
/// result ref / error that come from OTHER selectors — the status
/// string alone cannot build one.
[<RequireQualifiedAccess>]
type HttpStatusClass =
    | Pending
    | Running
    | Succeeded
    | Failed
    | Cancelled

/// The backend's status vocabulary, one list per state.
///
/// Five explicit lists rather than a `Map<string, HttpStatusClass>`: the
/// record makes it impossible to configure a vocabulary with no
/// `Succeeded` arm and obvious at a glance which state a label is
/// missing from, and the `Map` shape reads backwards from how an
/// operator thinks about it ("what does this service call success?").
type HttpComputeStatusMap = {
    /// Labels meaning accepted-but-not-started.
    Pending: string list
    /// Labels meaning executing.
    Running: string list
    /// Labels meaning terminal success.
    Succeeded: string list
    /// Labels meaning terminal failure.
    Failed: string list
    /// Labels meaning terminal cancellation.
    Cancelled: string list
}

[<RequireQualifiedAccess>]
module HttpComputeStatusMap =

    /// The vocabulary most REST compute services already speak. A
    /// deployment whose service uses one of these words needs no status
    /// configuration at all; one that says `"WORKING"` adds it.
    let defaults: HttpComputeStatusMap = {
        Pending = [ "pending"; "queued"; "submitted"; "accepted"; "waiting"; "scheduled" ]
        Running = [ "running"; "in_progress"; "inprogress"; "started"; "active"; "processing" ]
        Succeeded = [
            "succeeded"
            "success"
            "successful"
            "completed"
            "complete"
            "done"
            "finished"
        ]
        Failed = [ "failed"; "failure"; "error"; "errored" ]
        Cancelled = [ "cancelled"; "canceled"; "aborted"; "stopped"; "terminated" ]
    }

    /// Every label the map declares, paired with its class.
    let entries (map: HttpComputeStatusMap) : (string * HttpStatusClass) list = [
        for label in map.Pending -> label, HttpStatusClass.Pending
        for label in map.Running -> label, HttpStatusClass.Running
        for label in map.Succeeded -> label, HttpStatusClass.Succeeded
        for label in map.Failed -> label, HttpStatusClass.Failed
        for label in map.Cancelled -> label, HttpStatusClass.Cancelled
    ]

    /// Classify a status label, case- and whitespace-insensitively.
    /// `None` for a label the map does not declare — which the
    /// dispatcher reports as a terminal, config-naming failure rather
    /// than guessing, because every guess available is a lie about
    /// whether the work finished.
    let classify (map: HttpComputeStatusMap) (value: string) : HttpStatusClass option =
        let needle = if isNull value then "" else value.Trim().ToLowerInvariant()

        entries map
        |> List.tryPick (fun (label, cls) ->
            if (if isNull label then "" else label.Trim().ToLowerInvariant()) = needle then
                Some cls
            else
                None)

    /// Labels declared under more than one class, which is a
    /// configuration defect: whichever class won would be an
    /// implementation detail of list order deciding whether a job is
    /// reported as finished.
    let ambiguous (map: HttpComputeStatusMap) : string list =
        entries map
        |> List.map (fun (label, _) -> (if isNull label then "" else label.Trim().ToLowerInvariant()), ())
        |> List.countBy fst
        |> List.filter (fun (_, count) -> count > 1)
        |> List.map fst

/// How the companion authenticates to the compute service.
///
/// The secret is named, never carried: this record is JSON-serialisable
/// and reaches diagnostic surfaces, and it is read from `ISecretStore`
/// **on every request** so a rotation is picked up without a restart
/// (the build-once / read-per-call seam mismatch this estate tracks as a
/// named defect class).
type HttpComputeAuth = {
    /// Request header the credential is presented in — typically
    /// `Authorization`, sometimes a vendor header like `X-Api-Key`.
    HeaderName: string
    /// `ISecretStore` scope the secret lives under. `_platform` for a
    /// deployment-wide credential.
    SecretScope: string
    /// `ISecretStore` key holding the credential.
    SecretKey: string
    /// Header-value template. `{secret}` is substituted with the
    /// resolved secret; everything else is literal, so `Bearer
    /// {secret}` and a bare `{secret}` are both expressible.
    ValueFormat: string
}

[<RequireQualifiedAccess>]
module HttpComputeAuth =
    /// The `{secret}` placeholder in `HttpComputeAuth.ValueFormat`.
    [<Literal>]
    let SecretPlaceholder = "{secret}"

    /// A bearer-token header reading `key` from the `_platform` scope.
    let bearer (secretKey: string) : HttpComputeAuth = {
        HeaderName = "Authorization"
        SecretScope = "_platform"
        SecretKey = secretKey
        ValueFormat = "Bearer " + SecretPlaceholder
    }

    /// An API-key header reading `key` from the `_platform` scope.
    let apiKey (headerName: string) (secretKey: string) : HttpComputeAuth = {
        HeaderName = headerName
        SecretScope = "_platform"
        SecretKey = secretKey
        ValueFormat = SecretPlaceholder
    }

/// How a cancellation is expressed against the service. Absent when the
/// service has no cancel endpoint at all — the dispatcher then logs and
/// no-ops rather than inventing a request (322.D).
type HttpComputeCancel = {
    /// URL template. `{jobId}` is substituted with the handle's
    /// `NativeRef`.
    UrlTemplate: string
    /// HTTP method — `POST` and `DELETE` are the two real shapes.
    Method: string
}

/// The request-body field names the service expects on `Submit`.
///
/// Field NAMES rather than a body template, for the same reason the
/// selectors are a dotted path: a template language would need
/// escaping, conditionals for the optional fields, and a way to talk
/// about the hint map. An `option` field that is `None` is simply
/// omitted from the body, which covers "this service does not accept
/// that" without any syntax at all.
type HttpComputeSubmitFields = {
    /// Field carrying `ExternalWorkSpec.Kind`.
    KindField: string
    /// Field carrying `ExternalWorkSpec.Payload`.
    PayloadField: string
    /// `true` embeds the payload as a JSON value (the caller already
    /// serialised it, so this is the common case); `false` sends it as
    /// a JSON string, for a service that wants an opaque blob.
    PayloadAsRawJson: bool
    /// Field carrying `ExternalWorkSpec.ResourceHints` as a flat
    /// string→string object. `None` omits them — a hint the service
    /// cannot receive is still IGNORED rather than a refusal (Phase
    /// 318's contract).
    ResourceHintsField: string option
    /// Field carrying `ExternalWorkSpec.Timeout` in whole seconds.
    TimeoutSecondsField: string option
    /// Field carrying `ExternalWorkSpec.Idempotency`.
    IdempotencyField: string option
    /// Field carrying the submitting scope, so the service can partition
    /// its own records by tenant (GP 4).
    ScopeField: string option
    /// Field carrying the completion-callback URL, sent only when
    /// `HttpComputeConfig.Callback` is configured. The URL, not the
    /// secret: the per-handle secret does not exist until the platform
    /// has registered the handle `Submit` returned, so it is delivered
    /// separately — see `HttpComputeCallback`.
    CallbackUrlField: string option
}

[<RequireQualifiedAccess>]
module HttpComputeSubmitFields =
    /// Unremarkable JSON field names, matching what a hand-rolled
    /// Flask / FastAPI / Express compute wrapper usually exposes.
    let defaults: HttpComputeSubmitFields = {
        KindField = "kind"
        PayloadField = "payload"
        PayloadAsRawJson = true
        ResourceHintsField = Some "resources"
        TimeoutSecondsField = Some "timeoutSeconds"
        IdempotencyField = Some "idempotencyKey"
        ScopeField = Some "scope"
        CallbackUrlField = Some "callbackUrl"
    }

/// Where the per-handle completion-callback credential is delivered, for
/// a service that supports webhooks (Phase 320's push path).
///
/// **Two requests, not one, and the ordering is forced rather than
/// chosen.** The credential is keyed by `ExternalHandle.HandleId`, and
/// the handle does not exist until `Submit` has returned it and the
/// platform has durably registered it — so the secret cannot ride the
/// submit request. Phase 320 states the consequence plainly: the
/// credential arrives via `AcceptCallbackCredential`, immediately after
/// registration, and a backend fast enough to finish first resolves by
/// poll instead. What the submit request CAN carry is the callback URL,
/// which is deployment-static, so a service that wants its webhook
/// target up front gets it there and only the secret arrives second.
type HttpComputeCallback = {
    /// Public base URL of THIS deployment, as the compute service can
    /// reach it — e.g. `https://app.example.com`. The callback path is
    /// appended from the credential the platform mints, so a deployment
    /// mounted under a prefix stays correct.
    PublicBaseUrl: string
    /// URL the credential is delivered to. `{jobId}` is substituted with
    /// the handle's `NativeRef`, so a service that stores the webhook on
    /// its own job record is addressable.
    RegistrationUrlTemplate: string
    /// HTTP method for the credential delivery — `POST`, `PUT` or
    /// `PATCH`.
    RegistrationMethod: string
    /// Field the callback URL is delivered in.
    UrlField: string
    /// Field the per-handle secret is delivered in. **Never logged.**
    SecretField: string
    /// Field the platform's `HandleId` is echoed in, so the service can
    /// send it back on the callback (`ExternalCallbackPayload.HandleId`
    /// is the ingress's only routing input).
    HandleIdField: string option
}

/// The selectors that read a submit / status response.
type HttpComputeSelectors = {
    /// The backend's own job id, in the **submit** response. Becomes
    /// `ExternalHandle.NativeRef`.
    JobId: JsonPath
    /// The status label, in the **status** response.
    Status: JsonPath
    /// A fractional progress value, in the status response. `None` when
    /// the service reports no progress — the dispatcher then answers
    /// `Running None` rather than fabricating a figure (GP 12 rule 6).
    Progress: JsonPath option
    /// The opaque result reference, read on a `Succeeded` status.
    ResultRef: JsonPath option
    /// The failure description, read on a `Failed` status.
    Error: JsonPath option
    /// Whether the service considers the failure worth retrying, read on
    /// a `Failed` status. Absent ⇒ `false`: a service that does not say
    /// is not asserting the work is worth re-running, and defaulting the
    /// other way re-submits a malformed payload forever.
    Retriable: JsonPath option
}

/// The whole HTTP compute backend, as one value.
type HttpComputeConfig = {
    /// Stable backend label stamped onto every `ExternalHandle.Backend`
    /// this dispatcher mints. Distinct per composed service, so a
    /// routed fleet (Phase 484) can tell two HTTP backends apart.
    Backend: string
    /// URL `Submit` POSTs to.
    SubmitUrl: string
    /// URL `Poll` GETs. `{jobId}` is substituted with the handle's
    /// `NativeRef`, URL-escaped.
    StatusUrlTemplate: string
    /// How a cancel is issued, or `None` for a service without one.
    Cancel: HttpComputeCancel option
    /// How the companion authenticates, or `None` for an unauthenticated
    /// service (a sidecar on a private network).
    Auth: HttpComputeAuth option
    /// Request-body field names for `Submit`.
    Submit: HttpComputeSubmitFields
    /// Response selectors.
    Selectors: HttpComputeSelectors
    /// The service's status vocabulary.
    StatusValues: HttpComputeStatusMap
    /// Divisor applied to the selected progress value. `1.0` for a
    /// service reporting `0.0 .. 1.0`; `100.0` for one reporting a
    /// percentage. A knob rather than a guess: `0.4` and `40` are both
    /// plausible readings of "40% done" and inferring from magnitude
    /// would read a genuine 0.4% as 40%.
    ProgressScale: float
    /// Callback (push-completion) configuration, or `None` for a service
    /// that cannot call back. `None` is the poll-only path Phase 319
    /// already provides.
    Callback: HttpComputeCallback option
    /// Optional URL for the readiness probe + startup reachability
    /// check. A dedicated health endpoint, NOT the submit URL — probing
    /// the submit URL would submit work.
    HealthUrl: string option
    /// Per-request wallclock budget. A request that exceeds it is a
    /// **retriable** failure: an unanswered request says nothing about
    /// whether the work is viable.
    RequestTimeout: TimeSpan
}

[<RequireQualifiedAccess>]
module HttpComputeConfig =

    /// The `{jobId}` placeholder in the status / cancel / registration
    /// URL templates.
    [<Literal>]
    let JobIdPlaceholder = "{jobId}"

    /// Substitute the handle's native ref into a URL template. The ref
    /// is opaque and backend-minted, so it is URL-escaped rather than
    /// trusted to be path-safe.
    let expandJobId (nativeRef: string) (template: string) : string =
        template.Replace(JobIdPlaceholder, Uri.EscapeDataString(if isNull nativeRef then "" else nativeRef))

    /// A minimal config: submit + status, default field names, default
    /// status vocabulary, no auth, no cancel, no callback.
    ///
    /// The selectors have no default — a response shape is exactly the
    /// thing this companion cannot assume, and a silently-wrong default
    /// selector would present as "the backend never finishes".
    let create (backend: string) (submitUrl: string) (statusUrlTemplate: string) (jobId: JsonPath) (status: JsonPath) = {
        Backend = backend
        SubmitUrl = submitUrl
        StatusUrlTemplate = statusUrlTemplate
        Cancel = None
        Auth = None
        Submit = HttpComputeSubmitFields.defaults
        Selectors = {
            JobId = jobId
            Status = status
            Progress = None
            ResultRef = None
            Error = None
            Retriable = None
        }
        StatusValues = HttpComputeStatusMap.defaults
        ProgressScale = 1.0
        Callback = None
        HealthUrl = None
        RequestTimeout = TimeSpan.FromSeconds 30.0
    }

    /// Declare how cancellation is issued.
    let withCancel (method: string) (urlTemplate: string) (config: HttpComputeConfig) = {
        config with
            Cancel =
                Some {
                    UrlTemplate = urlTemplate
                    Method = method
                }
    }

    /// Declare the authentication seam.
    let withAuth (auth: HttpComputeAuth) (config: HttpComputeConfig) = { config with Auth = Some auth }

    /// Declare the result-ref selector — required for any service whose
    /// success carries a result.
    let withResultRef (path: JsonPath) (config: HttpComputeConfig) = {
        config with
            Selectors = {
                config.Selectors with
                    ResultRef = Some path
            }
    }

    /// Declare the progress selector and the scale its values are on.
    let withProgress (scale: float) (path: JsonPath) (config: HttpComputeConfig) = {
        config with
            Selectors = {
                config.Selectors with
                    Progress = Some path
            }
            ProgressScale = scale
    }

    /// Declare the failure-diagnostic selectors.
    let withFailureDetail (error: JsonPath) (retriable: JsonPath option) (config: HttpComputeConfig) = {
        config with
            Selectors = {
                config.Selectors with
                    Error = Some error
                    Retriable = retriable
            }
    }

    /// Declare that the service supports webhooks, and how the
    /// per-handle credential reaches it.
    let withCallback (callback: HttpComputeCallback) (config: HttpComputeConfig) = {
        config with
            Callback = Some callback
    }

    /// Declare the health / reachability endpoint.
    let withHealthUrl (url: string) (config: HttpComputeConfig) = { config with HealthUrl = Some url }

    /// Override the per-request wallclock budget.
    let withRequestTimeout (timeout: TimeSpan) (config: HttpComputeConfig) = { config with RequestTimeout = timeout }

    /// Override the status vocabulary.
    let withStatusValues (map: HttpComputeStatusMap) (config: HttpComputeConfig) = { config with StatusValues = map }

    let private absoluteUrlProblem (what: string) (url: string) : string option =
        if String.IsNullOrWhiteSpace url then
            Some $"%s{what} is empty"
        else
            match Uri.TryCreate(url, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> None
            | true, uri -> Some $"%s{what} '%s{url}' is not an http(s) URL (scheme '%s{uri.Scheme}')"
            | _ -> Some $"%s{what} '%s{url}' is not an absolute URL"

    /// Every problem with `config`, named. Empty ⇒ the config is
    /// well-formed.
    ///
    /// Shape only — nothing here touches the network, so it is safe to
    /// run at construction (where it raises) as well as in the startup
    /// validator (where it reports).
    let problems (config: HttpComputeConfig) : string list = [
        if String.IsNullOrWhiteSpace config.Backend then
            "Backend label is empty — it is stamped onto every handle and a routed fleet distinguishes backends by it"

        match absoluteUrlProblem "SubmitUrl" config.SubmitUrl with
        | Some p -> p
        | None -> ()

        match absoluteUrlProblem "StatusUrlTemplate" config.StatusUrlTemplate with
        | Some p -> p
        | None -> ()

        if not (config.StatusUrlTemplate.Contains JobIdPlaceholder) then
            $"StatusUrlTemplate '%s{config.StatusUrlTemplate}' does not contain %s{JobIdPlaceholder}, so every poll would read the same URL regardless of which unit it is asking about"

        match config.Cancel with
        | None -> ()
        | Some cancel ->
            match absoluteUrlProblem "Cancel.UrlTemplate" cancel.UrlTemplate with
            | Some p -> p
            | None -> ()

            if not (cancel.UrlTemplate.Contains JobIdPlaceholder) then
                $"Cancel.UrlTemplate '%s{cancel.UrlTemplate}' does not contain %s{JobIdPlaceholder}, so a cancel could not name the unit to tear down"

            if String.IsNullOrWhiteSpace cancel.Method then
                "Cancel.Method is empty"

        match config.Auth with
        | None -> ()
        | Some auth ->
            if String.IsNullOrWhiteSpace auth.HeaderName then
                "Auth.HeaderName is empty"

            if String.IsNullOrWhiteSpace auth.SecretKey then
                "Auth.SecretKey is empty — the credential is read from ISecretStore by this key on every request"

            if not (auth.ValueFormat.Contains HttpComputeAuth.SecretPlaceholder) then
                $"Auth.ValueFormat '%s{auth.ValueFormat}' does not contain %s{HttpComputeAuth.SecretPlaceholder}, so the resolved secret would never reach the header"

        match config.Callback with
        | None -> ()
        | Some callback ->
            match absoluteUrlProblem "Callback.PublicBaseUrl" callback.PublicBaseUrl with
            | Some p -> p
            | None -> ()

            match absoluteUrlProblem "Callback.RegistrationUrlTemplate" callback.RegistrationUrlTemplate with
            | Some p -> p
            | None -> ()

            if String.IsNullOrWhiteSpace callback.UrlField then
                "Callback.UrlField is empty"

            if String.IsNullOrWhiteSpace callback.SecretField then
                "Callback.SecretField is empty"

            if String.IsNullOrWhiteSpace callback.RegistrationMethod then
                "Callback.RegistrationMethod is empty"

        match config.HealthUrl with
        | None -> ()
        | Some url ->
            match absoluteUrlProblem "HealthUrl" url with
            | Some p -> p
            | None -> ()

        for label in HttpComputeStatusMap.ambiguous config.StatusValues do
            $"status label '%s{label}' is declared under more than one class in StatusValues — which class wins would be an accident of list order deciding whether a job is reported finished"

        if List.isEmpty config.StatusValues.Succeeded then
            "StatusValues.Succeeded is empty — no status the service reports could ever be read as success"

        if config.ProgressScale <= 0.0 || Double.IsNaN config.ProgressScale then
            $"ProgressScale %g{config.ProgressScale} is not a positive number"

        if config.RequestTimeout <= TimeSpan.Zero then
            $"RequestTimeout %O{config.RequestTimeout} is not positive"
    ]

    // ── Environment binding (GP 11) ─────────────────────────────────

    [<Literal>]
    let private Prefix = "TOOLUP_EXTERNAL_COMPUTE_HTTP_"

    /// The env var selecting this companion. `fromEnv` returns `None`
    /// unless it reads `http` (case-insensitively), so a deployment that
    /// has not opted in composes nothing and pays nothing (GP 13).
    [<Literal>]
    let SelectorEnvVar = "TOOLUP_EXTERNAL_COMPUTE"

    let private env (name: string) =
        match Environment.GetEnvironmentVariable(Prefix + name) with
        | null -> None
        | value when String.IsNullOrWhiteSpace value -> None
        | value -> Some(value.Trim())

    let private csv (name: string) =
        env name
        |> Option.map (fun value ->
            value.Split ','
            |> Array.map _.Trim()
            |> Array.filter (fun part -> part <> "")
            |> List.ofArray)

    /// Parse a selector env var into `Ok (Some path)` / `Ok None` (unset)
    /// / `Error message`.
    let private selector (name: string) : Result<JsonPath option, string> =
        match env name with
        | None -> Ok None
        | Some text ->
            match JsonPath.parse text with
            | Ok path -> Ok(Some path)
            | Error e -> Error $"%s{Prefix}%s{name}: %s{e}"

    /// Read the config from the environment.
    ///
    /// `None` when this companion is not selected. `Some (Error problems)`
    /// when it IS selected but the environment does not describe a usable
    /// backend — reported rather than raised, so a composition root can
    /// surface every problem at once instead of one exception per
    /// restart.
    let fromEnv () : Result<HttpComputeConfig, string list> option =
        let selected =
            match Environment.GetEnvironmentVariable SelectorEnvVar with
            | null -> false
            | value -> value.Trim().Equals("http", StringComparison.OrdinalIgnoreCase)

        if not selected then
            None
        else
            let submitUrl = env "SUBMIT_URL" |> Option.defaultValue ""
            let statusUrl = env "STATUS_URL" |> Option.defaultValue ""

            let jobIdText = env "JOBID_SELECTOR" |> Option.defaultValue "id"
            let statusText = env "STATUS_SELECTOR" |> Option.defaultValue "status"

            let selectorResults = [
                "JOBID_SELECTOR", JsonPath.parse jobIdText |> Result.map Some
                "STATUS_SELECTOR", JsonPath.parse statusText |> Result.map Some
                "PROGRESS_SELECTOR", selector "PROGRESS_SELECTOR"
                "RESULTREF_SELECTOR", selector "RESULTREF_SELECTOR"
                "ERROR_SELECTOR", selector "ERROR_SELECTOR"
                "RETRIABLE_SELECTOR", selector "RETRIABLE_SELECTOR"
            ]

            let selectorErrors =
                selectorResults
                |> List.choose (fun (name, result) ->
                    match result with
                    | Error e when e.StartsWith Prefix -> Some e
                    | Error e -> Some $"%s{Prefix}%s{name}: %s{e}"
                    | Ok _ -> None)

            let pick name =
                selectorResults
                |> List.tryPick (fun (n, result) ->
                    if n = name then
                        match result with
                        | Ok path -> path
                        | Error _ -> None
                    else
                        None)

            if not (List.isEmpty selectorErrors) then
                Some(Error selectorErrors)
            else
                let statusValues = {
                    Pending =
                        csv "STATUS_PENDING"
                        |> Option.defaultValue HttpComputeStatusMap.defaults.Pending
                    Running =
                        csv "STATUS_RUNNING"
                        |> Option.defaultValue HttpComputeStatusMap.defaults.Running
                    Succeeded =
                        csv "STATUS_SUCCEEDED"
                        |> Option.defaultValue HttpComputeStatusMap.defaults.Succeeded
                    Failed = csv "STATUS_FAILED" |> Option.defaultValue HttpComputeStatusMap.defaults.Failed
                    Cancelled =
                        csv "STATUS_CANCELLED"
                        |> Option.defaultValue HttpComputeStatusMap.defaults.Cancelled
                }

                let auth =
                    match env "AUTH_SECRET_KEY" with
                    | None -> None
                    | Some key ->
                        Some {
                            HeaderName = env "AUTH_HEADER" |> Option.defaultValue "Authorization"
                            SecretScope = env "AUTH_SECRET_SCOPE" |> Option.defaultValue "_platform"
                            SecretKey = key
                            ValueFormat =
                                env "AUTH_VALUE_FORMAT"
                                |> Option.defaultValue ("Bearer " + HttpComputeAuth.SecretPlaceholder)
                        }

                let cancel =
                    env "CANCEL_URL"
                    |> Option.map (fun template -> {
                        UrlTemplate = template
                        Method = env "CANCEL_METHOD" |> Option.defaultValue "POST"
                    })

                let callback =
                    match env "CALLBACK_BASE_URL", env "CALLBACK_REGISTRATION_URL" with
                    | Some baseUrl, Some registrationUrl ->
                        Some {
                            PublicBaseUrl = baseUrl
                            RegistrationUrlTemplate = registrationUrl
                            RegistrationMethod = env "CALLBACK_METHOD" |> Option.defaultValue "POST"
                            UrlField = env "CALLBACK_URL_FIELD" |> Option.defaultValue "callbackUrl"
                            SecretField = env "CALLBACK_SECRET_FIELD" |> Option.defaultValue "callbackSecret"
                            HandleIdField = Some(env "CALLBACK_HANDLE_FIELD" |> Option.defaultValue "handleId")
                        }
                    | _ -> None

                let timeout =
                    match env "TIMEOUT_SECONDS" with
                    | Some raw ->
                        match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
                        | true, seconds when seconds > 0.0 -> TimeSpan.FromSeconds seconds
                        | _ -> TimeSpan.FromSeconds 30.0
                    | None -> TimeSpan.FromSeconds 30.0

                let config = {
                    Backend = env "BACKEND" |> Option.defaultValue "http"
                    SubmitUrl = submitUrl
                    StatusUrlTemplate = statusUrl
                    Cancel = cancel
                    Auth = auth
                    Submit = HttpComputeSubmitFields.defaults
                    Selectors = {
                        JobId = pick "JOBID_SELECTOR" |> Option.defaultValue (JsonPath.ofString "id")
                        Status = pick "STATUS_SELECTOR" |> Option.defaultValue (JsonPath.ofString "status")
                        Progress = pick "PROGRESS_SELECTOR"
                        ResultRef = pick "RESULTREF_SELECTOR"
                        Error = pick "ERROR_SELECTOR"
                        Retriable = pick "RETRIABLE_SELECTOR"
                    }
                    StatusValues = statusValues
                    ProgressScale =
                        match env "PROGRESS_SCALE" with
                        | Some raw ->
                            match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
                            | true, scale when scale > 0.0 -> scale
                            | _ -> 1.0
                        | None -> 1.0
                    Callback = callback
                    HealthUrl = env "HEALTH_URL"
                    RequestTimeout = timeout
                }

                match problems config with
                | [] -> Some(Ok config)
                | found -> Some(Error found)