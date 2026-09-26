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

// ─── GA4 sentinel vocabulary (Phase 838) ─────────────────────────────
//
// The Data API never returns a JSON null for a dimension. A value GA4
// could not determine comes back as a literal parenthesised string, and
// that string is also perfectly legal dimension text — so nothing at a
// parse boundary can tell "absent" from "a value that happens to read
// `(not set)`". Only per-source knowledge decides it, and this connector
// is where that knowledge lives. So the vocabulary is DECLARED here, per
// column, and rides the schema (`ColumnInfo.AbsentSentinels`) to the
// reader, which turns a declared sentinel into absence once
// (`IngestedPayload.readCell`). A platform-wide list of magic strings is
// exactly the mistake this avoids: `(not set)` in a SQL `varchar` means
// nothing in particular.
//
// Not every GA4 parenthesised token is an absence, and the vocabulary
// below says which is which rather than leaving it to a consumer:
//
// - `(other)` is a CARDINALITY bucket — GA4 folding low-volume values
//   together when a report exceeds its row limit. The rows it stands for
//   HAVE values; they were merely aggregated. Reading it as absent would
//   silently relabel real traffic as "unknown", so it is deliberately NOT
//   declared as a sentinel and reads as its own text.
// - `(direct)` is a CLASSIFICATION — the source of traffic that arrived
//   with no referrer. "Direct" is a known answer, not a missing one, so
//   it too is deliberately not declared.

/// What a GA4 parenthesised token means. Only `Absent` tokens are
/// declared as sentinels.
type Ga4SentinelMeaning =
    /// The value is missing — reads as absent.
    | Absent
    /// A real value GA4 spells in parentheses — reads as its own text.
    | Classification
    /// An aggregate of low-volume values (a row-limit bucket) — reads as
    /// its own text, since the rows it stands for do have values.
    | CardinalityBucket

/// One GA4 token, its meaning, and why.
type Ga4Sentinel = {
    Token: string
    Meaning: Ga4SentinelMeaning
    Note: string
}

/// The GA4 parenthesised vocabulary, each token's meaning recorded beside
/// it.
let sentinelVocabulary: Ga4Sentinel list = [
    {
        Token = "(not set)"
        Meaning = Absent
        Note = "GA4 received no value for this dimension (no campaign tag, unknown city, unset page title)."
    }
    {
        Token = "(none)"
        Meaning = Absent
        Note = "No medium was attached to the session (direct traffic's medium)."
    }
    {
        Token = "(not provided)"
        Meaning = Absent
        Note = "The referrer withheld the value (search keywords hidden for privacy)."
    }
    {
        Token = "(direct)"
        Meaning = Classification
        Note = "Traffic that arrived with no referrer — a known source, not a missing one."
    }
    {
        Token = "(other)"
        Meaning = CardinalityBucket
        Note = "Low-volume values aggregated past a report's row limit — the rows have values; they were grouped."
    }
]

let private absentToken (token: string) =
    sentinelVocabulary
    |> List.exists (fun s -> s.Token = token && s.Meaning = Absent)

// Which dimensions emit which absence tokens. `(not set)` can appear on
// any dimension; `(none)` only on the medium dimensions (a session with
// no medium). A sentinel matches a WHOLE cell, so the composite
// source / medium dimensions — whose cells read `(direct) / (none)` —
// declare only `(not set)`: half a cell is data, not absence. Metrics
// are numbers and declare nothing. `(not provided)` is emitted by the
// search-term dimensions (`sessionManualTerm`, the Google Ads keyword
// family), none of which this property-independent catalogue carries, so
// no catalogued column declares it — it stays in the vocabulary, with its
// meaning, for the day a term dimension joins `dimensions`.
let private mediumDimensions = set [ "medium"; "sessionMedium" ]

/// The sentinels `field` declares as absent — the per-column declaration
/// `columns` carries on `ColumnInfo.AbsentSentinels`. `None` for a metric.
let absentSentinelsFor (field: Ga4Field) : Set<string> option =
    match field.Kind with
    | Metric -> None
    | Dimension ->
        [
            yield "(not set)"
            if mediumDimensions.Contains field.ApiName then
                yield "(none)"
        ]
        |> List.filter absentToken
        |> Set.ofList
        |> Some

/// Render the catalogue as the SDK's shared `ColumnInfo` shape — the
/// value `IDataSource.GetSchema` returns. Every column is nullable: GA4
/// omits absent rows rather than reporting nulls, so any field can fail
/// to appear in a response that asked for it. Each dimension carries the
/// absence tokens it can emit (`absentSentinelsFor`).
let columns: ColumnInfo list =
    allFields
    |> List.map (fun f -> {
        Name = f.ApiName
        DataType = f.DataType
        Nullable = true
        AbsentSentinels = absentSentinelsFor f
    })

/// Build the `TableSchema` for one GA4 property. The property resource
/// name is echoed as `TableName` so the caller can correlate the answer
/// with the request; the columns are the property-independent catalogue.
let tableSchema (propertyResourceName: string) : TableSchema = {
    TableName = propertyResourceName
    Columns = columns
}