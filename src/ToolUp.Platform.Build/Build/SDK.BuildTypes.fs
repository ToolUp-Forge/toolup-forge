namespace ToolUp.Platform

/// Documents the standard artefact output paths
type BuildOutput = {
    ServerPublishPath: string
    ClientBundlePath: string
}

module BuildOutput =
    let defaults = {
        ServerPublishPath = "deploy"
        ClientBundlePath = "deploy/public"
    }