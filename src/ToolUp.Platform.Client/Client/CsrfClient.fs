// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module CsrfClient

open Fable.Core
open Fable.Core.JsInterop

// ─── Phase 9j / Phase 13a — client-side request-header seam ──────────
//
// This module owns the SINGLE place the client attaches per-request
// dynamic headers. `installRequestGuard` wraps both the
// `XMLHttpRequest` transport (Fable.Remoting's only transport) AND
// `window.fetch` (used by the AI client-tool-result / cancel /
// audit-beacon POSTs), reading the live caches at *send* time. It is
// therefore correct no matter how — or whether — the calling proxy or
// closure was constructed.
//
// It replaced the old `UserSession.withRequestHeaders` splice:
// `Remoting.withCustomHeader` freezes its header list at proxy-build
// time, so any module-level proxy built before sign-in / before the
// async CSRF prefetch resolved silently lost BOTH the identity header
// and the CSRF header (401 / 403 under `DefaultSecurityHardening`).
//
// Phase 13a: `installRequestGuard` now takes the seam getters
// (`identityGetter`, `apiOriginGetter`) as explicit parameters rather
// than reading from module-level mutables. The CSRF token cache stays
// here (it's a fetch-result cache, not a registry) and is read via
// the local `tokenOrEmpty` helper. The caller (`SDK.Client.program`)
// composes the identity getter from
// `UserSession.identityHeaderPairs` + `config.RequestSeam.HeaderProviders`
// and supplies the api-origin getter from `config.RequestSeam.ApiOrigin`.
//
//  * Identity (`Authorization` / `X-User-Id`) is attached on every
//    eligible request, GET included (reads are authenticated too).
//  * `X-CSRF-Token` is attached only on state-changing methods.
//  * Eligibility: path starts with `/api/` AND origin is same-origin
//    OR the explicitly-configured API origin. A `/api/`-looking
//    request to an excluded origin emits a one-time `console.warn`
//    instead of failing silently.
//  * Headers are added only when absent; the original transport is
//    always invoked; never throws; idempotent via sentinels.
//
// Zero-impact under `NoSecurityHardening`: the `/api/csrf-token` route
// is unmounted, the cache stays `None`, no `X-CSRF-Token` is added,
// and the identity pairs are exactly what the old splice sent (GP 13).
//
// Module-level mutables here are sanctioned process-singleton state:
//   * `cachedToken` / `inFlight` — fetch-result cache + shared
//     in-flight Promise so multiple `ensure()` callers share one
//     `/api/csrf-token` round-trip.
//   * `guardInstalled` — one-time-install sentinel for the XHR /
//     fetch wrappers.
// These are sanctioned process-singleton state, not registry-style
// mutables — `setIdentityProvider` and `setApiOrigin` (the legacy
// registry seams) have been replaced by parameters on
// `installRequestGuard`.

/// Header the server's `CsrfMiddleware` validates.
[<Literal>]
let HeaderName = "X-CSRF-Token"

let mutable private cachedToken: string option = None

/// The cached token, if the startup pre-fetch succeeded.
let currentToken () : string option = cachedToken

/// Header pairs to splice into `Remoting.withCustomHeader`. Empty
/// until/unless a token has been fetched.
let headerPairs () : (string * string) list =
    match cachedToken with
    | Some t -> [ HeaderName, t ]
    | None -> []

[<Emit("JSON.parse($0)")>]
let private jsonParse (s: string) : obj = jsNative

// Same-origin GET so the ASP.NET session cookie rides along. Resolves
// to the JSON body string on 2xx, or `null` on any non-OK / network
// failure (then the cache simply stays None — fail-safe).
[<Emit("fetch('/api/csrf-token', { credentials: 'same-origin', headers: { 'Accept': 'application/json' } }).then(function(r){ return r.ok ? r.text() : null; }).catch(function(){ return null; })")>]
let private fetchTokenRaw () : JS.Promise<string> = jsNative

let private cache (raw: string) : unit =
    try
        if not (isNull (box raw)) && raw <> "" then
            match (jsonParse raw)?Token with
            | null -> ()
            | t ->
                let s = string t

                if not (System.String.IsNullOrEmpty s) then
                    cachedToken <- Some s
    with _ ->
        ()

