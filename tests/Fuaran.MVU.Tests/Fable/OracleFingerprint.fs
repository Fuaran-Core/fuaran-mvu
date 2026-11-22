// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The models' cross-host fingerprint: the six extracted models driven over one fixed generated
/// corpus, every answer folded into one number. The .NET host and the Fable runner both compute it and
/// hold it to `expected`, so Fable's transpilation of each model is held to the .NET run of it. This
/// file reaches the extracted modules and nothing else (it sits in the Fable oracle directory, which the
/// proof leg's independence guard holds to that rule), so it names no library module: its generator and
/// its fold are written out.
module OracleFingerprint

/// The fingerprint the two hosts last agreed on. A model edit that changes any answer moves it, and the
/// leg says so on both hosts.
let expected: bigint = 47942641I

let private modulus: bigint = 1000000007I

let private fold (h: bigint) (code: bigint) : bigint = (h * 31I + code) % modulus

/// A linear congruential step over bigint, the same on both hosts.
let private next (state: bigint) : bigint =
    (state * 1103515245I + 12345I) % 2147483648I

let rec private drainOps (state: bigint) (n: int) (acc: Drain.op<bigint> list) : Drain.op<bigint> list =
    if n = 0 then
        acc
    else
        let s = next state
        let op = if s % 10I < 6I then Drain.Push(s % 1000I) else Drain.Pop
        drainOps s (n - 1) (op :: acc)

let rec private ringOps (state: bigint) (n: int) (acc: FixedRing.op<bigint> list) : FixedRing.op<bigint> list =
    if n = 0 then
        acc
    else
        let s = next state

        let op =
            if s % 10I < 6I then
                FixedRing.Push(s % 1000I)
            else
                FixedRing.Pop

        ringOps s (n - 1) (op :: acc)

let private drainCode (o: Drain.opt<Drain.slot<bigint>>) : bigint =
    match o with
    | Drain.ONone -> 1I
    | Drain.OSome Drain.Placeholder -> 2I
    | Drain.OSome(Drain.Written v) -> 3I + v

let private ringCode (o: FixedRing.out<bigint>) : bigint =
    match o with
    | FixedRing.Popped FixedRing.ONone -> 1I
    | FixedRing.Popped(FixedRing.OSome FixedRing.Placeholder) -> 2I
    | FixedRing.Popped(FixedRing.OSome(FixedRing.Written v)) -> 3I + v
    | FixedRing.Pushed true -> 5000I
    | FixedRing.Pushed false -> 5001I

let rec private foldAll (code: 'a -> bigint) (h: bigint) (outs: 'a list) : bigint =
    match outs with
    | [] -> h
    | o :: rest -> foldAll code (fold h (code o)) rest

let rec private entries (state: bigint) (key: bigint) (n: int) : KeyedDiff.entry<bigint, bigint> list =
    if n = 0 then
        []
    else
        let s = next state

        if s % 2I = 0I then
            {
                KeyedDiff.entry.key = key
                KeyedDiff.entry.description = s % 3I
            }
            :: entries s (key + 1I) (n - 1)
        else
            entries s (key + 1I) (n - 1)

let rec private withoutKeysOf
    (running: KeyedDiff.entry<bigint, bigint> list)
    (xs: KeyedDiff.entry<bigint, bigint> list)
    =
    match xs with
    | [] -> []
    | e :: rest ->
        match KeyedDiff.lookup e.key running with
        | KeyedDiff.ONone -> e :: withoutKeysOf running rest
        | KeyedDiff.OSome _ -> withoutKeysOf running rest

let private entryCode (e: KeyedDiff.entry<bigint, bigint>) : bigint = 7I + e.key * 10I + e.description

let private keyCode (k: bigint) : bigint = 11I + k

let private actionCode (a: KeyedDiff.action<bigint, bigint>) : bigint =
    match a with
    | KeyedDiff.Stop k -> 100I + k
    | KeyedDiff.Start(k, d) -> 200I + k * 10I + d

let private diffRound (h: bigint) (seed: bigint) : bigint =
    let running = entries seed 0I 8
    let held = withoutKeysOf running (entries (seed + 1I) 0I 8)
    let wanted = entries (seed + 2I) 0I 8
    let plan = KeyedDiff.plan_of running held wanted

    let after =
        KeyedDiff.apply (fun k d -> (k + d + seed) % 3I <> 0I) running held wanted

    let h1 = foldAll keyCode h plan.stops
    let h2 = foldAll keyCode h1 plan.keeps
    let h3 = foldAll entryCode h2 plan.starts
    let h4 = foldAll actionCode h3 (KeyedDiff.sequence plan)
    let h5 = foldAll entryCode h4 after.first
    foldAll entryCode h5 after.second

