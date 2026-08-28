// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Hosts.EdgeCache

open System
open ToolUp.Platform
open ToolUp.MediaLibrary

// ─── Phase 472 — the reference IDelegatedUrlSigner sub-companion ──────
//
// `CallbackUrlSigner` implements `IDelegatedUrlSigner` over a signing
// callback the deployment supplies. It is the second implementation that
// proves that seam from the outside (GP 12), and it is the shape every
// CDN-native signer takes once the vendor specifics are removed:
//
//   1. forge decides WHAT is being granted — which item, to which scope,
//      until when — and turns it into an absolute origin-relative URL
//      the edge fronts;
//   2. the deployment turns that into a signed URL with its own private
//      key, by whatever canonicalisation its CDN documents;
//   3. forge hands the result to the caller.
//
// **Step 2 is deliberately not forge's.** Signing needs a private key,
// and a private key belongs to the deployment. Modelling key
// provisioning, rotation, and per-vendor canonical-request construction
// inside the SDK would put the most security-critical code in the
// system furthest from the people who own the key — and would make the
// SDK the thing that has to be upgraded when a vendor changes its
// scheme. The callback keeps that boundary where it belongs. It is the
// same choice `ICloudTranscodeProvider` makes for transcoding.
//
// The callback is a plain function rather than an interface because it
// has exactly one method and no state; wrapping it in a type would add
// a name without adding a decision.

/// What is being granted, handed to the deployment's signing callback.
/// Every field is a value (GP 12 rule 1) — there is no live handle and
/// nothing here needs the SDK to interpret it.
type DelegatedSignRequest = {
    /// The item's opaque id.
    MediaId: string
    /// The viewing scope's id and container, so a signer can bind the
    /// tenant into its signature (or into a signed policy document)
    /// exactly as the origin HMAC does.
    ScopeId: string
    Container: string
    /// The origin-relative path the CDN fronts for this item, e.g.
    /// `/api/media/stream/abc123`.
    ResourcePath: string
    /// `BaseUrl` + `ResourcePath` — the absolute, UNSIGNED URL. Supplied
    /// so the common case (append a signature query) needs no string
    /// assembly in the callback, and so a callback that wants to sign
    /// something else can ignore it.
    UnsignedUrl: string
    /// Absolute expiry. Already rounded to the precision the signer
    /// declared (see `CallbackUrlSignerConfig.TtlPrecision`), so the
    /// callback signs the instant the SDK reported, not a different one.
    ExpiresAt: DateTimeOffset
}

type CallbackUrlSignerConfig = {
    /// Reported by `IDelegatedUrlSigner.Name`.
    Name: string
    /// The CDN origin the signed URL points at, e.g.
    /// `https://media.example.com`. A trailing slash is trimmed.
    BaseUrl: string
    /// Which origin-relative path is signed for an item. Defaults to the
    /// progressive-download route; a deployment serving HLS signs the
    /// master manifest instead, which is why this is a function and not
    /// a constant.
    ResourcePath: MediaId -> string
    /// The expiry granularity this signer's edge honours (GP 12 rule 6).
    TtlPrecision: SignedUrl.SignedUrlTtlPrecision
    /// The deployment's signing step. Returns the signed absolute URL,
    /// or a message describing why it could not sign.
    Sign: DelegatedSignRequest -> Async<Result<string, string>>
}

module CallbackUrlSignerConfig =
    /// Default resource path: the progressive-download route, which is
    /// what `IMediaLibrary.SignedUrl` has always minted for.
    let defaultResourcePath (id: MediaId) : string = "/api/media/stream/" + MediaId.value id

    /// A second-precision signer over the default resource path.
    let create (name: string) (baseUrl: string) (sign: DelegatedSignRequest -> Async<Result<string, string>>) = {
        Name = name
        BaseUrl = baseUrl
        ResourcePath = defaultResourcePath
        TtlPrecision = SignedUrl.TtlSecond
        Sign = sign
    }

    /// Sign the HLS master manifest instead of the progressive original
    /// — the right choice for a library whose renditions are HLS.
    let withResourcePath (resourcePath: MediaId -> string) (config: CallbackUrlSignerConfig) = {
        config with
            ResourcePath = resourcePath
    }

    let withTtlPrecision (precision: SignedUrl.SignedUrlTtlPrecision) (config: CallbackUrlSignerConfig) = {
        config with
            TtlPrecision = precision
    }

/// `IDelegatedUrlSigner` over a deployment-supplied signing callback.
/// Stateless between calls (GP 12 rule 4).
type CallbackUrlSigner(config: CallbackUrlSignerConfig, clock: unit -> DateTimeOffset) =

    let baseUrl = config.BaseUrl.TrimEnd('/')

    /// Round the expiry DOWN to the declared precision.
    ///
    /// Down, never up, and that is the whole reason `TtlPrecision`
    /// exists: rounding up would silently extend a grant past what the
    /// caller asked for, and a grant that outlives its request is the
    /// one rounding error in this file that has a security meaning.
    let roundExpiry (expiresAt: DateTimeOffset) =
        match config.TtlPrecision with
        | SignedUrl.TtlSecond ->
            DateTimeOffset(expiresAt.Ticks - (expiresAt.Ticks % TimeSpan.TicksPerSecond), expiresAt.Offset)
        | SignedUrl.TtlMinute ->
            DateTimeOffset(expiresAt.Ticks - (expiresAt.Ticks % TimeSpan.TicksPerMinute), expiresAt.Offset)

    interface SignedUrl.IDelegatedUrlSigner with
        member _.Name = config.Name
        member _.TtlPrecision = config.TtlPrecision

        member _.SignUrl(id: MediaId, scope: StorageScope, ttl: TimeSpan) : Async<Result<string, SignedUrlError>> = async {
            let resourcePath = config.ResourcePath id
            let expiresAt = roundExpiry (clock () + ttl)

            let request = {
                MediaId = MediaId.value id
                ScopeId = scope.ScopeId
                Container = scope.Container
                ResourcePath = resourcePath
                UnsignedUrl = baseUrl + resourcePath
                ExpiresAt = expiresAt
            }

            try
                match! config.Sign request with
                | Ok signed -> return Ok signed
                // The callback's failure is reported as a key-resolution
                // failure rather than swallowed: the caller asked for a
                // URL and there is none, and a delegated signer that
                // failed must never fall through to an origin-relative
                // URL a CDN-fronted viewer cannot reach.
                | Error message -> return Error(KeyResolutionFailed(sprintf "%s: %s" config.Name message))
            with ex ->
                return Error(KeyResolutionFailed(sprintf "%s threw: %s" config.Name ex.Message))
        }

module CallbackUrlSigner =
    /// Construct the callback signer against the system clock.
    let create (config: CallbackUrlSignerConfig) : SignedUrl.IDelegatedUrlSigner =
        CallbackUrlSigner(config, fun () -> DateTimeOffset.UtcNow) :> SignedUrl.IDelegatedUrlSigner

    /// Construct against an explicit clock — the shape a test uses to
    /// assert the expiry a callback is handed without waiting for one.
    let createWith (clock: unit -> DateTimeOffset) (config: CallbackUrlSignerConfig) : SignedUrl.IDelegatedUrlSigner =
        CallbackUrlSigner(config, clock) :> SignedUrl.IDelegatedUrlSigner