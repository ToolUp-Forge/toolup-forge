# MixedMode — Phase 66 worked example

End-to-end demonstration of the mixed-mode platform shape — a single deployment serving three concurrent subject shapes: anonymous visitors, authenticated team members, and validated share-token bearers.

```fsharp
Surfaces = [
    SurfaceProfile.anonymous
    SurfaceProfile.team
    SurfaceProfile.claimBearer
]
```

## What this demonstrates

1. **Server composition** — `samples/MixedMode/src/Server/Server.fs` declares the mixed Surfaces list and registers three `ServerModule`s with distinct `DefaultSurfaceRequirement` predicates:
   - `Landing` — `SurfaceRequirement.public_` (every subject kind admitted).
   - `TeamDashboard` — `SurfaceRequirement.teamScoped` (TeamMember only).
   - `SharePortal` — `SurfaceRequirement.claimBearerOnly` (ClaimBearer only).
2. **Client composition** — `samples/MixedMode/src/Client/Client.fs` registers three `ClientModule`s with matching `Visibility: SubjectKind -> bool` predicates, so the SDK shell's sidebar filter hides modules the resolved subject can't reach.
3. **Substrate auto-wiring** — declaring `Team _` in `Surfaces` auto-registers `ITeamStore` (default `BlobTeamStore`); declaring `ClaimBearer _` auto-promotes `IShareTokenStore` to `BlobShareTokenStore`. The consumer writes nothing extra to enable either store.

## Layout

```
samples/MixedMode/
├── README.md
├── MixedMode.sln
├── .config/
│   └── dotnet-tools.json        — fable + fantomas
└── src/
    ├── Server/
    │   ├── Server.fs            — mixed-mode composition root + 3 ServerModules
    │   └── Server.fsproj
    └── Client/
        ├── Client.fs            — 3 ClientModules with distinct Visibility predicates
        └── Client.fsproj        — Nullable=disable per Fable-ecosystem mandate
```

## Verification

```powershell
# .NET-side build (both projects)
dotnet build samples/MixedMode/MixedMode.sln

# Fable compile (Client tier — drives ToolUp.Platform.Client through the Fable compiler)
cd samples/MixedMode/src/Client
dotnet tool restore
dotnet fable -o output --noCache
```

Both passes ship clean. The Fable acceptance gate catches Client-tier regressions in the SDK that pure `dotnet build` is blind to (nullable-reference cascades through pre-nullable Fable libraries, Erase / inline regressions, JS import-statement breakage).

## Scope

Illustrative sample by design — demonstrates the composition shape (Surfaces list, per-module `DefaultSurfaceRequirement`, per-module `Visibility`) without shipping a runnable browser host. A full end-to-end deployment adds:

- An `IAuthProvider` for the `Team` shape — `ServerApp.withAuth (AuthProvider.fromEnv logger OidcAuthProvider.fromConfig)` in the composition root.
- The Vite + `package.json` + `index.html` + `index.css` boot scaffolding the Client tier needs to mount in a browser (see `samples/MinimalClient/` for the in-tree minimal shape).
- Module-domain code — handlers + Fable.Remoting APIs + per-module data types.

The point of this sample is the *shape* of the Surfaces composition, not the running app. Consumers adopting the mixed-mode model copy the registration patterns shown here.

## Ports

This sample is illustrative and does not bind a port by default. If an operator wants to `dotnet run --project src/Server/Server.fsproj` for a manual boot check, the SDK reads `TOOLUP_PORT` from the environment; allocate from the application class band per the workspace `CLAUDE.md` port table.

## See also

- [`docs/platform/surfaces.md`](../../docs/platform/surfaces.md) — full Surfaces / Subject / SurfaceRequirement reference.
- [`docs/migrations/0.X.0-platform-mode-to-surfaces.md`](../../docs/migrations/0.X.0-platform-mode-to-surfaces.md) — consumer migration guide.
- [`samples/HelloWorld/`](../HelloWorld/) — pure-Individual deployment (single-shape `Surfaces.individual`).
- [`samples/MinimalApp/`](../MinimalApp/) — pure-Anonymous deployment (single-shape `Surfaces.anonymous`).
- [`samples/MinimalClient/`](../MinimalClient/) — minimal Fable smoke-test sample.
