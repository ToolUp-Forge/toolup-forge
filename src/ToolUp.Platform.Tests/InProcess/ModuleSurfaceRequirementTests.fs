module ToolUp.Platform.Tests.InProcess.ModuleSurfaceRequirementTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.SurfaceEnforcement

// ─── Phase 66 Stream B.3 — Module surface-requirement API tests ──────
//
// Pins the four pieces of the B.3 surface:
//
//   1. `ServerModule.create` carries the strict global default
//      (`SurfaceRequirement.userOrTeam`) and empty prefix / override
//      lists — pre-B.3 modules stay byte-identical.
//   2. The three new builders (`withDefaultSurfaceRequirement` /
//      `withRoutePrefix` / `withRouteSurfaceRequirement`) set the
//      fields the registry merge reads.
//   3. `ServerApp.addModule` fans the per-module declarations into the
//      app-level accumulators (`ModuleSurfaceDefaults` /
//      `RouteSurfaceOverrides`).
//   4. `SurfaceRequirementRegistry.merge` overlays the accumulators
//      onto the bridge registry with the documented precedence (module
//      exact wins; module prefixes append + longest-prefix-wins at
//      resolve time; case-insensitive method/path normalisation).
//
// `Visibility` smart-constructor coverage lives in a parallel `testList`
// at the foot — those predicates are typed `SubjectKind -> bool`, so
// the tests exercise them by enumerating every `SubjectKind` case.

let private sampleRequirement = SurfaceRequirement.public_

let private alternateRequirement = SurfaceRequirement.claimBearerOnly

