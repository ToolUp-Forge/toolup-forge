# Composition conformance: what forge certifies against the App-composition specification

The [App-composition wire specification](https://github.com/Fuaran-Build/fuaran-app-composition-specification)
(Apache-2.0, format version 1) defines the neutral composition document. forge's own composition profile,
`server-app-host`, is published in [`spec/`](../../spec/README.md) and stated in
[`composition-profile.md`](composition-profile.md). forge is the evaluator Fuaran app compositions reduce to. This
page says exactly what forge's test suite certifies against the specification, and what it does not.

## What forge certifies

Three checks in the Platform test pack, all run by `verify.ps1`:

| Check | Test list | What it holds |
|---|---|---|
| The profile against forge's surface | `CompositionProfileSurfaceTests` | The profile's registry against forge's reflected composable surface, in both directions, through `spec/surface-binding.json`. Also: the document against the registry, the setting grammars against the profile's vectors, and each vector against its recorded digest. See [`composition-profile.md`](composition-profile.md). |
| §P8 against the core's facet vectors | `CompositionProfileSurfaceTests` | Every facet value the profile declares is demonstrated by one of the specification's facet-family vectors for this profile, and no pinned vector demonstrates a value the profile does not declare. |
| The pinned vectors' provenance | `AppCompositionConformanceTests` | Every copy in `spec/fixtures/core/` is the specification's own vector at the pinned commit, byte for byte. The pin also covers every vector the specification scopes to `server-app-host`. |

### The provenance leg

`spec/fixtures/core/` holds copies of the specification's vectors, pinned at the commit that
`spec/fixtures/manifest.json` records as `core.commit`. The specification remains their home. The profile pack checks
each copy against the digest the manifest records beside it, but the same change writes the copies and the digests,
so that check alone shows only that neither has moved since. The provenance leg reads the specification itself, at
the pinned commit (through `git cat-file`, so whatever the clone has checked out does not matter), and fails when:

- a pinned copy is not enumerated by the specification at the pin, or is enumerated under another family;
- a recorded digest is not the digest the specification records;
- a copy is not byte-identical to the file in its home;
- the specification scopes a vector to `server-app-host` that is not pinned here.

The last check runs in one direction only. The pin carries one vector the specification does not scope to this
profile: `profile-placement/reject-vocabulary-unresolved`, which the profile pack's root-base check reads. So 19
copies certify against 18 scoped vectors, and that is expected. A pinned vector that is neither scoped nor
enumerated fails.

**Where the specification is found.** `TOOLUP_APP_COMPOSITION_SPEC_DIR`, when set; otherwise a checkout named
`fuaran-app-composition-specification` at the repository root (what CI clones); otherwise a bounded search from the
repository's main working tree, the same search the other externally pinned corpora use. The search recognises a
clone by its content and prefers the one holding the pinned commit. When none is found, or the pinned commit is absent
from the clone, the leg **fails** and names the remedy. It never skips, because a pin nobody
can check certifies nothing. CI checks the specification out at the pinned commit without a token, since the
repository is public. Move the pin by changing `core.commit` and the `ref` in `.github/workflows/checks.yml` together.

## What forge does not certify, and why

**Reading and refusing compositions.** The profile's conformance section (§P6) is an obligation on an implementation
that **reads** compositions. That implementation runs every core-scope family and the profile's own vectors, enforces
the composition rules of §P4, and refuses each reject vector with its class. forge is not such an implementation.
Its builders take typed values (`ServerApp`, the layer roots, `ServerConfig`), never a composition document, so there
is nothing in this repository for a reject vector to be refused by. The profile's own refusal classes appear nowhere in
forge's source, and forge does not bind the companion implementations a composition names (such as `Claude` or
`Postgres`) to its own types. A reader that reduces compositions to forge is a separate implementation, and it
certifies against the profile's vectors on its own terms. This repository publishes the profile and those vectors, and
it holds them to forge's surface, but it contains no such reader.

This is a statement of scope, not a gap with a fix pending. A composition-reading harness inside this repository's tests
would be a second reader, not a test of forge.

**Declared surface gaps.** forge composes two setting values that the profile does not register. Each is recorded,
with its reason, in `spec/surface-binding.json`:

| Setting | Value forge composes | Status in the profile |
|---|---|---|
| `ProcessProfile` | `DispatcherOnly` | Unregistered. A composition carrying it is refused as `knob-value-unregistered` until the profile registers it. |
| `JobScheduler` | `QuartzJobScheduler` | Unregistered. It carries a configuration payload, and a companion constructs the scheduler. |

The surface check holds both entries, so neither can drift silently. Registering either one is a profile version event
(§P7).

## Conformance is a test-time property

No `ToolUp.*` package references the specification, the profile or the pinned vectors, and nothing in `spec/` is
packed or loaded at run time. The provenance leg reads JSON and git objects and nothing else, and it takes no package
of the specification's. forge composes and runs exactly as it would with no Fuaran component present. Conformance is
what forge's test suite proves before a release ships. It is not a dependency.

## Running it

```powershell
dotnet build src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "App-composition core pin"
```

A passing run prints the commit, the pinned and scoped counts and the number of findings. A failing run lists each
finding by vector id.
