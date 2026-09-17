# Migration — `EntityPrincipal` (renamed), threaded through the four identity-less module seams

Phase 806 put the acting principal on every mutating member of `IEntityStore`, and every caller with a principal in reach passed it. Four module seams carried no caller at all, so the nine call sites behind them passed a comment-marked host principal: the lifecycle row for a form schema an admin deleted, a report template a team member saved, a booking resource an operator registered, or a page an author published all said `"system"`. This phase gives those seams a principal parameter and passes the caller from each handler. In the same change-set the principal record is **renamed**: `EntityActor` → `EntityPrincipal`, `EntityActor.system` → `EntityPrincipal.system`, and likewise `ofPrincipal` / `onBehalfOf` / `replaying` under the new module name.

**This is BREAKING**, on the same terms as 806: an implementation of one of the four interfaces gets a compile error rather than a wrong audit row, and a caller that has no principal to pass has to say so by passing `EntityPrincipal.system` visibly.

## 1. The rename — pure substitution

The record's shape is unchanged (`Principal` / `OnBehalfOf` / `Replay`) and the old name does not survive as an alias: the type landed in the untagged 0.23.0 draft (newest tag v0.22.0), so no released consumer ever named it. Search-and-replace the identifier:

```diff
- open ToolUp.Platform.EntityTypes   // unchanged — the module is the same
- let actor = EntityActor.ofPrincipal accessContext.UserId
+ let actor = EntityPrincipal.ofPrincipal accessContext.UserId
- let host = EntityActor.system
+ let host = EntityPrincipal.system
- let delegated = EntityActor.ofPrincipal adminId |> EntityActor.onBehalfOf memberId
+ let delegated = EntityPrincipal.ofPrincipal adminId |> EntityPrincipal.onBehalfOf memberId
- member _.Save<'T>(scopeId: string, actor: EntityActor, entity: 'T) = …
+ member _.Save<'T>(scopeId: string, actor: EntityPrincipal, entity: 'T) = …
```

Why: the type is the acting *principal* in the audit-trail sense — subject, on-behalf-of, replay provenance — and "actor" collides with the actor *model* the SDK's portability rules are written against (`grep -i actor` returned both). The name now matches the record's own `Principal` field and the SDK's subject / principal vocabulary. The 806 migration doc reads under the new name.

## 2. `IFormStore` — `SaveSchema` / `DeleteSchema` / `DeleteSubmission`

The principal is the **second** argument, as on `IEntityStore` (scope, principal, payload).

```diff
  type IFormStore =
-     abstract SaveSchema: scopeId: string * schema: FormSchema -> Async<Result<FormSchema, FormError>>
+     abstract SaveSchema: scopeId: string * principal: EntityPrincipal * schema: FormSchema -> Async<Result<FormSchema, FormError>>
-     abstract DeleteSchema: scopeId: string * schemaId: FormSchemaId -> Async<Result<unit, FormError>>
+     abstract DeleteSchema: scopeId: string * principal: EntityPrincipal * schemaId: FormSchemaId -> Async<Result<unit, FormError>>
-     abstract DeleteSubmission: scopeId: string * submissionId: SubmissionId -> Async<Result<unit, FormError>>
+     abstract DeleteSubmission: scopeId: string * principal: EntityPrincipal * submissionId: SubmissionId -> Async<Result<unit, FormError>>
```

Callers pass the resolved caller; the form API handler builds it once from `AccessContext.UserId`:

```diff
- formStore.SaveSchema(scopeId, schema)
+ formStore.SaveSchema(scopeId, EntityPrincipal.ofPrincipal accessContext.UserId, schema)
```

