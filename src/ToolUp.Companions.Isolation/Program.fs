// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Companions.Isolation

/// The worker's process entry. This assembly is a library to every
/// consumer and an executable to `dotnet exec`: the one entry point is
/// how the host starts the sacrificial process without a second
/// artefact to ship (see `ProcessIsolation`).
module Program =
    /// `--worker` serves one request over the standard streams; any
    /// other invocation prints what this executable is and exits 2.
    [<EntryPoint>]
    let main (argv: string[]) : int =
        if argv |> Array.contains IsolationWorker.WorkerFlag then
            IsolationWorker.runProcess ()
        else
            eprintfn
                "ToolUp.Companions.Isolation: the native-boundary isolation worker. Not for direct use — it is started by ProcessIsolation with %s and speaks a binary protocol on stdin/stdout."
                IsolationWorker.WorkerFlag

            2