// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IReleaseWindowContract

open System
open Expecto
open ToolUp.InterPlatform

// ─── IReleaseWindow contract pack ────────────────────────────────────
//
// What every release window owes the clean-room gate's near-duplicate
// check: a submission within the floor of a recorded release is refused
// and names it; a refused submission is never recorded, so it cannot shadow
// a later one; the same cohort again is no near duplicate; the comparison
// span and the retention bound what is compared; and keys do not share
// releases. Each implementation runs the same cases against a fresh window.

let private at = DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero)

let private members n = [ for i in 1..n -> $"respondent-{i}" ]

let private releaseAt (day: float) (cohort: string list) : ReleasedFilter = {
    Filter = FilterCohort.ofMembers cohort
    ReleasedAt = at.AddDays day
    CounterpartyPeerId = "buyer"
    MethodName = "Table"
}

let private release n = releaseAt 0.0 (members n)

let private keyFor (peerId: string) : ReleaseWindowKey = {
    TemplateId = "survey"
    Partition = ReleaseWindowPartition.Counterparty peerId
}

let private admitUnder (window: IReleaseWindow) key (r: ReleasedFilter) comparedFrom keptFrom =
    window.Admit(key, r, 10, comparedFrom, keptFrom) |> Async.RunSynchronously

let private admit (window: IReleaseWindow) (r: ReleasedFilter) =
    admitUnder window (keyFor "buyer") r DateTimeOffset.MinValue DateTimeOffset.MinValue

/// The cases, run against windows `factory` builds fresh.
let tests (name: string) (factory: unit -> IReleaseWindow) =
    testList $"{name} — IReleaseWindow contract" [

        test "a submission within the floor of a release is refused, naming it" {
            let window = factory ()
            Expect.equal (admit window (release 100)) WindowAdmission.Admitted "the first release is admitted"

            Expect.equal
                (admit window (release 99))
                (WindowAdmission.NearDuplicateOf (release 100).Filter.MembershipHash)
                "one member fewer is refused, naming the earlier cohort"

            Expect.equal (admit window (release 90)) WindowAdmission.Admitted "the floor away is admitted"
        }

        test "the same cohort again is no near duplicate" {
            let window = factory ()
            Expect.equal (admit window (release 100)) WindowAdmission.Admitted "the first release"
            Expect.equal (admit window (release 100)) WindowAdmission.Admitted "the same cohort, again"
        }

        test "a refused submission is never recorded" {
            let window = factory ()
            Expect.equal (admit window (release 100)) WindowAdmission.Admitted "the first release"
            Expect.notEqual (admit window (release 91)) WindowAdmission.Admitted "nine members fewer is refused"

            // Nine strangers added to those 91 are near the 91 and the floor
            // from the 100, so they are admitted only if the 91 was not
            // recorded.
            let strangers = releaseAt 0.0 (members 91 @ [ for i in 1..9 -> $"stranger-{i}" ])

            Expect.isFalse
                (FilterCohort.isNear 10 (release 100).Filter strangers.Filter)
                "the fixture: the strangers' cohort is the floor away from the 100"

            Expect.equal (admit window strangers) WindowAdmission.Admitted "the refused 91 left nothing behind"
        }

        test "a release before the comparison span is not compared, and one past retention is forgotten" {
            let window = factory ()
            let key = keyFor "buyer"

            Expect.equal
                (admitUnder window key (releaseAt 0.0 (members 100)) DateTimeOffset.MinValue DateTimeOffset.MinValue)
                WindowAdmission.Admitted
                "the first release"

            Expect.equal
                (admitUnder window key (releaseAt 30.0 (members 99)) (at.AddDays 10.0) DateTimeOffset.MinValue)
                WindowAdmission.Admitted
                "a release before the comparison span is not compared"

            Expect.equal
                (admitUnder window key (releaseAt 31.0 (members 98)) DateTimeOffset.MinValue (at.AddDays 31.0))
                WindowAdmission.Admitted
                "retention dropped both earlier releases"
        }

        test "keys do not share releases" {
            let window = factory ()
            Expect.equal (admit window (release 100)) WindowAdmission.Admitted "the buyer's release"

            Expect.equal
                (admitUnder window (keyFor "rival") (release 99) DateTimeOffset.MinValue DateTimeOffset.MinValue)
                WindowAdmission.Admitted
                "another counterparty's key holds none of the buyer's releases"

            let vintage: ReleaseWindowKey = {
                TemplateId = "survey"
                Partition = ReleaseWindowPartition.Vintage "buyer"
            }

            Expect.equal
                (admitUnder window vintage (release 99) DateTimeOffset.MinValue DateTimeOffset.MinValue)
                WindowAdmission.Admitted
                "a vintage named like a counterparty is a different key"
        }
    ]