# ToolUp.DataSources.GoogleAnalytics.Client

The client (Fable) tier of the ToolUp.Platform Google Analytics 4 connector: a three-step Feliz
credential panel — OAuth client credentials, the consent bounce, then property selection and
disconnect — registered against the per-Kind data-source credential UI registry consumed by the
built-in data-ingestion admin module.

This package carries UI only. The `IDataSource` implementation, the `IOAuthCredentialFlow`, the
dimension/metric catalogue and the Google client library references all live in the paired
server-tier package, `ToolUp.DataSources.GoogleAnalytics` — see its README for the connector as
a whole, configuration, and the OAuth application setup.

Install both packages to light up the connector end to end; install this one alone only when a
custom server composition supplies the server tier by other means.
