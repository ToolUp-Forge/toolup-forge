# ToolUp Trademark Policy

The "ToolUp" name and logo are trademarks of ToolUp Analytics Ltd. The
ToolUp SDK is licensed under Apache License 2.0 (see [LICENSE](LICENSE)),
but the Apache licence does not grant trademark rights. This document
describes how the marks may and may not be used.

The policy is modelled on the trademark practices of Linux Foundation
projects, the Rust Foundation, Mozilla (Firefox), and WordPress.org —
forks are explicitly allowed; brand confusion is not.

## What is and isn't allowed

| Use | Allowed? |
|---|---|
| Use unmodified SDK, refer to it as "ToolUp" | Yes |
| Run a self-hosted ToolUp instance and give it your own internal name (e.g. "Acme Internal Analytics") | Yes |
| Run a SaaS based on ToolUp and name it "ToolUp Cloud", "ToolUp Premium", or any "ToolUp"-prefixed brand | No — confusing branding |
| Run a SaaS based on a fork and give the fork its own name (e.g. "MetaUp") | Yes — fork name |
| Use the ToolUp logo on marketing material | Requires written permission |
| Build a third-party module / companion / connector and describe it as "Built for ToolUp" or "Compatible with ToolUp" | Yes |
| Ship an unmodified ToolUp instance with the in-product **"Powered by ToolUp"** sidebar badge removed while continuing to call the deployment "ToolUp" | No — trademark policy requires the badge stays in any deployment marketed as ToolUp |
| Ship a rebranded fork (e.g. "MetaUp") with the badge removed | Yes — once the deployment is no longer marketed as ToolUp, the trademark policy no longer applies |

## On the "Powered by ToolUp" sidebar badge

The ToolUp SDK ships with a small **"Powered by ToolUp"** badge rendered in
the sidebar of every running deployment. The badge is part of the trademark
identity of the ToolUp brand, not part of the licence terms.

Apache License 2.0 cannot mandate UI attribution. Adding a "the badge must
remain visible" clause to the licence itself would make ToolUp a
**badgeware** licence — incompatible with Apache 2.0's no-additional-
restrictions guarantee, OSI-rejected pattern (the SugarCRM SPL precedent
is the well-known cautionary tale). The licence keeps `NOTICE`-file
attribution as the only copyright-side requirement.

UI attribution is enforced via **trademark law** instead, the same lever
Mozilla uses for Firefox, Red Hat for Red Hat Enterprise Linux, and
WordPress.org for the WordPress mark:

- A deployment marketed as **ToolUp** must keep the registered ToolUp mark
  intact, which includes the in-product sidebar badge.
- A fork that strips the badge must also rebrand — once the deployment is
  no longer marketed as "ToolUp", the trademark policy no longer applies
  and the badge is not required.
- Customers genuinely wanting to white-label can either:
  1. Negotiate a written trademark licence that drops the badge requirement
     (handled commercially, separate from the OSS licence), or
  2. Fork and rebrand under their own name, at which point this policy
     stops applying.

## Forks are welcome

Apache 2.0 explicitly grants the right to fork. This trademark policy does
not restrict that right — it restricts only the use of the **ToolUp name
and logo** in the fork's branding. A fork named "MetaUp" (or anything
that is not a "ToolUp"-prefixed name) may do anything the licence allows,
including stripping the badge, customising the UI, and shipping commercial
SaaS deployments. The fork simply cannot be called "ToolUp".

## Compatibility statements

Third parties may describe their products as **"Built for ToolUp"**,
**"Compatible with ToolUp"**, or **"Integrates with ToolUp"** without
written permission, provided:

- The statement is factually accurate.
- The statement does not imply official endorsement, partnership, or
  certification by ToolUp Analytics Ltd.
- The third party's own product name does not confusingly imply ToolUp
  ownership (e.g. "ToolUp Connector for X" — not allowed; "X Connector for
  ToolUp" — allowed).

## How to ask for permission

For uses outside the table above (logo on marketing, formal partnerships,
white-label licences, etc.), contact:

> **trademark@toolup.pro**

Most reasonable requests are granted; unreasonable requests are refused
quickly so you can plan around it.

## Footnote on enforceability

This policy is drafted to align with the principles of trademark law in
the United States and the European Union. Active enforcement requires the
ToolUp mark to be **registered** in those jurisdictions; registration is
an operational precondition tracked separately. Until
registration completes, this policy reflects the intended posture and is
applied on a good-faith basis.

---

This trademark policy is independent of the [Apache License 2.0](LICENSE)
that governs the ToolUp SDK source code. Removing the marks is permitted
under the licence; using the marks in ways that conflict with this policy
is not.
