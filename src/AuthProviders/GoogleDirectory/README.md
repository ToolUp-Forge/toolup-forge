# ToolUp.AuthProviders.GoogleDirectory

Google Workspace directory companion for ToolUp Platform. Implements the
`IUserDirectory` substrate against a Workspace domain so the SDK's
team-invite UI can:

1. Surface a typeahead of matching directory entries instead of asking
   the operator to memorise an email (`SearchUsers`).
2. Render stored user ids as names and emails in the admin tables
   (`ResolveUsers`).
3. Send the invitee a branded "you've been invited to &lt;team&gt;" email
   when an invitation is issued (`NotifyInvitation`), so the operator
   doesn't have to message them out of band.

It is the Google analogue of `ToolUp.AuthProviders.EntraDirectory`, with
the same three capabilities, the same degradation semantics, and a
different auth model — see below, because the difference is the part
worth reading.

## When to use it

Wire this companion when:

- Your deployment's users live in a Google Workspace domain.
- Operators inviting members to teams know colleagues by name, not by
  full email.
- A Workspace super-admin is willing to authorise a service account for
  domain-wide delegation over the read-only directory scope, and
  (optionally) the Gmail send scope.

Without this companion the typeahead degrades to a plain text input and
the operator types the full email — the existing invite-by-email flow
still works, the invitee is just told out of band. Without a
`SenderUserId`, the typeahead and id resolution still work; the
invitation email is silently skipped.

## Dependencies

None beyond `ToolUp.Platform.Core` and `FSharp.Core`. The Admin SDK
Directory API, the Gmail API and the OAuth token exchange are all
reached with BCL `HttpClient`; there is no Google client SDK in the
dependency graph.

## The auth model, in one paragraph

Google Workspace has no managed-identity equivalent for these APIs. The
only application-scoped path is **domain-wide delegation**: a service
account, authorised by a Workspace super-admin, may impersonate users in
the domain for an explicit list of OAuth scopes. Every call this
companion makes therefore runs *as some user*. Directory reads
impersonate `ImpersonatedAdmin` (only an admin may list the directory);
the invitation email impersonates `SenderUserId` (Gmail sends as whoever
the token impersonates, which is what puts your invitations mailbox on
the From: line). Tokens are minted with an RS256-signed JWT-bearer grant
and cached per subject and scope set.

A delegated key can impersonate **any** user in the domain for the
granted scopes. Grant the narrowest scopes that work — this companion
only ever asks for `admin.directory.user.readonly` and `gmail.send` —
and store the key where your deployment audits secret access.

## Wiring

The service-account JSON never comes from an environment variable or a
file path. It is read from the deployment's `ISecretStore`, and `create`
takes that store:

```fsharp skip=fragment
open ToolUp.AuthProviders

let directory =
    GoogleDirectory.create secretStore {
        GoogleDirectoryConfig.defaults with
            Domain = "example.com"
            ImpersonatedAdmin = "directory-reader@example.com"
            SenderUserId = Some "invites@example.com"
    }

ServerApp.empty
|> ServerApp.withConfig config
// …other compose calls…
|> ServerApp.withUserDirectory (Some directory)
|> ServerApp.run
```

`CredentialScopeId` / `CredentialSecretKey` default to
`_platform` / `google_directory_service_account`; override them if the
credential lives elsewhere in your store. The stored value is the key
file's **contents**, verbatim — not a path to it.

Omit `SenderUserId` and the companion does directory search and id
resolution only; `NotifyInvitation` returns
`Error "notification disabled: …"` and the invite handler skips the
email step.

### Preflight and health

Register both in the same composition — they are what turn a silent
misconfiguration into a loud one:

```fsharp skip=fragment
let directoryConfig = {
    GoogleDirectoryConfig.defaults with
        Domain = "example.com"
        ImpersonatedAdmin = "directory-reader@example.com"
}

let directory = GoogleDirectory.create secretStore directoryConfig

// Aborts startup when the credential is missing, unparseable, or not
// delegated for the directory scope. Warns (never aborts) when only the
// Gmail delegation is missing.
let validator =
    GoogleDirectoryConfigValidator.create secretStore directoryConfig

// Readiness probe: a live authenticated directory call.
let probe = GoogleDirectoryHealth.create directory
```

## Granting the permissions

Two consoles, in this order. The second is the one people forget.

### 1. Google Cloud console — mint the service account

```powershell
# Requires the gcloud CLI, authenticated against the project that will
# own the service account.
$project = "my-project"
$sa = "toolup-directory"

gcloud iam service-accounts create $sa `
    --project $project `
    --display-name "ToolUp directory companion"

gcloud services enable admin.googleapis.com gmail.googleapis.com `
    --project $project

gcloud iam service-accounts keys create toolup-directory.json `
    --project $project `
    --iam-account "$sa@$project.iam.gserviceaccount.com"
