// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Experiments

open System
open System.Security.Cryptography
open System.Text

/// Deterministic, weight-respecting variant assignment. The bucket is a
/// stable hash of `"{experimentId}:{principalId}"` — the same principal
/// always lands in the same variant for a given experiment, across runs
/// and processes (SHA-256 is platform-stable), with no stored state
/// (GP 12 rule 4). Verdicts/significance are out of scope (see Types.fs).
module Assignment =

    /// A stable hash of `key` mapped into the half-open unit interval
    /// `[0, 1)`.
    let bucket (key: string) : float =
        use sha = SHA256.Create()
        let hash = sha.ComputeHash(Encoding.UTF8.GetBytes key)
        // First 8 bytes → uint64 → normalised. Divide by 2^64 so the
        // result is in [0, 1).
        let v = BitConverter.ToUInt64(hash, 0)
        float v / (float UInt64.MaxValue + 1.0)

    /// Assign a principal to a variant, respecting variant weights.
    /// `None` when the experiment has no variants or non-positive total
    /// weight. Negative weights are treated as zero.
    let assign (experiment: Experiment) (principalId: string) : Variant option =
        match experiment.Variants with
        | [] -> None
        | variants ->
            let weightOf (v: Variant) = max 0.0 v.Weight
            let total = variants |> List.sumBy weightOf

            if total <= 0.0 then
                None
            else
                let target = bucket (experiment.Id + ":" + principalId) * total

                let rec pick acc =
                    function
                    | [] -> List.last variants // floating-point guard
                    | v :: rest ->
                        let acc' = acc + weightOf v
                        if target < acc' then v else pick acc' rest

                Some(pick 0.0 variants)