[<Tests>]
let tests =
    testList "Phase 66 Stream B.3 — module surface-requirement API" [

        test "ServerModule.create carries the strict global default + empty lists" {
            let m = ServerModule.create "Foo"

            Expect.equal
                m.DefaultSurfaceRequirement
                SurfaceRequirement.userOrTeam
                "Default = userOrTeam (strict fail-closed per §3.0 OQ6)"

            Expect.isEmpty m.RoutePrefixes "no prefixes declared"
            Expect.isEmpty m.RouteSurfaceRequirements "no exact overrides declared"
        }

        test "withDefaultSurfaceRequirement replaces the module-level default" {
            let m =
                ServerModule.create "Foo"
                |> ServerModule.withDefaultSurfaceRequirement sampleRequirement

            Expect.equal m.DefaultSurfaceRequirement sampleRequirement "default replaced"
        }

        test "withDefaultSurfaceRequirement is last-write-wins" {
            let m =
                ServerModule.create "Foo"
                |> ServerModule.withDefaultSurfaceRequirement sampleRequirement
                |> ServerModule.withDefaultSurfaceRequirement alternateRequirement

            Expect.equal m.DefaultSurfaceRequirement alternateRequirement "last write wins"
        }

        test "withRoutePrefix accumulates in declaration order" {
            let m =
                ServerModule.create "Foo"
                |> ServerModule.withRoutePrefix "/api/foo/"
                |> ServerModule.withRoutePrefix "/api/foo/admin/"

            Expect.equal m.RoutePrefixes [ "/api/foo/"; "/api/foo/admin/" ] "two prefixes accumulated"
        }

        test "withRouteSurfaceRequirement accumulates exact (method, path) overrides" {
            let m =
                ServerModule.create "Foo"
                |> ServerModule.withRouteSurfaceRequirement "POST" "/api/foo/public/submit" alternateRequirement
                |> ServerModule.withRouteSurfaceRequirement "GET" "/api/foo/admin/list" sampleRequirement

            Expect.equal
                m.RouteSurfaceRequirements
                [
                    ("POST", "/api/foo/public/submit"), alternateRequirement
                    ("GET", "/api/foo/admin/list"), sampleRequirement
                ]
                "two overrides accumulated in declaration order"
        }

        test "ServerApp.addModule fans empty declarations into empty accumulators (pre-B.3 backcompat)" {
            // A module that declares no Phase 66 fields contributes
            // nothing to the app-level accumulators — registry stays
            // byte-identical to the fromServerConfig bridge.
            let m = ServerModule.create "Legacy"
            let app = ServerApp.empty |> ServerApp.addModule m

            Expect.isEmpty app.ModuleSurfaceDefaults "no prefixes → no module surface defaults"
            Expect.isEmpty app.RouteSurfaceOverrides "no overrides → no route overrides"
        }

        test "ServerApp.addModule fans declared prefixes × default into ModuleSurfaceDefaults" {
            let m =
                ServerModule.create "Forms"
                |> ServerModule.withDefaultSurfaceRequirement sampleRequirement
                |> ServerModule.withRoutePrefix "/api/forms/admin/"
                |> ServerModule.withRoutePrefix "/api/forms/public/"

            let app = ServerApp.empty |> ServerApp.addModule m

            Expect.equal
                app.ModuleSurfaceDefaults
                [
                    "/api/forms/admin/", sampleRequirement
                    "/api/forms/public/", sampleRequirement
                ]
                "one (prefix, default) per declared prefix"
        }

        test "ServerApp.addModule fans per-route overrides into RouteSurfaceOverrides" {
            let m =
                ServerModule.create "Forms"
                |> ServerModule.withRouteSurfaceRequirement "POST" "/api/forms/public/submit" alternateRequirement

            let app = ServerApp.empty |> ServerApp.addModule m

            Expect.equal
                app.RouteSurfaceOverrides
                [ ("POST", "/api/forms/public/submit"), alternateRequirement ]
                "one override accumulated"
        }

        test "ServerApp.addModule accumulates across multiple modules" {
            let formsModule =
                ServerModule.create "Forms"
                |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.userOrTeam
                |> ServerModule.withRoutePrefix "/api/forms/admin/"

            let publicModule =
                ServerModule.create "Public"
                |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.public_
                |> ServerModule.withRoutePrefix "/api/landing/"
                |> ServerModule.withRouteSurfaceRequirement "POST" "/api/landing/subscribe" alternateRequirement

            let app =
                ServerApp.empty
                |> ServerApp.addModule formsModule
                |> ServerApp.addModule publicModule

            Expect.equal
                app.ModuleSurfaceDefaults
                [
                    "/api/forms/admin/", SurfaceRequirement.userOrTeam
                    "/api/landing/", SurfaceRequirement.public_
                ]
                "two prefixes across two modules"

            Expect.equal
                app.RouteSurfaceOverrides
                [ ("POST", "/api/landing/subscribe"), alternateRequirement ]
                "one override from the second module"
        }

        test "SurfaceRequirementRegistry.merge with empty overlays returns the bridge unchanged" {
            // The pre-B.3 path: a deployment whose modules declare
            // nothing leaves the registry identical to fromServerConfig.
            let bridge =
                SurfaceRequirementRegistry.fromServerConfig {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.individual
                }

            let merged = bridge |> SurfaceRequirementRegistry.merge [] []

            Expect.equal merged.Exact bridge.Exact "exact entries unchanged"
            Expect.equal merged.ModulePrefixes bridge.ModulePrefixes "prefixes unchanged"
        }

        test "merge appends module prefix defaults to ModulePrefixes" {
            let bridge =
                SurfaceRequirementRegistry.fromServerConfig {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.individual
                }

            let merged =
                bridge
                |> SurfaceRequirementRegistry.merge [ "/api/forms/admin/", SurfaceRequirement.userOrTeam ] []

            Expect.contains
                merged.ModulePrefixes
                ("/api/forms/admin/", SurfaceRequirement.userOrTeam)
                "module prefix entry present"
        }

        test "merge folds exact overrides into the registry's Exact map" {
            let bridge =
                SurfaceRequirementRegistry.fromServerConfig {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.individual
                }

            let merged =
                bridge
                |> SurfaceRequirementRegistry.merge [] [ ("POST", "/api/forms/public/submit"), alternateRequirement ]

            Expect.equal
                (Map.tryFind ("POST", "/api/forms/public/submit") merged.Exact)
                (Some alternateRequirement)
                "module exact override visible at the normalised key"
        }

        test "merge normalises method to upper-case and path to lower-case" {
            let bridge =
                SurfaceRequirementRegistry.fromServerConfig {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.individual
                }

            // Declare a mixed-case key — merge should normalise so that
            // resolve hits regardless of casing in the inbound request.
            let merged =
                bridge
                |> SurfaceRequirementRegistry.merge [] [ ("post", "/API/Foo/SUBMIT"), alternateRequirement ]

            Expect.equal
                (Map.tryFind ("POST", "/api/foo/submit") merged.Exact)
                (Some alternateRequirement)
                "key stored normalised; resolve hits the registry"
        }

        test "merge module exact override wins over bridge exact entry" {
            // The bridge always emits ("GET", "/api/csrf-token") →
            // public_. A module declaring the same exact key with a
            // tighter requirement (claimBearerOnly is contrived here —
            // realistic cases pin Forms public-submit) must win.
            let bridge =
                SurfaceRequirementRegistry.fromServerConfig {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.individual
                }

            let merged =
                bridge
                |> SurfaceRequirementRegistry.merge [] [ ("GET", "/api/csrf-token"), alternateRequirement ]

            Expect.equal
                (Map.tryFind ("GET", "/api/csrf-token") merged.Exact)
                (Some alternateRequirement)
                "module declaration supersedes the bridge entry"
        }

        test "resolve picks the longest matching module prefix after merge" {
            let bridge = SurfaceRequirementRegistry.empty

            let merged =
                bridge
                |> SurfaceRequirementRegistry.merge [
                    "/api/forms/", SurfaceRequirement.userOrTeam
                    "/api/forms/public/", SurfaceRequirement.claimBearerOnly
                ] []

            // Request under the longer prefix gets the more-specific
            // declaration — same longest-prefix-wins semantics the
            // pre-B.3 registry already used.
            let resolved =
                SurfaceRequirementRegistry.resolve merged "POST" "/api/forms/public/submit"

            Expect.equal resolved SurfaceRequirement.claimBearerOnly "longer prefix wins"

            // Request under the shorter prefix falls back to it.
            let resolvedAdmin =
                SurfaceRequirementRegistry.resolve merged "POST" "/api/forms/admin/save"

            Expect.equal resolvedAdmin SurfaceRequirement.userOrTeam "shorter prefix when no longer match"
        }

        test "end-to-end: ServerApp + addModule + merge resolves an admin route" {
            // The realistic shape: build a module, fold it into ServerApp,
            // and check the registry resolves a request inside its prefix
            // to the module's default.
            let formsModule =
                ServerModule.create "Forms"
                |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.userOrTeam
                |> ServerModule.withRoutePrefix "/api/forms/admin/"
                |> ServerModule.withRouteSurfaceRequirement
                    "POST"
                    "/api/forms/public/submit"
                    SurfaceRequirement.claimBearerOnly

            let app = ServerApp.empty |> ServerApp.addModule formsModule

            let registry =
                SurfaceRequirementRegistry.fromServerConfig {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.individual
                }
                |> SurfaceRequirementRegistry.merge app.ModuleSurfaceDefaults app.RouteSurfaceOverrides

            // Admin route → module default.
            let adminResolved =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/forms/admin/save"

            Expect.equal adminResolved SurfaceRequirement.userOrTeam "admin route hits module default"

            // Public-submit override → claimBearerOnly.
            let publicResolved =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/forms/public/submit"

            Expect.equal publicResolved SurfaceRequirement.claimBearerOnly "exact override wins"
        }
    ]

