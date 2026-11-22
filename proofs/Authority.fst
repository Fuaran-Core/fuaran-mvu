(*
   Copyright 2026 Diametrical Ltd

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*)

/// Phase 648 — the gate: a principal's registry, a step decided through Core, and a grant, modelled in F*.
///
/// # What this module is
///
/// A model of `Gate.decideFor` and `Runtime.Approve` (`src/Fuaran.MVU/Gate.fs`, `Runtime.fs`): the decision the
/// step machine takes as its `decide` parameter, as the gate realises it through Core's capability registry. A
/// step (`request`: its invocations, each a kind and its canonical arguments) is decided for its author: the
/// domain's policy, the grant verdict (a grant presented is read as data — expired or from another registry
/// generation refuses), and for every invocation the author's registry — refuse an unregistered kind, else the
/// join of the host's gates and the author's restrictions, an approval requirement lifted to a permit when a
/// grant for exactly this request is presented. The whole is the join over all of it. `present` is the runtime's
/// approval: the grant names the parked step and the request as it is NOW, or the step is refused.
///
/// # The premise: Core's join
///
/// The join is a PARAMETER of every function here, and every theorem requires `is_join join`: the rank of
/// `join a b` is the larger of the two ranks. That is Core's own theorem about `PolicyDecision.join`
/// (`Fuaran-Core/proofs/Capability.fst`: `join_is_max`, from which `policy_join_monotone_in_context` and
/// `with_gate_tightens_in_context` follow), discharged there at v0.43.0 and taken here as a hypothesis, never
/// re-proved: a copied theorem is a fork. The differential hands the extraction Core's join itself.
///
/// # The abstraction function
///
/// `PolicyDecision` is `verdict` by rank. A kind is its id and its classes. A restriction is Core's `restrict`
/// (`Only`) or `withGate` (the two class gates, and `Custom` for a gate of the host's own); the host's own gates
/// are one function, `host_gate`. The decision context is the clock, the generation and the grant presented. The
/// request identity is the list of invocations — what `Proposal.request` renders injectively (Core's field
/// encoding, D249), so equality of identities is equality of lists here.
///
/// # The theorems — one per guarantee of the gate's row of the design's ladder
///
///   * `permit_means_each_permitted_or_granted` — a permitted step's every invocation was permitted by the
///     author's registry outright, or required an approval and a grant for exactly this request was presented;
///     with no grant the verdict is the raw one (`no_grant_no_lift`), and a refusal is never lifted
///     (`refuse_never_lifted`).
///   * `restricted_never_looser` / `restricted_step_never_looser` — a restricted principal's verdict, for an
///     invocation and for a whole step, is never of lower rank than the host's.
///   * `unregistered_refused` — a request naming an unregistered kind is refused, whatever else is true of it.
///   * `two_ceilings_never_looser` — the verdict under two ceilings is never of lower rank than under either.
///   * `present_commits_only_its_step` — a presentation commits only for the parked step the grant names, with
///     the request the grant was minted for, and a permit; `stale_refused` — a request that moved is refused.
///   * `expired_or_old_generation_refused` — a grant expired by the clock, or minted under another generation,
///     refuses the step.
///
/// # What is not modelled
///
/// The approver (Core's `approveGuardedIn` refuses an approver who is the author; tested), the guidance text of a
/// refusal, the destination as a field (it rides in the arguments here), and which parked step the runtime
/// finds (`gparked` is compared, the lookup is the runtime's). Nothing is admitted or assumed beyond `is_join`.
module Authority

#set-options "--fuel 0 --ifuel 0"

