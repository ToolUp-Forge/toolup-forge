module ToolUp.Platform.Tests.InProcess.MarkdownRendererTests

open Expecto
open ToolUp.Reporting
open ToolUp.Platform.Tests.Contracts

[<Tests>]
let tests =
    IReportRendererContract.tests "MarkdownRenderer" (fun () -> MarkdownRenderer.create ()) Markdown