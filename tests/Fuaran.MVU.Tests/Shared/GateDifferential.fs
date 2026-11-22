// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The gate's differential (Phase 648, t6): the extracted `Authority` model (proofs/oracle/, compiled from what
/// the prover writes) run beside the shipped `Gate` and `Runtime` over generated principals, registries (the
/// host's, and restricted ones with restrictions drawn), steps with invocations of registered and unregistered
/// kinds, and grants valid, expired, from another generation, for another step and stale — comparing the
/// decision, and the committed / parked / refused outcome of a dispatch and of a presentation. The extraction is
/// handed Core's own `PolicyDecision.join`, so the model's one premise is discharged by the join it was stated
/// about. Host-neutral: the Expecto suite runs it on .NET under the proof leg's `Proofs.Oracle` filter, and the
/// Node runner runs it under Fable.
module Fuaran.MVU.Tests.GateDifferential

open Fuaran.Core
open Fuaran.MVU
open Fuaran.MVU.Tests.Check
open Fuaran.MVU.Tests.Rand

/// The model's verdict as Core's decision and back; the join handed to the extraction is Core's.
module private Bridge =

    let ofVerdict (v: Authority.verdict) : PolicyDecision =
        match v with
        | Authority.Permit -> PolicyDecision.Allow
        | Authority.Approve -> PolicyDecision.NeedsApproval
        | Authority.Refuse -> PolicyDecision.deny "modelled"

    let toVerdict (d: PolicyDecision) : Authority.verdict =
        match d with
        | PolicyDecision.Allow -> Authority.Permit
        | PolicyDecision.NeedsApproval -> Authority.Approve
        | PolicyDecision.Deny _ -> Authority.Refuse

    let coreJoin (a: Authority.verdict) (b: Authority.verdict) : Authority.verdict =
        toVerdict (PolicyDecision.join (ofVerdict a) (ofVerdict b))

    let outcomeOf (d: Decision) : Authority.outcome =
        match d with
        | Allow -> Authority.Committed
        | NeedsApproval -> Authority.Parked
        | Deny _ -> Authority.Refused

