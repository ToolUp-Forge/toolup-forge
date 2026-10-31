# forge composition profile

This folder publishes **forge's composition profile**, `server-app-host` 7.0: the vocabulary a composition carries when
forge is the evaluator it reduces to. It is a profile of the
[App-composition wire specification](https://github.com/Fuaran-Build/fuaran-app-composition-specification), format
version 1, which registers it in its §8.5 and names this folder as its home. forge is the evaluator Fuaran app
compositions reduce to; the specification defines the neutral document, and this profile says what its slots mean
for forge.

| File | What it is |
|---|---|
| [`composition-profile.md`](composition-profile.md) | The profile: token vocabularies (§P2), profile-only node kinds (§P3), composition rules (§P4), conformance (§P6), forward coupling (§P7) and the facet declaration (§P8). Normative. |
| [`registry.json`](registry.json) | The same profile as data, for §P2, §P3 and §P8. Normative, and held to the document by the gate. |
| [`fixtures/manifest.json`](fixtures/manifest.json) | The enumeration of the profile's vectors with their digests, the facet declaration, and the pin of the specification's own facet-family vectors for this profile. |
| [`fixtures/profile-server-app-host/`](fixtures/profile-server-app-host) | The profile's vectors: its tokens, its profile-only kinds and its rules, each a whole composition. Synthetic: none is a captured application. |
| [`fixtures/core/`](fixtures/core) | Copies of the specification's vectors this profile is certified against, pinned by commit and digest. The specification remains their home. |
| [`surface-binding.json`](surface-binding.json) | forge's binding of each registered token to the type, field or builder it reduces to, and the parts of forge's composable surface the profile deliberately does not register. Not part of the wire format. |

## How it is checked

The profile's gate is `CompositionProfileSurfaceTests` in the Platform test pack, which `verify.ps1` runs. It fails
when the document and the registry disagree, when a fixture does not match its recorded digest, when §P8 declares a
facet value no pinned specification vector demonstrates (or a pinned vector demonstrates one it does not declare),
when a vector's settings contradict the registered grammars, and when the registry and forge's reflected composable
surface disagree. A forge change that moves that surface therefore changes this folder in the same commit. The
conformance statement is [`docs/platform/composition-profile.md`](../docs/platform/composition-profile.md).

## Versioning

The profile version moves independently of forge's package versions and of the specification's format version: it
moves when the vocabulary moves (§P1). Pin a profile version, not a forge commit. 7.0 is the version at which the
profile moved here; nothing normative changed with the move.

## Licence

Apache-2.0, as the rest of this repository — see [`LICENSE`](../LICENSE) and [`NOTICE.md`](../NOTICE.md). The files under
`fixtures/core/` are copies of Apache-2.0 vectors from the specification named above.