/// The step machine's subject, in the extraction's own types: a deterministic `update` with a few effects
/// and follow-ups per message (one message loops, so runs over the chain's fuel), resources read off the
/// model, and a decision read off the proposal's sums. The Fable runner's outright checks use these too.
/// Written out without the List module, as this directory's independence guard requires.
let rec private effectsFrom (m: bigint) (n: bigint) : StepChain.step<bigint, bigint> list =
    if n <= 0I then
        []
    else
        StepChain.Do(m + n - 1I) :: effectsFrom m (n - 1I)

let rec private revOnto (acc: 'a list) (xs: 'a list) : 'a list =
    match xs with
    | [] -> acc
    | x :: rest -> revOnto (x :: acc) rest

let rec private appendSteps
    (xs: StepChain.step<bigint, bigint> list)
    (ys: StepChain.step<bigint, bigint> list)
    : StepChain.step<bigint, bigint> list =
    match xs with
    | [] -> ys
    | x :: rest -> x :: appendSteps rest ys

let stepUpdate (m: bigint) (x: bigint) : StepChain.pair<bigint, StepChain.step<bigint, bigint> list> =
    let m' = (m * 31I + x + 7I) % 1000I
    let h = (m' * 17I + x) % 10I
    let effects = revOnto [] (effectsFrom m' (h % 3I))

    let follows =
        if x = 42I then
            [ StepChain.Then 42I ]
        elif h = 4I then
            [ StepChain.Then((x + 1I) % 50I) ]
        elif h = 9I && x < 25I then
            [ StepChain.Then(x + 1I); StepChain.Then(x + 2I) ]
        else
            []

    {
        StepChain.pair.first = m'
        StepChain.pair.second = appendSteps effects follows
    }

let rec private wantsFrom (bits: bigint) (desc: bigint) (k: bigint) (p: bigint) : StepMachine.want list =
    if k > 2I then
        []
    else
        let rest = wantsFrom bits desc (k + 1I) (p * 2I)

        if (bits / p) % 2I = 1I then
            {
                StepMachine.want.wkey = k
                StepMachine.want.wdesc = desc
            }
            :: rest
        else
            rest

let stepResources (m: bigint) : StepMachine.want list =
    wantsFrom (m / 7I) ((m / 50I) % 2I) 0I 1I

let rec private sumAll (xs: bigint list) : bigint =
    match xs with
    | [] -> 0I
    | x :: rest -> x + sumAll rest

let rec private count (xs: 'a list) : bigint =
    match xs with
    | [] -> 0I
    | _ :: rest -> 1I + count rest

let stepDecide (p: StepMachine.proposal<bigint>) : StepMachine.decision =
    let verdict = (sumAll p.effects + count p.starts + count p.stops + 3I) % 11I

    if verdict = 0I then StepMachine.Deny
    elif verdict = 1I then StepMachine.Park
    else StepMachine.Allow

let private envelopeCode (e: StepMachine.envelope<bigint>) : bigint = 13I + e.seq * 100I + e.msg

let private instCode (r: StepMachine.inst) : bigint =
    17I + r.ikey * 100I + r.idesc * 10I + r.tok

/// Thirty operations over the step machine from one seed: dispatches from outside and through instance
/// tokens, a termination near the end, then every observable folded in.
let private machineRound (h: bigint) (seed: bigint) : bigint =
    let rec go (st: StepMachine.st<bigint, bigint, bigint>) (state: bigint) (n: int) (refusals: bigint) =
        if n = 0 then
            st, refusals
        else
            let s = next state

            let src =
                if s % 4I = 0I then
                    StepMachine.FromInstance((s / 4I) % 6I + 1I)
                else
                    StepMachine.External

            if n = 3 then
                go (StepMachine.terminate st) s (n - 1) refusals
            else
                let r =
                    StepMachine.dispatch false 0I 1000I 8I stepUpdate stepResources stepDecide st (s % 50I) src

                go r.first s (n - 1) (if r.second then refusals else refusals + 1I)

    let st, refusals = go (StepMachine.initial 0I) seed 30 0I
    let h1 = fold h st.model
    let h2 = foldAll (fun (e: bigint) -> 3I + e) h1 st.handed
    let h3 = foldAll (fun (x: bigint) -> 5I + x) h2 st.refused
    let h4 = foldAll envelopeCode h3 st.parked
    let h5 = foldAll instCode h4 st.running
    let h6 = foldAll (fun (t: bigint) -> 7I + t) h5 st.live
    let h7 = fold h6 st.renders
    let h8 = fold h7 st.next_seq
    fold h8 refusals

/// The gate's subject in the extraction's types: four kinds with their classes, the host's gate refusing kind 3 at
/// an argument of seven or more, restrictions and requests drawn from the state, a grant on every other round.
let private authorityKinds: Authority.kind list = [
    {
        Authority.kind.kid = 0I
        Authority.kind.classes = [ 0I ]
    }
    {
        Authority.kind.kid = 1I
        Authority.kind.classes = [ 1I ]
    }
    {
        Authority.kind.kid = 2I
        Authority.kind.classes = [ 0I; 1I ]
    }
    {
        Authority.kind.kid = 3I
        Authority.kind.classes = []
    }
]

let private authorityHostGate (_: Authority.ctx) (i: Authority.invocation) : Authority.verdict =
    if i.ikind = 3I && i.iargs >= 7I then
        Authority.Refuse
    else
        Authority.Permit

/// The join as Core defines it, written out: the argument of the larger rank, the left on a tie. The fingerprint
/// reaches no library, so Core's own join is handed to the model by the differential, not here.
let private joinMax (a: Authority.verdict) (b: Authority.verdict) : Authority.verdict =
    if Authority.rank b > Authority.rank a then b else a

let rec private restrictionsFrom (state: bigint) (n: bigint) : Authority.restriction list =
    if n <= 0I then
        []
    else
        let s = next state

        let r =
            if s % 4I = 0I then
                Authority.Only [ s % 3I; (s / 3I) % 4I ]
            elif s % 4I = 1I then
                Authority.ApprovalFor [ s % 2I ]
            elif s % 4I = 2I then
                Authority.Refusing [ (s / 2I) % 2I ]
            else
                Authority.Custom(fun _ i ->
                    if i.ikind = 2I && i.iargs % 2I = 1I then
                        Authority.Refuse
                    else
                        Authority.Permit)

        r :: restrictionsFrom s (n - 1I)

let rec private requestFrom (state: bigint) (n: bigint) : Authority.invocation list =
    if n <= 0I then
        []
    else
        let s = next state

        {
            Authority.invocation.ikind = s % 6I
            Authority.invocation.iargs = (s / 6I) % 10I
        }
        :: requestFrom s (n - 1I)

let private verdictCode (v: Authority.verdict) : bigint =
    match v with
    | Authority.Permit -> 1I
    | Authority.Approve -> 2I
    | Authority.Refuse -> 3I

let private outcomeCode (o: Authority.outcome) : bigint =
    match o with
    | Authority.Committed -> 4I
    | Authority.Parked -> 5I
    | Authority.Refused -> 6I

/// One round over the gate's model: a step decided for a principal with drawn restrictions, under a second
/// ceiling, as a dispatch, and as a presentation of a grant — valid, stale, expired or from another generation.
let private authorityRound (h: bigint) (seed: bigint) : bigint =
    let rs = restrictionsFrom seed (seed % 3I)
    let request = requestFrom (seed + 5I) ((seed / 7I) % 5I)

    let policy =
        if (seed / 11I) % 3I = 0I then Authority.Permit
        elif (seed / 11I) % 3I = 1I then Authority.Approve
        else Authority.Refuse

    let grant =
        if seed % 2I = 0I then
            None
        else
            Some {
                Authority.grant.gparked = 1I
                Authority.grant.grequest =
                    (if seed % 4I = 1I then
                         request
                     else
                         {
                             Authority.invocation.ikind = 0I
                             Authority.invocation.iargs = 0I
                         }
                         :: request)
                Authority.grant.gexpires = (if seed % 8I = 3I then 5I else 50I)
                Authority.grant.ggeneration = (if seed % 16I = 7I then 2I else 1I)
            }

    let ctx: Authority.ctx = {
        Authority.ctx.now = 10I
        Authority.ctx.generation = 1I
        Authority.ctx.presented = grant
    }

    let h1 =
        fold h (verdictCode (Authority.step_verdict joinMax authorityKinds authorityHostGate rs policy ctx request))

    let h2 =
        fold
            h1
            (verdictCode (
                Authority.two_ceilings
                    joinMax
                    authorityKinds
                    authorityHostGate
                    rs
                    (restrictionsFrom (seed + 1I) 1I)
                    policy
                    ctx
                    request
            ))

    let h3 =
        fold h2 (outcomeCode (Authority.decide_step joinMax authorityKinds authorityHostGate rs policy 10I 1I request))

    fold h3 (outcomeCode (Authority.present joinMax authorityKinds authorityHostGate rs policy ctx 1I request))

let rec private rounds (h: bigint) (seed: bigint) (n: int) : bigint =
    if n = 0 then
        h
    else
        let d = Drain.run (Drain.create (seed % 5I)) (drainOps seed 120 [])
        let h1 = foldAll drainCode h d.second
        let h2 = fold h1 (d.first.write - d.first.read)
        let r = FixedRing.run (FixedRing.create (seed % 6I)) (ringOps (seed + 7I) 120 [])
        let h3 = foldAll ringCode h2 r.second
        let h4 = fold h3 (FixedRing.capacity r.first)
        let h5 = diffRound h4 seed
        let h6 = machineRound h5 seed
        let h7 = authorityRound h6 seed
        rounds h7 (seed + 13I) (n - 1)

/// The fingerprint of the six models over the fixed corpus: 40 rounds of a drain run, a ring run, a diff
/// round, a step-machine round (the chain is driven through the machine) and a gate round each.
let fingerprint () : bigint = rounds 17I 1I 40
