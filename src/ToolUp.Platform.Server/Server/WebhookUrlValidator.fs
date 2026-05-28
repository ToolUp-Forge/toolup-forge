module ToolUp.Platform.WebhookUrlValidator

open System
open System.Net
open System.Net.Sockets

// ─── Gap audit pass-2 #1 — Webhook URL SSRF defence ─────────────────
//
// `WebhookDispatcher` issues authenticated POST requests to
// tenant-supplied URLs. Without validation, any tenant can register
// a webhook URL pointing at the cloud-host's internal services
// (loopback `127.0.0.1`, IMDS `169.254.169.254`, RFC1918 private
// ranges) and exfiltrate metadata via the webhook delivery log.
// HMAC payload signing (already shipped) only proves origin to the
// receiver — it does not defend the SDK from being weaponised as
// the SSRF vehicle.
//
// This module is the registration-time URL guard. Called from
// `WebhookApiHandler.CreateSubscription` BEFORE the URL hits the
// registry. Refusal returns a clear `Error string` to the registrar
// (HTTP 400 via Fable.Remoting).
//
// Validation pipeline:
//   1. Parse as absolute URI; refuse non-parseable strings.
//   2. Refuse non-HTTP(S) schemes (file://, ftp://, gopher://, etc.).
//   3. Resolve hostname to IP address(es) via DNS.
//   4. For each resolved IP, refuse if it falls in:
//        * loopback (127.0.0.0/8 / ::1)
//        * link-local (169.254.0.0/16 / fe80::/10) — includes IMDS
//        * private RFC1918 (10/8, 172.16/12, 192.168/16)
//        * unique-local IPv6 (fc00::/7)
//        * IPv4-mapped IPv6 of any of the above
//        * `0.0.0.0` / `::` (this-network)
//   5. Allowlist beats deny: a host that exact-matches an entry in
//      `WebhookUrlAllowList` skips checks 3–4 entirely (legitimate
//      use case: staging deployments POSTing to internal services).
//
// Known limitation: DNS rebinding. An attacker could register
// `slow.evil.com` resolving to `1.2.3.4` (passes), then update DNS
// to `127.0.0.1` between registration and delivery. The dispatcher
// does its own DNS resolution at delivery time. Mitigating DNS
// rebinding is the dispatcher's responsibility (a future
// improvement: bind the resolved IP at registration into the
// subscription record and pass it to HttpClient as
// `IPEndPoint`-targeted URI, refusing if the IP changes). Out of
// scope for this fix; SSRF defence is "good enough" for the common
// cases that matter today.

/// Configurable allowlist + behavioural knobs. `Hosts` are matched
/// case-insensitively against the URL's host component (`Uri.Host`).
/// Empty list = allowlist disabled (every host runs through full
/// IP-range checks). Matching is exact-host, not pattern — the
/// operator names exactly the internal hosts they intend to reach.
type WebhookUrlPolicy = { AllowedHosts: string list }

module WebhookUrlPolicy =
    let empty: WebhookUrlPolicy = { AllowedHosts = [] }

let private isHostAllowed (policy: WebhookUrlPolicy) (host: string) : bool =
    policy.AllowedHosts
    |> List.exists (fun allowed -> String.Equals(allowed, host, StringComparison.OrdinalIgnoreCase))

