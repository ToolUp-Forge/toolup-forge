module ToolUp.Reporting.IReportTemplateStore

open ToolUp.Platform.EntityTypes
open ToolUp.Reporting

// ─── IReportTemplateStore interface ──────────────────────────────────
//
// Typed persistence facade for `ReportTemplate` records. Default
// impl (`EntityBackedTemplateStore`) delegates to `IEntityStore` for
// versioning + scope isolation; alternative impls (file-system
// backed for dev, KMS-backed for compliance edition's signed-template
// requirement) could land later without consumer code changes.
//
// Six-rule portability audit (Phase 9c, Guiding Principle 12):
//   1. Identity by value      — `TemplateId: string`,
//                               `scopeId: string`. No live handles.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry/supervision as data — failures flow through `string`
//                                  result (delegated impl uses
//                                  `EntityError` internally).
//   4. Stateless between calls — the default delegates to
//                                IEntityStore which is itself
//                                stateless per its contract.
//   5. No cross-shard ordering — list ordering is impl-defined.
//   6. Precision at lower bound — n/a (no time semantics).

type IReportTemplateStore =
    /// List every template at the given scope.
    abstract List: scopeId: string -> Async<ReportTemplate list>

    /// Fetch a specific template by id at the given scope.
    abstract Get: scopeId: string * id: TemplateId -> Async<ReportTemplate option>

    /// Save (create or update) a template. The store assigns the
    /// next monotonic version; the caller's `Version` field is
    /// overwritten.
    ///
    /// `principal` is the caller performing the write — the reporting
    /// API handler passes the resolved caller — and is what the
    /// lifecycle audit row records (Phase 814). The store never
    /// substitutes `EntityPrincipal.system` for it.
    abstract Save:
        scopeId: string * principal: EntityPrincipal * template: ReportTemplate -> Async<Result<ReportTemplate, string>>

    /// Delete a template. Idempotent — deleting a non-existent
    /// template returns `Ok ()`. `principal` is the caller deleting it,
    /// recorded on the `EntityDeleted` row (Phase 814).
    abstract Delete: scopeId: string * principal: EntityPrincipal * id: TemplateId -> Async<Result<unit, string>>