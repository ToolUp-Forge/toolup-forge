module ToolUp.Platform.Tests.InProcess.HtmlRendererTests

open Expecto
open ToolUp.Reporting
open ToolUp.Platform.Tests.Contracts

[<Tests>]
let tests =
    IReportRendererContract.tests "HtmlRenderer" (fun () -> HtmlRenderer.create ()) Html