/// The subject, on both sides: four registered kinds with classes, kinds 4 and 5 never registered, one host gate,
/// one custom gate, and a policy read off the request alone.
module private Subject =

    let classesOf (k: int) : int list =
        match k with
        | 0 -> [ 0 ]
        | 1 -> [ 1 ]
        | 2 -> [ 0; 1 ]
        | _ -> []

    let kindId (k: int) = "k" + string k

    let kinds: EffectKind list = [
        for k in 0..3 ->
            {
                Id = kindId k
                Shape = Shape.Call
                Classes = classesOf k |> List.map (fun c -> "c" + string c) |> Set.ofList
                Determinism = Set.empty
                Answers = FirstParty
                Replies = []
            }
    ]

    let kindsM: Authority.kind list = [
        for k in 0..3 ->
            {
                Authority.kind.kid = bigint k
                Authority.kind.classes = classesOf k |> List.map bigint
            }
    ]

    /// An effect is one invocation: the kind and its argument, as one integer.
    let encode (k: int, a: int) = k * 10 + a
    let decode (e: int) = e / 10, e % 10

    let invocationOf (k: int, a: int) : Invocation = {
        Kind = kindId k
        Args = Some(string a)
        Destination = Local
    }

    let invocationM (k: int, a: int) : Authority.invocation = {
        Authority.invocation.ikind = bigint k
        Authority.invocation.iargs = bigint a
    }

    let private argsValue (args: (string * string) list) : int =
        args
        |> List.tryFind (fun (addr, _) -> addr = Gate.ArgsAddress)
        |> Option.map (fun (_, v) -> int v)
        |> Option.defaultValue 0

    /// The host's own gate: kind 3 with an argument of seven or more is refused, for everyone.
    let hostGate: PolicyGate<DecisionContext, Capability, (string * string) list> = {
        Policy = "host"
        Decide =
            fun _ capability args ->
                if capability.Id = kindId 3 && argsValue args >= 7 then
                    PolicyDecision.deny "host"
                else
                    PolicyDecision.Allow
    }

    let hostGateM (_: Authority.ctx) (i: Authority.invocation) : Authority.verdict =
        if i.ikind = 3I && i.iargs >= 7I then
            Authority.Refuse
        else
            Authority.Permit

    /// A custom gate a host might add for one principal: kind 2 with an odd argument is refused.
    let custom: Restriction * Authority.restriction =
        Gated {
            Policy = "odd-k2"
            Decide =
                fun _ capability args ->
                    if capability.Id = kindId 2 && argsValue args % 2 = 1 then
                        PolicyDecision.deny "odd"
                    else
                        PolicyDecision.Allow
        },
        Authority.Custom(fun _ i ->
            if i.ikind = 2I && i.iargs % 2I = 1I then
                Authority.Refuse
            else
                Authority.Permit)

    /// A restriction drawn at random, on both sides.
    let restriction (rand: Rand) : Restriction * Authority.restriction =
        let subset (n: int) = [
            for x in 0 .. n - 1 do
                if rand.Chance(1, 2) then
                    yield x
        ]

        match rand.Next 4 with
        | 0 ->
            let ks = subset 4
            Only(ks |> List.map kindId |> Set.ofList), Authority.Only(ks |> List.map bigint)
        | 1 ->
            let cs = subset 2

            ApprovalFor(cs |> List.map (fun c -> "c" + string c) |> Set.ofList),
            Authority.ApprovalFor(cs |> List.map bigint)
        | 2 ->
            let cs = subset 2
            Refusing(cs |> List.map (fun c -> "c" + string c) |> Set.ofList), Authority.Refusing(cs |> List.map bigint)
        | _ -> custom

    /// The policy, from the request alone: refuse, require approval or allow by a sum.
    let policyCode (request: (int * int) list) : int =
        (List.sumBy (fun (k, a) -> k * 3 + a) request + 1) % 7

    let policyOf (code: int) : PolicyDecision =
        match code with
        | 0 -> PolicyDecision.deny "policy"
        | 1 -> PolicyDecision.NeedsApproval
        | _ -> PolicyDecision.Allow

    let requestOfProposal (p: Proposal<int, NoResource>) : (int * int) list =
        p.Effects |> List.map (fun (_, e) -> decode e)

    /// A request: up to four invocations, kinds 0 to 5 (4 and 5 unregistered), arguments 0 to 9.
    let request (rand: Rand) : (int * int) list = [ for _ in 1 .. rand.Next 5 -> rand.Next 6, rand.Next 10 ]

    let agent = Agent("gpt", "5", "a")
    let foreign = Foreign "feed"
    let user = User "u"
    let approver = User "b"
    let principals = [ Host; user; agent; foreign ]
    let now = 1000L

/// A gate and its model, built from one seed: the agent's and the foreign source's restrictions drawn.
type private Built = {
    Gate: Gate<int, NoResource>
    RestrictionsM: Principal -> Authority.restriction list
}

let private build (rand: Rand) : Built =
    let draw () = [ for _ in 1 .. rand.Next 3 -> Subject.restriction rand ]

    let forAgent = draw ()
    let forForeign = draw ()

    let pairsOf (principal: Principal) =
        match principal with
        | Agent _ -> forAgent
        | Foreign _ -> forForeign
        | Host
        | User _ -> []

    let policy (_: Principal) (p: Proposal<int, NoResource>) =
        Subject.policyOf (Subject.policyCode (Subject.requestOfProposal p))

    match Gate.create Subject.kinds [ Subject.hostGate ] (fun p -> pairsOf p |> List.map fst) policy with
    | Error why -> failwith why
    | Ok gate -> {
        Gate = gate
        RestrictionsM = fun p -> pairsOf p |> List.map snd
      }

let private proposalOf (author: Principal) (request: (int * int) list) : Proposal<int, NoResource> = {
    Author = author
    Sequence = 1L
    At = Subject.now
    Effects = request |> List.map (fun ka -> Subject.invocationOf ka, Subject.encode ka)
    Starts = []
    Stops = []
    Grant = None
}

let private policyM (request: (int * int) list) : Authority.verdict =
    Bridge.toVerdict (Subject.policyOf (Subject.policyCode request))

let private grantM (g: Grant) (request: (int * int) list) (parked: int) : Authority.grant = {
    Authority.grant.gparked = bigint parked
    Authority.grant.grequest = request |> List.map Subject.invocationM
    Authority.grant.gexpires = bigint g.ExpiresAt
    Authority.grant.ggeneration = bigint g.Generation
}

