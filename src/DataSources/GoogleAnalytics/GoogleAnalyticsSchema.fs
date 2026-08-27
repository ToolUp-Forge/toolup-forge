// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.GoogleAnalyticsSchema

open ToolUp.Platform

// ─── GA4 dimension + metric catalogue ────────────────────────────────
//
// The Google Analytics 4 Data API has no schema-introspection call that
// is cheap enough to sit behind `IDataSource.GetSchema` — the API's
// `GetMetadata` round-trips the network per property and returns a few
// hundred entries, most of which no report ever selects. `GetSchema`
// therefore answers from this static catalogue: the common,
// property-independent subset of GA4's published dimension and metric
// API names, so an admin UI can populate a dimension / metric picker
// without a credentialed call.
//
// **Property-specific fields are deliberately absent.** Custom
// dimensions / metrics, and the ecommerce item-scoped fields a property
// only reports when its data stream sends them, vary per property; a
// static catalogue cannot know them. A caller that needs the exact
// per-property set queries the Data API's metadata endpoint directly —
// this catalogue is the picker default, not an authority on what a given
// property will accept.
//
// **`DataType` values are the wire shape, not GA4's own type taxonomy.**
// The Data API returns every dimension value as a string and every
// metric value as a string carrying a number; the connector's report
// output preserves that. `"string"` / `"number"` here describe how a
// consumer should parse the cell, which is the question a schema
// consumer is actually asking.
//
// Rendered through `ColumnInfo` (the SDK's shared schema shape) so the
// admin UI renders a GA4 property with the same component it renders a
// warehouse table with. Every entry is `Nullable = true`: GA4 omits a
// row entirely rather than reporting a null, so any given field can be
// absent from a response the caller asked for.

/// Which half of the GA4 report model a catalogue entry belongs to.
/// A `RunReportRequest` names dimensions and metrics in separate lists,
/// so a picker has to keep them apart.
type Ga4FieldKind =
    /// A qualitative attribute of the data — a date, a country, a page
    /// path. Goes in `RunReportRequest.dimensions`.
    | Dimension
    /// A quantitative measurement — a count, a duration, a revenue
    /// figure. Goes in `RunReportRequest.metrics`.
    | Metric

/// One catalogue entry. `ApiName` is the literal string a
/// `RunReportRequest` carries (`"activeUsers"`, `"sessionSource"`);
/// `UiName` is the label the Google Analytics web UI shows for the same
/// field, so an operator can match what they see in the picker to what
/// they see in the product.
type Ga4Field = {
    ApiName: string
    UiName: string
    Kind: Ga4FieldKind
    /// Grouping used by the GA4 UI ("Time", "Geography", "Traffic
    /// source", …). Admin UIs group the picker by it.
    Category: string
    /// `"string"` for dimensions, `"number"` for metrics — how the cell
    /// should be parsed, not GA4's own type name. See the module note.
    DataType: string
}

let private dim category apiName uiName = {
    ApiName = apiName
    UiName = uiName
    Kind = Dimension
    Category = category
    DataType = "string"
}

let private met category apiName uiName = {
    ApiName = apiName
    UiName = uiName
    Kind = Metric
    Category = category
    DataType = "number"
}

