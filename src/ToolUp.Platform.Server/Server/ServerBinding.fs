// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1000 — where a forge server's HTTP listener binds, which setting
/// decided it, and the preflight that refuses a deployed server left on the
/// loopback default.
///
/// Until 0.26.0 the host builder hard-coded `UseUrls("http://0.0.0.0:<port>")`:
/// every forge server, local runs and test runs included, listened on every
/// interface, and `ASPNETCORE_URLS` had no effect because a URL set in code
/// overrides it. The listen address is now configuration, resolved in this
/// precedence (first match wins):
///
///   1. `Kestrel:Endpoints:*` configuration (`Kestrel__Endpoints__<name>__Url`)
///      — ASP.NET Core binds those and ignores every URL setting below;
///   2. the `urls` host setting — `ASPNETCORE_URLS`, `DOTNET_URLS`, an `urls`
///      key in `appsettings.json`, or a launch profile's `applicationUrl`;
///   3. `SERVER_BIND_ADDRESS`, then `ServerConfig.BindAddress`, on
///      `SERVER_PORT` / `ServerConfig.Port`;
///   4. nothing configured: loopback `127.0.0.1` on that port.
///
/// Forge calls `UseUrls` only for rungs 3 and 4, so rungs 1 and 2 behave
/// exactly as ASP.NET Core documents them.
module ToolUp.Platform.ServerBinding

open System
open System.Net
open Microsoft.Extensions.Configuration

/// The address an unconfigured forge server listens on.
[<Literal>]
let DefaultBindAddress = "127.0.0.1"

/// The environment variable that overrides `ServerConfig.BindAddress`. Like
/// `SERVER_PORT` it carries no `TOOLUP_` prefix: orchestrators and launchers
/// inject the pair together.
[<Literal>]
let BindAddressEnvVar = "SERVER_BIND_ADDRESS"

/// Which setting decided where the HTTP listener binds, in precedence order.
type ListenSource =
    /// `Kestrel:Endpoints:*` configuration. ASP.NET Core binds these
    /// endpoints and ignores the `urls` setting and forge's address.
    | KestrelEndpoints
    /// The `urls` host setting (`ASPNETCORE_URLS`, `DOTNET_URLS`, an `urls`
    /// configuration key, a launch profile's `applicationUrl`).
    | HostUrls
    /// `SERVER_BIND_ADDRESS`, else `ServerConfig.BindAddress`.
    | ConfiguredBindAddress
    /// Nothing configured: the loopback default.
    | DefaultLoopback

/// The listener's resolved binding: the deciding setting and the URLs it
/// names. Forge applies `Urls` with `UseUrls` only when `Source` is
/// `ConfiguredBindAddress` or `DefaultLoopback`.
type ListenPlan = {
    Source: ListenSource
    Urls: string list
}

/// Validate a bind address: an IPv4 / IPv6 literal (IPv6 with or without
/// brackets) or `localhost`. Returns the canonical form, or the reason it
/// was refused. A bare integer (`"1"`, which `IPAddress.TryParse` would read
/// as `0.0.0.1`) and a hostname are refused, because neither is what an
/// operator writing a bind address means.
let parseBindAddress (raw: string) : Result<string, string> =
    let trimmed = if isNull raw then "" else raw.Trim()

    let unbracketed =
        if trimmed.StartsWith "[" && trimmed.EndsWith "]" then
            trimmed.Substring(1, trimmed.Length - 2)
        else
            trimmed

    if String.Equals(unbracketed, "localhost", StringComparison.OrdinalIgnoreCase) then
        Ok "localhost"
    else
        let looksLikeLiteral = unbracketed.Contains ':' || unbracketed.Split('.').Length = 4

        match IPAddress.TryParse unbracketed with
        | true, ip when looksLikeLiteral -> Ok(ip.ToString())
        | _ ->
            Error(
                sprintf
                    "%s=%s is not a bind address. Expected an IPv4 or IPv6 literal or `localhost`: 127.0.0.1 (loopback, the default), 0.0.0.0 (all IPv4 interfaces) or :: (all interfaces)."
                    BindAddressEnvVar
                    trimmed
            )

/// The bind address in force: `SERVER_BIND_ADDRESS` when set (non-empty),
/// else `ServerConfig.BindAddress`; `None` means the loopback default.
/// Fails loud on an address `parseBindAddress` refuses, naming it.
let configuredBindAddress (config: ServerConfig) (envValue: string option) : string option =
    let raw =
        match envValue with
        | Some v when not (String.IsNullOrWhiteSpace v) -> Some v
        | _ -> config.BindAddress

    raw
    |> Option.map (fun r ->
        match parseBindAddress r with
        | Ok address -> address
        | Error reason -> failwith reason)

/// `http://<address>:<port>`, bracketing an IPv6 literal.
let private forgeUrl (address: string) (port: string) =
    if address.Contains ':' then
        sprintf "http://[%s]:%s" address port
    else
        sprintf "http://%s:%s" address port

let private splitUrls (urls: string) =
    urls.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
    |> List.ofArray

/// Resolve the listen plan from the host configuration (the
/// `WebApplicationBuilder`'s, which carries `ASPNETCORE_*` variables, the
/// `urls` key and the `Kestrel` section), the configured bind address
/// (`configuredBindAddress`) and the port.
let resolve (configuration: IConfiguration) (bindAddress: string option) (serverPort: string) : ListenPlan =
    let endpointUrls =
        configuration.GetSection("Kestrel:Endpoints").GetChildren()
        |> Seq.map (fun endpoint -> endpoint["Url"])
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> List.ofSeq

    let hostUrls =
        match configuration["urls"] with
        | null -> []
        | urls -> splitUrls urls

    if not endpointUrls.IsEmpty then
        {
            Source = KestrelEndpoints
            Urls = endpointUrls
        }
    elif not hostUrls.IsEmpty then
        { Source = HostUrls; Urls = hostUrls }
    else
        match bindAddress with
        | Some address -> {
            Source = ConfiguredBindAddress
            Urls = [ forgeUrl address serverPort ]
          }
        | None -> {
            Source = DefaultLoopback
            Urls = [ forgeUrl DefaultBindAddress serverPort ]
          }

