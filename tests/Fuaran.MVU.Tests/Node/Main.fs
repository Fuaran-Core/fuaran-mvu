// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The Node runner: the kernel's and the runtime's laws and the differential against the extracted models,
/// under Fable. A
/// failure ends in an uncaught exception, so the process exits non-zero.
module Fuaran.MVU.Tests.NodeMain

open Fuaran.MVU.Tests.Check

let private failed =
    runAll "laws" Laws.cases
    + runAll "port-laws" PortLaws.cases
    + runAll "runtime-laws" RuntimeLaws.cases
    + runAll "gate-laws" GateLaws.cases
    + runAll "session-laws" SessionLaws.cases
    + runAll "oracle" Differential.cases
    + runAll "oracle" StepDifferential.cases
    + runAll "oracle" GateDifferential.cases

if failed > 0 then
    failwithf "%d case(s) failed under Fable" failed

printfn "fable: every law and every differential case passed"
