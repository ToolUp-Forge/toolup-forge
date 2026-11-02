# forge's composition profile — conformance statement

forge publishes its own composition profile, `server-app-host`, in [`spec/`](../../spec/README.md) at the root of this
repository. It is a vocabulary profile of the
[App-composition wire specification](https://github.com/Fuaran-Build/fuaran-app-composition-specification) (format
version 1), which registers it and names `spec/` as its home. forge is the evaluator Fuaran app compositions reduce
to, and the profile is the vocabulary such a composition carries when forge evaluates it: the base hosts, capability
layers, companion slots and settings forge composes, registered as found on forge's surface.

## Which profile version this release satisfies

**This release of forge satisfies `server-app-host` 7.0**, the version in `spec/composition-profile.md`, its
registry and its fixture manifest. The profile is versioned with the surface it describes, so the version a forge
release satisfies is the one in that release's own `spec/` folder. Pin a profile version, not a forge commit; a
profile version moves only when its vocabulary does (§P1 of the profile).

7.0 is the version at which the profile moved here from the specification's home, at 6.8. Nothing normative changed
with the move.

## How the check works

`CompositionProfileSurfaceTests`, in the Platform test pack that `verify.ps1` runs, is the profile's §P7 gate. It is
six checks, each a pure function that returns the disagreements it finds:

| Check | What fails it |
|---|---|
| Fixtures | A vector that does not hash to the digest `spec/fixtures/manifest.json` records, a vector file the manifest does not list, or a profile version that differs between the document's header, its §P1 table, the registry, the manifest and the surface binding. |
| Document and registry | A token, argument-taking case, citation-bearing view, registered setting, value grammar, profile-only node kind, refusal class or facet value that one rendering states and the other does not. Both directions. |
| Setting grammars | A vector that must be accepted carrying a registered setting its grammar does not admit, a `knob-value-unregistered` vector carrying none, or a grammar no vector refuses under. |
| Facet declaration | A §P8 value no pinned specification vector for this profile demonstrates, or a pinned vector demonstrating a value §P8 does not honour (core §11.3), or a declaration missing an axis or a note (core §11.4). The vectors are the specification's own facet-family vectors, copied to `spec/fixtures/core/` and held to the digests the specification's manifest records at the pinned commit. Whether each vector's content exhibits what it claims is checked by the specification's own suite, where the vectors live. |
| Root bases | A duplicated `appRoot.base` token, or a pinned `vocabulary-unresolved` vector whose root names a base this profile registers (it would then resolve, and falsify nothing). The specification checks base ownership only where a vocabulary registry is published, which is here. |
| Composable surface | See below. |

### The surface check

The registry's tokens are wire spellings (`blobStorage`), not forge names (`IBlobStorage`), so
`spec/surface-binding.json` records what each registered token reduces to in forge. The check then holds both
directions:

- **Registry to forge.** Every registered base and layer resolves to the forge type or builder the binding names.
  Every registered slot names a forge companion interface. Every registered setting names a `ServerConfig` field, or a
  builder where forge has no field. Every value a setting registers under the `enum` grammar is a case of that
  field's union.
- **forge to registry.** Every slot `ComposableSurface.slots ()` reflects, every setting
  `ComposableSurface.configKnobs ()` reflects, and every case of a registered setting's field is either registered or
  named in the binding as deliberately unregistered, with its reason.

So a forge change that adds a companion slot, a composition-mode setting or a mode value fails the check until the
same change either registers it in the profile (a profile version event, §P7) or says in the binding why the profile
does not. Bases and layers are checked in the first direction only: forge's composable surface enumerates slots and
settings, and has no enumeration of composition roots to check against.

A last case plants a mismatch for each check and asserts it is reported, so none of them can pass by reading nothing.

## Conformance is a test-time property

No `ToolUp.*` package references the specification, the profile or its fixtures, and nothing in `spec/` is packed or
loaded at run time. forge composes and runs exactly as it would with no Fuaran component present. The profile is what
forge **claims** about its surface, and the check is how a forge build proves the claim before it ships.

## Running it

```powershell
dotnet build src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "Composition profile"
```