// One shared in-flight fetch. A JS promise is multicast, so the eager
// `prefetch` warm-up and a later awaited `ensure` resolve from the same
// single `/api/csrf-token` round-trip.
let mutable private inFlight: JS.Promise<string> option = None

let private fetchOnce () : JS.Promise<string> =
    match inFlight with
    | Some p -> p
    | None ->
        let p = fetchTokenRaw ()
        inFlight <- Some p
        p

/// Await the per-session CSRF token fetch. Completes once the token is
/// cached or the fetch has definitively failed (cache then stays `None`
/// and no header is attached — the `NoSecurityHardening` default is
/// unchanged). Safe to await from several boot loaders; they share one
/// round-trip. Never throws.
let ensure () : Async<unit> = async {
    if Option.isNone cachedToken then
        try
            let! raw = fetchOnce () |> Async.AwaitPromise
            cache raw
        with _ ->
            ()
}

let private tokenOrEmpty () : string =
    match cachedToken with
    | Some t -> t
    | None -> ""

let mutable private guardInstalled = false

// One-time wrap of BOTH `XMLHttpRequest.prototype.{open,send}` and
// `window.fetch`. $0 = CSRF-token getter, $1 = identity-pairs getter
// (`[[k,v],...]`), $2 = configured-API-origin getter ("" = unset),
// $3 = per-request correlation-id getter (returns fresh string per call).
// Reads the getters at send time, so it is correct regardless of when
// the calling proxy/closure was built. Never throws; the original
// transport is always invoked; idempotent via sentinels.
[<Emit("""(function(getTok, getIdent, getApiOrigin, getCorrId){
  var WARNED = {};
  function warnOnce(k, msg){ if (!WARNED[k]) { WARNED[k] = true; try { console.warn('[ToolUp request-guard] ' + msg); } catch(e){} } }
  function eligible(method, urlStr){
    var u;
    try { u = new URL(String(urlStr), window.location.href); } catch(e){ return null; }
    if (u.pathname.indexOf('/api/') !== 0) { return null; }
    var ao = '';
    try { ao = getApiOrigin() || ''; } catch(e){}
    if (u.origin === window.location.origin || (ao && u.origin === ao)) {
      return { method: String(method || 'GET').toUpperCase() };
    }
    warnOnce('xorigin', 'request to ' + u.origin + u.pathname + ' is not same-origin and no matching ClientConfig.RequestSeam.ApiOrigin is configured; identity/CSRF headers were NOT attached. If this is a split-origin SPA/API deployment, set ClientConfig.RequestSeam.ApiOrigin from the composition root.');
    return null;
  }
  function applyHeaders(ctx, hasHeader, setHeader){
    var pairs;
    try { pairs = getIdent() || []; } catch(e){ pairs = []; }
    for (var i = 0; i < pairs.length; i++) {
      var k = pairs[i][0], v = pairs[i][1];
      if (k && v != null && !hasHeader(k)) { try { setHeader(k, v); } catch(e){} }
    }
    // 0.4.1 — x-correlation-id attached to every eligible request so
    // server-side observability (Giraffe adapter reads the header per
    // Phase 69b.D) can stitch client -> server traces. Each call to
    // getCorrId() returns a fresh value (the SDK default generates a
    // GUID per request; consumers can override via
    // ClientConfig.RequestSeam.CorrelationIdProvider).
    if (!hasHeader('x-correlation-id')) {
      var corrId = '';
      try { corrId = getCorrId() || ''; } catch(e){}
      if (corrId) { try { setHeader('x-correlation-id', corrId); } catch(e){} }
    }
    if (ctx.method === 'POST' || ctx.method === 'PUT' || ctx.method === 'PATCH' || ctx.method === 'DELETE') {
      var tok = '';
      try { tok = getTok() || ''; } catch(e){}
      if (tok) { if (!hasHeader('X-CSRF-Token')) { try { setHeader('X-CSRF-Token', tok); } catch(e){} } }
      else { warnOnce('notoken', 'a state-changing /api request was issued before the CSRF token resolved; if it 403s this is a startup race (the boot prefetch normally wins). A manual retry will succeed.'); }
    }
  }

  if (typeof XMLHttpRequest !== 'undefined' && !XMLHttpRequest.prototype.__toolupReqGuard) {
    var P = XMLHttpRequest.prototype;
    P.__toolupReqGuard = true;
    var rawOpen = P.open;
    var rawSend = P.send;
    P.open = function(){ try { this.__tuMethod = arguments[0]; this.__tuUrl = arguments[1]; this.__tuSet = {}; } catch (e) {} return rawOpen.apply(this, arguments); };
    P.send = function(){
      try {
        var ctx = eligible(this.__tuMethod, this.__tuUrl);
        if (ctx) {
          var self = this;
          applyHeaders(ctx,
            function(k){ return !!(self.__tuSet && self.__tuSet[k.toLowerCase()]); },
            function(k, v){ if (self.__tuSet) { self.__tuSet[k.toLowerCase()] = true; } self.setRequestHeader(k, v); });
        }
      } catch (e) {}
      return rawSend.apply(this, arguments);
    };
  }

  if (typeof window !== 'undefined' && typeof window.fetch === 'function' && !window.fetch.__toolupReqGuard) {
    var rawFetch = window.fetch;
    var wrapped = function(input, init){
      try {
        var isReq = input && typeof input === 'object' && 'url' in input;
        var url = isReq ? input.url : input;
        var method = (init && init.method) || (isReq && input.method) || 'GET';
        var ctx = eligible(method, url);
        if (ctx) {
          var h = new Headers((init && init.headers) || (isReq && input.headers) || undefined);
          applyHeaders(ctx, function(k){ return h.has(k); }, function(k, v){ h.set(k, v); });
          var newInit = Object.assign({}, init || {}, { headers: h });
          return rawFetch.call(this, input, newInit);
        }
      } catch (e) {}
      return rawFetch.apply(this, arguments);
    };
    wrapped.__toolupReqGuard = true;
    window.fetch = wrapped;
  }
})($0, $1, $2, $3)""")>]
let private installGuardJs
    (tokenGetter: unit -> string)
    (identityGetter: unit -> (string * string)[])
    (apiOriginGetter: unit -> string)
    (correlationGetter: unit -> string)
    : unit =
    jsNative