/// Classify a single IP. Returns `Some reason` when the address falls
/// in a deny range; `None` when it's a routable public IP. Recursive
/// because the IPv4-mapped IPv6 case (`::ffff:0:0/96`) extracts the
/// embedded IPv4 and re-classifies.
let rec private classifyAddress (addr: IPAddress) : string option =
    if IPAddress.IsLoopback addr then
        Some "loopback (127.0.0.0/8 or ::1)"
    elif addr.AddressFamily = AddressFamily.InterNetwork then
        let bytes = addr.GetAddressBytes()
        // 0.0.0.0/8 — "this network"
        if bytes[0] = 0uy then
            Some "this-network (0.0.0.0/8)"
        // 10.0.0.0/8
        elif bytes[0] = 10uy then
            Some "private RFC1918 (10.0.0.0/8)"
        // 169.254.0.0/16 — link-local; covers AWS / Azure / GCP IMDS
        elif bytes[0] = 169uy && bytes[1] = 254uy then
            Some "link-local (169.254.0.0/16) — includes cloud instance-metadata service"
        // 172.16.0.0/12 (172.16 - 172.31)
        elif bytes[0] = 172uy && bytes[1] >= 16uy && bytes[1] <= 31uy then
            Some "private RFC1918 (172.16.0.0/12)"
        // 192.168.0.0/16
        elif bytes[0] = 192uy && bytes[1] = 168uy then
            Some "private RFC1918 (192.168.0.0/16)"
        else
            None
    elif addr.AddressFamily = AddressFamily.InterNetworkV6 then
        let bytes = addr.GetAddressBytes()
        // ::/128 — unspecified
        if bytes |> Array.forall (fun b -> b = 0uy) then
            Some "unspecified (::)"
        // fe80::/10 — link-local
        elif bytes[0] = 0xFEuy && (bytes[1] &&& 0xC0uy) = 0x80uy then
            Some "link-local (fe80::/10)"
        // fc00::/7 — unique-local
        elif (bytes[0] &&& 0xFEuy) = 0xFCuy then
            Some "unique-local IPv6 (fc00::/7)"
        // ::ffff:0:0/96 — IPv4-mapped — recurse on the embedded IPv4
        elif
            bytes[0..9] |> Array.forall (fun b -> b = 0uy)
            && bytes[10] = 0xFFuy
            && bytes[11] = 0xFFuy
        then
            classifyAddress (IPAddress(bytes[12..15]))
        else
            None
    else
        // Unknown address family — be conservative.
        Some(sprintf "unknown address family (%A)" addr.AddressFamily)

/// Validate a tenant-supplied webhook URL against the SSRF policy.
/// Returns `Ok ()` when the URL is safe to register; `Error reason`
/// when the URL is structurally invalid, uses a forbidden scheme, or
/// resolves to a private/loopback/link-local address.
///
/// DNS resolution happens synchronously here; expect ~10–500 ms
/// latency on first registration of a previously-unseen hostname.
/// Subsequent registrations of the same hostname hit the OS resolver
/// cache. Acceptable for a one-time admin-UI action; bulk registration
/// flows should batch + cache externally.
let validate (policy: WebhookUrlPolicy) (url: string) : Result<unit, string> =
    if String.IsNullOrWhiteSpace url then
        Error "TargetUrl is empty"
    else
        match Uri.TryCreate(url, UriKind.Absolute) with
        | false, _ -> Error(sprintf "TargetUrl '%s' is not a valid absolute URI" url)
        | true, uri ->
            if uri.Scheme <> Uri.UriSchemeHttp && uri.Scheme <> Uri.UriSchemeHttps then
                Error(sprintf "TargetUrl scheme '%s' is not permitted; webhook URLs must use http or https" uri.Scheme)
            elif isHostAllowed policy uri.Host then
                // Operator-supplied allowlist — skip IP-range checks.
                // The operator has explicitly named this host as a
                // legitimate target (typically an internal staging
                // service or a known mock host).
                Ok()
            else
                try
                    let addresses = Dns.GetHostAddresses uri.Host

                    if Array.isEmpty addresses then
                        Error(sprintf "TargetUrl host '%s' did not resolve to any IP address" uri.Host)
                    else
                        // Refuse if ANY resolved address is in a deny range.
                        // A single-address tenant can't hide a private IP
                        // behind a multi-A-record DNS entry.
                        let firstDenied =
                            addresses
                            |> Array.tryPick (fun addr ->
                                match classifyAddress addr with
                                | Some reason -> Some(addr, reason)
                                | None -> None)

                        match firstDenied with
                        | Some(addr, reason) ->
                            Error(
                                sprintf
                                    "TargetUrl host '%s' resolves to %s — %s. Webhook URLs must point at routable public addresses to prevent SSRF against internal services. If this is an intentional internal target (staging deployment, internal mock), add the host to ServerConfig.WebhookUrlAllowedHosts."
                                    uri.Host
                                    (string addr)
                                    reason
                            )
                        | None -> Ok()
                with ex ->
                    Error(sprintf "TargetUrl host '%s' resolution failed: %s" uri.Host ex.Message)