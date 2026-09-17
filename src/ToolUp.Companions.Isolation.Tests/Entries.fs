// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 687 — the sacrificial entry points the process-isolation
/// cases run. Each stands in for one way a native parser can misbehave
/// on hostile input: reject it cleanly, fault, hang, or eat memory.
/// They are public with parameterless constructors because that is the
/// contract the worker instantiates them by.
namespace ToolUp.Companions.Isolation.Tests.Entries

#nowarn "9" // NativePtr on purpose: the fault IS the test.

open System
open System.Runtime.InteropServices
open System.Threading
open FSharp.NativeInterop
open ToolUp.Companions.Isolation

/// Answers with the input reversed, prefixed by the args joined with
/// `|` — proves both halves of the request crossed intact.
type EchoEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            let prefix = Text.Encoding.UTF8.GetBytes(String.Join("|", request.Args) + "\n")
            Ok(Array.append prefix (Array.rev request.Input))

/// The clean rejection: what a parser does with malformed input when
/// it is behaving.
type RejectingEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            Error $"rejected {request.Input.Length} bytes"

/// Raises — a managed failure the worker must translate, not die on.
type ThrowingEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke _ = invalidOp "the entry point raised"

/// A native-class crash: a raw pointer write to an unmapped address,
/// which faults in JIT-compiled code and is NOT catchable — the runtime
/// fails fast on an access violation outside a marshalling helper
/// (`Marshal.WriteByte` would raise a catchable
/// `AccessViolationException`, which is exactly NOT what a native
/// parser does). The closest a test can get to a parser dereferencing
/// a bad pointer without shipping a broken parser.
type AccessViolationEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke _ =
            eprintfn "AccessViolationEntry: about to dereference an unmapped address"
            // Far above the null-check page range (a fault in the first
            // 64 KiB is reported as a catchable NullReferenceException) and
            // far below the user-space ceiling: unmapped in any real process.
            NativePtr.write (NativePtr.ofNativeInt<byte> (nativeint 0x0000010000000000L)) 0uy
            Ok [||]

/// A fail-fast: what an `abort()` in native code looks like to the
/// host — the process ends with no managed unwinding.
type FailFastEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke _ =
            Environment.FailFast "FailFastEntry: simulated native abort"
            Ok [||]

/// Hangs for the number of milliseconds named by its first arg.
type SleepingEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            Thread.Sleep(int request.Args.Head)
            Ok [||]

/// Allocates and TOUCHES native memory in 32 MiB steps until killed or
/// until a gigabyte is resident, then holds it. Native, not managed,
/// so the GC heap limit cannot stop it — only the host's sampler can.
type AllocatingEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke _ =
            let step = 32 * 1024 * 1024
            let mutable held = []

            for _ in 1..32 do
                let block = Marshal.AllocHGlobal step
                // One write per page is what makes the allocation
                // RESIDENT; an untouched reservation costs nothing.
                for offset in 0..4096 .. step - 1 do
                    Marshal.WriteByte(block, offset, 1uy)

                held <- block :: held
                Thread.Sleep 20

            Thread.Sleep 10000

            for block in held do
                Marshal.FreeHGlobal block

            Ok [||]

/// Has no parameterless constructor, so the worker cannot instantiate
/// it: the `Unresolvable` path.
type UnconstructibleEntry(_marker: int) =
    interface IIsolatedEntryPoint with
        member _.Invoke _ = Ok [||]