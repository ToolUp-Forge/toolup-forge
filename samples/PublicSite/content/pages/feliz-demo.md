---
title: Feliz layout demo
description: A page rendered through a Feliz.ViewEngine layout
layout: feliz
---

This page renders through a layout authored in the **Feliz DSL** and
registered with `withFelizLayout`. The hero card above is a shared
presentational component — the same source compiles client-side under
Feliz (Fable → React) behind an `#if FABLE_COMPILER` conditional open.