/// The catalogue's dimensions, in picker order (category groups first,
/// alphabetical-by-UI-label within a group is deliberately NOT imposed —
/// the order below is the order the GA4 UI presents them, which is what
/// an operator recognises).
let dimensions: Ga4Field list = [
    // Time
    dim "Time" "date" "Date"
    dim "Time" "dateHour" "Date + hour"
    dim "Time" "dateHourMinute" "Date + hour + minute"
    dim "Time" "day" "Day"
    dim "Time" "dayOfWeekName" "Day of week"
    dim "Time" "hour" "Hour"
    dim "Time" "month" "Month"
    dim "Time" "nthDay" "Nth day"
    dim "Time" "week" "Week"
    dim "Time" "year" "Year"

    // Geography
    dim "Geography" "city" "City"
    dim "Geography" "country" "Country"
    dim "Geography" "countryId" "Country ID"
    dim "Geography" "continent" "Continent"
    dim "Geography" "region" "Region"

    // Platform / device
    dim "Platform and device" "browser" "Browser"
    dim "Platform and device" "deviceCategory" "Device category"
    dim "Platform and device" "deviceModel" "Device model"
    dim "Platform and device" "language" "Language"
    dim "Platform and device" "operatingSystem" "Operating system"
    dim "Platform and device" "operatingSystemVersion" "OS version"
    dim "Platform and device" "platform" "Platform"
    dim "Platform and device" "screenResolution" "Screen resolution"

    // Traffic source
    dim "Traffic source" "campaignName" "Campaign"
    dim "Traffic source" "defaultChannelGroup" "Default channel group"
    dim "Traffic source" "medium" "Medium"
    dim "Traffic source" "sessionCampaignName" "Session campaign"
    dim "Traffic source" "sessionDefaultChannelGroup" "Session default channel group"
    dim "Traffic source" "sessionMedium" "Session medium"
    dim "Traffic source" "sessionSource" "Session source"
    dim "Traffic source" "sessionSourceMedium" "Session source / medium"
    dim "Traffic source" "source" "Source"
    dim "Traffic source" "sourceMedium" "Source / medium"

    // Page / screen
    dim "Page / screen" "contentGroup" "Content group"
    dim "Page / screen" "fullPageUrl" "Full page URL"
    dim "Page / screen" "hostName" "Hostname"
    dim "Page / screen" "landingPage" "Landing page"
    dim "Page / screen" "pagePath" "Page path"
    dim "Page / screen" "pagePathPlusQueryString" "Page path + query string"
    dim "Page / screen" "pageTitle" "Page title"
    dim "Page / screen" "unifiedScreenName" "Page title / screen name"

    // Events
    dim "Events" "eventName" "Event name"
    dim "Events" "isConversionEvent" "Is key event"

    // User
    dim "User" "newVsReturning" "New / returning"
    dim "User" "signedInWithUserId" "Signed in with user ID"
    dim "User" "userAgeBracket" "Age"
    dim "User" "userGender" "Gender"
]

/// The catalogue's metrics, in picker order.
let metrics: Ga4Field list = [
    // Users
    met "Users" "activeUsers" "Active users"
    met "Users" "newUsers" "New users"
    met "Users" "totalUsers" "Total users"
    met "Users" "userEngagementDuration" "User engagement duration"

    // Sessions
    met "Sessions" "sessions" "Sessions"
    met "Sessions" "engagedSessions" "Engaged sessions"
    met "Sessions" "engagementRate" "Engagement rate"
    met "Sessions" "bounceRate" "Bounce rate"
    met "Sessions" "averageSessionDuration" "Average session duration"
    met "Sessions" "sessionsPerUser" "Sessions per user"

    // Page / screen
    met "Page / screen" "screenPageViews" "Views"
    met "Page / screen" "screenPageViewsPerSession" "Views per session"
    met "Page / screen" "screenPageViewsPerUser" "Views per user"

    // Events
    met "Events" "eventCount" "Event count"
    met "Events" "eventCountPerUser" "Event count per user"
    met "Events" "eventsPerSession" "Events per session"
    met "Events" "conversions" "Key events"

    // Revenue
    met "Revenue" "totalRevenue" "Total revenue"
    met "Revenue" "purchaseRevenue" "Purchase revenue"
    met "Revenue" "averagePurchaseRevenue" "Average purchase revenue"
    met "Revenue" "averagePurchaseRevenuePerUser" "ARPPU"
    met "Revenue" "transactions" "Transactions"
    met "Revenue" "ecommercePurchases" "Ecommerce purchases"
    met "Revenue" "itemRevenue" "Item revenue"
    met "Revenue" "grossItemRevenue" "Gross item revenue"
]

/// Every catalogue entry — dimensions first, then metrics.
let allFields: Ga4Field list = dimensions @ metrics

/// The catalogue's distinct category labels, in first-appearance order.
/// Admin UIs use it to build the picker's group headers without
/// re-deriving the set.
let categories: string list =
    allFields
    |> List.map _.Category
    |> List.fold (fun acc c -> if List.contains c acc then acc else acc @ [ c ]) []

/// Look up one entry by its API name. `None` for a name the catalogue
/// does not carry — which is not the same as "GA4 will reject it": a
/// custom dimension is valid at the API and absent here (see the module
/// note), so callers must not use this as a validator.
let tryFind (apiName: string) : Ga4Field option =
    allFields |> List.tryFind (fun f -> f.ApiName = apiName)

/// Render the catalogue as the SDK's shared `ColumnInfo` shape — the
/// value `IDataSource.GetSchema` returns. Every column is nullable: GA4
/// omits absent rows rather than reporting nulls, so any field can fail
/// to appear in a response that asked for it.
let columns: ColumnInfo list =
    allFields
    |> List.map (fun f -> {
        Name = f.ApiName
        DataType = f.DataType
        Nullable = true
    })

/// Build the `TableSchema` for one GA4 property. The property resource
/// name is echoed as `TableName` so the caller can correlate the answer
/// with the request; the columns are the property-independent catalogue.
let tableSchema (propertyResourceName: string) : TableSchema = {
    TableName = propertyResourceName
    Columns = columns
}