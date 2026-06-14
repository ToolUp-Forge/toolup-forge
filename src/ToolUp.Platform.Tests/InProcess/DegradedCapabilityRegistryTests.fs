module ToolUp.Platform.Tests.InProcess.DegradedCapabilityRegistryTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.DegradedCapabilities

// ─── Phase 118 — degraded-capability registry ────────────────────────
//
// The registry is the substrate behind the "boots fine but a capability
// is down" signal. These tests pin the three behaviours the read
// surfaces and the ComposeEncryption first-adopter rely on:
//   * empty by default (GP 13 — /health stays byte-for-byte unchanged),
//   * register makes the entry visible; clear removes it (the
//     "successful resubscribe clears it" acceptance), and
//   * re-registering the same id preserves the original DegradedSince so
//     the trail records when the degradation FIRST began.

let private entry (capability: string) (since: DateTimeOffset) : DegradedCapability = {
    Capability = capability
    DegradedSince = since
    Reason = "test reason"
    Impact = "test impact"
    Remediation = "test remediation"
}

[<Tests>]
let tests =
    testList "DegradedCapabilityRegistry" [
        test "empty by default" {
            let reg = DegradedCapabilityRegistry()
            Expect.isTrue reg.IsEmpty "fresh registry reports empty"
            Expect.isEmpty (reg.Snapshot()) "fresh registry snapshot is []"
        }

        test "register surfaces the entry; clear removes it" {
            let reg = DegradedCapabilityRegistry()
            let now = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
            reg.Register(entry "cap-a" now)

            Expect.isFalse reg.IsEmpty "registry non-empty after register"
            Expect.equal (reg.Snapshot() |> List.map _.Capability) [ "cap-a" ] "snapshot lists the registered id"

            reg.Clear "cap-a"
            Expect.isTrue reg.IsEmpty "registry empty after clear (resubscribe-clears acceptance)"
        }

        test "clear of an unregistered id is a no-op" {
            let reg = DegradedCapabilityRegistry()
            reg.Clear "never-registered"
            Expect.isTrue reg.IsEmpty "clearing an absent id leaves the registry empty"
        }

        test "re-register preserves the original DegradedSince but updates the text" {
            let reg = DegradedCapabilityRegistry()
            let first = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
            let later = DateTimeOffset(2026, 6, 14, 1, 0, 0, TimeSpan.Zero)

            reg.Register(entry "cap-a" first)

            reg.Register {
                entry "cap-a" later with
                    Reason = "refreshed reason"
            }

            let snap = reg.Snapshot()
            Expect.hasLength snap 1 "re-registering the same id does not duplicate"
            Expect.equal snap.Head.DegradedSince first "original DegradedSince preserved across re-register"
            Expect.equal snap.Head.Reason "refreshed reason" "reason text refreshed on re-register"
        }

        test "snapshot is ordered oldest-degradation-first" {
            let reg = DegradedCapabilityRegistry()
            let older = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
            let newer = DateTimeOffset(2026, 6, 14, 2, 0, 0, TimeSpan.Zero)
            // Register the newer one first to prove ordering is by
            // DegradedSince, not insertion order.
            reg.Register(entry "cap-newer" newer)
            reg.Register(entry "cap-older" older)

            Expect.equal
                (reg.Snapshot() |> List.map _.Capability)
                [ "cap-older"; "cap-newer" ]
                "snapshot ordered by DegradedSince ascending"
        }
    ]