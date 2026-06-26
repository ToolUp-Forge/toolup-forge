# ToolUp.Cli — the `dotnet toolup` admin CLI

A thin, dependency-free (pure BCL + `FSharp.Core`) command host for ToolUp Platform SDK admin
tasks. It is the substrate other capabilities plug subcommands into; today it ships two leaves and
grows additively.

## Install

```bash
# Project-local tool (recommended — pins the version in .config/dotnet-tools.json)
dotnet new tool-manifest          # once per repo, if you have no manifest yet
dotnet tool install ToolUp.Cli

# Or globally
dotnet tool install --global ToolUp.Cli
```

The package id is `ToolUp.Cli`; the installed command is `toolup`.

## Usage

```bash
toolup --help            # list registered commands
toolup version           # print the CLI version
toolup docker emit --help
```

### Commands

| Command | What it does |
|---|---|
| `version` | Prints the installed CLI version. |
| `docker emit` | Re-emits the maintained Docker host artefacts (`Dockerfile`, `.dockerignore`, `healthcheck.sh`, `compose.yml`) at a solution root, substituting the deployment's project / image / port tokens. |

#### `docker emit`

```bash
toolup docker emit \
    --server-project MyApp-Server \
    --server-dll MyApp-Server \
    --image-name myapp \
    --host-port 8080
```

| Option | Required | Default | Meaning |
|---|---|---|---|
| `--server-project <dir>` | yes | — | Server project directory under `src/` (the build stage publishes it). |
| `--server-dll <name>` | yes | — | Server assembly name without `.dll` (the runtime stage's entrypoint). |
| `--image-name <name>` | yes | — | Container image name (lowercase). |
| `--host-port <port>` | no | `8080` | Host-side port compose publishes (container-side is always `5000`). |
| `--output-dir <dir>` | no | `.` | Directory to write the four files into. |
| `--force` | no | off | Overwrite existing files instead of refusing. |

The emitted files are identical to those produced by `dotnet new platformsdk-docker` — `docker emit`
exists so you can re-emit after changing a deployment's companion set without re-running the
template with `--force`. Redis stays commented in `compose.yml` (the `none` notification-channel
default); uncomment it by hand for multi-silo deployments.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (including `--help` output). |
| `1` | The command ran but failed at runtime (e.g. refusing to overwrite without `--force`). |
| `2` | The invocation was wrong (unknown / incomplete command, bad or missing arguments). |

## Design

The host is a registry of `Command` records (`Path` / `Summary` / `Help` / `Run`) with
longest-path-prefix dispatch. Subcommands are appended in `Program.fs` and never edit the dispatcher
or each other — vendor-specific subcommands isolate their dependencies in their own modules (GP 1),
and the base CLI carries no paid or cloud dependency (GP 2).

Licensed under Apache-2.0.
