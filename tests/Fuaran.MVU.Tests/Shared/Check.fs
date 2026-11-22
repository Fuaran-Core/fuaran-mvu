// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// A check is a named body that records failures: the same shape runs under Expecto on .NET and under
/// a plain runner on Node, so every law and every differential case is written once.
module Fuaran.MVU.Tests.Check

/// A named check; `Run` answers the failures, in the order they were recorded.
type Case = {
    Name: string
    Run: unit -> string list
}

/// Build a case from a body that is handed `expect what holds`.
let case (name: string) (body: (string -> bool -> unit) -> unit) : Case = {
    Name = name
    Run =
        fun () ->
            let failures = ResizeArray<string>()

            body (fun what holds ->
                if not holds then
                    failures.Add what)

            List.ofSeq failures
}

/// Run every case, print one line each, and answer the number that failed.
let runAll (label: string) (cases: Case list) : int =
    let mutable failed = 0

    for c in cases do
        match c.Run() with
        | [] -> printfn "PASS %s: %s" label c.Name
        | failures ->
            failed <- failed + 1
            printfn "FAIL %s: %s" label c.Name

            for f in failures do
                printfn "     %s" f

    printfn "%s: %d case(s), %d failed" label (List.length cases) failed
    failed
