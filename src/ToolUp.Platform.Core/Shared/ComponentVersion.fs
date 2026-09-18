// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// One named component's version, as reported by the debug-only
/// `/dev/version` endpoint.
///
/// The endpoint already lists every loaded `ToolUp.*` assembly with zero
/// registration, so a managed companion never needs to register one of
/// these. The record exists for the case an assembly version cannot
/// express: a companion that wraps a NATIVE library (a P/Invoke
/// companion) and wants to report the version of the native binary it
/// actually loaded, or any other component whose version is known only
/// at runtime. Register as many as needed as DI singletons:
///
/// ```fsharp
/// services.AddSingleton<ComponentVersion>({ Name = "libverovio"; Version = "4.3.1" })
/// ```
///
/// The handler resolves every registered instance
/// (`IEnumerable<ComponentVersion>`) and appends them to the report's
/// `Companions` list after the assembly-derived entries. Nothing is
/// registered by default, so a deployment that declares none is
/// unchanged (GP 11).
type ComponentVersion = {
    /// Display name of the component — a native library name
    /// (`"libverovio"`), a package id, or any stable label an operator
    /// will recognise. Not required to be unique; the endpoint lists,
    /// it does not key.
    Name: string
    /// The version string exactly as the component reports it. Free
    /// text on purpose — native libraries do not all speak SemVer, and
    /// a rewritten version is one an operator can no longer match
    /// against the vendor's release notes.
    Version: string
}