// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.NotificationChannels.Tests.Program

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.NotificationChannels.Tests" [
        RedisNotificationChannelTests.tests
        RedisDistributedLockTests.tests
        SmtpNotificationSinkTests.tests
        SendGridNotificationSinkTests.tests
        TwilioNotificationSinkTests.tests
        TwilioWhatsAppNotificationSinkTests.tests
        WebPushNotificationSinkTests.tests
        MetaWhatsAppCloudNotificationSinkTests.tests
        RedisNotificationChannelHealthTests.tests
    ]

/// Phase 722 - the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery.
let private sourceLinked = [
    {
        TestRegistrationGuard.Binding = "ToolUp.Platform.Tests.InProcess.TransactionalDispatcherTests.tests"
        TestRegistrationGuard.Reason =
            "source-linked for its CapturingAuditLog fixture, which the Meta WhatsApp sink's "
            + "delivery cases assert against; its cases are the Platform pack's and run there."
    }
]

let allTests =
    TestRegistrationGuard.withGuardExempting (Assembly.GetExecutingAssembly()) 0 sourceLinked registeredTests

// Sequenced by default - Expecto deadlocks when parallel tests write to
// the console. `--parallel` still overrides (Phase 617).
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests