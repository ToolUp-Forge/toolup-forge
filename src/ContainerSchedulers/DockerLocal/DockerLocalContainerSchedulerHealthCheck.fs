// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ContainerSchedulers.DockerLocal.HealthCheck

open System
open System.IO
open System.IO.Pipes
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Threading
open ToolUp.Platform.HealthChecks
open ToolUp.ContainerSchedulers.DockerLocal.Scheduler

/// `IHealthCheck` probe for `DockerLocalContainerScheduler`. Confirms
/// the local Docker socket / named pipe is reachable. Cheap — does a
/// transport-level connect only; does not exercise the HTTP API.
type DockerLocalContainerSchedulerHealthCheck(config: DockerLocalContainerSchedulerConfig) =
    interface IHealthCheck with
        member _.Name = "container_scheduler:docker_local"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 2.0

        member _.Check() = async {
            try
                use cts = new CancellationTokenSource(TimeSpan.FromSeconds 1.0)

                if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                    use pipe =
                        new NamedPipeClientStream(
                            ".",
                            config.WindowsPipeName,
                            PipeDirection.InOut,
                            PipeOptions.Asynchronous
                        )

                    do! pipe.ConnectAsync cts.Token |> Async.AwaitTask
                    return Healthy
                else
                    use socket =
                        new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

                    let endpoint = UnixDomainSocketEndPoint config.UnixSocketPath
                    do! socket.ConnectAsync(endpoint, cts.Token).AsTask() |> Async.AwaitTask
                    return Healthy
            with
            | :? OperationCanceledException -> return Unhealthy "Docker socket connect timed out"
            | ex -> return Unhealthy(sprintf "Docker socket unreachable: %s" ex.Message)
        }