/// True when a listen URL's host is loopback (`localhost`, `127.x`, `::1`).
/// The wildcard hosts (`+`, `*`), the any-addresses (`0.0.0.0`, `::`) and a
/// hostname (which Kestrel binds on every interface) are not.
let isLoopbackUrl (url: string) : bool =
    let afterScheme =
        match url.IndexOf "://" with
        | -1 -> url
        | i -> url.Substring(i + 3)

    let authority =
        match afterScheme.IndexOf '/' with
        | -1 -> afterScheme
        | i -> afterScheme.Substring(0, i)

    let host =
        if authority.StartsWith "[" then
            match authority.IndexOf ']' with
            | -1 -> authority
            | i -> authority.Substring(1, i - 1)
        else
            match authority.LastIndexOf ':' with
            | -1 -> authority
            | i -> authority.Substring(0, i)

    if String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) then
        true
    else
        match IPAddress.TryParse host with
        | true, ip -> IPAddress.IsLoopback ip
        | _ -> false

/// The signals by which forge recognises a deployed posture, each rendered
/// as the sentence the preflight quotes. Empty for a local run.
///
///   * `DOTNET_RUNNING_IN_CONTAINER=true` — set by the official .NET container
///     images, which the shipped Dockerfile templates build on;
///   * `KUBERNETES_SERVICE_HOST` set — present in every Kubernetes pod;
///   * `ServerConfig.ReplicaCount > 1` — instances behind a load balancer,
///     which cannot reach a loopback listener on another host.
let deployedSignals (config: ServerConfig) (getEnv: string -> string option) : string list = [
    match getEnv "DOTNET_RUNNING_IN_CONTAINER" with
    | Some v when String.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase) ->
        "DOTNET_RUNNING_IN_CONTAINER=true (the process runs in a container)"
    | _ -> ()

    match getEnv "KUBERNETES_SERVICE_HOST" with
    | Some v when not (String.IsNullOrWhiteSpace v) ->
        "KUBERNETES_SERVICE_HOST is set (the process runs in a Kubernetes pod)"
    | _ -> ()

    if config.ReplicaCount > 1 then
        sprintf "ServerConfig.ReplicaCount = %d (instances behind a load balancer)" config.ReplicaCount
]

/// Read an environment variable, folding null / empty to `None`.
let envValue (name: string) : string option =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

/// Phase 1000 preflight. A server whose every listen URL is loopback, in a
/// posture forge recognises as deployed, cannot be reached by anything
/// outside its own host or network namespace:
///
///   * on the DEFAULT loopback (nothing configured) it is an `Error` naming
///     the signal and `SERVER_BIND_ADDRESS=0.0.0.0` — the 0.26.0 default
///     changed under the deployment, and a server that cannot be reached
///     must not start looking healthy;
///   * on an EXPLICITLY configured loopback it is a `Warning` — the operator
///     stated it, and a sidecar proxy sharing the pod's network namespace
///     reaching the server over loopback is a legitimate topology.
///
/// `Ok` for a local run, a `WorkerOnly` process (no listener: pass
/// `plan = None`), or any listen URL that is not loopback.
type ServerBindsConfiguredAddressValidator(plan: ListenPlan option, signals: string list, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout ConfigValidation.IConfigValidator.defaultTimeout

    interface ConfigValidation.IConfigValidator with
        member _.Name = "server-bind-address"
        member _.Timeout = timeout

        member _.Validate() = async {
            match plan with
            | None -> return ConfigValidation.ValidationResult.Ok
            | Some _ when signals.IsEmpty -> return ConfigValidation.ValidationResult.Ok
            | Some p when p.Urls.IsEmpty || not (p.Urls |> List.forall isLoopbackUrl) ->
                return ConfigValidation.ValidationResult.Ok
            | Some p ->
                let urls = String.concat ", " p.Urls
                let why = String.concat "; " signals

                match p.Source with
                | DefaultLoopback ->
                    return
                        ConfigValidation.ValidationResult.Error(
                            sprintf
                                "This server listens only on loopback (%s), the default since 0.26.0, but it runs in a deployed posture: %s. Nothing outside this host or container can reach it. Set SERVER_BIND_ADDRESS=0.0.0.0 (or ServerConfig.BindAddress = Some \"0.0.0.0\"; \"::\" for all interfaces) to listen on every interface. See docs/migrations/1000-server-bind-address.md."
                                urls
                                why
                        )
                | ConfiguredBindAddress
                | HostUrls
                | KestrelEndpoints ->
                    let setting =
                        match p.Source with
                        | KestrelEndpoints -> "the Kestrel:Endpoints configuration"
                        | HostUrls -> "the urls host setting (ASPNETCORE_URLS / DOTNET_URLS / applicationUrl)"
                        | _ -> "SERVER_BIND_ADDRESS / ServerConfig.BindAddress"

                    return
                        ConfigValidation.ValidationResult.Warning(
                            sprintf
                                "This server listens only on loopback (%s), as %s states, in a deployed posture: %s. Only a caller sharing its network namespace (a sidecar proxy in the same pod) can reach it. If that is not the topology, change %s to a non-loopback address (SERVER_BIND_ADDRESS=0.0.0.0)."
                                urls
                                setting
                                why
                                setting
                        )
        }