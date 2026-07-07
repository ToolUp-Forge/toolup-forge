// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Classification.Tests.InProcess.ClassificationGateTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Classification.Tests.Support
open ToolUp.Platform.Classification.Tests.Support.Doubles
open ToolUp.Platform.Classification.Tests.Contracts

let private classifier () : IFieldClassifier =
    DefaultFieldClassifier.create SampleRegistry.customer

let private fixture () : IFieldClassifierContract.FieldClassifierFixture = {
    Classifier = classifier ()
    Classifications = SampleRegistry.customer
}

/// A flat view of a Customer entity's fields, as the gate consumes it.
let private customerFields =
    Map.ofList [
        "Name", "Acme Ltd"
        "Email", "ceo@acme.example"
        "RevenueUsd", "1000000"
        "HealthNotes", "n/a"
        "DisplayHandle", "acme"
    ]

let private gateTests =
    testList "ClassificationGate — policy + redaction + audit" [
        testCaseAsync "defaultPolicy: Public + Confidential always Allow"
        <| async {
            let ctx = Ctx.plainUser "u1"
            Expect.equal (ClassificationGate.defaultPolicy Public ctx) Allow "Public allowed"
            Expect.equal (ClassificationGate.defaultPolicy Confidential ctx) Allow "Confidential allowed"
        }

        testCaseAsync "defaultPolicy: sensitive Redact for a plain user, Allow for a reader, Allow for admin"
        <| async {
            let plain = Ctx.plainUser "u1"
            let piiReader = Ctx.readerOf Pii "u2"
            let admin = Ctx.admin "u3"
            Expect.equal (ClassificationGate.defaultPolicy Pii plain) Redact "plain user redacted"
            Expect.equal (ClassificationGate.defaultPolicy Pii piiReader) Allow "PiiReader allowed"
            Expect.equal (ClassificationGate.defaultPolicy Pii admin) Allow "PlatformAdmin allowed"
            // A PiiReader is NOT automatically a FinancialReader.
            Expect.equal
                (ClassificationGate.defaultPolicy Financial piiReader)
                Redact
                "PiiReader does not satisfy Financial"
        }

        testCaseAsync "redactFields redacts sensitive fields for a non-reader, leaving Public/Confidential intact"
        <| async {
            let audit = InMemoryAuditLog()
            let ctx = Ctx.plainUser "u1"

            let! result =
                ClassificationGate.redactFields
                    (classifier ())
                    ClassificationGate.defaultPolicy
                    audit
                    ctx
                    "Customer"
                    customerFields

            Expect.equal result["Email"] ClassificationGate.RedactedPlaceholder "Pii redacted for plain user"
            Expect.equal result["RevenueUsd"] ClassificationGate.RedactedPlaceholder "Financial redacted"
            Expect.equal result["HealthNotes"] ClassificationGate.RedactedPlaceholder "Spi redacted"
            Expect.equal result["Name"] "Acme Ltd" "Confidential passes through"
            Expect.equal result["DisplayHandle"] "acme" "Public passes through"
        }

        testCaseAsync "redactFields leaves the Pii value intact for a PiiReader"
        <| async {
            let audit = InMemoryAuditLog()
            let ctx = Ctx.readerOf Pii "u2"

            let! result =
                ClassificationGate.redactFields
                    (classifier ())
                    ClassificationGate.defaultPolicy
                    audit
                    ctx
                    "Customer"
                    customerFields

            Expect.equal result["Email"] "ceo@acme.example" "Pii visible to a PiiReader"
            // But Financial (different axis) is still redacted.
            Expect.equal
                result["RevenueUsd"]
                ClassificationGate.RedactedPlaceholder
                "Financial still redacted for a PiiReader"
        }

        testCaseAsync
            "redactFields emits ClassifiedFieldRead audit (AuditOnRead fields only) with the redacted flag, value-free"
        <| async {
            let audit = InMemoryAuditLog()
            let ctx = Ctx.plainUser "u1"

            let! _ =
                ClassificationGate.redactFields
                    (classifier ())
                    ClassificationGate.defaultPolicy
                    audit
                    ctx
                    "Customer"
                    customerFields

            let reads =
                audit.AllEvents
                |> List.choose (fun (_, e) ->
                    match e with
                    | AuditEvent.ClassifiedFieldRead p -> Some p
                    | _ -> None)

            // Email (Pii) + HealthNotes (Spi) have AuditOnRead; RevenueUsd
            // (Financial) does NOT, so no read event for it even though it
            // was redacted.
            let paths = reads |> List.map _.FieldPath |> List.sort
            Expect.equal paths [ "Email"; "HealthNotes" ] "only AuditOnRead fields emit read events"
            Expect.all reads _.Redacted "plain user — both reads recorded as redacted"
            Expect.all reads (fun p -> p.UserId = "u1") "caller stamped"

            Expect.all
                reads
                (fun p -> p.Level = "Pii" || p.Level = "Spi")
                "level recorded; no field value travels in the payload shape"
        }

        testCaseAsync "redactFields records a non-redacted read for a reader (AuditOnRead set)"
        <| async {
            let audit = InMemoryAuditLog()
            let ctx = Ctx.readerOf Pii "u2"

            let! _ =
                ClassificationGate.redactFields
                    (classifier ())
                    ClassificationGate.defaultPolicy
                    audit
                    ctx
                    "Customer"
                    customerFields

            let emailRead =
                audit.AllEvents
                |> List.choose (fun (_, e) ->
                    match e with
                    | AuditEvent.ClassifiedFieldRead p when p.FieldPath = "Email" -> Some p
                    | _ -> None)
                |> List.tryHead

            match emailRead with
            | Some p -> Expect.isFalse p.Redacted "PiiReader's Email read recorded as not-redacted"
            | None -> failtest "expected a ClassifiedFieldRead for Email"
        }

        testCaseAsync "recordWrites emits ClassifiedFieldWritten for classified written fields only"
        <| async {
            let audit = InMemoryAuditLog()
            let ctx = Ctx.plainUser "u1"

            do!
                ClassificationGate.recordWrites (classifier ()) audit ctx "Customer" [
                    "Email"
                    "DisplayHandle"
                    "NotAField"
                ]

            let writes =
                audit.AllEvents
                |> List.choose (fun (_, e) ->
                    match e with
                    | AuditEvent.ClassifiedFieldWritten p -> Some p.FieldPath
                    | _ -> None)
                |> List.sort

            // Email is classified; DisplayHandle is Public (still classified
            // → recorded); NotAField is unclassified → no event.
            Expect.equal writes [ "DisplayHandle"; "Email" ] "only classified fields emit write events"
        }
    ]

[<Tests>]
let tests =
    testList "ToolUp.Platform.Classification" [
        IFieldClassifierContract.tests "DefaultFieldClassifier" fixture
        gateTests
    ]