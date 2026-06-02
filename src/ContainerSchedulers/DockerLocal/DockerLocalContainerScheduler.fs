// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ContainerSchedulers.DockerLocal.Scheduler

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.IO.Pipes
open System.Net.Http
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ToolUp.Platform

// ─── DockerLocalContainerScheduler (Phase 26 reference companion) ────
//
// **Dev-grade — single-host, no auth, no TLS; production deployments
// use a cloud-specific companion.** This companion proves the
// `IContainerScheduler` abstraction against a real Docker socket
// without binding the substrate to any cloud target (GP 1). Operators
// running production-shape deployments wire a downstream
// cloud-specific companion (Fly Machines, Kubernetes, AWS ECS, Azure
// App Service, Google Cloud Run) rather than this one.
//
// The transport is `HttpClient` over a custom `SocketsHttpHandler`
// whose `ConnectCallback` returns either a Unix Domain Socket
// (Linux/macOS) or a named pipe (Windows) connected to Docker's local
// API. No vendor SDK is pulled in — per the forge companion-authoring
// guide, HTTP-shaped companions use BCL `HttpClient` to minimise the
// OSS supply-chain surface.
//
// The Docker HTTP API reference is at
// https://docs.docker.com/engine/api/v1.43/.

/// Configuration for `DockerLocalContainerScheduler`. The defaults
/// match Docker's documented defaults so a fresh install needs no
/// override.
type DockerLocalContainerSchedulerConfig = {
    /// On Linux/macOS, the Unix-socket path Docker listens on.
    /// Default: `/var/run/docker.sock`.
    UnixSocketPath: string
    /// On Windows, the named pipe Docker Desktop exposes.
    /// Default: `docker_engine` (the pipe name; the `\\.\pipe\` prefix
    /// is added automatically).
    WindowsPipeName: string
    /// Docker HTTP API version to negotiate. Default `v1.43` matches
    /// Docker Engine 24+. Older daemons accept lower versions.
    ApiVersion: string
}

module DockerLocalContainerSchedulerConfig =
    let defaults: DockerLocalContainerSchedulerConfig = {
        UnixSocketPath = "/var/run/docker.sock"
        WindowsPipeName = "docker_engine"
        // Docker Engine 27.x+ minimum API version. Older daemons
        // need the operator to override this; matches Docker Desktop
        // 4.x defaults.
        ApiVersion = "v1.47"
    }

// ─── Transport ───────────────────────────────────────────────────────

