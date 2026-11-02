# ToolUp.Platform.Server.Jobs

One part of the ToolUp server tier. It holds background work and delivery: the job store and scheduler with its handlers, the distributed lock seam, notifications, presence and co-editing, transactional notification dispatch and its preference filters, and webhooks.

## Do I reference this package?

Usually not. Reference `ToolUp.Platform.Server`: it holds the composition root (`ServerApp`) and
references every part of the server tier, so one reference resolves all of them. Reference this
part directly only when you want this subsystem without the rest of the tier above it.

## How the server tier is cut

The tier is one compile order cut into contiguous runs, one assembly per run. Each part references
only the parts before it, so the build graph says which subsystem a change touched:

1. `ToolUp.Platform.Server.Remoting`
2. `ToolUp.Platform.Server.Infra`
3. `ToolUp.Platform.Server.Config`
4. `ToolUp.Platform.Server.Scope`
5. `ToolUp.Platform.Server.Jobs`
6. `ToolUp.Platform.Server.Audit`
7. `ToolUp.Platform.Server`: file management, the composition root, boot and preflight

This part depends on: Remoting, Infra, Config and Scope.

## Namespaces

The cut moved no namespace. Code in this part keeps the namespaces it had inside
`ToolUp.Platform.Server` (`ToolUp.Platform`, `ToolUp.Platform.Server`, and the module paths beside them), so a consumer's `open`
lines compile unchanged whichever package they reference.

## License

Apache-2.0. See the repository's `LICENSE`.
