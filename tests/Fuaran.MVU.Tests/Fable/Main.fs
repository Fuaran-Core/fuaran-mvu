// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The Fable oracle's runner, run under Node by the proof leg: the extracted models over the fixed corpus
/// must fingerprint as they do on .NET, and a few answers are checked outright. An expectation that fails
/// is an uncaught exception, so the process exits non-zero and the leg is red. This directory is held to the
/// leg's independence guard, so this file reaches the extracted modules and nothing else.
module FableOracleMain

let private expect (what: string) (holds: bool) =
    if not holds then
        failwithf "the extracted oracle disagrees under Fable: %s" what

let private drained =
    Drain.run (Drain.create 2I) [ Drain.Push 1I; Drain.Push 2I; Drain.Pop; Drain.Pop; Drain.Pop ]

expect
    "Drain: FIFO, then empty"
    (drained.second = [ Drain.OSome(Drain.Written 1I); Drain.OSome(Drain.Written 2I); Drain.ONone ])

expect "Drain: the cursors reset" (drained.first.read = 0I && drained.first.write = 0I)

let private rung =
    FixedRing.run (FixedRing.create 1I) [ FixedRing.Push 1I; FixedRing.Push 2I; FixedRing.Pop; FixedRing.Pop ]

expect
    "FixedRing: the second push is refused, then FIFO, then empty"
    (rung.second = [
        FixedRing.Pushed true
        FixedRing.Pushed false
        FixedRing.Popped(FixedRing.OSome(FixedRing.Written 1I))
        FixedRing.Popped FixedRing.ONone
    ])

let private planned =
    KeyedDiff.plan_of
        [
            {
                KeyedDiff.entry.key = 1I
                KeyedDiff.entry.description = 1I
            }
            {
                KeyedDiff.entry.key = 2I
                KeyedDiff.entry.description = 1I
            }
        ] [] [
            {
                KeyedDiff.entry.key = 2I
                KeyedDiff.entry.description = 2I
            }
            {
                KeyedDiff.entry.key = 3I
                KeyedDiff.entry.description = 1I
            }
        ]

expect
    "KeyedDiff: stop 1 and 2, start 2 and 3"
    (KeyedDiff.sequence planned = [
        KeyedDiff.Stop 1I
        KeyedDiff.Stop 2I
        KeyedDiff.Start(2I, 2I)
        KeyedDiff.Start(3I, 1I)
    ])

let private machine =
    StepMachine.dispatch
        false
        0I
        100I
        8I
        OracleFingerprint.stepUpdate
        OracleFingerprint.stepResources
        OracleFingerprint.stepDecide
        (StepMachine.initial 0I)
        42I
        StepMachine.External

expect
    "StepMachine: a chain over its fuel is refused whole, and the drain rendered once"
    (machine.first.refused = [ 1I ]
     && machine.first.model = 0I
     && machine.first.handed = []
     && machine.first.renders = 1I)

expect
    "StepMachine: after termination an offer is refused"
    (not (StepMachine.accept false 0I (StepMachine.terminate machine.first) 1I StepMachine.External).second)

/// The gate's model, outright: a kind outside a principal's `Only` is refused whole, a grant for exactly the
/// request lifts an approval requirement, and a grant for another request is stale.
let private authorityJoin (a: Authority.verdict) (b: Authority.verdict) =
    if Authority.rank b > Authority.rank a then b else a

let private authorityKinds: Authority.kind list = [
    {
        Authority.kind.kid = 0I
        Authority.kind.classes = [ 0I ]
    }
    {
        Authority.kind.kid = 1I
        Authority.kind.classes = []
    }
]

let private noHostGate (_: Authority.ctx) (_: Authority.invocation) = Authority.Permit

let private request: Authority.invocation list = [
    {
        Authority.invocation.ikind = 0I
        Authority.invocation.iargs = 4I
    }
]

let private approvalForClass0 = [ Authority.ApprovalFor [ 0I ] ]

let private noGrant: Authority.ctx = {
    Authority.ctx.now = 10I
    Authority.ctx.generation = 1I
    Authority.ctx.presented = None
}

let private grantFor (grequest: Authority.invocation list) : Authority.ctx = {
    noGrant with
        Authority.ctx.presented =
            Some {
                Authority.grant.gparked = 1I
                Authority.grant.grequest = grequest
                Authority.grant.gexpires = 50I
                Authority.grant.ggeneration = 1I
            }
}

expect
    "Authority: a kind outside the principal's Only is refused"
    (Authority.step_verdict
        authorityJoin
        authorityKinds
        noHostGate
        [ Authority.Only [ 1I ] ]
        Authority.Permit
        noGrant
        request = Authority.Refuse)

expect
    "Authority: a class gate parks, and a grant for exactly the request lifts it"
    (Authority.decide_step authorityJoin authorityKinds noHostGate approvalForClass0 Authority.Permit 10I 1I request = Authority.Parked
     && Authority.present
         authorityJoin
         authorityKinds
         noHostGate
         approvalForClass0
         Authority.Permit
         (grantFor request)
         1I
         request = Authority.Committed)

expect
    "Authority: a grant for another request is stale"
    (Authority.present
        authorityJoin
        authorityKinds
        noHostGate
        approvalForClass0
        Authority.Permit
        (grantFor [])
        1I
        request = Authority.Refused)

let private actual = OracleFingerprint.fingerprint ()

expect
    (sprintf "the models' fingerprint is %A on .NET and %A here" OracleFingerprint.expected actual)
    (actual = OracleFingerprint.expected)

printfn "fable oracle: green (fingerprint %A)" actual