`SaveSubmission` is unchanged — the submission names its own author, and the row carries that (an authenticated user's id, or the prefix-tagged token identity of an invited respondent). A submission deleted by its respondent passes that same prefix-tagged identity (`SubmissionAuthor.toIndexValue`), so the creation row and the deletion row agree.

Contract pack: `IFormStoreContract.principalTests label mkEntityStore mkStore` binds beside `IFormStoreContract.tests` and asserts the caller on each of the three members is the principal the entity store receives.

## 3. `IReportTemplateStore` — `Save` / `Delete`, and `ReportApiHandler.create`

```diff
  type IReportTemplateStore =
-     abstract Save: scopeId: string * template: ReportTemplate -> Async<Result<ReportTemplate, string>>
+     abstract Save: scopeId: string * principal: EntityPrincipal * template: ReportTemplate -> Async<Result<ReportTemplate, string>>
-     abstract Delete: scopeId: string * id: TemplateId -> Async<Result<unit, string>>
+     abstract Delete: scopeId: string * principal: EntityPrincipal * id: TemplateId -> Async<Result<unit, string>>
```

`ReportApiHandler.create` gains the `principal: string` parameter `createWithDisclosureGate` already carried, in the same position — resolve it upstream alongside `scopeId`, both are per-caller:

```diff
- ReportApiHandler.create templateStore registry storeBlob audit config scopeId
+ ReportApiHandler.create principal templateStore registry storeBlob audit config scopeId
  // createWithDisclosureGate gate principal templateStore … scopeId — unchanged
```

Both factories now record every template save and delete under the caller.

## 4. `IBookingScheduler` — `RegisterResource` / `AddAvailabilityException` / `RemoveAvailabilityException`

```diff
  type IBookingScheduler =
-     abstract RegisterResource: scopeId: string * resource: BookableResource -> Async<Result<unit, BookingError>>
+     abstract RegisterResource: scopeId: string * principal: EntityPrincipal * resource: BookableResource -> Async<Result<unit, BookingError>>
-     abstract AddAvailabilityException: scopeId: string * exc: AvailabilityException -> Async<Result<unit, BookingError>>
+     abstract AddAvailabilityException: scopeId: string * principal: EntityPrincipal * exc: AvailabilityException -> Async<Result<unit, BookingError>>
-     abstract RemoveAvailabilityException: scopeId: string * id: string -> Async<Result<unit, BookingError>>
+     abstract RemoveAvailabilityException: scopeId: string * principal: EntityPrincipal * id: string -> Async<Result<unit, BookingError>>
```

The booking operations (`Book` / `Cancel` / `Reschedule` / `MarkNoShow`) keep their `actorUserId: string` — that string is what the booking *events* stamp. The resource and availability writes are entity-store writes, so they take the record the entity store stamps rather than a parallel string; the scheduling API handler passes the same resolved user to both.

Contract pack: `IBookingSchedulerContract.principalTests label mkEntityStore mkScheduler`.

## 5. `INarrativePagePublisher.PublishAsync`

The seam has no scope argument, so the principal is its **first** parameter:

```diff
  type INarrativePagePublisher =
      abstract member PublishAsync:
+         principal: EntityPrincipal *
          slug: string *
          titleOverride: string option *
          descriptionOverride: string option *
          layoutHint: string option *
          collisionPolicy: SlugCollisionPolicy *
          document: NarrativeDocument ->
              Async<NarrativePublishOutcome>
```

```diff
- publisher.PublishAsync(slug, title, description, layout, policy, document)
+ publisher.PublishAsync(EntityPrincipal.ofPrincipal userId, slug, title, description, layout, policy, document)
```

The `publish_narrative` AI tool passes the request's resolved user. The default publisher stamps it on the page entity's lifecycle row and never substitutes the host principal.

## 6. The one legitimate `EntityPrincipal.system` site, and the guard

`ContentLifecycle.runScheduledPublishSweep store actor now` (retyped in 806) is the only seam where `EntityPrincipal.system` is a legitimate argument: the sweep fires from a timer, not a request, so there is no caller to name. A deployment whose job scheduler carries a principal passes that; one with none passes `EntityPrincipal.system`, and the row then says exactly that.

The Platform test pack now carries a **source-reading guard** (`SeamPrincipalTests`): any `EntityPrincipal.system` call site in production code outside its definition and the sweep fails the pack, naming the file, and so does any surviving use of the pre-814 spelling. If you vendor SDK sources, the same check is a one-line grep in CI:

```powershell
# code sites only — comments are documentation, not calls
Get-ChildItem src -Recurse -Filter *.fs | Select-String 'EntityPrincipal\.system' | Where-Object { $_.Line -notmatch '^\s*//' }
```

## Verification

1. Build. Every implementation and call site the four retypes reach fails to compile until it passes a principal — there is no runtime path that silently keeps the old row.
2. Bind `IFormStoreContract.principalTests` / `IBookingSchedulerContract.principalTests` beside the existing packs if you implement either interface.
3. After an admin deletes a form schema, a member saves a report template, an operator registers a resource, or an author publishes a narrative: read the lifecycle row from your audit trail — `UserId` is the caller, not `"system"`.

## Rollback

Revert the phase's commits. Rows written under it carry a real `UserId`; a pre-814 reader reads the principal as before. The rename has no data-shape consequence — the record's fields are unchanged and nothing on the wire names the type.

## Version notes

Breaking (SemVer-on-0.x: minor) — the rename `EntityActor` → `EntityPrincipal` (type + module; no alias; landed in the unreleased 0.23.0 draft, so no tagged consumer is affected), and four retyped seams: `IFormStore` (`SaveSchema` / `DeleteSchema` / `DeleteSubmission`), `IReportTemplateStore` (`Save` / `Delete`), `IBookingScheduler` (`RegisterResource` / `AddAvailabilityException` / `RemoveAvailabilityException`), `INarrativePagePublisher.PublishAsync`; plus the retyped `ReportApiHandler.create` (gains `principal`). Additive: `IFormStoreContract.principalTests`, `IBookingSchedulerContract.principalTests`. Landed against the frozen 0.23.0 draft without moving `<Version>`; the operator classes the next cut.
