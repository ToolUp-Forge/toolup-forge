---
title: PublicSite + Modules — Wave 13 worked example
description: Demonstrates ToolUp.PublicRendering (SSR) composed with a domain module on one pipeline. Powered by Phase 80c additive composition + Phase 81 BrandKit primitives.
layout: page
slug: index
---

This page is rendered by `ToolUp.PublicRendering`. The recent-notes cards above are composed from the live `Notes` in-memory store the SSR `/notes/{slug}` detail handler reads from.

The composition root layers both surfaces on a single `ServerApp` via `PublicRenderingCompose.withPublicRendering` — the Phase 80c additive extension Wave 13 ships.