/// Connect callback that opens either a Unix Domain Socket or a named
/// pipe depending on the host OS. Returns the raw transport stream
/// `HttpClient` then runs HTTP over.
let private openLocalStream (config: DockerLocalContainerSchedulerConfig) (ct: CancellationToken) : Task<Stream> = task {
    if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
        let pipe =
            new NamedPipeClientStream(".", config.WindowsPipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

        do! pipe.ConnectAsync ct
        return pipe :> Stream
    else
        let socket =
            new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

        let endpoint = UnixDomainSocketEndPoint config.UnixSocketPath
        do! socket.ConnectAsync(endpoint, ct).AsTask()
        return new NetworkStream(socket, ownsSocket = true) :> Stream
}

let private buildHttpClient (config: DockerLocalContainerSchedulerConfig) : HttpClient =
    let handler = new SocketsHttpHandler()
    // The host name is irrelevant for socket / pipe transports; Docker
    // ignores the Host header. The ConnectCallback is what selects the
    // real transport.
    handler.ConnectCallback <-
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>(fun _ ct ->
            ValueTask<Stream>(openLocalStream config ct))

    let client = new HttpClient(handler)
    client.BaseAddress <- Uri(sprintf "http://localhost/%s/" config.ApiVersion)
    client.Timeout <- TimeSpan.FromMinutes 5.0
    client

// ─── JSON helpers ────────────────────────────────────────────────────

let private jsonOptions =
    JsonSerializerOptions(PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true)

let private serialise (value: 'T) : StringContent =
    let json = JsonSerializer.Serialize(value, jsonOptions)
    new StringContent(json, Encoding.UTF8, "application/json")

let private readJson<'T> (response: HttpResponseMessage) : Async<'T> = async {
    use! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask

    let! result =
        JsonSerializer.DeserializeAsync<'T>(stream, jsonOptions).AsTask()
        |> Async.AwaitTask

    return result
}

// ─── Docker API DTOs (minimal — only the fields we touch) ────────────

[<CLIMutable>]
type private DockerCreateRequest = {
    Image: string
    Env: string[]
    Labels: Dictionary<string, string>
    ExposedPorts: Dictionary<string, obj>
    Healthcheck: obj
}

[<CLIMutable>]
type private DockerCreateResponse = { Id: string; Warnings: string[] }

[<CLIMutable>]
type private DockerContainerState = {
    Status: string
    Running: bool
    Restarting: bool
    Dead: bool
    ExitCode: int
    StartedAt: string
    FinishedAt: string
    Error: string
}

[<CLIMutable>]
type private DockerContainerConfig = {
    Image: string
    Labels: Dictionary<string, string>
}

[<CLIMutable>]
type private DockerInspectResponse = {
    Id: string
    State: DockerContainerState
    Config: DockerContainerConfig
}

[<CLIMutable>]
type private DockerListItem = {
    Id: string
    Image: string
    Labels: Dictionary<string, string>
    State: string
    Status: string
}

// ─── Status mapping ──────────────────────────────────────────────────

let private parseUtc (s: string) : DateTime =
    match DateTime.TryParse s with
    | true, dt -> dt.ToUniversalTime()
    | false, _ -> DateTime.UtcNow

let private mapStatus (state: DockerContainerState) : ContainerStatus =
    if isNull (box state) then
        ContainerNotFound
    elif state.Restarting then
        ContainerRestarting
    elif state.Running then
        ContainerRunning(parseUtc state.StartedAt)
    elif state.Dead || state.ExitCode <> 0 then
        let reason =
            if String.IsNullOrEmpty state.Error then
                sprintf "exit %d" state.ExitCode
            else
                state.Error

        ContainerCrashed(reason, parseUtc state.FinishedAt)
    elif String.Equals(state.Status, "created", StringComparison.OrdinalIgnoreCase) then
        ContainerCreated
    elif String.Equals(state.Status, "exited", StringComparison.OrdinalIgnoreCase) then
        ContainerExited(state.ExitCode, parseUtc state.FinishedAt)
    else
        ContainerCreated

let private mapListItemStatus (item: DockerListItem) : ContainerStatus =
    match item.State with
    | s when String.Equals(s, "running", StringComparison.OrdinalIgnoreCase) -> ContainerRunning DateTime.UtcNow
    | s when String.Equals(s, "restarting", StringComparison.OrdinalIgnoreCase) -> ContainerRestarting
    | s when String.Equals(s, "created", StringComparison.OrdinalIgnoreCase) -> ContainerCreated
    | s when String.Equals(s, "exited", StringComparison.OrdinalIgnoreCase) -> ContainerExited(0, DateTime.UtcNow)
    | s when String.Equals(s, "dead", StringComparison.OrdinalIgnoreCase) -> ContainerCrashed("dead", DateTime.UtcNow)
    | _ -> ContainerCreated

// ─── Log-frame parser ────────────────────────────────────────────────
//
// Docker's multiplexed log stream uses an 8-byte header per frame:
//   byte 0    : stream type (1 = stdout, 2 = stderr)
//   bytes 1-3 : padding (zero)
//   bytes 4-7 : payload size, big-endian uint32
// Followed by `size` bytes of payload (raw bytes, may include
// embedded newlines). We split the payload into per-line `LogEntry`
// records using `\n` as the terminator.

let private readExact (stream: Stream) (count: int) (ct: CancellationToken) : Task<byte[] voption> = task {
    let buffer = Array.zeroCreate<byte> count
    let mutable read = 0

    while read < count do
        let! n = stream.ReadAsync(buffer.AsMemory(read, count - read), ct).AsTask()
        if n = 0 then read <- -1 else read <- read + n

        if read = -1 then
            () // exit loop on EOF

    if read < 0 then
        return ValueNone
    else
        return ValueSome buffer
}

let private readFrame (stream: Stream) (ct: CancellationToken) : Task<(LogStream * byte[]) voption> = task {
    let! header = readExact stream 8 ct

    match header with
    | ValueNone -> return ValueNone
    | ValueSome h ->
        let streamKind =
            match h[0] with
            | 1uy -> Stdout
            | 2uy -> Stderr
            | _ -> Stdout // raw / TTY mode — treat as stdout

        let size = int (BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(h, 4, 4)))

        if size = 0 then
            return ValueSome(streamKind, Array.empty)
        else
            let! payload = readExact stream size ct

            match payload with
            | ValueNone -> return ValueNone
            | ValueSome p -> return ValueSome(streamKind, p)
}

/// Hand-rolled `IAsyncEnumerable<LogEntry>` over the Docker log stream.
/// Buffers incomplete trailing-line bytes between frames so a payload
/// that splits a line is reassembled before yielding.
type private DockerLogEnumerator(stream: Stream, cancellation: CancellationTokenSource) =
    let mutable current: LogEntry = Unchecked.defaultof<_>
    let mutable disposed = false
    let pendingStdout = ResizeArray<byte>()
    let pendingStderr = ResizeArray<byte>()
    let yieldQueue = Queue<LogEntry>()

    let queueLines (kind: LogStream) (acc: ResizeArray<byte>) (payload: byte[]) =
        // Append, then split on '\n'. Anything after the final '\n'
        // stays in the accumulator until a subsequent frame completes
        // the line.
        acc.AddRange payload
        let bytes = acc.ToArray()
        acc.Clear()
        let mutable i = 0
        let mutable lineStart = 0

        while i < bytes.Length do
            if bytes[i] = byte '\n' then
                let lineLen = i - lineStart

                let line =
                    if lineLen > 0 && bytes[i - 1] = byte '\r' then
                        Encoding.UTF8.GetString(bytes, lineStart, lineLen - 1)
                    else
                        Encoding.UTF8.GetString(bytes, lineStart, lineLen)

                yieldQueue.Enqueue {
                    At = DateTime.UtcNow
                    Stream = kind
                    Line = line
                }

                lineStart <- i + 1

            i <- i + 1

        if lineStart < bytes.Length then
            acc.AddRange(bytes |> Array.skip lineStart)

    interface IAsyncEnumerator<LogEntry> with
        member _.Current = current

        member self.MoveNextAsync() =
            ValueTask<bool>(
                task {
                    if disposed then
                        return false
                    elif yieldQueue.Count > 0 then
                        current <- yieldQueue.Dequeue()
                        return true
                    else
                        let mutable produced = false
                        let mutable eof = false

                        while not produced && not eof do
                            let! frame = readFrame stream cancellation.Token

                            match frame with
                            | ValueNone -> eof <- true
                            | ValueSome(kind, payload) ->
                                let acc =
                                    match kind with
                                    | Stdout -> pendingStdout
                                    | Stderr -> pendingStderr

                                queueLines kind acc payload

                                if yieldQueue.Count > 0 then
                                    current <- yieldQueue.Dequeue()
                                    produced <- true

                        if produced then
                            return true
                        else
                            // EOF — flush any trailing partial line so callers
                            // don't lose it.
                            let flush (kind: LogStream) (acc: ResizeArray<byte>) =
                                if acc.Count > 0 then
                                    yieldQueue.Enqueue {
                                        At = DateTime.UtcNow
                                        Stream = kind
                                        Line = Encoding.UTF8.GetString(acc.ToArray())
                                    }

                                    acc.Clear()

                            flush Stdout pendingStdout
                            flush Stderr pendingStderr

                            if yieldQueue.Count > 0 then
                                current <- yieldQueue.Dequeue()
                                return true
                            else
                                return false
                }
            )

        member _.DisposeAsync() =
            ValueTask(
                task {
                    if not disposed then
                        disposed <- true
                        cancellation.Cancel()
                        do! stream.DisposeAsync()
                        cancellation.Dispose()
                }
            )

type private DockerLogEnumerable(streamFactory: CancellationToken -> Task<Stream>) =
    interface IAsyncEnumerable<LogEntry> with
        member _.GetAsyncEnumerator(ct: CancellationToken) =
            let cts = CancellationTokenSource.CreateLinkedTokenSource ct
            let stream = streamFactory(cts.Token).GetAwaiter().GetResult()
            new DockerLogEnumerator(stream, cts) :> IAsyncEnumerator<LogEntry>

// ─── Implementation ──────────────────────────────────────────────────

let private toEnvArray (envVars: Map<string, string>) : string[] =
    envVars
    |> Map.toSeq
    |> Seq.map (fun (k, v) -> sprintf "%s=%s" k v)
    |> Array.ofSeq

let private toLabelDict (extras: Map<string, string>) (tenantId: TenantId) : Dictionary<string, string> =
    let d = Dictionary<string, string>(extras)
    d["tenantId"] <- tenantId
    d

let private exposedPortsDict (port: int option) : Dictionary<string, obj> =
    let d = Dictionary<string, obj>()

    match port with
    | Some p -> d[sprintf "%d/tcp" p] <- box (obj ())
    | None -> ()

    d

/// `IContainerScheduler` implementation over a local Docker socket.
/// Single-host, dev-grade — see file header.
type DockerLocalContainerScheduler(config: DockerLocalContainerSchedulerConfig) =
    let client = buildHttpClient config

    let openLogStream (path: string) (ct: CancellationToken) : Task<Stream> = task {
        // Use HttpClient with `ResponseHeadersRead` so the response
        // body streams as Docker emits frames, rather than buffering.
        // On non-success status (404 missing container, etc.) return
        // an empty stream so the IAsyncEnumerator terminates cleanly
        // on the first MoveNextAsync.
        let request = new HttpRequestMessage(HttpMethod.Get, path)

        let! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)

        if not response.IsSuccessStatusCode then
            return Stream.Null
        else
            let! body = response.Content.ReadAsStreamAsync ct
            return body
    }

    interface IContainerScheduler with

        member _.LaunchContainer(tenantId: TenantId, spec: ContainerSpec) = async {
            if String.IsNullOrWhiteSpace spec.Image then
                return Error(InvalidContainerSpec "ContainerSpec.Image is required")
            else
                match spec.ExposedPort with
                | Some p when p < 1 || p > 65535 ->
                    return Error(InvalidContainerSpec(sprintf "ExposedPort %d outside 1..65535" p))
                | _ ->
                    let createRequest = {
                        Image = spec.Image
                        Env = toEnvArray spec.EnvVars
                        Labels = toLabelDict spec.Labels tenantId
                        ExposedPorts = exposedPortsDict spec.ExposedPort
                        Healthcheck = null
                    }

                    try
                        let! createResponse =
                            client.PostAsync("containers/create", serialise createRequest)
                            |> Async.AwaitTask

                        if not createResponse.IsSuccessStatusCode then
                            let! body = createResponse.Content.ReadAsStringAsync() |> Async.AwaitTask

                            if int createResponse.StatusCode = 404 then
                                return Error(ImagePullFailed(spec.Image, body))
                            else
                                return Error(SchedulerUnavailable body)
                        else
                            let! created = readJson<DockerCreateResponse> createResponse

                            let! startResponse =
                                client.PostAsync(sprintf "containers/%s/start" created.Id, new StringContent(""))
                                |> Async.AwaitTask

                            if not startResponse.IsSuccessStatusCode then
                                let! body = startResponse.Content.ReadAsStringAsync() |> Async.AwaitTask
                                return Error(SchedulerUnavailable body)
                            else
                                return Ok created.Id
                    with ex ->
                        return Error(SchedulerUnavailable ex.Message)
        }

        member _.StopContainer(containerId: ContainerId) = async {
            try
                let! response =
                    client.PostAsync(sprintf "containers/%s/stop" containerId, new StringContent(""))
                    |> Async.AwaitTask

                let code = int response.StatusCode

                if code = 404 then
                    return Error(UnknownContainer containerId)
                elif code = 204 || code = 304 then
                    // 204 = stopped now; 304 = already stopped (idempotent).
                    return Ok()
                else
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return Error(SchedulerUnavailable body)
            with ex ->
                return Error(SchedulerUnavailable ex.Message)
        }

        member _.RestartContainer(containerId: ContainerId) = async {
            try
                let! response =
                    client.PostAsync(sprintf "containers/%s/restart" containerId, new StringContent(""))
                    |> Async.AwaitTask

                let code = int response.StatusCode

                if code = 404 then
                    return Error(UnknownContainer containerId)
                elif code = 204 then
                    return Ok()
                else
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return Error(SchedulerUnavailable body)
            with ex ->
                return Error(SchedulerUnavailable ex.Message)
        }

        member _.GetContainerStatus(containerId: ContainerId) = async {
            try
                let! response = client.GetAsync(sprintf "containers/%s/json" containerId) |> Async.AwaitTask

                let code = int response.StatusCode

                if code = 404 then
                    return Ok ContainerNotFound
                elif not response.IsSuccessStatusCode then
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return Error(SchedulerUnavailable body)
                else
                    let! inspect = readJson<DockerInspectResponse> response
                    return Ok(mapStatus inspect.State)
            with ex ->
                return Error(SchedulerUnavailable ex.Message)
        }

        member _.ListContainers(tenantId: TenantId option) = async {
            try
                let path =
                    match tenantId with
                    | Some tid ->
                        let filtersJson = sprintf "{\"label\":[\"tenantId=%s\"]}" (Uri.EscapeDataString tid)

                        sprintf "containers/json?all=true&filters=%s" (Uri.EscapeDataString filtersJson)
                    | None -> "containers/json?all=true"

                let! response = client.GetAsync path |> Async.AwaitTask

                if not response.IsSuccessStatusCode then
                    return []
                else
                    let! items = readJson<DockerListItem[]> response

                    return
                        items
                        |> Array.choose (fun item ->
                            if isNull item.Labels then
                                None
                            else
                                match item.Labels.TryGetValue "tenantId" with
                                | true, tid ->
                                    Some {
                                        ContainerId = item.Id
                                        TenantId = tid
                                        ImageRef = item.Image
                                        Status = mapListItemStatus item
                                        Labels = item.Labels |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                                    }
                                | _ -> None)
                        |> Array.toList
            with _ ->
                return []
        }

        member _.StreamLogs(containerId: ContainerId, fromTime: DateTime option) =
            let sincePart =
                match fromTime with
                | Some t -> sprintf "&since=%d" (DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds())
                | None -> ""

            let path =
                sprintf "containers/%s/logs?follow=true&stdout=1&stderr=1%s" containerId sincePart

            DockerLogEnumerable(fun ct -> openLogStream path ct) :> IAsyncEnumerable<LogEntry>

module DockerLocalContainerScheduler =
    /// Convenience: create the scheduler from a config record.
    let create (config: DockerLocalContainerSchedulerConfig) : IContainerScheduler =
        DockerLocalContainerScheduler config :> IContainerScheduler