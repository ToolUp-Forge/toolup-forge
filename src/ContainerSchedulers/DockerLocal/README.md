# ToolUp.ContainerSchedulers.DockerLocal

Dev-grade `IContainerScheduler` companion for `ToolUp.Platform`. Drives Docker over the local socket (Unix Domain Socket on Linux/macOS, named pipe `\\.\pipe\docker_engine` on Windows). Proves the `IContainerScheduler` abstraction against a real backend without privileging any cloud target.

The companion is the substrate's **reference implementation** — the canonical pattern any cloud-specific impl (Fly Machines, Kubernetes, AWS ECS, Azure App Service, Google Cloud Run) follows. It is **not** the recommended production deployment: it talks to a single Docker host, has no auth surface, and assumes the operator trusts every process with socket access. Production deployments ship a cloud-specific companion downstream (managed-cloud operators run Fly Machines / Cloud Run / ECS; self-hosted operators wire Docker Swarm / Kubernetes / etc.).

Wire it alongside the other substrate defaults:

```fsharp skip=fragment
open ToolUp.Platform
open ToolUp.ContainerSchedulers.DockerLocal

let scheduler = DockerLocalContainerScheduler.create DockerLocalContainerSchedulerConfig.defaults
services.AddSingleton<IContainerScheduler>(scheduler)
```

Configuration carries the socket path (Linux/macOS) or pipe name (Windows); defaults match Docker's published defaults so a fresh Docker Desktop install needs no override.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
