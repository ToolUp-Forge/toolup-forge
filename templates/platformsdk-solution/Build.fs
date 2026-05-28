module MyTemplate.Build

open Fake.Core
open ToolUp.Platform.Build

let config = {
    BuildConfig.defaults with
        Port = 5000
}

[<EntryPoint>]
let main args =
    init args
    registerTargets config
    execute args