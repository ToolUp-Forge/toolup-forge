// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Hosts.EdgeCache

open System
open System.Net.Http
open System.Text
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 472 — the reference IEdgeCache sub-companion ───────────────
//
// `HttpEdgeCache` implements `IEdgeCache` over a plain HTTP purge API.
// It exists to prove the seam FROM THE OUTSIDE (GP 12): a second
// implementation, built the way a real one would be, holding the seam to
// a bar the in-tree no-op cannot.
//
// **No vendor SDK, and no vendor knowledge (GP 1).** Commercial CDN
// purge APIs of the CloudFront / Cloudflare class share one shape — an
// authenticated HTTP call carrying a list of paths or tags as JSON —
// and differ in the URL, the auth header, and the exact JSON. Those
// three differences are supplied as DATA by the deployment:
//
//   - the endpoint URL and method, as values;
//   - the auth header name and scheme, with the credential read from
//     `ISecretStore` on every call (never an env var, never a
//     constructor-time snapshot — token rotation is the operator's
//     lever and it must take effect without a restart);
//   - the request body, as a `EdgePurgeRequest -> string` callback.
//
// So this companion carries a dependency on nobody, and a deployment
// targeting a CDN nothing here has heard of composes it unchanged.
//
// **What it deliberately does not do.** It does not retry — retry is the
// caller's, expressed as an `EdgePurgeRetry` record and applied by
// `EdgeCache.purgeDetached` (GP 12 rule 3). It does not parse the
// response body: an HTTP status is the whole contract it can honestly
// claim across vendors, and inventing a shared error taxonomy from
// response bodies it cannot see would be a guess presented as a fact.
// The status text is carried into the `EdgePurgeError` verbatim instead.

/// What is being purged. Handed to the deployment's body renderer.
type EdgePurgeRequest =
    | PurgeRequestPaths of paths: string list
    | PurgeRequestPrefix of prefix: string
    | PurgeRequestTags of tags: string list

/// How the credential is presented on the purge call. Both shapes are
/// header-based because every purge API of this class is; a scheme this
/// cannot express is a reason to write a vendor sub-companion, not to
/// widen this one into a general HTTP client.
type EdgeAuthScheme =
    /// `Authorization: Bearer <secret>`.
    | EdgeBearerToken
    /// `<headerName>: <secret>` — the API-key header shape.
    | EdgeApiKeyHeader of headerName: string

/// Where the credential lives. Read through `ISecretStore` on every
/// call, per the companion-authoring rule: a rotated token flows through
/// immediately rather than at the next restart.
type EdgeCredential = {
    SecretContainer: string
    SecretName: string
    Scheme: EdgeAuthScheme
}

/// Everything that differs between one purge API and the next.
type HttpEdgeCacheConfig = {
    /// Name reported by `IEdgeCache.Name`, and the one that appears in
    /// an audited purge failure. Name the distribution, not the vendor —
    /// an operator reading the line needs to know WHICH edge failed.
    Name: string
    /// The purge endpoint.
    Endpoint: Uri
    /// HTTP method. `POST` for most; some purge APIs use `DELETE`.
    Method: HttpMethod
    /// The credential, or `None` for an endpoint that needs none (a
    /// self-hosted cache on a private network).
    Credential: EdgeCredential option
    /// Render the request body for one purge. The vendor's JSON shape
    /// lives here, in the deployment's own code, which is what keeps
    /// this companion vendor-free.
    RenderBody: EdgePurgeRequest -> string
    /// Content type of the rendered body.
    ContentType: string
    /// What this edge's purge actually promises (GP 12 rule 6).
    /// `PurgeEventualUnbounded` unless the vendor documents a bound.
    Propagation: EdgePurgePropagation
    /// Does this endpoint accept a PREFIX purge? Many do not — they
    /// accept only exact paths, or a wildcard path that means the same
    /// thing. `false` makes `PurgePrefix` return
    /// `PurgeNotSupported "PurgePrefix"` rather than sending a request
    /// the API will reject, or worse, silently accept as a literal path.
    SupportsPrefix: bool
    /// Does this endpoint accept a TAG / surrogate-key purge?
    SupportsTags: bool
}

