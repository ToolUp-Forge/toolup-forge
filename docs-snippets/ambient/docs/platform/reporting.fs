// Ambient context for `docs/platform/reporting.md`.
//
// The Export-button block is an excerpt of a page's own program. What the
// page supplies around it — its report-API proxy, the template it renders,
// the narrative it is showing, and the deployment's own save / toast /
// open-stored-report affordances — is declared here so the block compiles
// as written and every SDK name in it is checked, rather than being marked
// `skip=fragment` and checked by nothing.
//
// The two opens are SDK modules the block genuinely needs and a real
// module would carry; they are out of band only so the snippet a reader
// copies is the handler and not the file header around it.
//
// Nothing here redeclares an SDK name: `reportApi`, `templateId`,
// `pageNarrative`, `saveToDisk`, `openStoredReport` and `toast` are the
// page's own bindings, each given the SDK type where it has one so the
// type is still checked.

open ToolUp.Platform.Narrative
open ToolUp.Reporting

[<AutoOpen>]
module PageAmbient =

    /// The page's report-API proxy.
    let reportApi: IReportApi = failwith "ambient"

    /// The template the page's Export button renders.
    let templateId: TemplateId = failwith "ambient"

    /// The narrative the page is currently showing.
    let pageNarrative: NarrativeDocument = failwith "ambient"

    /// The deployment's own "save these bytes as a file" affordance.
    let saveToDisk (fileName: string) (bytes: byte[]) (mimeType: string) : unit = failwith "ambient"

    /// The deployment's own "open a stored data object" affordance.
    let openStoredReport (dataObjectKey: string) (version: int) : unit = failwith "ambient"

    /// The deployment's own transient-notification affordance.
    let toast (message: string) : unit = failwith "ambient"