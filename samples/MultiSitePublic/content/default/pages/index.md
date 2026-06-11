---
title: Default Site
description: The fallback site served on every host no satellite claims.
layout: page
---

This is the **default site** — the `ServerConfig`-level content root, served on
every host that no registered satellite claims, including a bare
`http://localhost:13950/` browser hit.

Try the satellites from the same listener by pinning the `Host` header:

- `curl -s -H "Host: sitea.example" http://localhost:13950/`
- `curl -s -H "Host: siteb.example" http://localhost:13950/`
