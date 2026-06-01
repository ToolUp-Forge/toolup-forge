namespace ToolUp.Remoting.Server 


module Errors = 
  let unhandled (funcName: string) = 
    { error = sprintf "Error occured while running the function %s" funcName
      ignored = true 
      handled = false } 

  let ignored (funcName: string) = 
    { error = sprintf "Error occured while running the function %s" funcName
      ignored = true 
      handled = true } 

  let propagated (value: obj) =
    { error = value
      ignored = false
      handled = true }

  /// Phase 69b.E — categorised error envelope. Same shape as
  /// `propagated` plus a `category` field for client-side branching.
  /// Category strings are the lowercase DU case names ("user",
  /// "system", "ratelimit", "auth", "notfound", "validation").
  /// Phase 69j — variant that stamps the supplied `__schema_version`.
  /// `Errors.categorised` retains the default-1 behaviour for backwards
  /// compatibility with internal callers; adapters that consume
  /// `RemotingOptions.SchemaVersion` use the explicit variant.
  let categorisedWithSchema (schemaVersion: int) (category: ErrorCategory) (value: obj) : CategorisedErrorResult<obj> =
    let categoryString =
      match category with
      | ErrorCategory.User -> "user"
      | ErrorCategory.System -> "system"
      | ErrorCategory.RateLimit -> "ratelimit"
      | ErrorCategory.Auth -> "auth"
      | ErrorCategory.NotFound -> "notfound"
      | ErrorCategory.Validation -> "validation"
    { error = value
      ignored = false
      handled = true
      category = categoryString
      __schema_version = schemaVersion }

  let categorised (category: ErrorCategory) (value: obj) : CategorisedErrorResult<obj> =
    categorisedWithSchema 1 category value