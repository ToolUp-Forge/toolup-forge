# ToolUp.Stripe.TierToken

HMAC-signed tier-claim cookie machinery for paid-tier gating. The
cookie format is
`{tierClaim}.{expUnix}.{sigBase64Url}` where
`sig = HMAC-SHA256(payload="{tierClaim}.{expUnix}", key=secret)`,
encoded with base64-url (no padding).

## Surface

```fsharp
type Tier = Anonymous | Free | Personal | Teacher

module Tier =
    val rank      : Tier -> int
    val tryParse  : string | null -> Tier
    val toClaim   : Tier -> string

module TierGate =
    val tierAtLeast : required:Tier -> active:Tier -> bool

type MintError    = SecretMissing | InvalidLifetime
type ValidateError = SecretMissing | MalformedToken | SignatureMismatch | Expired | UnknownTier

module Token =
    val mint     : tier:Tier -> lifetimeSeconds:int -> now:DateTimeOffset -> secret:byte[] -> Result<string, MintError>
    val validate : now:DateTimeOffset -> token:string -> secret:byte[] -> Result<Tier, ValidateError>

type CookieConfig =
    { CookieName: string
      InsecureCookiesEnvVar: string option }

module Cookie =
    val issue              : config:CookieConfig -> ctx:HttpContext -> tier:Tier -> lifetimeSeconds:int -> secret:byte[] -> Result<unit, MintError>
    val clear              : config:CookieConfig -> ctx:HttpContext -> unit
    val resolveFromRequest : config:CookieConfig -> ctx:HttpContext -> now:DateTimeOffset -> secret:byte[] -> Tier option
```

## Cookie defaults

`HttpOnly = true`, `SameSite = Lax`, `Secure = true` (unless the
configured `InsecureCookiesEnvVar` is set to `"1"` — dev / preview /
local override).
