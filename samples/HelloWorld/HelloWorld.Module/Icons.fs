module Toolup.Template.Icons

open Fable.Core.JsInterop
open Fable.React
open ToolUp.Platform

let private chartSvg: obj = importDefault "./icons/chart.svg?react"

let chart: ReactElement = Icon.ofImport chartSvg