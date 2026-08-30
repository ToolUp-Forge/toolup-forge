# ToolUp.Cli — the `dotnet toolup` admin CLI

A thin, dependency-free (pure BCL + `FSharp.Core`) command host for ToolUp Platform SDK admin
tasks. It is the substrate other capabilities plug subcommands into; the command registry grows
additively.

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
| `stamp` | Writes/refreshes a module-binding manifest (`module-bindings.json`) — the deploy-time stamper for the module-binding gate. |
| `module add` / `module remove` | Transactionally scaffold + register a module into an app, or reverse it byte-for-byte. |
| `memberships doctor` | Detects membership-integrity drift in a local-file deployment's blob layout; `--repair` fixes the provably-safe subset. |
| `tenants list` / `preview` / `offboard` | Scripted tenant lifecycle over a running deployment's admin API: enumerate teams, preview an offboard's blast radius, run one. |
| `users list` / `offboard` | Enumerate the principals the substrate has evidence for (`--team-less` for the stray-account residue), and offboard one's personal scope. |

#### `module add` / `module remove`

```bash
toolup module add --name Sales --app-root . \
    --register src/App-Server/App-Server.fsproj \
    --register src/App-Client/Client.fs

toolup module remove --name Sales --app-root .
```

`module add` scaffolds the four-file module under `<app-root>/Modules/<Name>/` and **append-only**
registers it into each `--register` file at a `toolup:modules` marker (`<!-- toolup:modules -->` in
`.fsproj`/`.props` gets a `<ProjectReference>`; `// toolup:modules` in a `.fs` gets a
`ClientView.register()` call), recording every edit in a per-module ledger under
`<app-root>/.toolup/modules/`. `module remove` replays that ledger in reverse — deleting exactly the
inserted lines and the scaffolded folder — so the tree returns **byte-for-byte** to its pre-add
state. Registration is append-only (never edits an existing line), so concurrent adds merge cleanly.
A `--register` target that lacks the marker is refused before anything is written (the add is
all-or-nothing).

#### `stamp`

```bash
# Symmetric (HMAC) anchor
toolup stamp --manifest module-bindings.json --module Sales --key-id anchor-1 --mac-key-file anchor.key

# Asymmetric (ES256) anchor
toolup stamp --manifest module-bindings.json --module Inventory --key-id ec-1 --ec-key-file signing.pem

# Re-bind (re-run with a different key) / unbind
toolup stamp --manifest module-bindings.json --module Sales --unbind
```

Mints each named module's binding stamp over the module's identifier bytes and merges it into the
manifest (other modules untouched). A module's binding is a **deployment** property: the same module
artefact ships unbound, bound to deployment A, or re-bound to B with no rebuild. The host reads the
manifest at startup and the module-binding gate verifies each stamp against the deployment's
configured trust anchors. Crypto is pure BCL (HMAC-SHA256 / ES256 over a NIST P-256 key); Ed25519
*minting* is not yet exposed here (the verifier already accepts Ed25519 anchors).

| Option | Meaning |
|---|---|
| `--manifest <path>` | The `module-bindings.json` to create/update (required). |
| `--module <Name>` | A module to stamp; repeatable (required). |
| `--key-id <id>` | Anchor key id recorded with the stamp (required to stamp). |
| `--mac-key-file <f>` / `--mac-key <base64>` | Base64 HMAC-SHA256 key (symmetric anchor). |
| `--ec-key-file <pem>` | PEM P-256 EC private key (asymmetric / ES256 anchor). |
| `--unbind` | Remove the named modules' entries instead of stamping. |

#### `memberships doctor`

```bash
toolup memberships doctor --data-root /var/data/myapp          # report + CI-friendly exit code
toolup memberships doctor --data-root /var/data/myapp --repair # also fix the safe subset
```

Walks `<data-root>/_platform/{memberships,teams,active-team}` (the local-file blob layout) and
classifies membership drift: membership blobs keyed by an **email address** (a legacy add path),
blobs keyed by an id the identity sanitiser refuses (e.g. a raw provider-prefixed JWT `sub` after a
deployment switched claim resolution), rows naming a **purged team**, and **dangling active-team
pointers**. Exits `0` on a clean store, non-zero when findings exist.

`--repair` applies only the provably-safe subset — deleting rows that name a nonexistent team and
clearing dangling pointers. Email-keyed / unresolvable blobs are never touched: the right fix
(re-adding the member under the resolved id) needs operator knowledge, so they stay in the report.
Offline repair edits blob files directly (no audit events, no cache-evict publications) — run it
against a stopped deployment, or drive the in-process `MembershipDoctor` substrate for an audited
live repair.

| Option | Meaning |
|---|---|
| `--data-root <dir>` | The deployment's local blob-storage root (required). |
| `--repair` | Apply the safe subset instead of reporting only. |

#### `tenants` / `users` — tenant + principal lifecycle

Unlike every other verb here, these talk to a **running deployment** over its admin API rather than
to files on disk. They are a thin client: every decision — who may call, what a preview counts,
whether an offboard needs a confirmation token, which user id the audit trail records — is made
server-side and is not re-implemented, relaxed, or second-guessed here.

```bash
$env:TOOLUP_ADMIN_ENDPOINT = "https://app.example.com"
$env:TOOLUP_ADMIN_TOKEN    = "<a Platform-Admin bearer token>"

toolup users list --team-less                 # principals with a login and no team
toolup tenants preview user-u42               # what an offboard WOULD destroy
toolup users offboard u42 --reason "left the company"
```

| Setting | Meaning |
|---|---|
| `TOOLUP_ADMIN_ENDPOINT` / `--endpoint <url>` | Deployment origin. The flag wins over the variable. |
| `TOOLUP_ADMIN_TOKEN` | Platform-Admin bearer credential. **Environment only** — a credential passed as an argument lands in shell history and in every process listing on the machine. |

| Command | What it does |
|---|---|
| `tenants list` | Every team on the deployment with its membership summary (the deployment-wide admin read). |
| `tenants preview <scopeId>` | Each lifecycle hook's would-affect projection — the key that would be destroyed, the jobs that would be cancelled, the records that would be erased. Modifies nothing. |
| `tenants offboard <scopeId>` | Runs every deprovision hook. **Irreversible.** |
| `users list [--team-less]` | The derived principal enumeration — membership blobs, personal `user-<id>` scopes, and the sign-in audit trail, merged per user. `--team-less` keeps exactly those holding no membership row. |
| `users offboard <userId>` | Sugar for `tenants offboard user-<userId>` — same hooks, same gate, same audit trail. |

`offboard` options: `--export-first` writes the tenant's data-export archive as a durable pre-step
and erases only once it is written (a failed export aborts before any destruction, and the archive
reference is printed so you can hand it to the departing customer); `--reason <text>` is recorded in
the audit trail; `--token <t>` replays a confirmation token.

**On the confirmation gate.** When a deployment runs a confirmation mode, the server refuses a
token-less offboard. The CLI surfaces that refusal **verbatim** and exits non-zero — it does not
mint a token, and there is no flag that makes it. Under the two-person rule the minting admin must
be a *different person* from the redeeming one, which is not a property a command-line flag can
attest to; the token reaches you out of band and `--token` replays it unchanged.

Exit codes are the CI-relevant part. `0` means the call succeeded and, for an offboard, that no hook
failed. `1` covers a server refusal (including the confirmation banner), an unreachable deployment,
and a **partially completed** offboard — the sweep does not abort on a failing hook, so the rest of
the erasure still runs and the tenant is left half-offboarded, which a scripted sweep should stop on
rather than report as clean. `2` is a bad invocation, and nothing was sent.

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
