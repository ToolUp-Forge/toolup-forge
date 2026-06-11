---
title: About the Default Site
description: What the default site is and when a request lands here.
layout: page
---

The default site is **not** a `PublicSiteDef` — it is the fallback every
unmatched host serves: the `EnabledPublicRendering` content root, the
compose-registered layouts, and `ServerConfig.PublicBaseUrl`.

A pipeline that never calls `withSite` behaves exactly like this site,
byte-for-byte.
