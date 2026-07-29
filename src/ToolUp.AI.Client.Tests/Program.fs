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
        // Phase 572 — per-user entry hiding: buildSections exclusion, the
        // Hidden items reveal section, and the legacy-blob load path
        // (Platform.Client).
        SidebarHidingTests.tests
        // Phase 611 — rail placement as declared data: an undeclared row
        // buckets exactly as before, a declared row lands in its slot, and
        // no reserved row can resolve to the collapsed `_other` catch-all
        // (the Phase 608 fresh-profile switcher defect).
        SidebarPlacementTests.tests
        // Phase 612 — rail keyboard navigation: the traversal order (a
        // collapsed group announced, not skipped), the arrow/Home/End
        // bindings, the roving single tab stop, and the modifier bail-out
        // that keeps Ctrl+K the Phase 571 palette's.
        SidebarKeyboardTests.tests
        // Phase 573 follow-up — shell program-lifetime effects: the
        // outer-composer contract (withSidePanel must re-attach the
        // shell's background subscriptions; the admin-tile click-through
        // silent-no-op regression).
        ShellLifetimeEffectsTests.tests
        // Phase 610 — the Phase 180 a11y floor over the shell rail's STATES:
        // the real component mounted in each rail state (both widths, both
        // areas, a collapsed group, the hidden-items list, the
        // no-active-team collapse) and run through the shipped rules.
        SidebarRailA11yTests.tests
    ]

[<EntryPoint>]
let main _argv = runTests allTests