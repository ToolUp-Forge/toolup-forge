// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Common

open System
open ToolUp.Platform
open ToolUp.Platform.Transport

// ─── Phase 128 — typed paged fetch over IHttpTransport ───────────
//
// The paging loop every third-party API connector re-derives: ask for a
// page, decode it into typed rows, follow the provider's continuation,
// stop at the end — and, when a page fails, keep what the earlier pages
// produced and say exactly where to resume. A failure on page 7 is a
// structured `PageError` beside six pages of rows, never an exception
// that throws the six away.
//
// Three continuation styles cover the providers in the wild: an opaque
// continuation token (Google, Stripe `starting_after`, HubSpot `after`),
// a numeric offset (classic `offset`/`limit` APIs), and a next-page URL
// (`Link: rel="next"`, Microsoft Graph `@odata.nextLink`). The connector
// owns the mapping between its wire and a `PageCursor`; this module owns
// the loop, the retry policy (data — GP 12 rule 3), the stall guard and
// the per-run page cap.
//
// Six-rule portability audit (GP 12):
//   1. Identity by value  — `PageCursor` is a value; `encode`/`tryDecode`
//                           round-trip it through a string for persistence.
//   2. Async at boundary  — every fetch returns `Async<_>`.
//   3. Retry as data      — every endpoint carries its resolved `RetryPolicy`;
//                           failures are `PageFailure` values.
//   4. Stateless          — the loop holds nothing between runs; a run
//                           resumes from whatever cursor it is handed.
//   5. No cross-shard order — pages of one resource are fetched in order;
//                           nothing is promised across resources.
//   6. Precision          — n/a (no time semantics).

/// Where a paged read is, in the provider's own continuation vocabulary.
[<RequireQualifiedAccess>]
type PageCursor =
    /// The first page — no continuation yet.
    | First
    /// An opaque continuation token the provider returned.
    | Token of token: string
    /// A numeric offset into the result set.
    | Offset of offset: int
    /// An absolute next-page URL the provider returned.
    | NextUrl of url: string

[<RequireQualifiedAccess>]
module PageCursor =

    /// A stable string form, for persisting a resume point.
    let encode (cursor: PageCursor) : string =
        match cursor with
        | PageCursor.First -> "first"
        | PageCursor.Token token -> "token:" + token
        | PageCursor.Offset offset -> "offset:" + string offset
        | PageCursor.NextUrl url -> "url:" + url

    /// The inverse of `encode`; `None` for a string `encode` never wrote.
    let tryDecode (text: string) : PageCursor option =
        let after (prefix: string) = text.Substring prefix.Length

        if text = "first" then
            Some PageCursor.First
        elif text.StartsWith("token:", StringComparison.Ordinal) then
            Some(PageCursor.Token(after "token:"))
        elif text.StartsWith("offset:", StringComparison.Ordinal) then
            match Int32.TryParse(after "offset:") with
            | true, offset when offset >= 0 -> Some(PageCursor.Offset offset)
            | _ -> None
        elif text.StartsWith("url:", StringComparison.Ordinal) then
            Some(PageCursor.NextUrl(after "url:"))
        else
            None

/// One decoded page: its typed rows and the continuation, `None` on the
/// last page.
type Page<'Row> = {
    Rows: 'Row list
    Next: PageCursor option
}

/// Why one page could not be taken.
[<RequireQualifiedAccess>]
type PageFailure =
    /// The connector could not build the request (a configuration problem;
    /// nothing was sent).
    | Request of message: string
    /// The call failed — after the retry policy, where it was retry-worthy.
    | Transport of error: TransportError
    /// The response arrived but did not decode into a page.
    | Decode of message: string
    /// The consumer of the page refused it (a sink or cursor-store failure).
    | Consume of message: string
    /// The provider returned the cursor it was asked for as the next one —
    /// following it would loop forever.
    | Stalled of cursor: PageCursor

/// A failed page, located: its 0-based index within this run and the
/// cursor that was being fetched.
type PageError = {
    PageIndex: int
    Cursor: PageCursor
    Failure: PageFailure
}

/// The result of a paged run. Everything taken before a failure is kept.
type PagedOutcome<'State> = {
    /// The folded state over every page successfully taken.
    State: 'State
    /// How many pages were taken.
    Pages: int
    /// The page that failed, if one did. The run stops at the first failure.
    Failure: PageError option
    /// Where the next run starts: `None` when the resource is exhausted,
    /// otherwise the failing cursor (after a failure) or the next cursor
    /// (after the page cap).
    ResumeFrom: PageCursor option
}

/// How one paged resource is read: build the request for a cursor,
/// decode a success response into a page (both pure), and the retry
/// policy every page call runs under. For a connector, build it with
/// `ExternalApi.endpoint`, which resolves the policy in one place.
type PagedEndpoint<'Row> = {
    Request: PageCursor -> Result<HttpRequest, string>
    Decode: HttpResponse -> Result<Page<'Row>, string>
    Retry: RetryPolicy
}