/// The pure decision: the shipped gate beside the model over generated proposals, with and without a grant for
/// exactly the request (the runtime presents no other kind to the gate), and under a second ceiling.
let private decisions (expect: string -> bool -> unit) (seed: int) =
    let rand = Rand seed
    let built = build rand

    for i in 1..30 do
        let request = Subject.request rand
        let author = Subject.principals.[rand.Next 4]
        let proposal = proposalOf author request
        let requestM = request |> List.map Subject.invocationM

        let grant =
            if rand.Chance(3, 10) then
                Some {
                    Parked = 1
                    Request = Proposal.request proposal
                    Approver = Subject.approver
                    ExpiresAt = (if rand.Chance(1, 4) then 500L else 2000L)
                    Generation = (if rand.Chance(1, 4) then 2 else built.Gate.Generation)
                }
            else
                None

        let ctx: Authority.ctx = {
            Authority.ctx.now = bigint Subject.now
            Authority.ctx.generation = bigint built.Gate.Generation
            Authority.ctx.presented = grant |> Option.map (fun g -> grantM g request 1)
        }

        let shipped =
            Gate.decide built.Gate { proposal with Grant = grant } |> Bridge.outcomeOf

        let modelled =
            Authority.outcome_of (
                Authority.step_verdict
                    Bridge.coreJoin
                    Subject.kindsM
                    Subject.hostGateM
                    (built.RestrictionsM author)
                    (policyM request)
                    ctx
                    requestM
            )

        expect
            (sprintf
                "seed %d step %d: %A by %A with grant %A: shipped %A, model %A"
                seed
                i
                request
                author
                grant
                shipped
                modelled)
            (shipped = modelled)

        let other = Subject.principals.[rand.Next 4]

        let shippedUnder =
            Gate.decideUnder built.Gate [ other ] { proposal with Grant = grant }
            |> Bridge.outcomeOf

        let modelledUnder =
            Authority.outcome_of (
                Authority.two_ceilings
                    Bridge.coreJoin
                    Subject.kindsM
                    Subject.hostGateM
                    (built.RestrictionsM author)
                    (built.RestrictionsM other)
                    (policyM request)
                    ctx
                    requestM
            )

        expect
            (sprintf
                "seed %d step %d under %A and %A: shipped %A, model %A"
                seed
                i
                author
                other
                shippedUnder
                modelledUnder)
            (shippedUnder = modelledUnder)

/// What the runtime did with one envelope, read from its log.
let private outcomeOf
    (runtime: Runtime<unit, int, int, int, NoResource, int>)
    (sequence: int64)
    : Authority.outcome option =
    runtime.Log
    |> List.tryFindBack (fun r ->
        match r.Of with
        | Step e -> e.Sequence = sequence
        | BootStep _ -> false)
    |> Option.map (fun r ->
        match r.Outcome with
        | Committed -> Authority.Committed
        | Parked _ -> Authority.Parked
        | Refused _ -> Authority.Refused)