/// Install the one-time request-guard — the SINGLE client-side seam
/// for per-request dynamic headers (identity on every eligible
/// `/api/*` request, `X-CSRF-Token` on state-changing ones), over
/// both XHR and `fetch`. Reads the live caches at send time, so every
/// proxy/closure is correct regardless of when it was constructed.
///
/// Phase 13a — `identityGetter` and `apiOriginGetter` are now passed
/// in by the caller (`SDK.Client.program`) instead of read from
/// module-level mutables. The composition is:
///   * `identityGetter` = `UserSession.identityHeaderPairs ++
///     each provider in config.RequestSeam.HeaderProviders`
///   * `apiOriginGetter` = `config.RequestSeam.ApiOrigin >> Option.defaultValue ""`
///
/// SECURITY: this wrapper IS the CSRF synchroniser-token (and
/// identity) delivery path; see SECURITY.md.
///
/// Idempotent: subsequent calls are no-ops (the install sentinel
/// inside `installGuardJs` guards against double-wrapping).
let installRequestGuard
    (identityGetter: unit -> (string * string)[])
    (apiOriginGetter: unit -> string)
    (correlationGetter: unit -> string)
    : unit =
    if not guardInstalled then
        guardInstalled <- true

        try
            installGuardJs tokenOrEmpty identityGetter apiOriginGetter correlationGetter
        with _ ->
            ()

/// Fire the one-shot token fetch eagerly. `SDK.Client.program` calls
/// this immediately after `installRequestGuard` to shrink the startup
/// race to the pre-first-paint window. Idempotent; never throws.
let prefetch () : unit =
    try
        ensure () |> Async.StartImmediate
    with _ ->
        ()