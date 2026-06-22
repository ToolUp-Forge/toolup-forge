namespace ToolUp.Stripe.Server

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading.Tasks

/// Shared `HttpClient` request helper for the thin Stripe REST wrappers
/// (`CustomerPortal`, `Checkout`). BCL `HttpClient` only — Stripe's raw
/// HTTP surface for these endpoints is small enough that owning the
/// request shape per call site is cheaper than absorbing the multi-MB
/// `Stripe.net` dependency into the companion (GP 1). A consumer wanting
/// the richer Stripe client API consumes `Stripe.net` directly alongside
/// this package.
module StripeHttp =
    [<Literal>]
    let ApiBase = "https://api.stripe.com"

    // One shared client per process — the documented BCL reuse pattern
    // (avoids socket exhaustion from per-call construction). Tests inject
    // their own client through the `*With` wrapper variants.
    let private sharedClient = lazy (new HttpClient())

    /// The process-shared `HttpClient` used by the default wrapper
    /// entry points.
    let shared () : HttpClient = sharedClient.Value

    /// Extract Stripe's error message from a non-2xx body without
    /// echoing any secret material. Stripe error bodies are
    /// `{ "error": { "message", "type", ... } }`.
    let private describeError (status: int) (body: string) : string =
        try
            use doc = JsonDocument.Parse body

            match doc.RootElement.TryGetProperty "error" with
            | true, err ->
                match err.TryGetProperty "message" with
                | true, m when m.ValueKind = JsonValueKind.String ->
                    sprintf "Stripe API error (%d): %s" status (m.GetString())
                | _ -> sprintf "Stripe API error (%d)" status
            | _ -> sprintf "Stripe API error (%d)" status
        with _ ->
            sprintf "Stripe API error (%d)" status

    /// POST a form-encoded body to a Stripe endpoint with bearer auth.
    /// Returns the parsed JSON root (cloned, so it survives document
    /// disposal) on 2xx; a structured `Error` carrying Stripe's message
    /// (never the API key) on non-2xx or network failure.
    let postForm
        (client: HttpClient)
        (apiKey: string)
        (path: string)
        (form: (string * string) list)
        : Task<Result<JsonElement, string>> =
        task {
            try
                use req = new HttpRequestMessage(HttpMethod.Post, ApiBase + path)
                req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey)

                req.Content <-
                    new FormUrlEncodedContent(form |> List.map (fun (k, v) -> KeyValuePair<string, string>(k, v)))

                let! resp = client.SendAsync req
                let! body = resp.Content.ReadAsStringAsync()

                if resp.IsSuccessStatusCode then
                    try
                        use doc = JsonDocument.Parse body
                        return Ok(doc.RootElement.Clone())
                    with ex ->
                        return Error(sprintf "Stripe response parse error: %s" ex.Message)
                else
                    return Error(describeError (int resp.StatusCode) body)
            with ex ->
                return Error(sprintf "Stripe request failed: %s" ex.Message)
        }

    /// GET a Stripe endpoint with bearer auth. Returns the parsed JSON
    /// root (cloned) on 2xx; a structured `Error` carrying Stripe's
    /// message (never the API key) on non-2xx or network failure.
    /// Phase 240 — consumed by `StripeBillingProvider.GetSubscriptionStatus`.
    let getJson (client: HttpClient) (apiKey: string) (path: string) : Task<Result<JsonElement, string>> = task {
        try
            use req = new HttpRequestMessage(HttpMethod.Get, ApiBase + path)
            req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey)
            let! resp = client.SendAsync req
            let! body = resp.Content.ReadAsStringAsync()

            if resp.IsSuccessStatusCode then
                try
                    use doc = JsonDocument.Parse body
                    return Ok(doc.RootElement.Clone())
                with ex ->
                    return Error(sprintf "Stripe response parse error: %s" ex.Message)
            else
                return Error(describeError (int resp.StatusCode) body)
        with ex ->
            return Error(sprintf "Stripe request failed: %s" ex.Message)
    }

    /// Read an absolute-URL string field from a Stripe JSON response.
    let readUri (root: JsonElement) (field: string) : Result<Uri, string> =
        match root.TryGetProperty field with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match Uri.TryCreate(v.GetString(), UriKind.Absolute) with
            | true, uri -> Ok uri
            | _ -> Error(sprintf "Stripe response field '%s' was not an absolute URL" field)
        | _ -> Error(sprintf "Stripe response missing string field '%s'" field)