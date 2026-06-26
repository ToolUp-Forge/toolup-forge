// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Shared `{{token}}` substitution + embedded-template reading used by
/// file-emitting subcommands (today `docker emit`; later phases reuse
/// the same seam). The four `platformsdk-docker` template files are
/// embedded into the tool assembly as resources, so the single source
/// of truth stays the `templates/platformsdk-docker/` tree — no copy
/// drifts.
module ToolUp.Cli.Templating

open System.IO
open System.Reflection

/// Replace every `{{key}}` occurrence with its bound value. A token with
/// no binding is left intact, so an unrelated literal `{{x}}` in content
/// survives untouched.
let substitute (bindings: (string * string) list) (content: string) =
    bindings
    |> List.fold (fun (acc: string) (key, value) -> acc.Replace("{{" + key + "}}", value)) content

/// Read an embedded template resource by the `LogicalName` declared in
/// the fsproj's `<EmbeddedResource>` items. Fails loudly (not at some
/// later P/Invoke-deep point) if the resource is missing — a packaging
/// regression should break the first call, not corrupt output.
let readEmbedded (logicalName: string) : string =
    let asm = Assembly.GetExecutingAssembly()

    match asm.GetManifestResourceStream logicalName with
    | null ->
        let available = asm.GetManifestResourceNames() |> String.concat ", "
        failwithf "Embedded template '%s' not found. Available resources: %s" logicalName available
    | stream ->
        use stream = stream
        use reader = new StreamReader(stream)
        reader.ReadToEnd()