/// Run options, as data.
type PagedFetchOptions = {
    /// A per-run retry override. `None` (the default) defers to the
    /// connector's declared policy, then to `RetryPolicy.defaults` — see
    /// `ExternalApi.effectiveRetry`, the only reader of this field.
    Retry: RetryPolicy option
    /// Stop after this many pages in one run (resumable through
    /// `ResumeFrom`). `None` reads to the end.
    MaxPages: int option
}

[<RequireQualifiedAccess>]
module PagedFetchOptions =
    /// No retry override, no page cap.
    let defaults: PagedFetchOptions = { Retry = None; MaxPages = None }

[<RequireQualifiedAccess>]
module PageFailure =

    /// Whether a later run could take the page (the transport may recover,
    /// a quota may refill, a sink may come back). Request, decode and stall
    /// failures repeat identically.
    let isTransient (failure: PageFailure) : bool =
        match failure with
        | PageFailure.Transport(TransportError.Permanent _) -> false
        | PageFailure.Transport _
        | PageFailure.Consume _ -> true
        | PageFailure.Request _
        | PageFailure.Decode _
        | PageFailure.Stalled _ -> false

    /// Human-readable rendering.
    let toMessage (failure: PageFailure) : string =
        match failure with
        | PageFailure.Request message -> $"Could not build the page request: {message}"
        | PageFailure.Transport error -> TransportError.toMessage error
        | PageFailure.Decode message -> $"Could not decode the page: {message}"
        | PageFailure.Consume message -> $"The page was not consumed: {message}"
        | PageFailure.Stalled cursor -> $"The provider returned the same cursor again ({PageCursor.encode cursor})"

module PagedFetch =

    /// Fetch and decode one page, under the endpoint's retry policy.
    let page
        (transport: IHttpTransport)
        (endpoint: PagedEndpoint<'Row>)
        (cursor: PageCursor)
        : Async<Result<Page<'Row>, PageFailure>> =
        async {
            match endpoint.Request cursor with
            | Error message -> return Error(PageFailure.Request message)
            | Ok request ->
                let! sent = HttpCall.send endpoint.Retry transport request

                match sent with
                | Error error -> return Error(PageFailure.Transport error)
                | Ok response ->
                    match endpoint.Decode response with
                    | Error message -> return Error(PageFailure.Decode message)
                    | Ok decoded ->
                        match decoded.Next with
                        | Some next when next = cursor -> return Error(PageFailure.Stalled cursor)
                        | _ -> return Ok decoded
        }

    /// Fold every page from `start` through `folder`, stopping at the last
    /// page, the first failure, or the page cap. `folder` sees each page
    /// once, in order; its `Error` stops the run as `PageFailure.Consume`
    /// with the page's cursor as the resume point, so a consumer that did
    /// not take a page gets it again next run.
    let fold
        (options: PagedFetchOptions)
        (transport: IHttpTransport)
        (endpoint: PagedEndpoint<'Row>)
        (folder: 'State -> Page<'Row> -> Async<Result<'State, string>>)
        (state: 'State)
        (start: PageCursor)
        : Async<PagedOutcome<'State>> =
        let failed state pages cursor failure = {
            State = state
            Pages = pages
            Failure =
                Some {
                    PageIndex = pages
                    Cursor = cursor
                    Failure = failure
                }
            ResumeFrom = Some cursor
        }

        let rec loop state pages cursor = async {
            match options.MaxPages with
            | Some cap when pages >= cap ->
                return {
                    State = state
                    Pages = pages
                    Failure = None
                    ResumeFrom = Some cursor
                }
            | _ ->
                let! fetched = page transport endpoint cursor

                match fetched with
                | Error failure -> return failed state pages cursor failure
                | Ok taken ->
                    let! folded = folder state taken

                    match folded with
                    | Error message -> return failed state pages cursor (PageFailure.Consume message)
                    | Ok next ->
                        match taken.Next with
                        | None ->
                            return {
                                State = next
                                Pages = pages + 1
                                Failure = None
                                ResumeFrom = None
                            }
                        | Some continuation -> return! loop next (pages + 1) continuation
        }

        loop state 0 start

    /// Every row from `start`, in provider order — plus, on a failure, the
    /// structured error and the resume point beside the rows already taken.
    let all
        (options: PagedFetchOptions)
        (transport: IHttpTransport)
        (endpoint: PagedEndpoint<'Row>)
        (start: PageCursor)
        : Async<PagedOutcome<'Row list>> =
        async {
            let! outcome =
                fold options transport endpoint (fun pages page -> async { return Ok(page.Rows :: pages) }) [] start

            return {
                State = outcome.State |> List.rev |> List.concat
                Pages = outcome.Pages
                Failure = outcome.Failure
                ResumeFrom = outcome.ResumeFrom
            }
        }