// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.MediaLibraryTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.MediaLibrary
open ToolUp.Platform.Tests.Contracts

// ─── Phase 88 — media library tests ───────────────────────────────────
//
// Three layers: the pure `ByteRange.parse` 206/416 decision, the pure
// `SignedUrl` mint/verify + expiry crypto, and the full `IMediaLibrary`
// contract pack run against `DefaultMediaLibrary` over an in-memory blob
// store.

// ─── Test doubles ─────────────────────────────────────────────────────

type private NullLogger() =
    interface ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(_) = ()
        member _.Error(_, _) = ()

/// Trivial in-memory `ISecretStore` — mirrors the `ShareTokenStoreTests`
/// double; the signing key is generated on first use and persisted here.
type private InMemorySecretStore() =
    let store =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(container, name) = async {
            match store.TryGetValue((container, name)) with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.SetSecret(container, name, value) = async {
            store[(container, name)] <- value
            return Ok()
        }

        member _.DeleteSecret(container, name) = async {
            store.TryRemove((container, name)) |> ignore
            return Ok()
        }

        member _.ListKeys(_) = async { return [] }

let private makeLibrary () : IMediaLibrary =
    let blob =
        InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

    let secrets = InMemorySecretStore() :> ISecretStore
    let signer = SignedUrl.MediaUrlSigner(secrets)

    DefaultMediaLibrary(
        blob,
        signer,
        NoopMediaDerivation.create (),
        NoopMediaTranscoder.create (),
        None,
        MediaLibraryOptions.defaults,
        NullLogger()
    )
    :> IMediaLibrary

// ─── Pure ByteRange.parse — the 206 / 416 decision ────────────────────

let private rangeTests =
    testList "ByteRange.parse" [
        test "empty header → NoRange (serve 200)" { Expect.equal (ByteRange.parse "" 100L) NoRange "empty" }

        test "non-bytes unit → NoRange" { Expect.equal (ByteRange.parse "items=0-10" 100L) NoRange "unknown unit" }

        test "bytes=10-19 → Satisfiable 10..19" {
            Expect.equal (ByteRange.parse "bytes=10-19" 100L) (Satisfiable { Start = 10L; End = 19L }) "closed range"
        }

        test "bytes=10- → Satisfiable to end" {
            Expect.equal (ByteRange.parse "bytes=10-" 100L) (Satisfiable { Start = 10L; End = 99L }) "open-ended"
        }

        test "bytes=-20 → final 20 bytes" {
            Expect.equal (ByteRange.parse "bytes=-20" 100L) (Satisfiable { Start = 80L; End = 99L }) "suffix"
        }

        test "bytes=90-200 → end clamped to last byte" {
            Expect.equal (ByteRange.parse "bytes=90-200" 100L) (Satisfiable { Start = 90L; End = 99L }) "clamped"
        }

        test "bytes=100- (start == length) → Unsatisfiable (416)" {
            Expect.equal (ByteRange.parse "bytes=100-" 100L) RangeRequest.Unsatisfiable "start at length"
        }

        test "bytes=200-300 (start past end) → Unsatisfiable (416)" {
            Expect.equal (ByteRange.parse "bytes=200-300" 100L) RangeRequest.Unsatisfiable "fully past"
        }

        test "zero-length resource → Unsatisfiable" {
            Expect.equal (ByteRange.parse "bytes=0-10" 0L) RangeRequest.Unsatisfiable "empty resource"
        }

        test "Length member counts inclusive bytes" {
            Expect.equal ({ Start = 10L; End = 19L }: ByteRange).Length 10L "10 bytes"
        }
    ]

// ─── Pure SignedUrl crypto + expiry ───────────────────────────────────

let private fixedKey = Array.create 32 7uy

let private signScope: StorageScope = {
    ScopeId = "u1"
    Container = "user-u1"
    Persist = true
}

let private now = DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero)

let private signedUrlTests =
    testList "SignedUrl" [
        test "mint then verify round-trips the payload" {
            let token = SignedUrl.mint fixedKey (MediaId "abc") signScope (now.AddHours 1.0)

            match SignedUrl.verify fixedKey token now with
            | Ok payload ->
                Expect.equal payload.MediaId "abc" "media id"
                Expect.equal payload.ScopeId "u1" "scope id"
                Expect.equal payload.Container "user-u1" "container"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "an expired token fails with Expired" {
            let token = SignedUrl.mint fixedKey (MediaId "abc") signScope (now.AddMinutes 5.0)

            match SignedUrl.verify fixedKey token (now.AddHours 1.0) with
            | Error SignedUrlError.Expired -> ()
            | other -> failtestf "expected Expired, got %A" other
        }

        test "a token signed with another key fails verification" {
            let token = SignedUrl.mint fixedKey (MediaId "abc") signScope (now.AddHours 1.0)
            let otherKey = Array.create 32 9uy

            match SignedUrl.verify otherKey token now with
            | Error SignedUrlError.InvalidSignature -> ()
            | other -> failtestf "expected InvalidSignature, got %A" other
        }

        test "a tampered token fails verification" {
            let token = SignedUrl.mint fixedKey (MediaId "abc") signScope (now.AddHours 1.0)
            // Flip the final signature character.
            let tampered =
                token.Substring(0, token.Length - 1) + (if token.EndsWith "A" then "B" else "A")

            match SignedUrl.verify fixedKey tampered now with
            | Error _ -> ()
            | Ok _ -> failtest "tampered token must not verify"
        }

        test "a malformed token fails with Malformed" {
            match SignedUrl.verify fixedKey "not-a-token" now with
            | Error SignedUrlError.Malformed -> ()
            | other -> failtestf "expected Malformed, got %A" other
        }

        testCaseAsync "MediaUrlSigner sign/verify round-trips via the secret store"
        <| async {
            let signer = SignedUrl.MediaUrlSigner(InMemorySecretStore() :> ISecretStore)
            let! signed = signer.SignAsync(MediaId "xyz", signScope, TimeSpan.FromHours 1.0, now)
            let token = Expect.wantOk signed "sign"
            let! verified = signer.VerifyAsync(token, now)
            let payload = Expect.wantOk verified "verify"
            Expect.equal payload.MediaId "xyz" "round-trip media id"
        }
    ]

[<Tests>]
let tests =
    testList "MediaLibrary (Phase 88)" [
        rangeTests
        signedUrlTests
        IMediaLibraryContract.tests "DefaultMediaLibrary" makeLibrary
    ]