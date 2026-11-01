// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The package closures the three template gates pack at the gate version:
/// `VerifyTemplates` (`templates`), `VerifyPackagedModuleTemplate`
/// (`packagedModule`) and `VerifyDataPlatformTemplate` (`dataPlatform`). Each
/// is the `Closure` of its gate's `TemplateGateSpec` in `Build.fs`; the
/// mechanism that packs, evicts and reconciles them is `TemplateGate.run`.
/// Kept apart from the driver so the Build test pack can hold the closures
/// to the tree.
module ToolUp.Forge.TemplateGatePackages

open ToolUp.Platform.Build

// The SDK packages the gated templates reference, plus the closure
// of ToolUp.* packages those declare. Kept explicit rather than
// globbed: packing all ~43 is minutes of work for packages no
// template names. Adding an SDK->SDK dependency without adding it
// here does not silently degrade — the gate's NU1603 escalation turns
// the resulting version fallback into a build error naming the
// missing package.
//
// Each entry declares its PackageId AND the project that
// produces it. They were one string, with the project derived as
// `src/<id>/<id>.fsproj`; that assumption died when the AG Grid /
// AG Charts bindings took `ToolUp.Feliz.*` ids while their projects
// stayed at `src/Feliz.*/`. The gate reconciles the two
// against what was actually emitted (`TemplateGate.unproducedIds`),
// so an id this repo does not produce fails the gate by name rather
// than falling through to a stranger's package on nuget.org.
let templates = [
    TemplateGate.package "ToolUp.Platform.Core" "src/ToolUp.Platform.Core/ToolUp.Platform.Core.fsproj"
    TemplateGate.package "ToolUp.Platform.Client" "src/ToolUp.Platform.Client/ToolUp.Platform.Client.fsproj"
    TemplateGate.package "ToolUp.Platform.Server" "src/ToolUp.Platform.Server/ToolUp.Platform.Server.fsproj"
    // Phase 307 — declared by Platform.Client (the UI toolkit,
    // promoted out of the client tier into its own package). This is
    // exactly the SDK->SDK dependency the note above describes: without
    // it, Platform.Client packed at the gate version would declare a
    // ToolUp.Platform.UI the scratch feed cannot serve, and the NU1603
    // escalation would fail the gate by name.
    TemplateGate.package "ToolUp.Platform.UI" "src/ToolUp.Platform.UI/ToolUp.Platform.UI.fsproj"
    // Also declared by Platform.Client, since Phase 344 promoted the
    // AG Grid / AG Charts bindings out of the client tier. They were
    // not added here at the time, and the consequence is not benign:
    // BOTH bare `Feliz.*` ids exist on nuget.org (Feliz.AgGrid 0.0.1,
    // Feliz.AgCharts 0.23.0 — unrelated packages by another author),
    // so restore silently fell through to a stranger's package and
    // NU1603 turned that into a hard error. The `templates` CI job
    // was red on main from 344 until Phase 307 found it while adding
    // ToolUp.Platform.UI above — the very case the note at the head
    // of this list describes.
    //
    // Phase 754 settled the ids for good: the packages are
    // `ToolUp.Feliz.*` now (that prefix is also the glob the repo's
    // nuget.org Trusted Publishing policy is registered against), and
    // the id/project split above is what lets the projects keep their
    // `src/Feliz.*/` directories while the emitted ids move. The
    // reconciliation after the pack loop is the structural successor
    // to "someone remembers to add the package": it fails on any id
    // this repo does not itself emit, which is precisely what a
    // stranger's package is.
    TemplateGate.package "ToolUp.Feliz.AgGrid" "src/Feliz.AgGrid/Feliz.AgGrid.fsproj"
    TemplateGate.package "ToolUp.Feliz.AgCharts" "src/Feliz.AgCharts/Feliz.AgCharts.fsproj"
    // Declared by Platform.Core.
    TemplateGate.package "ToolUp.AI.Wire" "src/ToolUp.AI.Wire/ToolUp.AI.Wire.fsproj"
    // Declared by AI.Wire and Platform.Core (Phase 128).
    TemplateGate.package "ToolUp.Platform.Transport" "src/ToolUp.Platform.Transport/ToolUp.Platform.Transport.fsproj"
    // Declared by Platform.Server, and its own ProjectReference.
    TemplateGate.package "ToolUp.Graph.InMemory" "src/ToolUp.Graph.InMemory/ToolUp.Graph.InMemory.fsproj"
    TemplateGate.package "ToolUp.Graph.Core" "src/ToolUp.Graph.Core/ToolUp.Graph.Core.fsproj"
]

// The VerifyTemplates closure plus the Build package: the generated
// repo's Build.fsproj references ToolUp.Platform.Build for the
// packaged-module conformance target. Everything else it restores
// (Feliz, Fable.*, Expecto, FAKE) comes from nuget.org, which is
// what a real consumer does.
let packagedModule =
    templates
    @ [
        TemplateGate.package "ToolUp.Platform.Build" "src/ToolUp.Platform.Build/ToolUp.Platform.Build.fsproj"
    ]

