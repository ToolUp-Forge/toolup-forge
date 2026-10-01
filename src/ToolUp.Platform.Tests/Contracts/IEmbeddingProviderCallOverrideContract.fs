// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IEmbeddingProviderCallOverrideContract

open System
open Expecto
open ToolUp.Platform.IEmbeddingProvider

// ─── Phase 945 — IEmbeddingProviderCallOverride conformance pack ──────
//
// The portable contract of a per-call resilience override: an overridden
// call runs under the override's retry policy, and ONLY that call does.
// The query path depends on the first half (a single-attempt override
// makes one request, so the query policy decides the attempts); ingestion
// depends on the second (the configured retries are untouched).
//
// The factory builds a FRESH provider over a transport that fails
// transiently on its first two requests and answers from the third, and
// returns it with that transport's request count. The provider's
// configured retry policy must allow at least three attempts with short
// backoff, so the plain call recovers inside the test.

/// A provider under test, and the count of requests its transport saw.
type Subject = {
    Provider: IEmbeddingProvider
    Requests: unit -> int
}

let private overridable (subject: Subject) =
    match subject.Provider with
    | :? IEmbeddingProviderCallOverride as o -> o
    | _ -> failwith "the provider does not implement IEmbeddingProviderCallOverride"

let tests (name: string) (factory: unit -> Subject) =
    testList $"{name} — IEmbeddingProviderCallOverride contract" [
        test "the provider answers the override probe" {
            Expect.isTrue
                (EmbedCallOverride.isSupported (factory ()).Provider)
                "the capability is visible to a type test"
        }

        testCaseAsync "a single-attempt override makes one request and surfaces the failure"
        <| async {
            let subject = factory ()

            let! outcome =
                (overridable subject)
                    .GenerateEmbeddingWith(
                        EmbedCallOverride.singleAttemptWithin (TimeSpan.FromSeconds 5.0),
                        "contract text"
                    )
                |> Async.Catch

            Expect.isTrue
                (match outcome with
                 | Choice2Of2 _ -> true
                 | Choice1Of2 _ -> false)
                "the first request failed and the override allowed no retry"

            Expect.equal (subject.Requests()) 1 "exactly one request reached the transport"
        }

        testCaseAsync "an override with retries recovers, and embeds a vector of the provider's dimensions"
        <| async {
            let subject = factory ()

            let! vector =
                (overridable subject)
                    .GenerateEmbeddingWith(
                        {
                            Retry = {
                                MaxAttempts = 3
                                InitialBackoff = TimeSpan.FromMilliseconds 1.0
                                MaxBackoff = TimeSpan.FromMilliseconds 1.0
                                JitterFactor = 0.0
                            }
                            RequestTimeout = None
                        },
                        "contract text"
                    )

            Expect.equal vector.Length subject.Provider.Dimensions "a full vector"
            Expect.equal (subject.Requests()) 3 "the override's three attempts were spent"
        }

        testCaseAsync "a plain call keeps the configured retry policy"
        <| async {
            let subject = factory ()
            let! vector = subject.Provider.GenerateEmbedding "contract text"
            Expect.equal vector.Length subject.Provider.Dimensions "the configured retries recovered"
            Expect.equal (subject.Requests()) 3 "two failures retried, the third request answered"
        }
    ]