```

The service account needs **no IAM roles on the Cloud project**. Its
authority over Workspace comes entirely from the delegation granted in
step 2 — an easy thing to over-grant by reflex.

Note the numeric **client id** (the `client_id` field in the JSON, also
shown on the service account's Details tab). Step 2 needs it.

### 2. Workspace admin console — authorise domain-wide delegation

This step has no API and no CLI; it is a super-admin acting in the
browser.

1. Sign in to `admin.google.com` as a super-admin of the domain.
2. **Security → Access and data control → API controls**.
3. Under *Domain-wide delegation*, choose **Manage domain-wide
   delegation**, then **Add new**.
4. Paste the service account's numeric **client id**.
5. In *OAuth scopes*, enter the scopes as a comma-separated list —
   exactly these strings, and only the ones you need:

   ```text
   https://www.googleapis.com/auth/admin.directory.user.readonly
   https://www.googleapis.com/auth/gmail.send
   ```

6. **Authorise.**

Grants can take a few minutes to propagate. Until they do, the token
exchange fails with `unauthorized_client` — which is also exactly what
it says if you mistyped a scope, so re-read the strings before assuming
propagation.

### 3. Store the key

```powershell
# Whatever your ISecretStore is fronted by. With the platform secrets
# CLI against the reserved _platform scope:
toolup secrets set `
    --scope "_platform" `
    --key "google_directory_service_account" `
    --value (Get-Content -Raw ./toolup-directory.json)

Remove-Item ./toolup-directory.json
```

Delete the downloaded key file once it is in the store. A JSON key with
domain-wide delegation is a domain-wide credential; it should not
survive on a laptop or in a build workspace.

### 4. Pick the impersonated admin

`ImpersonatedAdmin` must be an account with directory read rights. A
dedicated account holding a custom admin role with only *Users → Read*
is the right shape — a super-admin works but grants the delegated key
far more reach than the typeahead needs.

## Local development

There is no `gcloud auth application-default login` shortcut here:
Google's user-credential ADC flow cannot impersonate, so it cannot serve
a domain-wide-delegation grant. The local story is production with a
smaller blast radius — mint a **separate** service account against a test
Workspace domain, delegate only
`admin.directory.user.readonly`, and put its JSON in whatever
`ISecretStore` the dev composition wires. Leave `SenderUserId` unset and
the mail path is inert.

If no test domain is available, compose no directory at all: the SDK's
handler short-circuits to `Ok []`, the typeahead degrades to a plain
email input, and the invite flow still works end to end.

## What the companion returns

`UserSummary { UserId; DisplayName; Email }`:

- `UserId` — the Directory API's `id`, the stable numeric-string user id.
  It is the id the admin tables store and later hand back to
  `ResolveUsers`. Note it is **not** the OIDC `sub` your auth provider
  sees; if you need those to join, resolve by email.
- `DisplayName` — `name.fullName`, falling back to
  `givenName` + `familyName` when a directory entry carries only the
  parts.
- `Email` — `primaryEmail`.

The companion enforces a minimum prefix length of 2 characters
server-side; shorter queries return `Ok []` without reaching Google.

## Query shape

Google's `query` parameter takes `field:value` prefix terms and **ANDs**
multiple terms — there is no OR. Matching display name *or* email, which
is what an operator typing into a typeahead means, is therefore two
requests (`name:'…'` and `email:'…'`) merged and de-duplicated by id, and
truncated to `take`. `EntraDirectory` expresses the same intent as a
single OData filter with `or` clauses; the observable behaviour is the
same.

Single quotes and backslashes are stripped from the query term. Google
documents no escape sequence for `query`, so removal is the only sound
handling.

## Errors

Transient Directory API 429 / 5xx surface as
`Error "directory unavailable: …"`, and so does a credential that will
not load or a delegation that was never granted. The SDK's typeahead UI
renders the string under the input and continues to accept full email
entry.

Mail-send failures surface as `Error "notification unavailable: …"`. The
team-invite handler swallows it: the invite still lands via the
pending-by-email store, and the invitee is told out of band.

`ResolveUsers` never errors on an unrecognised id — a 404 from the
Directory API is a skipped entry, and the caller renders the raw id.

## Privacy posture

The companion only ever issues prefix-scoped `users.list` queries and
per-id `users.get` reads against the configured domain. It never
enumerates the directory, never downloads bulk data, and never writes to
the Directory API. The only capability it holds beyond reading is
`gmail.send`, which cannot read a mailbox.

Operator queries are not logged by the companion. Audit-trail
configuration in the consuming SDK's `IAuditLog` applies to the
team-invite action itself, not to the directory lookups that informed
it. Note that Google logs the impersonated access on its side — the
Workspace admin audit log shows the delegated calls against
`ImpersonatedAdmin`.