[<Tests>]
let visibilityTests =
    testList "Phase 66 Stream B.3 — Visibility smart constructors" [

        test "visibleToAll admits every SubjectKind" {
            let v = Visibility.visibleToAll
            Expect.isTrue (v AnonymousKind) "anonymous admitted"
            Expect.isTrue (v UserKind) "user admitted"
            Expect.isTrue (v TeamMemberKind) "team member admitted"
            Expect.isTrue (v ClaimBearerKind) "claim bearer admitted"
        }

        test "visibleToAuthenticated rejects only AnonymousKind" {
            let v = Visibility.visibleToAuthenticated
            Expect.isFalse (v AnonymousKind) "anonymous rejected"
            Expect.isTrue (v UserKind) "user admitted"
            Expect.isTrue (v TeamMemberKind) "team member admitted"
            Expect.isTrue (v ClaimBearerKind) "claim bearer admitted (token-bound auth counts)"
        }

        test "visibleToAnonymous admits only AnonymousKind" {
            let v = Visibility.visibleToAnonymous
            Expect.isTrue (v AnonymousKind) "anonymous admitted"
            Expect.isFalse (v UserKind) "user rejected"
            Expect.isFalse (v TeamMemberKind) "team member rejected"
            Expect.isFalse (v ClaimBearerKind) "claim bearer rejected"
        }

        test "visibleTo admits only the explicit kind set" {
            let v = Visibility.visibleTo [ TeamMemberKind ]
            Expect.isFalse (v AnonymousKind) "anonymous rejected"
            Expect.isFalse (v UserKind) "user rejected"
            Expect.isTrue (v TeamMemberKind) "team member admitted"
            Expect.isFalse (v ClaimBearerKind) "claim bearer rejected"
        }

        test "visibleTo with empty list rejects every kind" {
            let v = Visibility.visibleTo []
            Expect.isFalse (v AnonymousKind) "empty list rejects anonymous"
            Expect.isFalse (v UserKind) "empty list rejects user"
            Expect.isFalse (v TeamMemberKind) "empty list rejects team member"
            Expect.isFalse (v ClaimBearerKind) "empty list rejects claim bearer"
        }
    ]