// The VerifyTemplates closure plus every ToolUp.* package the scaffold's
// server, module and test projects reference, and their own ToolUp.*
// dependencies. Incomplete here fails the gate's `StrayPins` check by name.
let dataPlatform =
    templates
    @ [
        TemplateGate.package "ToolUp.AI.Core" "src/ToolUp.AI.Core/ToolUp.AI.Core.fsproj"
        TemplateGate.package "ToolUp.AI.Server" "src/ToolUp.AI.Server/ToolUp.AI.Server.fsproj"
        TemplateGate.package "ToolUp.RAG.Core" "src/ToolUp.RAG.Core/ToolUp.RAG.Core.fsproj"
        TemplateGate.package "ToolUp.RAG.Server" "src/ToolUp.RAG.Server/ToolUp.RAG.Server.fsproj"
        TemplateGate.package "ToolUp.Facts.Core" "src/ToolUp.Facts.Core/ToolUp.Facts.Core.fsproj"
        TemplateGate.package "ToolUp.Facts.Shared" "src/ToolUp.Facts.Shared/ToolUp.Facts.Shared.fsproj"
        TemplateGate.package "ToolUp.Facts.Server" "src/ToolUp.Facts.Server/ToolUp.Facts.Server.fsproj"
        TemplateGate.package "ToolUp.ArtefactSigning" "src/ToolUp.ArtefactSigning/ArtefactSigning.fsproj"
        TemplateGate.package "ToolUp.PublicRendering" "src/ToolUp.PublicRendering/ToolUp.PublicRendering.fsproj"
        TemplateGate.package
            "ToolUp.KnowledgeBase.Core"
            "src/ToolUp.KnowledgeBase.Core/ToolUp.KnowledgeBase.Core.fsproj"
        TemplateGate.package
            "ToolUp.KnowledgeBase.Server"
            "src/ToolUp.KnowledgeBase.Server/ToolUp.KnowledgeBase.Server.fsproj"
        TemplateGate.package "ToolUp.OpenXml" "src/ToolUp.OpenXml/ToolUp.OpenXml.fsproj"
        TemplateGate.package "ToolUp.Tabular" "src/ToolUp.Tabular/ToolUp.Tabular.fsproj"
        TemplateGate.package "ToolUp.AIProviders.Claude" "src/AIProviders/Claude/ClaudeAIProvider.fsproj"
        TemplateGate.package
            "ToolUp.EmbeddingProviders.Local"
            "src/EmbeddingProviders/Local/LocalEmbeddingProvider.fsproj"
        TemplateGate.package "ToolUp.VectorStores.Hnsw" "src/VectorStores/Hnsw/HnswVectorStore.fsproj"
        TemplateGate.package "ToolUp.AuthProviders.Oidc" "src/AuthProviders/Oidc/OidcAuthProvider.fsproj"
    ]
// ─── The three gates, as specs ───────────────────────────────────────
//
// Each is run by `TemplateGate.run` from its target in `Build.fs`
// (`dotnet run --project Build.fsproj -- <Name>`). They differ only in the
// data below; the pack, the eviction, the reconciliation and the checks are
// written once. Each keeps its own scratch feed and scratch root.

/// `VerifyTemplates` — the root-inheriting `dotnet new` scaffolds compile.
///
/// The `templates/` scaffolds are shipped to consumers via `dotnet new`, and
/// nothing compiled them: two had been broken for an unknown span (a
/// `ToolUp.Remoting.Client` reference the 0.4.4 fold deleted, and an
/// `IDataSource` shape the SDK no longer had), so a consumer running
/// `dotnet new` got a project that did not compile.
///
/// Not in `ToolUp.Forge.sln`: template projects reference `ToolUp.Platform.*`
/// by PackageReference, as a consumer's copy does, and CI creates an EMPTY
/// `../../local-nuget-feed`, so a template project in the solution would fail
/// restore and take `verify-all` with it. The gate supplies the packages
/// instead of assuming them.
///
/// Not covered, deliberately: `templates/safer/` and
/// `templates/platformsdk-solution/` are standalone solutions with their own
/// nuget.config and (safer) a literal `TOOLUP_SDK_VERSION` placeholder, so
/// neither is buildable in-repo without rewriting what makes it a template —
/// the class the two instantiating gates below exist for.
let verifyTemplates: TemplateGate.TemplateGateSpec = {
    Name = "VerifyTemplates"
    Closure = templates
    Feed = "obj/template-gate-feed"
    Checks = []
    Scaffold =
        TemplateGate.InPlace [
            "templates/platformsdk-application/src/MyApp-Client/MyApp-Client.fsproj"
            "templates/platformsdk-application/src/MyApp-Server/MyApp-Server.fsproj"
            "templates/platformsdk-datamanager/MyDataManager/MyDataManager.fsproj"
            "templates/platformsdk-module/MyModule/MyModule.fsproj"
        ]
    Done = ""
}

