module ToolUp.Platform.Tests.InProcess.WorkflowEngineTests

open Expecto
open ToolUp.Workflow
open ToolUp.Workflow.Server

// ─── Phase 243 — BPMN-shaped workflow engine ─────────────────────────
//
// Proves the four mechanics ToolUp.Forms' linear workflow doesn't cover:
// parallel split + join (waits for all branches), exclusive choice (one
// branch only), external-trigger resume, and saga compensation
// (reverse-order, compensable-only). Plus stateless re-entrancy.

let private task id = { Id = id; Kind = TaskNode false }
let private ctask id = { Id = id; Kind = TaskNode true } // compensable
let private gw id k = { Id = id; Kind = GatewayNode k }
let private edge f t = { From = f; To = t; Guard = Always }

let private startEnd = [ { Id = "s"; Kind = StartNode }; { Id = "e"; Kind = EndNode } ]

let tests =
    testList "WorkflowEngine (Phase 243)" [
        test "linear flow completes" {
            let g = {
                Nodes = startEnd @ [ task "a" ]
                Edges = [ edge "s" "a"; edge "a" "e" ]
            }

            let inst = WorkflowEngine.start g
            Expect.isTrue (inst.Active.Contains "a") "a active after start"
            let inst = WorkflowEngine.complete g inst "a"
            Expect.equal (WorkflowEngine.status g inst) Finished "finished"
        }

        test "parallel join waits for ALL branches" {
            // s -> split -> (a, b) -> join -> e
            let g = {
                Nodes =
                    startEnd
                    @ [ gw "split" ParallelSplit; task "a"; task "b"; gw "join" ParallelJoin ]
                Edges = [
                    edge "s" "split"
                    edge "split" "a"
                    edge "split" "b"
                    edge "a" "join"
                    edge "b" "join"
                    edge "join" "e"
                ]
            }

            let inst = WorkflowEngine.start g
            Expect.isTrue (inst.Active.Contains "a" && inst.Active.Contains "b") "both branches active"
            let inst = WorkflowEngine.complete g inst "a"
            Expect.isFalse (inst.Completed.Contains "join") "join not fired on one branch"
            Expect.equal (WorkflowEngine.status g inst) Running "still running"
            let inst = WorkflowEngine.complete g inst "b"
            Expect.isTrue (inst.Completed.Contains "join") "join fired once both done"
            Expect.equal (WorkflowEngine.status g inst) Finished "finished"
        }

        test "exclusive choice activates only the chosen branch" {
            // s -> choice -> (yes:a | no:b) -> e
            let g = {
                Nodes = startEnd @ [ gw "choice" ExclusiveChoice; task "a"; task "b" ]
                Edges = [
                    edge "s" "choice"
                    {
                        From = "choice"
                        To = "a"
                        Guard = Choice "yes"
                    }
                    {
                        From = "choice"
                        To = "b"
                        Guard = Choice "no"
                    }
                    edge "a" "e"
                    edge "b" "e"
                ]
            }

            let inst = WorkflowEngine.start g
            Expect.isTrue (inst.Active.Contains "choice") "choice awaits decision"
            let inst = WorkflowEngine.choose g inst "choice" "yes"
            Expect.isTrue (inst.Active.Contains "a") "chosen branch active"
            Expect.isFalse (inst.Active.Contains "b") "unchosen branch not active"
            let inst = WorkflowEngine.complete g inst "a"
            Expect.equal (WorkflowEngine.status g inst) Finished "finished via chosen branch"
        }

        test "external trigger resumes a waiting transition" {
            // s -> a -[await approve]-> b -> e
            let g = {
                Nodes = startEnd @ [ task "a"; task "b" ]
                Edges = [
                    edge "s" "a"
                    {
                        From = "a"
                        To = "b"
                        Guard = AwaitTrigger "approve"
                    }
                    edge "b" "e"
                ]
            }

            let inst = WorkflowEngine.start g
            let inst = WorkflowEngine.complete g inst "a"
            Expect.isFalse (inst.Active.Contains "b") "b waits for the trigger"
            let inst = WorkflowEngine.fireTrigger g inst "approve"
            Expect.isTrue (inst.Active.Contains "b") "b active once trigger fires"
        }

        test "failure builds the compensation chain in reverse, compensable-only" {
            // s -> a(comp) -> x(non-comp) -> b(comp) -> c -> e ; fail at c
            let g = {
                Nodes = startEnd @ [ ctask "a"; task "x"; ctask "b"; task "c" ]
                Edges = [ edge "s" "a"; edge "a" "x"; edge "x" "b"; edge "b" "c"; edge "c" "e" ]
            }

            let inst = WorkflowEngine.start g
            let inst = WorkflowEngine.complete g inst "a"
            let inst = WorkflowEngine.complete g inst "x"
            let inst = WorkflowEngine.complete g inst "b"
            // c is active; it fails
            let inst = WorkflowEngine.fail g inst "c"
            Expect.equal (WorkflowEngine.status g inst) Aborted "aborted"
            Expect.equal inst.CompensationOrder [ "b"; "a" ] "reverse-order, compensable tasks only (x excluded)"
        }

        test "engine is stateless — same instance re-completed is idempotent" {
            let g = {
                Nodes = startEnd @ [ task "a" ]
                Edges = [ edge "s" "a"; edge "a" "e" ]
            }

            let inst = WorkflowEngine.start g
            let once = WorkflowEngine.complete g inst "a"
            // completing an already-completed (no longer active) node is a no-op
            let twice = WorkflowEngine.complete g once "a"
            Expect.equal once twice "re-completing a non-active node changes nothing"
        }
    ]