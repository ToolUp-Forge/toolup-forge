// Ambient context for `docs/platform/auth-ui-vendor-neutrality.md`.
//
// The deployment-side example reads the Clerk publishable key the
// consumer's own bootstrap resolved. Declared here so the block stays
// the two lines that make the point.
[<AutoOpen>]
module PageAmbient =

    /// The `pk_…` publishable key — browser-visible by Clerk's design.
    let key: string = failwith "ambient"