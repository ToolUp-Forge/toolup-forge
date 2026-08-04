// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.OcrProviders.Tesseract.Health

open System
open System.IO
open ToolUp.Platform.HealthChecks
open ToolUp.RAG.OcrProviders.Tesseract.TesseractOcrProvider

// ─── Phase 500 — Tesseract OCR health probe ──────────────────────────
//
// Deliberately NEVER `Unhealthy`. OCR unavailability degrades ingestion
// QUALITY — a scanned upload lands with the "OCR unavailable" status the
// KB extractor emits — it does not break the deployment, and taking the
// replica out of rotation (what `Unhealthy` on a readiness probe does)
// would turn a document-quality problem into an availability one. Every
// replica reads the same tessdata volume anyway, so the rotation would
// simply empty. Same reasoning as the Redis embedding-cache probe.
//
// What it actually checks is the thing that goes wrong in production:
// the tessdata volume unmounting under a running process. The native
// library cannot vanish (it is loaded), but a mounted model directory
// very much can, and the first symptom would otherwise be every scanned
// document failing at ingestion.

/// Companion-contributed `IHealthCheck` for the Tesseract OCR provider.
/// Pass the same options the provider was created from, so the probe
/// watches the directory actually in use.
type TesseractOcrHealthCheck(options: TesseractOcrOptions) =
    interface IHealthCheck with
        member _.Name = "ocr:tesseract"
        member _.Kind = Readiness

        // A directory + file existence check on a local or mounted
        // volume; 1s absorbs a slow network mount without hiding one
        // that has actually gone away.
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            if not (Directory.Exists options.TessDataPath) then
                return
                    Degraded(
                        sprintf
                            "tessdata directory '%s' is no longer readable — scanned documents will report OCR unavailable until the volume is restored"
                            options.TessDataPath
                    )
            else
                let missing =
                    TesseractOcrOptions.languages options
                    |> List.filter (fun language ->
                        not (File.Exists(Path.Combine(options.TessDataPath, language + ".traineddata"))))

                if missing.IsEmpty then
                    return Healthy
                else
                    return
                        Degraded(
                            sprintf
                                "tessdata directory '%s' is missing %s — documents in those languages will OCR poorly or not at all"
                                options.TessDataPath
                                (missing |> List.map (fun l -> l + ".traineddata") |> String.concat ", ")
                        )
        }

/// Create the probe from the options the provider was composed with.
let create (options: TesseractOcrOptions) : IHealthCheck =
    TesseractOcrHealthCheck(options) :> IHealthCheck