---
title: Site A Home
description: Satellite site A — its own content root, inheriting the shared layout.
layout: page
---

Welcome to **Site A** (`sitea.example` / `www.sitea.example`). This page comes
from `content/sitea/`, a content root the default site and site B never see.

Site A declares no layouts of its own, so it renders through the **shared**
compose-registered layout — view the page source and find
`data-layout="shared"`.

- [Getting started](/getting-started)
- [Launch announcement](/news/2026-06-01-launch)