/// The packaged-module template's directory.
let packagedModuleTemplateDir = "templates/platformsdk-module-packaged"

/// `VerifyPackagedModuleTemplate` — scaffold -> build -> both conformance
/// layers -> pack, for `platformsdk-module-packaged`.
///
/// That template is a whole repository (its own CPM props, nuget.config,
/// global.json and a literal `TOOLUP_SDK_VERSION` placeholder), so it is
/// `dotnet new`ed into a scratch directory OUTSIDE the repo — nuget.config
/// files merge up the tree, so forge's sources would paper over a broken
/// feed declaration in the template — and its own pipeline runs. One
/// command covers both conformance layers: the scaffold's `Pack` depends on
/// `Test` (the module-seam contract pack) and `VerifyPackagedModule` (the
/// packaging layout laws). The template's test project VENDORS the laws
/// half of the SDK's ModuleContract pack (the SDK's test project is not
/// packable), so the copy is compared with its source first.
let verifyPackagedModuleTemplate: TemplateGate.TemplateGateSpec =
    let name = "Gate.Module"

    {
        Name = "VerifyPackagedModuleTemplate"
        Closure = packagedModule
        Feed = "obj/packaged-module-template-feed"
        Checks = [
            TemplateGate.VendoredPack(
                "src/ToolUp.Platform.Tests/Contracts/ModuleContract.fs",
                packagedModuleTemplateDir + "/tests/MyModule.Tests/Contracts/ModuleContract.fs",
                "// ── a conforming reference module"
            )
        ]
        Scaffold =
            TemplateGate.Instantiate {
                Template = packagedModuleTemplateDir
                ShortName = "platformsdk-module-packaged"
                Name = name
                Args = [ "--namespace-root"; name ]
                Scratch = "tu-pmt"
                Steps = [
                    TemplateGate.Dotnet [ "run"; "--project"; "Build.fsproj"; "--"; "Pack" ]
                    TemplateGate.Verify(
                        TemplateGate.StrayPins(Some(System.IO.Path.Combine("src", name, "obj", "project.assets.json")))
                    )
                    // The half a pre-Pack declaration check cannot prove: what
                    // NuGet actually produced.
                    TemplateGate.Verify(
                        TemplateGate.NupkgLayout(
                            [
                                "fable/" + name + ".fsproj"
                                "fable/SharedTypes.fs"
                                "fable/ClientModel.fs"
                                "fable/Icons.fs"
                                "fable/ClientRegister.fs"
                                "fable/icons/module-icon.svg"
                            ],
                            [ "fable/Server.fs" ]
                        )
                    )
                ]
            }
        Done = "scaffold -> build -> conformance -> pack, green"
    }

/// The semicolon-separated variable that carries the private publication-boundary
/// token list when `neutrality-tokens.local.txt` is absent: a development and CI
/// knob read by this driver and the OSS guard tests, never by shipped code.
let neutralityTokensVariable = "TOOLUP_NEUTRALITY_TOKENS"

/// The data-platform template's directory.
let dataPlatformTemplateDir = "templates/platformsdk-dataplatform"

/// `VerifyDataPlatformTemplate` — the `platformsdk-dataplatform` template
/// scaffolds outside the repo, builds, and passes its own end-to-end test
/// (which uploads the synthetic delivery, writes both fact tables,
/// generates the grounded report and probes the gated page anonymously and
/// as a reader). It also holds the publication boundary: the template's
/// files and the scaffolded tree are scanned for the private vocabulary the
/// OSS guard bans, so a regeneration from an upstream composition cannot
/// leak a name into a public scaffold.
let verifyDataPlatformTemplate: TemplateGate.TemplateGateSpec =
    let name = "GateData"

    {
        Name = "VerifyDataPlatformTemplate"
        Closure = dataPlatform
        Feed = "obj/dataplatform-template-feed"
        Checks = [ TemplateGate.Neutral(TemplateGate.TemplateFiles, neutralityTokensVariable) ]
        Scaffold =
            TemplateGate.Instantiate {
                Template = dataPlatformTemplateDir
                ShortName = "platformsdk-dataplatform"
                Name = name
                Args = []
                Scratch = "tu-dpt"
                Steps = [
                    TemplateGate.Verify(TemplateGate.Neutral(TemplateGate.ScaffoldedTree, neutralityTokensVariable))
                    TemplateGate.Dotnet [ "build"; name + ".slnx"; "--nologo" ]
                    TemplateGate.Verify(TemplateGate.StrayPins None)
                    TemplateGate.Dotnet [
                        "run"
                        "--project"
                        System.IO.Path.Combine("tests", name + ".Tests", name + ".Tests.fsproj")
                        "--no-build"
                    ]
                ]
            }
        Done = "neutral -> scaffold -> build -> end-to-end test, green"
    }

/// The three gates, for the checks that hold them to each other.
let all = [ verifyTemplates; verifyPackagedModuleTemplate; verifyDataPlatformTemplate ]