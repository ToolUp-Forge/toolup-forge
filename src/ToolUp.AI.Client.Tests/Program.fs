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
    ]

[<EntryPoint>]
let main _argv = runTests allTests