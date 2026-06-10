// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.MediaConfigValidator

open System
open ToolUp.Platform.ConfigValidation

// ─── Phase 88 — media library options preflight ───────────────────────
//
// Runs once at compose end (before `app.Run()`): a misconfigured media
// library — a zero size cap, an empty MIME allowlist (no upload would
// ever be accepted), or a non-positive signed-URL TTL — aborts startup
// with a single-line error rather than failing silently at request time.

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