(* ───────────────────────────────────────────────────────────────────
   Types.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `PolicyDecision` — `Allow`, `NeedsApproval`, `Deny`.
type verdict =
  | Permit : verdict
  | Approve : verdict
  | Refuse : verdict

#push-options "--fuel 1 --ifuel 1"

/// F#: `PolicyDecision.rank`.
let rank (v: verdict) : nat =
  match v with
  | Permit -> 0
  | Approve -> 1
  | Refuse -> 2

/// The premise on the join parameter: Core's `PolicyDecision.join` — the argument of the larger rank, the left
/// on a tie (`if rank b > rank a then b else a`), of which `join_is_max` is Core's theorem.
let is_join (join: verdict -> verdict -> verdict) : prop =
  forall (a b: verdict). join a b == (if rank b > rank a then b else a)

/// F#: `EffectKind`, reduced to what the gate reads: the id and the classes fixed at registration.
type kind = { kid: nat; classes: list nat }

/// F#: `Invocation`: the kind and the canonical arguments (the destination rides in them here).
type invocation = { ikind: nat; iargs: nat }

/// F#: the decision context's view of a `Grant`: the parked step, the request as previewed, the expiry and the
/// registry generation it was minted under.
type grant = { gparked: nat; grequest: list invocation; gexpires: nat; ggeneration: nat }

/// F#: `DecisionContext`: the host's clock as data, the gate's generation, the grant presented.
type ctx = { now: nat; generation: nat; presented: option grant }

/// F#: `Restriction` — Core's `restrict` (`Only`) and `withGate` (the class gates and a gate of the host's own).
noeq type restriction =
  | Only : kinds: list nat -> restriction
  | ApprovalFor : classes: list nat -> restriction
  | Refusing : classes: list nat -> restriction
  | Custom : gate: (ctx -> invocation -> verdict) -> restriction

/// F#: `Decision` as the step machine sees the gate's answer: committed, parked or refused.
type outcome =
  | Committed : outcome
  | Parked : outcome
  | Refused : outcome

(* ───────────────────────────────────────────────────────────────────
   List helpers the model owns, so the extraction references Prims only.
   ─────────────────────────────────────────────────────────────────── *)

let rec mem (x: nat) (xs: list nat) : Tot bool (decreases xs) =
  match xs with
  | [] -> false
  | y :: rest -> x = y || mem x rest

let rec intersects (xs ys: list nat) : Tot bool (decreases xs) =
  match xs with
  | [] -> false
  | x :: rest -> mem x ys || intersects rest ys

let rec registered (kinds: list kind) (k: nat) : Tot bool (decreases kinds) =
  match kinds with
  | [] -> false
  | kd :: rest -> kd.kid = k || registered rest k

/// The classes a kind was registered with; none for a kind the gate does not hold.
let rec classes_of (kinds: list kind) (k: nat) : Tot (list nat) (decreases kinds) =
  match kinds with
  | [] -> []
  | kd :: rest -> if kd.kid = k then kd.classes else classes_of rest k

/// Whether an invocation is one of a request's.
let rec mem_inv (i: invocation) (invs: list invocation) : Tot bool (decreases invs) =
  match invs with
  | [] -> false
  | j :: rest -> i = j || mem_inv i rest

(* ───────────────────────────────────────────────────────────────────
   The gate.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Gate.grantVerdict`: a grant presented is read as data — expired by the clock, or minted under another
/// registry generation, refuses; no grant, or a current one, permits.
let grant_verdict (c: ctx) : verdict =
  match c.presented with
  | None -> Permit
  | Some g -> if g.gexpires < c.now || g.ggeneration <> c.generation then Refuse else Permit

/// F#: one restriction's gate, as `Gate.registryOf` installs it: `Only` is Core's `restrict`, so a kind outside it
/// is unregistered for the principal and refused; the class gates read the kind's REGISTERED classes.
let restriction_verdict (kinds: list kind) (r: restriction) (c: ctx) (i: invocation) : verdict =
  match r with
  | Only ks -> if mem i.ikind ks then Permit else Refuse
  | ApprovalFor cs -> if intersects cs (classes_of kinds i.ikind) then Approve else Permit
  | Refusing cs -> if intersects cs (classes_of kinds i.ikind) then Refuse else Permit
  | Custom gate -> gate c i

/// F#: Core's `decideNamed` fold over the gates a principal's restrictions added, from the host's verdict.
let rec gates (join: verdict -> verdict -> verdict) (kinds: list kind) (rs: list restriction) (c: ctx)
              (i: invocation) (acc: verdict) : Tot verdict (decreases rs) =
  match rs with
  | [] -> acc
  | r :: rest -> gates join kinds rest c i (join acc (restriction_verdict kinds r c i))

/// F#: `CapabilityRegistry.decide` with no grant read: an unregistered kind is refused (default-deny by shape);
/// otherwise the host's gates, then the principal's restrictions, joined.
let raw (join: verdict -> verdict -> verdict) (kinds: list kind) (host_gate: ctx -> invocation -> verdict)
        (rs: list restriction) (c: ctx) (i: invocation) : verdict =
  if not (registered kinds i.ikind) then Refuse
  else gates join kinds rs c i (host_gate c i)

/// Whether the grant presented is for exactly this request.
let covers (c: ctx) (request: list invocation) : bool =
  match c.presented with
  | None -> false
  | Some g -> g.grequest = request

/// F#: `PolicyDecision.lifted` under the runtime's request check: an approval requirement is lifted to a permit
/// when the grant presented is for exactly this request; every other verdict stands.
let lifted (c: ctx) (request: list invocation) (v: verdict) : verdict =
  if Approve? v && covers c request then Permit else v

/// F#: Core's `decideGranted`: the registry's verdict for one invocation of a request, lifted.
let registry_verdict (join: verdict -> verdict -> verdict) (kinds: list kind)
                     (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (c: ctx)
                     (request: list invocation) (i: invocation) : verdict =
  lifted c request (raw join kinds host_gate rs c i)

/// The join over every invocation of a request, from an accumulator.
let rec step_gates (join: verdict -> verdict -> verdict) (kinds: list kind)
                   (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (c: ctx)
                   (request: list invocation) (invs: list invocation) (acc: verdict)
  : Tot verdict (decreases invs) =
  match invs with
  | [] -> acc
  | i :: rest -> step_gates join kinds host_gate rs c request rest (join acc (registry_verdict join kinds host_gate rs c request i))

/// F#: `Gate.decideFor`: the policy's verdict for this step — lifted by a grant as Core's approval reads the
/// approver's policy (an approval requirement met by the approval) — the grant verdict, and every invocation's
/// registry verdict through the author's registry, joined.
let step_verdict (join: verdict -> verdict -> verdict) (kinds: list kind)
                 (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (policy: verdict) (c: ctx)
                 (request: list invocation) : verdict =
  step_gates join kinds host_gate rs c request request (join (lifted c request policy) (grant_verdict c))

/// F#: `Gate.decideUnder` with one further ceiling: the author's verdict joined with the other principal's.
let two_ceilings (join: verdict -> verdict -> verdict) (kinds: list kind)
                 (host_gate: ctx -> invocation -> verdict) (rs other: list restriction) (policy: verdict)
                 (c: ctx) (request: list invocation) : verdict =
  join (step_verdict join kinds host_gate rs policy c request) (step_verdict join kinds host_gate other policy c request)

/// F#: `Decision` into the step machine's outcome.
let outcome_of (v: verdict) : outcome =
  match v with
  | Permit -> Committed
  | Approve -> Parked
  | Refuse -> Refused

/// F#: a step as it is first decided — no grant presented.
let decide_step (join: verdict -> verdict -> verdict) (kinds: list kind)
                (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (policy: verdict)
                (now generation: nat) (request: list invocation) : outcome =
  outcome_of (step_verdict join kinds host_gate rs policy { now = now; generation = generation; presented = None } request)

/// F#: `Runtime.Approve`: the grant names the parked step and the request as it is NOW (else stale or another
/// step's: refused, nothing committed), then the step is decided again with the grant presented and commits only
/// on a permit; a verdict short of a permit refuses, because the parked step is spent.
let present (join: verdict -> verdict -> verdict) (kinds: list kind)
            (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (policy: verdict)
            (c: ctx) (parked_id: nat) (request_now: list invocation) : outcome =
  match c.presented with
  | None -> Refused
  | Some g ->
      if g.gparked <> parked_id || g.grequest <> request_now then Refused
      else
        match step_verdict join kinds host_gate rs policy c request_now with
        | Permit -> Committed
        | _ -> Refused

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The premise, read.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

/// Reading the premise at one pair.
let join_is (join: verdict -> verdict -> verdict) (a b: verdict)
  : Lemma (requires is_join join) (ensures join a b == (if rank b > rank a then b else a)) = ()

/// Core's `join_is_max`: the join's rank is the larger rank.
let join_rank (join: verdict -> verdict -> verdict) (a b: verdict)
  : Lemma (requires is_join join)
          (ensures rank (join a b) == (if rank a >= rank b then rank a else rank b)) = join_is join a b

/// The join is an upper bound of both arguments (Core: `with_gate_tightens_in_context`).
let join_upper (join: verdict -> verdict -> verdict) (a b: verdict)
  : Lemma (requires is_join join) (ensures rank (join a b) >= rank a /\ rank (join a b) >= rank b) =
  join_rank join a b

/// The join is monotone in both arguments (Core: `policy_join_monotone_in_context`).
let join_monotone (join: verdict -> verdict -> verdict) (a a' b b': verdict)
  : Lemma (requires is_join join /\ rank a <= rank a' /\ rank b <= rank b')
          (ensures rank (join a b) <= rank (join a' b')) =
  join_rank join a b; join_rank join a' b'

/// A verdict of rank two is a refusal; of rank zero a permit.
let rank_cases (v: verdict) : Lemma (ensures (rank v == 2 <==> v == Refuse) /\ (rank v == 0 <==> v == Permit)) = ()

(* ───────────────────────────────────────────────────────────────────
   Lemmas on the folds.
   ─────────────────────────────────────────────────────────────────── *)

/// The gates' fold never drops below its accumulator.
let rec gates_rises (join: verdict -> verdict -> verdict) (kinds: list kind) (rs: list restriction) (c: ctx)
                    (i: invocation) (acc: verdict)
  : Lemma (requires is_join join) (ensures rank (gates join kinds rs c i acc) >= rank acc) (decreases rs) =
  match rs with
  | [] -> ()
  | r :: rest ->
      join_upper join acc (restriction_verdict kinds r c i);
      gates_rises join kinds rest c i (join acc (restriction_verdict kinds r c i))

/// The gates' fold is monotone in its accumulator.
let rec gates_monotone (join: verdict -> verdict -> verdict) (kinds: list kind) (rs: list restriction) (c: ctx)
                       (i: invocation) (acc acc': verdict)
  : Lemma (requires is_join join /\ rank acc <= rank acc')
          (ensures rank (gates join kinds rs c i acc) <= rank (gates join kinds rs c i acc')) (decreases rs) =
  match rs with
  | [] -> ()
  | r :: rest ->
      let v = restriction_verdict kinds r c i in
      join_monotone join acc acc' v v;
      gates_monotone join kinds rest c i (join acc v) (join acc' v)

/// The step's fold never drops below its accumulator.
let rec step_gates_rises (join: verdict -> verdict -> verdict) (kinds: list kind)
                         (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (c: ctx)
                         (request invs: list invocation) (acc: verdict)
  : Lemma (requires is_join join)
          (ensures rank (step_gates join kinds host_gate rs c request invs acc) >= rank acc) (decreases invs) =
  match invs with
  | [] -> ()
  | i :: rest ->
      let v = registry_verdict join kinds host_gate rs c request i in
      join_upper join acc v;
      step_gates_rises join kinds host_gate rs c request rest (join acc v)

/// The step's fold is at least each invocation's verdict.
let rec step_gates_covers (join: verdict -> verdict -> verdict) (kinds: list kind)
                          (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (c: ctx)
                          (request invs: list invocation) (acc: verdict) (i: invocation)
  : Lemma (requires is_join join /\ mem_inv i invs)
          (ensures rank (step_gates join kinds host_gate rs c request invs acc) >=
                   rank (registry_verdict join kinds host_gate rs c request i)) (decreases invs) =
  match invs with
  | [] -> ()
  | j :: rest ->
      let v = registry_verdict join kinds host_gate rs c request j in
      if i = j then begin
        join_upper join acc v;
        step_gates_rises join kinds host_gate rs c request rest (join acc v)
      end
      else step_gates_covers join kinds host_gate rs c request rest (join acc v) i


(* ───────────────────────────────────────────────────────────────────
   THEOREM 1 — permitted, or granted for exactly this request.
   ─────────────────────────────────────────────────────────────────── *)

/// With no grant presented the registry's verdict is the raw one: nothing else lifts an approval requirement.
let no_grant_no_lift (join: verdict -> verdict -> verdict) (kinds: list kind)
                     (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (c: ctx)
                     (request: list invocation) (i: invocation)
  : Lemma (requires None? c.presented)
          (ensures registry_verdict join kinds host_gate rs c request i == raw join kinds host_gate rs c i) = ()

/// A refusal is never lifted, whatever is presented.
let refuse_never_lifted (join: verdict -> verdict -> verdict) (kinds: list kind)
                        (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (c: ctx)
                        (request: list invocation) (i: invocation)
  : Lemma (requires raw join kinds host_gate rs c i == Refuse)
          (ensures registry_verdict join kinds host_gate rs c request i == Refuse) = ()

/// THEOREM — `permit_means_each_permitted_or_granted`. A permitted step's policy verdict is a permit, or an approval
/// requirement the grant for exactly this request met, and every
/// invocation of its request was permitted by the author's registry outright, or required an approval that a
/// grant presented for exactly this request lifted.
let permit_means_each_permitted_or_granted (join: verdict -> verdict -> verdict) (kinds: list kind)
                                           (host_gate: ctx -> invocation -> verdict) (rs: list restriction)
                                           (policy: verdict) (c: ctx) (request: list invocation) (i: invocation)
  : Lemma (requires is_join join /\ mem_inv i request /\
                    step_verdict join kinds host_gate rs policy c request == Permit)
          (ensures lifted c request policy == Permit /\ grant_verdict c == Permit /\
                   (raw join kinds host_gate rs c i == Permit \/
                    (raw join kinds host_gate rs c i == Approve /\ covers c request))) =
  let acc = join (lifted c request policy) (grant_verdict c) in
  step_gates_rises join kinds host_gate rs c request request acc;
  join_upper join (lifted c request policy) (grant_verdict c);
  rank_cases (lifted c request policy); rank_cases (grant_verdict c);
  step_gates_covers join kinds host_gate rs c request request acc i;
  rank_cases (registry_verdict join kinds host_gate rs c request i)

(* ───────────────────────────────────────────────────────────────────
   THEOREM 2 — a restricted principal is never looser than the host.
   ─────────────────────────────────────────────────────────────────── *)

/// The raw verdict under restrictions is at least the host's (`[]`).
let raw_restricted_rises (join: verdict -> verdict -> verdict) (kinds: list kind)
                         (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (c: ctx) (i: invocation)
  : Lemma (requires is_join join)
          (ensures rank (raw join kinds host_gate rs c i) >= rank (raw join kinds host_gate [] c i)) =
  if registered kinds i.ikind then gates_rises join kinds rs c i (host_gate c i)

/// THEOREM — `restricted_never_looser`. For one invocation, a restricted principal's registry verdict is never
/// of lower rank than the host's, grant presented or not.
let restricted_never_looser (join: verdict -> verdict -> verdict) (kinds: list kind)
                            (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (c: ctx)
                            (request: list invocation) (i: invocation)
  : Lemma (requires is_join join)
          (ensures rank (registry_verdict join kinds host_gate rs c request i) >=
                   rank (registry_verdict join kinds host_gate [] c request i)) =
  raw_restricted_rises join kinds host_gate rs c i

/// The step's fold under restrictions is at least the fold under none, from accumulators so ordered.
let rec step_gates_restricted (join: verdict -> verdict -> verdict) (kinds: list kind)
                              (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (c: ctx)
                              (request invs: list invocation) (acc acc': verdict)
  : Lemma (requires is_join join /\ rank acc <= rank acc')
          (ensures rank (step_gates join kinds host_gate [] c request invs acc) <=
                   rank (step_gates join kinds host_gate rs c request invs acc')) (decreases invs) =
  match invs with
  | [] -> ()
  | i :: rest ->
      let v = registry_verdict join kinds host_gate [] c request i in
      let v' = registry_verdict join kinds host_gate rs c request i in
      restricted_never_looser join kinds host_gate rs c request i;
      join_monotone join acc acc' v v';
      step_gates_restricted join kinds host_gate rs c request rest (join acc v) (join acc' v')

/// THEOREM — `restricted_step_never_looser`. For a whole step, the same.
let restricted_step_never_looser (join: verdict -> verdict -> verdict) (kinds: list kind)
                                 (host_gate: ctx -> invocation -> verdict) (rs: list restriction)
                                 (policy: verdict) (c: ctx) (request: list invocation)
  : Lemma (requires is_join join)
          (ensures rank (step_verdict join kinds host_gate rs policy c request) >=
                   rank (step_verdict join kinds host_gate [] policy c request)) =
  let acc = join (lifted c request policy) (grant_verdict c) in
  step_gates_restricted join kinds host_gate rs c request request acc acc

(* ───────────────────────────────────────────────────────────────────
   THEOREM 3 — an unregistered kind is refused.
   ─────────────────────────────────────────────────────────────────── *)

/// THEOREM — `unregistered_refused`. A request naming an unregistered kind is refused whole.
let unregistered_refused (join: verdict -> verdict -> verdict) (kinds: list kind)
                         (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (policy: verdict)
                         (c: ctx) (request: list invocation) (i: invocation)
  : Lemma (requires is_join join /\ mem_inv i request /\ not (registered kinds i.ikind))
          (ensures step_verdict join kinds host_gate rs policy c request == Refuse) =
  let acc = join (lifted c request policy) (grant_verdict c) in
  step_gates_covers join kinds host_gate rs c request request acc i;
  rank_cases (step_verdict join kinds host_gate rs policy c request)

(* ───────────────────────────────────────────────────────────────────
   THEOREM 4 — two ceilings.
   ─────────────────────────────────────────────────────────────────── *)

/// THEOREM — `two_ceilings_never_looser`. The verdict under the author's ceiling and another's is never of lower
/// rank than under either alone.
let two_ceilings_never_looser (join: verdict -> verdict -> verdict) (kinds: list kind)
                              (host_gate: ctx -> invocation -> verdict) (rs other: list restriction)
                              (policy: verdict) (c: ctx) (request: list invocation)
  : Lemma (requires is_join join)
          (ensures rank (two_ceilings join kinds host_gate rs other policy c request) >=
                     rank (step_verdict join kinds host_gate rs policy c request) /\
                   rank (two_ceilings join kinds host_gate rs other policy c request) >=
                     rank (step_verdict join kinds host_gate other policy c request)) =
  join_upper join (step_verdict join kinds host_gate rs policy c request)
                  (step_verdict join kinds host_gate other policy c request)

(* ───────────────────────────────────────────────────────────────────
   THEOREMS 5 to 7 — the grant's rules.
   ─────────────────────────────────────────────────────────────────── *)

/// THEOREM — `present_commits_only_its_step`. A presentation that commits presented a grant naming this parked
/// step and this very request, and the step's verdict with the grant was a permit.
let present_commits_only_its_step (join: verdict -> verdict -> verdict) (kinds: list kind)
                                  (host_gate: ctx -> invocation -> verdict) (rs: list restriction)
                                  (policy: verdict) (c: ctx) (parked_id: nat) (request_now: list invocation)
  : Lemma (requires present join kinds host_gate rs policy c parked_id request_now == Committed)
          (ensures Some? c.presented /\
                   (Some?.v c.presented).gparked == parked_id /\
                   (Some?.v c.presented).grequest == request_now /\
                   step_verdict join kinds host_gate rs policy c request_now == Permit) = ()

/// THEOREM — `stale_refused`. A grant for a request other than the one the step makes now refuses.
let stale_refused (join: verdict -> verdict -> verdict) (kinds: list kind)
                  (host_gate: ctx -> invocation -> verdict) (rs: list restriction) (policy: verdict)
                  (c: ctx) (g: grant) (parked_id: nat) (request_now: list invocation)
  : Lemma (requires c.presented == Some g /\ g.grequest <> request_now)
          (ensures present join kinds host_gate rs policy c parked_id request_now == Refused) = ()

/// THEOREM — `expired_or_old_generation_refused`. A grant expired by the clock, or minted under another registry
/// generation, refuses the step whatever else is true of it.
let expired_or_old_generation_refused (join: verdict -> verdict -> verdict) (kinds: list kind)
                                      (host_gate: ctx -> invocation -> verdict) (rs: list restriction)
                                      (policy: verdict) (c: ctx) (g: grant) (request: list invocation)
  : Lemma (requires is_join join /\ c.presented == Some g /\
                    (g.gexpires < c.now \/ g.ggeneration <> c.generation))
          (ensures step_verdict join kinds host_gate rs policy c request == Refuse) =
  let acc = join (lifted c request policy) (grant_verdict c) in
  join_upper join (lifted c request policy) (grant_verdict c);
  step_gates_rises join kinds host_gate rs c request request acc;
  rank_cases (step_verdict join kinds host_gate rs policy c request)

#pop-options
