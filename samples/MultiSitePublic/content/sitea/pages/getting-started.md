---
title: Getting Started with Site A
description: A second site-A page, proving per-site slug universes.
layout: page
---

This slug (`/getting-started`) exists **only** on site A. Request it with
`Host: siteb.example` or against the default site and you get a 404 — each
site resolves pages against its own content root.

It also appears only in site A's `sitemap.xml`, with an absolute
`https://sitea.example/...` location.