module HttpEdgeCacheConfig =
    /// A minimal config: a bearer-authenticated JSON POST whose body is
    /// the deployment's to render. Everything else is left at the
    /// conservative answer — no prefix support, no tag support, no
    /// propagation promise — so a deployment declares each capability it
    /// actually has rather than inheriting an optimistic default.
    let create (name: string) (endpoint: Uri) (renderBody: EdgePurgeRequest -> string) : HttpEdgeCacheConfig = {
        Name = name
        Endpoint = endpoint
        Method = HttpMethod.Post
        Credential = None
        RenderBody = renderBody
        ContentType = "application/json"
        Propagation = PurgeEventualUnbounded
        SupportsPrefix = false
        SupportsTags = false
    }

    let withCredential (credential: EdgeCredential) (config: HttpEdgeCacheConfig) = {
        config with
            Credential = Some credential
    }

    let withPrefixSupport (config: HttpEdgeCacheConfig) = { config with SupportsPrefix = true }

    let withTagSupport (config: HttpEdgeCacheConfig) = { config with SupportsTags = true }

    let withPropagation (propagation: EdgePurgePropagation) (config: HttpEdgeCacheConfig) = {
        config with
            Propagation = propagation
    }

/// `IEdgeCache` over an HTTP purge API. Construct via
/// `HttpEdgeCache.create`.
///
/// Stateless between calls (GP 12 rule 4): it holds an `HttpClient` and
/// a config, and reads the credential per call. Nothing carries over
/// from one purge to the next, so an instance is safe to share and safe
/// to recycle.
type HttpEdgeCache(httpClient: HttpClient, secretStore: ISecretStore option, config: HttpEdgeCacheConfig) =

    let resolveCredential () = async {
        match config.Credential with
        | None -> return Ok None
        | Some credential ->
            match secretStore with
            | None ->
                return
                    Error(
                        PurgeRejected
                            "a credential is configured but no ISecretStore was supplied to HttpEdgeCache.create"
                    )
            | Some store ->
                let! secret = store.GetSecret(credential.SecretContainer, credential.SecretName)

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Ok(Some(credential.Scheme, value))
                | _ ->
                    return
                        Error(
                            PurgeRejected(
                                sprintf
                                    "secret %s/%s is absent or empty — the purge endpoint would be called unauthenticated"
                                    credential.SecretContainer
                                    credential.SecretName
                            )
                        )
    }

    let send (request: EdgePurgeRequest) : Async<Result<unit, EdgePurgeError>> = async {
        match! resolveCredential () with
        | Error e -> return Error e
        | Ok credential ->
            try
                use message = new HttpRequestMessage(config.Method, config.Endpoint)

                message.Content <- new StringContent(config.RenderBody request, Encoding.UTF8, config.ContentType)

                match credential with
                | None -> ()
                | Some(EdgeBearerToken, value) ->
                    message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + value)
                    |> ignore
                | Some(EdgeApiKeyHeader headerName, value) ->
                    message.Headers.TryAddWithoutValidation(headerName, value) |> ignore

                let! response = httpClient.SendAsync message |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    return Ok()
                else
                    // 4xx is the endpoint refusing a well-formed request
                    // (credentials, an unknown distribution, a rate
                    // limit); 5xx is the endpoint failing to answer.
                    // The distinction matters to the retry policy above
                    // this, which is why it is drawn here rather than
                    // flattened into one error.
                    let detail =
                        sprintf
                            "%s returned %d %s"
                            (config.Endpoint.ToString())
                            (int response.StatusCode)
                            response.ReasonPhrase

                    if int response.StatusCode >= 500 then
                        return Error(PurgeTransportFailure detail)
                    else
                        return Error(PurgeRejected detail)
            with ex ->
                return Error(PurgeTransportFailure ex.Message)
    }

    interface IEdgeCache with
        member _.Name = config.Name
        member _.Propagation = config.Propagation

        member _.PurgePaths(paths) =
            match paths with
            | [] -> async.Return(Ok())
            | _ -> send (PurgeRequestPaths paths)

        member _.PurgePrefix(prefix) =
            if not config.SupportsPrefix then
                async.Return(Error(PurgeNotSupported "PurgePrefix"))
            elif String.IsNullOrWhiteSpace prefix then
                async.Return(Ok())
            else
                send (PurgeRequestPrefix prefix)

        member _.PurgeTags(tags) =
            if not config.SupportsTags then
                async.Return(Error(PurgeNotSupported "PurgeTags"))
            else
                match tags with
                | [] -> async.Return(Ok())
                | _ -> send (PurgeRequestTags tags)

module HttpEdgeCache =
    /// Construct the HTTP purge adapter. `secretStore` is required when
    /// the config declares a credential and ignored otherwise — the
    /// companion never reads an env var or a config file directly, per
    /// the companion-authoring rule.
    ///
    /// The `HttpClient` is supplied rather than constructed so a
    /// deployment owns its lifetime, its timeout and its handler chain
    /// (a socket-exhausting per-call client is the classic defect here,
    /// and it is not this companion's to introduce).
    let create (httpClient: HttpClient) (secretStore: ISecretStore option) (config: HttpEdgeCacheConfig) : IEdgeCache =
        HttpEdgeCache(httpClient, secretStore, config) :> IEdgeCache