/// The runtime over the gate beside the model: dispatches decided, parked steps presented with grants minted
/// for them, expired, stale, or for another step.
let private presentations (expect: string -> bool -> unit) (seed: int) =
    let rand = Rand seed
    let built = build rand

    // The request a message asks for is drawn from the message, so a parked step's chain asks the same thing when it
    // is computed again, and a stale grant has to be forged.
    let requestOf (n: int) = Subject.request (Rand(seed * 1000 + n))

    let subject: Component<unit, int, int, int, NoResource, int> = {
        Init = fun () -> 0, []
        Update = fun _ n model -> model + 1, [ for ka in requestOf n -> Do(Subject.encode ka) ]
        Resources = fun _ -> Map.empty
        View = id
    }

    let interpreter: Interpreter<int, int, NoResource> = {
        Kinds = Subject.kinds
        Effect =
            fun e -> {
                Invocation = Subject.invocationOf (Subject.decode e)
                Perform = fun () -> Signal ignore
            }
        Resource = fun _ _ -> failwith "no resource is wanted"
        Refused = fun _ -> None
        Lost = fun _ _ _ -> None
        DrainEnded = ignore
    }

    let host = {
        Host.create (fun () -> Subject.now) 1UL ignore with
            Decide = Gate.decide built.Gate
    }

    let runtime = Runtime(subject, interpreter, host, ())
    runtime.Start()

    for n in 1..20 do
        let request = requestOf n
        let requestM = request |> List.map Subject.invocationM
        let author = Subject.principals.[rand.Next 4]

        match runtime.Dispatch(n, author) with
        | Error why -> expect (sprintf "seed %d message %d accepted (%A)" seed n why) false
        | Ok sequence ->
            let shipped = outcomeOf runtime sequence

            let modelled =
                Authority.decide_step
                    Bridge.coreJoin
                    Subject.kindsM
                    Subject.hostGateM
                    (built.RestrictionsM author)
                    (policyM request)
                    (bigint Subject.now)
                    (bigint built.Gate.Generation)
                    requestM

            expect
                (sprintf "seed %d message %d: %A by %A: shipped %A, model %A" seed n request author shipped modelled)
                (shipped = Some modelled)

            if shipped = Some Authority.Parked then
                let parked = runtime.Parked |> List.find (fun p -> p.Envelope.Sequence = sequence)
                let variant = rand.Next 4
                let expiresAt = if variant = 1 then 500L else 2000L
                let minted = Gate.grant built.Gate parked Subject.approver expiresAt

                // 0: as minted; 1: expired; 2: forged for a request the step does not make now; 3: for another step.
                let otherRequest = (0, 0) :: request

                let grant =
                    match variant with
                    | 2 -> {
                        minted with
                            Request = Proposal.request (proposalOf author otherRequest)
                      }
                    | 3 -> { minted with Parked = parked.Id + 100 }
                    | _ -> minted

                let ctx: Authority.ctx = {
                    Authority.ctx.now = bigint Subject.now
                    Authority.ctx.generation = bigint built.Gate.Generation
                    Authority.ctx.presented =
                        Some(grantM grant (if variant = 2 then otherRequest else request) grant.Parked)
                }

                let found = runtime.Approve grant

                let modelled =
                    Authority.present
                        Bridge.coreJoin
                        Subject.kindsM
                        Subject.hostGateM
                        (built.RestrictionsM author)
                        (policyM request)
                        ctx
                        (bigint parked.Id)
                        requestM

                if variant = 3 then
                    expect
                        (sprintf "seed %d message %d: a grant for another step names no parked step" seed n)
                        (not found)

                    expect (sprintf "seed %d message %d: the model refuses it" seed n) (modelled = Authority.Refused)

                    expect
                        (sprintf "seed %d message %d: the step stays parked" seed n)
                        (runtime.Parked |> List.exists (fun p -> p.Id = parked.Id))
                else
                    expect (sprintf "seed %d message %d: the grant named the parked step" seed n) found

                    expect
                        (sprintf
                            "seed %d message %d: presented variant %d: shipped %A, model %A"
                            seed
                            n
                            variant
                            (outcomeOf runtime sequence)
                            modelled)
                        (outcomeOf runtime sequence = Some modelled)

/// Every differential case over the gate, each named for the proof leg's `Proofs.Oracle` filter.
let cases: Case list = [
    case
        "Proofs.Oracle authority: the extracted gate model and Gate.decide answer alike over generated principals, registries, steps and grants"
        (fun expect ->
            for seed in 1..40 do
                decisions expect seed)
    case
        "Proofs.Oracle authority: the extracted model and Runtime agree on what a dispatch commits, parks or refuses, and on every grant presented"
        (fun expect ->
            for seed in 1..40 do
                presentations expect seed)
    case
        "Proofs.Oracle authority: an unregistered kind is refused on both sides (go-red control: a registered one is not)"
        (fun expect ->
            let built = build (Rand 7)

            let decide request =
                Bridge.outcomeOf (Gate.decide built.Gate (proposalOf Subject.user request))

            let model request =
                Authority.decide_step
                    Bridge.coreJoin
                    Subject.kindsM
                    Subject.hostGateM
                    []
                    (policyM request)
                    (bigint Subject.now)
                    (bigint built.Gate.Generation)
                    (request |> List.map Subject.invocationM)

            expect "kind 4 refused by the gate" (decide [ 4, 1 ] = Authority.Refused)
            expect "kind 4 refused by the model" (model [ 4, 1 ] = Authority.Refused)
            expect "kind 0 with argument 1 is not refused by the gate" (decide [ 0, 1 ] <> Authority.Refused)
            expect "kind 0 with argument 1 is not refused by the model" (model [ 0, 1 ] <> Authority.Refused))
]
