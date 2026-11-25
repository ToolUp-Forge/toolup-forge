module ToolUp.AI.Client.Tests.Program

open ToolUp.AI.Client.Tests.NodeTest

let allTests =
    testList "ToolUp.AI.Client.Tests" [
        PlatformAIKeysAdminUITests.tests
        AIScreenEffectsTests.tests
        // ClientHostCapabilities routing (Platform.Client tier).
        ClientHostBridgeTests.tests
        // NotificationClient identity-aware SSE lifecycle
        // (Platform.Client tier).
        NotificationClientTests.tests
        // Boot-degradation surface (Platform.Client tier).
        BootDegradationTests.tests
        // ConsentBanner MVU core (Platform.Client tier).
        ConsentBannerTests.tests
        // Home/Overview landing selection: prepareModules
        // head-injection behind ClientConfig.HomeModule (Platform.Client
        // tier; ClientConfig.defaults only resolves under Fable).
        HomeLandingTests.tests
        // The ProcessedData summary envelope's browser half:
        // Fable.SimpleJson reads what the server's FableConverters wrote.
        ProcessedDataEnvelopeTests.tests
        // A conversation block part: the browser decodes the
        // parts the server's conversation-blob writer wrote.
        ConversationBlockPartDecodeTests.tests
        // The client's JSON response decode joins the algebra:
        // the pinned payload read in the browser, the proxy through the
        // registry, and the 785.F losses closed on the client.
        JsonClientDecodeTests.tests
        // Declared reads: identical in-flight calls share one
        // request, Cacheable reads are served stale-while-revalidate, and
        // the interceptor chain counts one request once.
        ReadPolicyTests.tests
        // The fetch transport, and same-tick calls as one request.
        RemoteBatchingTests.tests
        // Generated client proxies and encoders, transpiled.
        GeneratedProxyTests.tests
        // Module-contributed Home-widget seam: the
        // HomeWidgetRegistry contract (flatten + weight-sort) in
        // Platform.Client.
        HomeWidgetContributorTests.tests
        // Tenant-lifecycle diagnostics admin MVU
        // (TenantLifecycleAdminUI in Platform.Client tier).
        TenantLifecycleAdminUITests.tests
        // Background-jobs admin MVU: standing, ordering,
        // re-run offer and the TriggerOnce round-trip (Platform.Client).
        JobsAdminUITests.tests
        AdminScreenEffectsTests.tests
        AdminSubModelTests.tests
        // Sidebar admin-group role gate — platform-scoped vs team-scoped
        // group split behind the 4f.2 filter (Platform.Client tier).
        SidebarAdminGroupGateTests.tests
        SidebarAreaTests.tests
        // Nested multi-page module sidebar entries — buildSections/flatten
        // nesting + module-expand persistence + legacy blob (Platform.Client).
        SidebarNestingTests.tests
        // Per-user entry hiding: buildSections exclusion, the
        // Hidden items reveal section, and the legacy-blob load path
        // (Platform.Client).
        SidebarHidingTests.tests
        // Rail placement as declared data: an undeclared row
        // buckets exactly as before, a declared row lands in its slot, and
        // no reserved row can resolve to the collapsed `_other` catch-all
        // (the fresh-profile switcher defect).
        SidebarPlacementTests.tests
        // Rail keyboard navigation: the traversal order (a
        // collapsed group announced, not skipped), the arrow/Home/End
        // bindings, the roving single tab stop, and the modifier bail-out
        // that keeps Ctrl+K the command palette's.
        SidebarKeyboardTests.tests
        // Shell program-lifetime effects: the
        // outer-composer contract (withSidePanel must re-attach the
        // shell's background subscriptions; the admin-tile click-through
        // silent-no-op regression).
        ShellLifetimeEffectsTests.tests
        ShellUpdateEffectsTests.tests
        BootSourcesTests.tests
        ModuleHostTests.tests
        BrowserHistoryTests.tests
        // Shell slots: the prepared module list and the
        // built-ins' admin tiles over reference configurations, pinned by
        // baselines recorded before the slot refactor.
        ShellSlotSnapshotTests.tests
        ShellSlotTests.tests
        // The a11y floor over the shell rail's STATES:
        // the real component mounted in each rail state (both widths, both
        // areas, a collapsed group, the hidden-items list, the
        // no-active-team collapse) and run through the shipped rules.
        SidebarRailA11yTests.tests
        // Structural snapshot gate for the composed shell: the
        // rail's shape (section keys, row order, row ids, accessible names,
        // collapse flags) serialised per state and diffed against committed
        // baselines, so a shape change is a reviewable diff rather than a
        // discovery. Plus the two rendered-tree invariants the keyboard tests' pure
        // pack could only assert one side of.
        SidebarRailShapeSnapshotTests.tests
        // The a11y floor over an ordinary MODULE view — the
        // UsageDashboard SDK module driven through its real init/update
        // and rendered through `ModuleHarness.AssertAccessibleView`, so a
        // module gets the floor over its OWN `view` rather than over a
        // hand-written tree that can drift away from it.
        ModuleViewA11yTests.tests
        // The data-manager template publishes processed data:
        // `MyDataManager.ClientView.register()` declares
        // `ClientModule.withProcessedData`, and the extractor publishes
        // what `IngestSucceeded` recorded, driven through the template's
        // real `init` / `update` (Platform.Client tier).
        DataManagerTemplateProcessedDataTests.tests
        // The KB version-history client surface: the GP 11
        // gate (a single-version row renders byte-for-byte what it did
        // before the phase, asserted over mounted markup) and the
        // drawer's lazy-fetch MVU arms.
        KnowledgeVersionHistoryTests.tests
        // The module-visibility profile editor: the identity-rule seed for
        // an unconfigured scope, the exclusions round-trip the editor does
        // not surface, and the resolved-vs-edited distinction the pane
        // exists to make legible.
        ModuleVisibilityAdminUITests.tests
        // The remoting wire corpus's Fable parity leg: the
        // committed `tests/remoting-corpus/*.msgpack` fixtures decoded
        // through the MsgPack reader as Fable transpiles it, compared
        // against the same F# declaration the .NET suites compare
        // against. Two readers, one corpus, one set of expected values.
        RemotingCorpusParityTests.tests
        StreamingProxyTests.tests
        // ClientModule.withInspectState: the latest UiStateReport per
        // module at the shell publish points; undeclared modules report nothing.
        ModuleInspectStateTests.tests
        // The module dispatch prop is stable across renders.
        ModuleDispatchStabilityTests.tests
        // The sliced model store: store binding, selectors, shell
        // render scope, and the AG Grid props compare.
        SlicedStoreTests.tests
        // A tree surface's fuaran-* classes all have a rule in the reference
        // stylesheet of the renderer this project restored.
        TreeStylesheetTests.tests
        NodeToolkitTests.tests
        TreeViewParityTests.tests
        // The UI-awareness companion's inspect_active_module executor:
        // the live report for the request's active module and both status branches.
        UiAwarenessInspectTests.tests
        // The UI-awareness companion's list_active_actions executor:
        // enabled/disabled action states for the request's active module, and
        // both status branches.
        ListActiveActionsTests.tests
        // Every identity-change route clears the declared-read
        // cache: tokens, boot configuration, the team switch, another tab.
        IdentityChangeReadCacheTests.tests
        // The fact browse client companion and the knowledge
        // base list's fact-table badge.
        FactBrowseClientTests.tests
        // The sliced store reaches every composer: the HMR
        // pass-throughs, hydration parity, the AI composer's stable dispatch
        // and store, and the chrome's memoised context values.
        ComposerStoreTests.tests
        // An AI-composed shell wires the shell dispatcher, the
        // whole-tree hydrating binding adopts the server markup, and the
        // conversation panel's fact link, mounted.
        ComposerStoreTests.tests931
        // The team output-visibility section and the member
        // notice, mounted.
        TeamOutputVisibilityUITests.tests
        TeamScreenEffectsTests.tests
        // The panel draws a block part, re-read under the
        // reader's gate, with its furniture and the page's theme; a
        // conversation with no block renders as it did before.
        ConversationBlockTests.tests
        BlockRenderTests.tests
        // The chat watchdog measures idleness, not elapsed time:
        // a live turn's events re-arm it on both chat surfaces, and a silent
        // turn still gets the note.
        ChatWatchdogTests.tests
        ChatStreamTests.tests
        NarrativePromptAdminUITests.tests
        // Remote calls as effect values: interceptors, retry and the read
        // policy through the performer, one message per effect, and the
        // ported modules' stated effects.
        RemoteEffectTests.tests
    ]

// `output/Program.js ClientBench` runs the browser-runtime
// benchmark INSTEAD of the tests; `node --test` passes no arguments, so the
// ordinary run never reaches it.
[<EntryPoint>]
let main argv =
    if Array.contains "ClientBench" argv then
        ClientBench.run argv
        0
    else
        runTests allTests