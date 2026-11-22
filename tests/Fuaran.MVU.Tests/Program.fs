// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The Expecto host. Every host-neutral case list becomes one Expecto test per case, named so that the
/// proof leg's host step can select a family by `--filter` (`Proofs.Oracle`, `Proofs.Ladder`).
module Fuaran.MVU.Tests.Program

open Expecto
open Fuaran.MVU.Tests.Check

let private ofCases (label: string) (cases: Case list) =
    testList label [
        for c in cases ->
            testCase c.Name (fun () ->
                match c.Run() with
                | [] -> ()
                | failures -> failtestf "%s" (String.concat "\n" failures))
    ]

let private tests =
    testList "Fuaran.MVU" [
        ofCases "Laws" Laws.cases
        ofCases "PortLaws" PortLaws.cases
        ofCases "RuntimeLaws" RuntimeLaws.cases
        ofCases "GateLaws" GateLaws.cases
        ofCases "SessionLaws" (SessionLaws.cases @ SessionLaws.dotnetCases)
        ofCases "Proofs.Oracle" (Differential.cases @ StepDifferential.cases @ GateDifferential.cases)
        ofCases "Proofs.Ladder" Ladder.cases
    ]

/// The proof leg's host step runs `--filter <family>`. Expecto's own `--filter` matches a full test path,
/// not a family's label, so the host selects the family itself: every test whose joined name contains the
/// hint. A hint that selects NOTHING is refused (exit 2) rather than passed vacuously: the first run of the
/// leg here passed "0 tests run", which is the vacuity this guard exists for.
[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "--filter" :: hint :: rest ->
        let selected =
            Test.filter "." (fun parts -> (String.concat "." parts).Contains hint) tests

        let count = Test.toTestCodeList selected |> Seq.length

        if count = 0 then
            eprintfn "the filter %A selects no test; a host step that runs nothing cannot pass" hint
            2
        else
            printfn "filter %A selects %d test(s)" hint count
            runTestsWithCLIArgs [] (Array.ofList rest) selected
    | _ -> runTestsWithCLIArgs [] argv tests
