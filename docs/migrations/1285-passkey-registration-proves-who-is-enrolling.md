# Phase 1285 — passkey registration proves who is enrolling

**Who this is for:** deployments composing `ToolUp.AuthProviders.Passkey`, and clients that call
`POST /api/passkey/register/begin` (including `ToolUp.AuthProviders.Passkey.Client`). **Breaking:** the
request body no longer carries `email`, an invitation is redeemed by its link token, the bootstrap token is
spent on first use, and user verification is required. The auth-providers group advances to 0.40.0.

## What changes

1. **The request never names an existing identity.** Without a signed-in session, `register/begin` refuses a
   `username` that already holds a passkey, is a platform admin (`IPlatformAdminStore`) or is a member of any
   team (`ITeamStore`). `register/complete` checks again before it persists, so two ceremonies cannot race to
   one new id. Adding a passkey to an existing user is done from that user's own session.
2. **A session always wins.** The grant order is session, bootstrap token, invitation, open registration. A
   signed-in caller enrols for itself whatever `AllowOpenRegistration` says, and its email is the session's;
   the body's is never adopted.
3. **An invitation is redeemed by its secret.** `inviteToken` is the token of the `/invite/<token>` link
   `IssueInvite` returns. It is validated (signature, expiry, revocation, use limit) at begin, and at complete
   one use is consumed atomically before the member is added with the invited role. The identity is minted by
   the server (`pk-` and 32 hex digits); the body's `username` is ignored and its `email` is not read. A
   pending-by-email invitation no longer admits a passkey registration: knowing an address proved nothing.
4. **The bootstrap token is spent on first successful use.** The marker is the blob
   `auth/passkey-bootstrap/consumed.json` in the `_platform` container of the deployment's `IBlobStorage`, the
   store the credentials live in, written create-only (`IConditionalBlobStorage`, `IfAbsent`), so every
   replica sharing that store sees it and exactly one of two racing ceremonies wins. Every shipped backend
   implements the conditional interface; on one that does not, the bootstrap path refuses.
5. **User verification is required.** Both ceremonies demand it. `PasskeyConfig.UserVerificationWaiver =
   Some reason` lowers it to preferred, for authenticators that cannot verify the user; a blank reason waives
   nothing.
6. **Surface.** `RegisterBeginRequest.Email` is removed and `InviteToken` added; `PendingChallenge` gains
   `InviteToken`; `resolveIdentity` takes a `RegistrationLookups` record; `completeRegistration` takes the
   admission step it runs before persisting; `TeamInvitationHandler.validateInviteToken` and
   `redeemInviteForNewPrincipal` are new. `PasskeyClient.register`'s third argument is the invitation token.

## Do this

Send the invitation link's token, not an email:

```diff
-{ "username": "dana", "email": "dana@example.com" }
+{ "displayName": "Dana", "inviteToken": "<the token from /invite/<token>>" }
```

```diff
-PasskeyClient.register cfg username displayName email bootstrapToken
+PasskeyClient.register cfg username displayName inviteToken bootstrapToken
```

Name a reason if your authenticators cannot verify the user:

```diff
 let config = {
     PasskeyConfig.create "example.com" "Example" [ "https://example.com" ]
-        with BootstrapToken = bootstrapToken
+        with
+            BootstrapToken = bootstrapToken
+            UserVerificationWaiver = Some "kiosk security keys have no PIN"
 }
```

Enrol the first administrator in two steps: present the bootstrap token with a new username, then set
`TOOLUP_INITIAL_PLATFORM_ADMIN` to that id and restart (the seed fires against an empty admin list). An id
already seeded as an admin is a platform principal and is refused.

## Verify

- `register/begin` with `{"username":"<an existing user>"}` and no session answers 403.
- A second `register/begin` with the bootstrap token answers 403 on every replica.
- Opening an invitation link and enrolling signs the invitee in under a `pk-…` id that is a member of the
  team with the invited role; the same single-use link enrols nobody else.
- The begin options carry `"userVerification":"required"`.

## Rollback

Pin the previous auth-providers version (0.39.2). A consumed bootstrap marker is ignored by it. Credentials
enrolled under this version are ordinary records and keep working. To re-arm the bootstrap token deliberately,
delete the marker blob; that is a deliberate act and should be audited.

Review credentials enrolled before this version: a `PasskeyCredentialRegistered` audit row whose grant is
`PendingInvite`, `Bootstrap` or `OpenRegistration` names an identity the request chose.
