module ToolUp.AI.Client.Tests.Program

open ToolUp.AI.Client.Tests.NodeTest

let allTests =
    testList "ToolUp.AI.Client.Tests" [
        PlatformAIKeysAdminUITests.tests
        // Phase 110 — ClientHostCapabilities routing (Platform.Client tier).
        ClientHostBridgeTests.tests
        // Phase 117 — NotificationClient identity-aware SSE lifecycle
        // (Platform.Client tier).
        NotificationClientTests.tests
        // Phase 121 — boot-degradation surface (Platform.Client tier).
        BootDegradationTests.tests
        // Phase 159 — ConsentBanner MVU core (Platform.Client tier).
        ConsentBannerTests.tests
        // Phase 171 — Home/Overview landing selection: prepareModules
        // head-injection behind ClientConfig.HomeModule (Platform.Client
        // tier; ClientConfig.defaults only resolves under Fable).
        HomeLandingTests.tests
        // Phase 217 — module-contributed Home-widget seam: the
        // HomeWidgetRegistry contract (flatten + weight-sort) in
        // Platform.Client.
        HomeWidgetContributorTests.tests
        // Phase 54e — tenant-lifecycle diagnostics admin MVU
        // (TenantLifecycleAdminUI in Platform.Client tier).
        TenantLifecycleAdminUITests.tests
        // Sidebar admin-group role gate — platform-scoped vs team-scoped
        // group split behind the 4f.2 filter (Platform.Client tier).
        SidebarAdminGroupGateTests.tests
        SidebarAreaTests.tests
        // Nested multi-page module sidebar entries — buildSections/flatten
        // nesting + module-expand persistence + legacy blob (Platform.Client).
        SidebarNestingTests.tests
    ]

[<EntryPoint>]
let main _argv = runTests allTests