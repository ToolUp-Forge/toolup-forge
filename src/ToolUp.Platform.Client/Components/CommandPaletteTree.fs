// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The open command palette as a Fuaran-UI tree holding one island, the
/// overlay, which owns focus on open and the keyboard cursor's scrolling.
/// `CommandPalette` mounts this file through `TreeSurface.deferred` while the
/// palette is open, so the renderer is fetched the first time it opens.
/// Nothing imports this file statically.
module internal Components.CommandPaletteTree

open ToolUp.Platform
open Components.CommandPalette

/// The deferred component `CommandPalette` mounts.
[<Feliz.ReactComponent>]
let Surface (props: CommandPaletteProps) : Fable.React.ReactElement =
    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps
            (TreeLowering.TreeHost.ofSources HostBindingSources.empty)
            ignore<unit>
            (TreeLowering.island "command-palette" (fun () -> Overlay props))
    )