// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IWhatsAppTemplateRegistryContract

open Expecto
open ToolUp.Platform

/// Contract assertions every `IWhatsAppTemplateRegistry` must satisfy
/// (Phase 827). A binding supplies a factory that returns a registry
/// holding exactly the templates it is given — the blob-backed default
/// writes them where it reads them, a database-backed one inserts rows —
/// so every law below is stated against a known population.
///
/// The laws are the ones the WhatsApp send policy leans on: a registered
/// template comes back exactly as registered (its arity is what a send is
/// checked against), an unregistered name is `None` rather than a guess
/// or an exception, a name that could not be a template name is `None`
/// too (the policy refuses on `None`, so a registry that threw instead
/// would turn a malformed send into a crashed publish), and the answer
/// does not depend on what was asked before (portability rule 4).
let tests (name: string) (factory: WhatsAppTemplateDescriptor list -> Async<IWhatsAppTemplateRegistry>) =
    let reminder: WhatsAppTemplateDescriptor = {
        Name = "appointment_reminder"
        Languages = [ "en_GB"; "cy" ]
        HeaderParameterCount = 1
        BodyParameterCount = 2
        ButtonParameterCount = 1
    }

    let shipped: WhatsAppTemplateDescriptor = {
        Name = "order-shipped"
        Languages = [ "en_US" ]
        HeaderParameterCount = 0
        BodyParameterCount = 1
        ButtonParameterCount = 0
    }

    testList $"{name} — IWhatsAppTemplateRegistry contract" [
        testCaseAsync "a registered template is returned exactly as registered"
        <| async {
            let! registry = factory [ reminder; shipped ]
            let! found = registry.GetTemplate reminder.Name
            Expect.equal found (Some reminder) "name, languages and every component's arity round-trip"

            let! other = registry.GetTemplate shipped.Name
            Expect.equal other (Some shipped) "each template is answered independently"
        }

        testCaseAsync "an unregistered name answers None"
        <| async {
            let! registry = factory [ reminder ]
            let! missing = registry.GetTemplate "not_registered"
            Expect.isNone missing "a registry never vouches for a template it does not hold"

            let! empty = factory []
            let! none = empty.GetTemplate reminder.Name
            Expect.isNone none "an empty registry knows nothing"
        }

        testCaseAsync "a name that cannot be a template name answers None rather than throwing"
        <| async {
            let! registry = factory [ reminder ]

            for bad in [ ""; "../appointment_reminder"; "a/b"; "with space" ] do
                let! found = registry.GetTemplate bad
                Expect.isNone found $"'%s{bad}' is not a template name"
        }

        testCaseAsync "the answer does not depend on earlier lookups"
        <| async {
            let! registry = factory [ reminder ]
            let! first = registry.GetTemplate reminder.Name
            let! _ = registry.GetTemplate "not_registered"
            let! _ = registry.GetTemplate shipped.Name
            let! again = registry.GetTemplate reminder.Name
            Expect.equal again first "stateless between calls"
        }
    ]