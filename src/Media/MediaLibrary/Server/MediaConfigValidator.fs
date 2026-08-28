// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.MediaConfigValidator

open System
open System.Text
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation

// ─── Phase 88 — media library options preflight ───────────────────────
//
// Runs once at compose end (before `app.Run()`): a misconfigured media
// library — a zero size cap, an empty MIME allowlist (no upload would
// ever be accepted), or a non-positive signed-URL TTL — aborts startup
// with a single-line error rather than failing silently at request time.
//
// ─── Phase 468 — ranged-read capability advisory ─────────────────────
//
// A second validator in the same family (`media_library:ranged-reads`)
// reports whether the composed `IBlobStorage` actually serves bounded
// ranged reads. It does not gate startup: a store that refuses them is
// CORRECT, just expensive to seek — Phase 468's fallback downloads the
// whole original and slices, exactly as the pre-468 library always did.
// The refusal that matters in practice is the Phase 22
// `EncryptedBlobStorage` decorator (whole-blob AES-GCM: a mid-blob
// ciphertext range is undecryptable), so this is precisely the case an
// operator should be TOLD about rather than stopped for — hence
// `Warning`, never `Error`.
//
// It is a live probe rather than a type test, and that is the point: a
// decorator stack (`Resilient` over `Encrypted`) type-tests as the
// outermost decorator while refusing ranges underneath, and a
// consumer's own store can refuse for its own reasons. Preflight runs
// once, off the request path, where a sentinel write/read/delete is
// sanctioned — so the probe asks the store the actual question.
//
// Every arm that cannot answer returns `Ok`. If the sentinel cannot be
// written (a read-only `_platform`, a store that is simply down) the
// validator says nothing rather than guessing: a preflight advisory
// that fires for the wrong reason is worse than one that stays quiet.

type private Impl(options: MediaLibraryOptions) =
    interface IConfigValidator with
        member _.Name = "media_library:options"
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            if options.MaxBytes <= 0L then
                return ValidationResult.Error "MediaLibrary MaxBytes must be positive"
            elif Set.isEmpty options.AcceptedMimeTypes then
                return ValidationResult.Error "MediaLibrary AcceptedMimeTypes is empty — no uploads would be accepted"
            elif options.SignedUrlDefaultTtl <= TimeSpan.Zero then
                return ValidationResult.Error "MediaLibrary SignedUrlDefaultTtl must be positive"
            else
                return ValidationResult.Ok
        }

let create (options: MediaLibraryOptions) : IConfigValidator = Impl(options) :> IConfigValidator

/// Reserved container + deterministic name for the ranged-read probe
/// sentinel. Overwritten on every boot and deleted immediately, so a
/// failed delete leaves one small, self-describing blob rather than
/// accumulating.
[<Literal>]
let private probeContainer = "_platform"

[<Literal>]
let private probeBlob = "media/_preflight/range-probe"

type private RangeProbe(storage: IBlobStorage) =
    interface IConfigValidator with
        member _.Name = "media_library:ranged-reads"
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            let payload = Encoding.UTF8.GetBytes "toolup-media-library-range-probe"

            try
                // `Ok` / `Error` unqualified are `ValidationResult`
                // cases here (this module opens `ConfigValidation`), so
                // the store's own `Result` cases are spelled out.
                match! storage.Upload(probeContainer, probeBlob, payload) with
                | Result.Error _ ->
                    // Cannot stage a sentinel — nothing can be concluded
                    // about ranged reads, so conclude nothing.
                    return ValidationResult.Ok
                | Result.Ok _ ->
                    let! ranged = storage.DownloadRange(probeContainer, probeBlob, 4L, 8)
                    let! _ = storage.Delete(probeContainer, probeBlob)

                    match ranged with
                    | Result.Ok bytes when bytes.Length > 0 -> return ValidationResult.Ok
                    | _ ->
                        return
                            ValidationResult.Warning
                                "MediaLibrary: the composed IBlobStorage refuses ranged reads (the whole-blob AES-GCM encryption decorator does, by design). Media still serves correctly, but every Range request downloads the whole original and slices it in memory — seeking a large encrypted item is O(object), not O(range). See docs/companions/media-library.md."
            with _ ->
                return ValidationResult.Ok
        }

/// Phase 468 — the ranged-read capability advisory. Registered
/// alongside the options validator whenever an `IBlobStorage` is
/// composed.
let createRangeProbe (storage: IBlobStorage) : IConfigValidator = RangeProbe(storage) :> IConfigValidator