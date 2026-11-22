(*
   Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
   Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
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

/// Phase 647 — the step machine (the design's runtime semantics, rules 1 to 25), modelled in F*.
///
/// # What this module is
///
/// A model of `Runtime` (`src/Fuaran.MVU/Runtime.fs`): acceptance, the step — a chain computed whole
/// (`StepChain`), decided as a unit, then committed, parked or refused — the drain with its fuel and its one
/// render, the resource plan with a liveness token per instance, and termination. Its shape — the queue,
/// the drain, the render once at the end, the absorbing termination — descends from the dispatch loop
/// model of the ToolUp Analytics open-source SDK (toolup-forge, Apache-2.0), itself a model of
/// Fable.Elmish's program loop; new here are envelopes with sources, chains with fuel, the decision as a
/// unit, parked steps, tokens per instance and per reply, and the fuel on the drain.
///
/// # The representation
///
/// The queue is a list of envelopes, oldest first: `Drain.fst` and `FixedRing.fst` prove that each kernel
/// queue IS its unread list over any sequence of pushes and pops, so the machine stands on that list. A
/// bounded host refuses an acceptance when the list holds `capacity` envelopes. Four observables are kept
/// beside the state's cells: `accepted` (every envelope accepted, in order), `updated` (every envelope
/// whose chain was computed, in order), `handed` (every effect handed to a performer, in order) and
/// `renders` (how many times the view was rendered). The differential host drives the EXTRACTION of this
/// module beside the shipped runtime over generated envelope sequences and requires the same model, the
/// same effects in the same order, the same refusals and the same render count.
///
/// # The abstraction function
///
/// Acceptance order is the list `accepted`; what reached `update` is the list `updated`; what is still
/// queued is `queue`. The running invariant is `accepted == append updated queue`: every accepted envelope
/// is either processed or still waiting, with nothing lost, duplicated or reordered. Termination truncates
/// the queue and keeps `updated` a prefix of `accepted`.
///
/// # The theorems — one per guarantee of the step machine's row of the design's ladder
///
///   * `exactly_once_in_order` — after a drain that finishes, every accepted envelope reached `update`
///     exactly once, in acceptance order: `accepted == updated` as lists. While a drain is in flight or
///     yielded, `accepted == append updated queue` (`order_inv`); after termination `updated` is a prefix.
///   * `step_whole_or_nothing` — a step installs the chain's final model and hands over every effect of
///     the chain, in order, when the decision allows; otherwise the model and the handed list are exactly
///     as they were. Nothing is ever half applied.
///   * `revoked_instance_refused` — an envelope from an instance whose token is revoked is refused at
///     acceptance and changes nothing; a committed step that stops a key revokes its instance's token, and
///     no later start re-issues it.
///   * `effects_handed_once_in_order` — the handed list only grows, and a committed step appends exactly
///     the chain's effects, once, in emission order (with `step_whole_or_nothing`); through any drain the
///     effects handed before are a prefix of those handed after.
///   * `one_render_per_drain` — processing steps renders nothing; a drain that is not terminated renders
///     exactly once at its end, whether it finished or yielded.
///   * `yield_drops_nothing` — a drain that stops at its fuel and resumes over the rest from the state it
///     left is the drain over the whole: the same final state and verdict. The state between IS the backlog.
///   * `terminated_absorbing` — after termination nothing is accepted, nothing is processed, nothing is
///     handed over, nothing is started and nothing is rendered; termination is idempotent and leaves no
///     live token, no running instance, no queued envelope and no parked step.
///
/// # The parameters, and what is assumed of each
///
/// The message, model and effect types are any `Type0`. `update` is total (a throw is a refusal in the
/// shipped runtime, rule 11, which the model does not state). `resources: md -> list want` gives the wanted
/// keys with their descriptions; the key order the shipped runtime applies is the list order here.
/// `decide: proposal e -> decision` is total and reads only the proposal. `fuel` (the chain bound) is
/// positive; the drain bound is any natural. The identity skip, `Resources` throwing, a start that
/// throws (held), the interpreter's `DrainEnded` hook and envelopes a performer accepts synchronously
/// inside a step are not modelled: they are the shipped runtime's and are tested. Nothing is admitted or
/// assumed.
module StepMachine

open StepChain

#set-options "--fuel 0 --ifuel 0"

(* ───────────────────────────────────────────────────────────────────
   Types.
   ─────────────────────────────────────────────────────────────────── *)

/// Where an envelope came from: outside, or an instance with a liveness token (a resource instance, an
/// in-flight reply).
type source =
  | External : source
  | FromInstance : token: nat -> source

/// F#: `Envelope<'msg>`, reduced to what the machine reads: the sequence number, the message, the source.
type envelope (m: Type0) = { seq: nat; msg: m; src: source }

/// F#: `Decision`.
type decision =
  | Allow : decision
  | Park : decision
  | Deny : decision

/// A wanted resource: a key and the description it is wanted under.
type want = { wkey: nat; wdesc: nat }

/// A running instance: its key, its description and its liveness token.
type inst = { ikey: nat; idesc: nat; tok: nat }

/// F#: `Proposal`: the effects, the starts and the stops the decision reads.
type proposal (e: Type0) = { effects: list e; starts: list want; stops: list nat }

/// F#: the runtime's cells and the differential's observables.
noeq type st (m: Type0) (md: Type0) (e: Type0) = {
  queue: list (envelope m);
  terminated: bool;
  model: md;
  next_seq: nat;
  next_tok: nat;
  live: list nat;
  running: list inst;
  parked: list (envelope m);
  accepted: list (envelope m);
  updated: list (envelope m);
  handed: list e;
  renders: nat;
  refused: list nat;
}

/// The initial state over a model.
let initial (#m #md #e: Type0) (model: md) : st m md e = {
  queue = [];
  terminated = false;
  model = model;
  next_seq = 1;
  next_tok = 1;
  live = [];
  running = [];
  parked = [];
  accepted = [];
  updated = [];
  handed = [];
  renders = 0;
  refused = [];
}

(* ───────────────────────────────────────────────────────────────────
   Lists over naturals and instances.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

let rec mem (x: nat) (xs: list nat) : Tot bool (decreases xs) =
  match xs with
  | [] -> false
  | y :: rest -> x = y || mem x rest

let rec remove (x: nat) (xs: list nat) : Tot (list nat) (decreases xs) =
  match xs with
  | [] -> []
  | y :: rest -> if x = y then remove x rest else y :: remove x rest

let rec remove_all (xs ys: list nat) : Tot (list nat) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> remove_all rest (remove x ys)

/// Every token below `n`.
let rec all_below (xs: list nat) (n: nat) : Tot bool (decreases xs) =
  match xs with
  | [] -> true
  | x :: rest -> x < n && all_below rest n

let rec has_want (key desc: nat) (ws: list want) : Tot bool (decreases ws) =
  match ws with
  | [] -> false
  | w :: rest -> (w.wkey = key && w.wdesc = desc) || has_want key desc rest

let rec has_inst (key desc: nat) (rs: list inst) : Tot bool (decreases rs) =
  match rs with
  | [] -> false
  | r :: rest -> (r.ikey = key && r.idesc = desc) || has_inst key desc rest

/// The tokens of the instances running under `key`.
let rec tokens_of_key (key: nat) (rs: list inst) : Tot (list nat) (decreases rs) =
  match rs with
  | [] -> []
  | r :: rest -> if r.ikey = key then r.tok :: tokens_of_key key rest else tokens_of_key key rest

let rec without_key (key: nat) (rs: list inst) : Tot (list inst) (decreases rs) =
  match rs with
  | [] -> []
  | r :: rest -> if r.ikey = key then without_key key rest else r :: without_key key rest

let rec inst_tokens (rs: list inst) : Tot (list nat) (decreases rs) =
  match rs with
  | [] -> []
  | r :: rest -> r.tok :: inst_tokens rest

(* ───────────────────────────────────────────────────────────────────
   The plan — `ResourceDiff.plan`, over lists: stop what is running and not wanted under the same
   description; start what is wanted and not running under the same description. A key both stopped and
   started is a restart.
   ─────────────────────────────────────────────────────────────────── *)

let rec stops_of (running: list inst) (wanted: list want) : Tot (list nat) (decreases running) =
  match running with
  | [] -> []
  | r :: rest -> if has_want r.ikey r.idesc wanted then stops_of rest wanted else r.ikey :: stops_of rest wanted

let rec starts_of (running: list inst) (wanted: list want) : Tot (list want) (decreases wanted) =
  match wanted with
  | [] -> []
  | w :: rest -> if has_inst w.wkey w.wdesc running then starts_of running rest else w :: starts_of running rest

(* ───────────────────────────────────────────────────────────────────
   The machine — `Runtime.fs`, clause for clause.
   ─────────────────────────────────────────────────────────────────── *)

/// Whether a source's token is live.
let source_live (live: list nat) (src: source) : bool =
  match src with
  | External -> true
  | FromInstance t -> mem t live

/// F#: `accept` (rule 3): not terminated, the source live, room in the queue. The envelope is given the
/// next sequence number and joins the back of the queue; the producer is told.
let accept (#m #md #e: Type0) (bounded: bool) (capacity: nat) (s: st m md e) (msg: m) (src: source)
  : pair (st m md e) bool =
  if s.terminated then Pair s false
  else if not (source_live s.live src) then Pair s false
  else if bounded && length s.queue >= capacity then Pair s false
  else
    let env = { seq = s.next_seq; msg = msg; src = src } in
    Pair ({ s with queue = append s.queue [env]; next_seq = s.next_seq + 1; accepted = append s.accepted [env] }) true

/// F#: `stop` (rule 18): the instance's token is revoked before it is disposed.
let stop_key (#m #md #e: Type0) (key: nat) (s: st m md e) : st m md e =
  { s with live = remove_all (tokens_of_key key s.running) s.live; running = without_key key s.running }

let rec stop_keys (#m #md #e: Type0) (keys: list nat) (s: st m md e) : Tot (st m md e) (decreases keys) =
  match keys with
  | [] -> s
  | k :: rest -> stop_keys rest (stop_key k s)

/// F#: `start` (rule 18): the instance is issued its token before it is called.
let start_want (#m #md #e: Type0) (w: want) (s: st m md e) : st m md e =
  { s with running = append s.running [{ ikey = w.wkey; idesc = w.wdesc; tok = s.next_tok }];
           live = s.next_tok :: s.live;
           next_tok = s.next_tok + 1 }

let rec start_wants (#m #md #e: Type0) (ws: list want) (s: st m md e) : Tot (st m md e) (decreases ws) =
  match ws with
  | [] -> s
  | w :: rest -> start_wants rest (start_want w s)

/// F#: `commit` (rule 8): install the model, carry out the plan — every stop, then every start — and hand
/// every effect over, once, in emission order.
let commit (#m #md #e: Type0) (s: st m md e) (model: md) (effs: list e) (stops: list nat) (starts: list want)
  : st m md e =
  let s1 = { s with model = model } in
  let s2 = start_wants starts (stop_keys stops s1) in
  { s2 with handed = append s2.handed effs }

/// F#: `processStep` and `decide` (rules 4 to 11): the chain, computed whole; the plan; the decision as
/// a unit; then commit, park or refuse. The envelope reaches `update` here and nowhere else.
let process (#m #md #e: Type0)
            (fuel: nat)
            (update: md -> m -> pair md (list (step m e)))
            (resources: md -> list want)
            (decide: proposal e -> decision)
            (s: st m md e) (env: envelope m) : st m md e =
  let s0 = { s with updated = append s.updated [env] } in
  match chain update fuel s.model env.msg with
  | TooLong -> { s0 with refused = append s0.refused [env.seq] }
  | Computed model' effs _ ->
      let wanted = resources model' in
      let stops = stops_of s.running wanted in
      let starts = starts_of s.running wanted in
      match decide { effects = effs; starts = starts; stops = stops } with
      | Allow -> commit s0 model' effs stops starts
      | Park -> { s0 with parked = append s0.parked [env] }
      | Deny -> { s0 with refused = append s0.refused [env.seq] }

/// F#: the drain's loop (rules 12 and 14): process queued envelopes until the queue is empty, or the fuel
/// is spent. The verdict says which: `true` finished, `false` yielded with a backlog.
let rec drain_steps (#m #md #e: Type0)
                    (fuel: nat)
                    (chain_fuel: nat)
                    (update: md -> m -> pair md (list (step m e)))
                    (resources: md -> list want)
                    (decide: proposal e -> decision)
                    (s: st m md e) : Tot (pair (st m md e) bool) (decreases fuel) =
  if s.terminated then Pair s true
  else
    match s.queue with
    | [] -> Pair s true
    | env :: rest ->
        if fuel = 0 then Pair s false
        else drain_steps (fuel - 1) chain_fuel update resources decide (process chain_fuel update resources decide { s with queue = rest } env)

/// F#: `drain` (rule 13): the loop, then one render — unless terminated, when nothing is rendered.
let drain (#m #md #e: Type0)
          (fuel: nat)
          (chain_fuel: nat)
          (update: md -> m -> pair md (list (step m e)))
          (resources: md -> list want)
          (decide: proposal e -> decision)
          (s: st m md e) : st m md e =
  let Pair s' _ = drain_steps fuel chain_fuel update resources decide s in
  if s'.terminated then s' else { s' with renders = s'.renders + 1 }

/// F#: `Dispatch`: accept, and if accepted, drain.
let dispatch (#m #md #e: Type0)
             (bounded: bool) (capacity: nat)
             (fuel: nat) (chain_fuel: nat)
             (update: md -> m -> pair md (list (step m e)))
             (resources: md -> list want)
             (decide: proposal e -> decision)
             (s: st m md e) (msg: m) (src: source) : pair (st m md e) bool =
  let Pair s1 ok = accept bounded capacity s msg src in
  if ok then Pair (drain fuel chain_fuel update resources decide s1) true else Pair s1 false

/// Every parked envelope's sequence number.
let rec seqs_of (#m: Type0) (envs: list (envelope m)) : Tot (list nat) (decreases envs) =
  match envs with
  | [] -> []
  | env :: rest -> env.seq :: seqs_of rest

/// F#: `teardown` (rule 24): the flag; every token revoked; every instance stopped; the queue discarded;
/// every parked step resolved as cancelled, which is a refusal.
let terminate (#m #md #e: Type0) (s: st m md e) : st m md e =
  if s.terminated then s
  else { s with terminated = true; live = []; running = []; queue = []; parked = [];
                refused = append s.refused (seqs_of s.parked) }

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The specification — ghost.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

[@@ noextract_to "FSharp"]
let rec take (#a: Type0) (n: nat) (xs: list a) : Tot (list a) (decreases xs) =
  if n = 0 then []
  else
    match xs with
    | [] -> []
    | x :: rest -> x :: take (n - 1) rest

/// `xs` is a prefix of `ys`.
[@@ noextract_to "FSharp"]
let is_prefix (#a: Type0) (xs ys: list a) : prop = take (length xs) ys == xs

/// The order invariant: while running, every accepted envelope is processed or still queued, in order;
/// once terminated, what was processed is a prefix of what was accepted.
[@@ noextract_to "FSharp"]
let order_inv (#m #md #e: Type0) (s: st m md e) : prop =
  if s.terminated then is_prefix s.updated s.accepted else s.accepted == append s.updated s.queue

/// The termination invariant: a terminated machine holds no live token, no running instance, no queued
/// envelope and no parked step.
[@@ noextract_to "FSharp"]
let term_inv (#m #md #e: Type0) (s: st m md e) : prop =
  s.terminated ==> (s.live == [] /\ s.running == [] /\ s.queue == [] /\ s.parked == [])

/// The token invariant: every live token and every instance's token is below the next token to issue.
[@@ noextract_to "FSharp"]
let tok_inv (#m #md #e: Type0) (s: st m md e) : prop =
  all_below s.live s.next_tok /\ all_below (inst_tokens s.running) s.next_tok

(* ───────────────────────────────────────────────────────────────────
   List lemmas.
   ─────────────────────────────────────────────────────────────────── *)

let rec length_append (#a: Type0) (xs ys: list a)
  : Lemma (ensures length (append xs ys) = length xs + length ys) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> length_append rest ys

let rec take_append (#a: Type0) (xs ys: list a)
  : Lemma (ensures take (length xs) (append xs ys) == xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> take_append rest ys

#push-options "--fuel 2"
/// A one-element list appended is a cons.
let append_one (#a: Type0) (x: a) (xs: list a) : Lemma (ensures append [x] xs == x :: xs) = ()
#pop-options

/// A list is a prefix of itself followed by anything.
let prefix_append (#a: Type0) (xs ys: list a) : Lemma (ensures is_prefix xs (append xs ys)) = take_append xs ys

let rec take_take_append (#a: Type0) (n: nat) (xs ys: list a)
  : Lemma (requires n <= length xs) (ensures take n (append xs ys) == take n xs) (decreases xs) =
  if n = 0 then ()
  else
    match xs with
    | [] -> ()
    | _ :: rest -> take_take_append (n - 1) rest ys

let rec length_take (#a: Type0) (n: nat) (xs: list a)
  : Lemma (requires n <= length xs) (ensures length (take n xs) = n) (decreases xs) =
  if n = 0 then ()
  else
    match xs with
    | [] -> ()
    | _ :: rest -> length_take (n - 1) rest

/// A prefix of a prefix is a prefix.
let prefix_trans (#a: Type0) (xs ys zs: list a)
  : Lemma (requires is_prefix xs ys /\ is_prefix ys zs) (ensures is_prefix xs zs) =
  let rec go (xs ys zs: list a)
    : Lemma (requires take (length xs) ys == xs /\ take (length ys) zs == ys)
            (ensures take (length xs) zs == xs) (decreases xs) =
    match xs, ys, zs with
    | [], _, _ -> ()
    | _ :: xr, _ :: yr, _ :: zr -> go xr yr zr
    | _ -> ()
  in
  go xs ys zs

let rec mem_remove (x y: nat) (xs: list nat)
  : Lemma (ensures mem x (remove y xs) = (mem x xs && x <> y)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> mem_remove x y rest

/// A token removed is gone, and nothing else is.
let rec mem_remove_all (x: nat) (xs ys: list nat)
  : Lemma (ensures mem x (remove_all xs ys) = (mem x ys && not (mem x xs))) (decreases xs) =
  match xs with
  | [] -> ()
  | y :: rest ->
      mem_remove x y ys;
      mem_remove_all x rest (remove y ys)

let rec all_below_mem (xs: list nat) (n x: nat)
  : Lemma (requires all_below xs n /\ mem x xs) (ensures x < n) (decreases xs) =
  match xs with
  | [] -> ()
  | y :: rest -> if x = y then () else all_below_mem rest n x

let rec all_below_remove (x: nat) (xs: list nat) (n: nat)
  : Lemma (requires all_below xs n) (ensures all_below (remove x xs) n) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> all_below_remove x rest n

let rec all_below_remove_all (xs ys: list nat) (n: nat)
  : Lemma (requires all_below ys n) (ensures all_below (remove_all xs ys) n) (decreases xs) =
  match xs with
  | [] -> ()
  | x :: rest ->
      all_below_remove x ys n;
      all_below_remove_all rest (remove x ys) n

let rec all_below_weaken (xs: list nat) (n k: nat)
  : Lemma (requires all_below xs n /\ n <= k) (ensures all_below xs k) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> all_below_weaken rest n k

let rec inst_tokens_without (key: nat) (rs: list inst) (n: nat)
  : Lemma (requires all_below (inst_tokens rs) n) (ensures all_below (inst_tokens (without_key key rs)) n)
          (decreases rs) =
  match rs with
  | [] -> ()
  | _ :: rest -> inst_tokens_without key rest n

let rec inst_tokens_append (rs qs: list inst)
  : Lemma (ensures inst_tokens (append rs qs) == append (inst_tokens rs) (inst_tokens qs)) (decreases rs) =
  match rs with
  | [] -> ()
  | _ :: rest -> inst_tokens_append rest qs

let rec all_below_append (xs ys: list nat) (n: nat)
  : Lemma (requires all_below xs n /\ all_below ys n) (ensures all_below (append xs ys) n) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> all_below_append rest ys n

let rec tokens_of_key_mem (key: nat) (rs: list inst) (t: nat)
  : Lemma (requires mem t (tokens_of_key key rs)) (ensures mem t (inst_tokens rs)) (decreases rs) =
  match rs with
  | [] -> ()
  | r :: rest -> if r.ikey = key && r.tok = t then () else tokens_of_key_mem key rest t

(* ───────────────────────────────────────────────────────────────────
   Acceptance.
   ─────────────────────────────────────────────────────────────────── *)

/// **`accept_spec`.** An accepted envelope joins the back of the queue and the end of the accepted list,
/// with the next sequence number; a refused one changes nothing. Nothing else moves.
let accept_spec (#m #md #e: Type0) (bounded: bool) (capacity: nat) (s: st m md e) (msg: m) (src: source)
  : Lemma (ensures (let Pair s' ok = accept bounded capacity s msg src in
                    (ok ==> (let env = { seq = s.next_seq; msg = msg; src = src } in
                             s'.queue == append s.queue [env] /\ s'.accepted == append s.accepted [env] /\
                             s'.next_seq = s.next_seq + 1)) /\
                    (not ok ==> s' == s) /\
                    s'.terminated = s.terminated /\ s'.model == s.model /\ s'.updated == s.updated /\
                    s'.handed == s.handed /\ s'.live == s.live /\ s'.running == s.running /\
                    s'.renders = s.renders /\ s'.next_tok = s.next_tok)) = ()

/// **`accept_preserves_order`.** Acceptance keeps the order invariant: the envelope goes to the back of
/// both lists.
let accept_preserves_order (#m #md #e: Type0) (bounded: bool) (capacity: nat) (s: st m md e) (msg: m) (src: source)
  : Lemma (requires order_inv s)
          (ensures (let Pair s' _ = accept bounded capacity s msg src in order_inv s')) =
  if s.terminated then ()
  else
    let Pair s' ok = accept bounded capacity s msg src in
    if ok then begin
      let env = { seq = s.next_seq; msg = msg; src = src } in
      append_assoc s.updated s.queue [env]
    end

/// **`revoked_not_accepted`.** An envelope from an instance whose token is not live is refused at
/// acceptance and changes nothing (rule 18).
let revoked_not_accepted (#m #md #e: Type0) (bounded: bool) (capacity: nat) (s: st m md e) (msg: m) (t: nat)
  : Lemma (requires not (mem t s.live))
          (ensures accept bounded capacity s msg (FromInstance t) == Pair s false) = ()

/// **`full_queue_refuses`.** On a bounded host an envelope offered to a full queue is refused and changes
/// nothing; on a growing host nothing is refused for room.
let full_queue_refuses (#m #md #e: Type0) (capacity: nat) (s: st m md e) (msg: m) (src: source)
  : Lemma (requires not s.terminated /\ source_live s.live src)
          (ensures (length s.queue >= capacity ==> accept true capacity s msg src == Pair s false) /\
                   (let Pair _ ok = accept false capacity s msg src in ok)) = ()

(* ───────────────────────────────────────────────────────────────────
   The plan's tokens.
   ─────────────────────────────────────────────────────────────────── *)

/// Stopping keys never issues a token, touches the model, the handed list or the lists of envelopes.
let rec stop_keys_spec (#m #md #e: Type0) (keys: list nat) (s: st m md e)
  : Lemma (ensures (let s' = stop_keys keys s in
                    s'.next_tok = s.next_tok /\ s'.model == s.model /\ s'.handed == s.handed /\
                    s'.queue == s.queue /\ s'.accepted == s.accepted /\ s'.updated == s.updated /\
                    s'.terminated = s.terminated /\ s'.renders = s.renders /\ s'.parked == s.parked /\
                    s'.refused == s.refused /\ s'.next_seq = s.next_seq /\
                    (forall (t: nat). mem t s'.live ==> mem t s.live)))
          (decreases keys) =
  match keys with
  | [] -> ()
  | k :: rest ->
      let s1 = stop_key k s in
      introduce forall (t: nat). mem t s1.live ==> mem t s.live
      with introduce _ ==> _ with mem_remove_all t (tokens_of_key k s.running) s.live;
      stop_keys_spec rest s1

/// Starting wants touches nothing but the running list, the live list and the next token, and every
/// token it issues is at least the next token it started from.
let rec start_wants_spec (#m #md #e: Type0) (ws: list want) (s: st m md e)
  : Lemma (ensures (let s' = start_wants ws s in
                    s'.next_tok >= s.next_tok /\ s'.model == s.model /\ s'.handed == s.handed /\
                    s'.queue == s.queue /\ s'.accepted == s.accepted /\ s'.updated == s.updated /\
                    s'.terminated = s.terminated /\ s'.renders = s.renders /\ s'.parked == s.parked /\
                    s'.refused == s.refused /\ s'.next_seq = s.next_seq /\
                    (forall (t: nat). mem t s'.live ==> mem t s.live \/ t >= s.next_tok)))
          (decreases ws) =
  match ws with
  | [] -> ()
  | w :: rest -> start_wants_spec rest (start_want w s)

/// **`stop_key_revokes`.** Every token of the key stopped is revoked.
let stop_key_revokes (#m #md #e: Type0) (key: nat) (s: st m md e) (t: nat)
  : Lemma (requires mem t (tokens_of_key key s.running))
          (ensures not (mem t (stop_key key s).live)) =
  mem_remove_all t (tokens_of_key key s.running) s.live

/// Stopping more keys keeps a revoked token revoked.
let rec stop_keys_keep_revoked (#m #md #e: Type0) (keys: list nat) (s: st m md e) (t: nat)
  : Lemma (requires not (mem t s.live)) (ensures not (mem t (stop_keys keys s).live)) (decreases keys) =
  match keys with
  | [] -> ()
  | k :: rest ->
      mem_remove_all t (tokens_of_key k s.running) s.live;
      stop_keys_keep_revoked rest (stop_key k s) t

/// Stopping another key leaves this key's tokens where they were.
let rec tokens_of_key_without (k key: nat) (rs: list inst) (t: nat)
  : Lemma (requires k <> key /\ mem t (tokens_of_key key rs))
          (ensures mem t (tokens_of_key key (without_key k rs))) (decreases rs) =
  match rs with
  | [] -> ()
  | r :: rest -> if r.ikey = key && r.tok = t then () else tokens_of_key_without k key rest t

/// A key in the plan's stops, stopped somewhere along `stop_keys`, has every token revoked afterwards.
let rec stop_keys_revoke (#m #md #e: Type0) (keys: list nat) (s: st m md e) (key t: nat)
  : Lemma (requires mem key keys /\ mem t (tokens_of_key key s.running))
          (ensures not (mem t (stop_keys keys s).live)) (decreases keys) =
  match keys with
  | [] -> ()
  | k :: rest ->
      let s1 = stop_key k s in
      if k = key then begin
        stop_key_revokes k s t;
        stop_keys_keep_revoked rest s1 t
      end
      else begin
        // Not this key: the token is still that key's, in the same running list minus `k`'s instances.
        tokens_of_key_without k key s.running t;
        stop_keys_revoke rest s1 key t
      end


/// **`tok_inv_stop_keys`.** Stops keep every token below the next.
let rec tok_inv_stop_keys (#m #md #e: Type0) (keys: list nat) (s: st m md e)
  : Lemma (requires tok_inv s) (ensures tok_inv (stop_keys keys s)) (decreases keys) =
  match keys with
  | [] -> ()
  | k :: rest ->
      all_below_remove_all (tokens_of_key k s.running) s.live s.next_tok;
      inst_tokens_without k s.running s.next_tok;
      tok_inv_stop_keys rest (stop_key k s)

/// **`tok_inv_start_wants`.** Starts issue tokens from the next upwards, so every token stays below the
/// next. Fuel 2: the one-instance lists unfold twice.
#push-options "--fuel 2"
let rec tok_inv_start_wants (#m #md #e: Type0) (ws: list want) (s: st m md e)
  : Lemma (requires tok_inv s) (ensures tok_inv (start_wants ws s)) (decreases ws) =
  match ws with
  | [] -> ()
  | w :: rest ->
      let s1 = start_want w s in
      all_below_weaken s.live s.next_tok (s.next_tok + 1);
      all_below_weaken (inst_tokens s.running) s.next_tok (s.next_tok + 1);
      inst_tokens_append s.running [{ ikey = w.wkey; idesc = w.wdesc; tok = s.next_tok }];
      all_below_append (inst_tokens s.running) [s.next_tok] (s.next_tok + 1);
      tok_inv_start_wants rest s1
#pop-options

/// **`commit_revokes`.** A committed step whose plan stops `key` revokes every token `key` ran under, and
/// no start of the same step re-issues it: the token was below the next token, and starts issue from
/// the next upwards.
let commit_revokes (#m #md #e: Type0) (s: st m md e) (model: md) (effs: list e) (stops: list nat) (starts: list want) (key t: nat)
  : Lemma (requires tok_inv s /\ mem key stops /\ mem t (tokens_of_key key s.running))
          (ensures not (mem t (commit s model effs stops starts).live)) =
  let s1 = { s with model = model } in
  let s2 = stop_keys stops s1 in
  stop_keys_revoke stops s1 key t;
  stop_keys_spec stops s1;
  start_wants_spec starts s2;
  tokens_of_key_mem key s.running t;
  all_below_mem (inst_tokens s.running) s.next_tok t

(* ───────────────────────────────────────────────────────────────────
   The step.
   ─────────────────────────────────────────────────────────────────── *)

/// **`process_updated`.** Every processed envelope reaches `update` exactly once: `process` appends it to
/// the updated list, whatever the step's outcome, and touches neither the queue nor the accepted list.
let process_updated (#m #md #e: Type0)
                    (fuel: nat) (update: md -> m -> pair md (list (step m e))) (resources: md -> list want)
                    (decide: proposal e -> decision) (s: st m md e) (env: envelope m)
  : Lemma (ensures (let s' = process fuel update resources decide s env in
                    s'.updated == append s.updated [env] /\ s'.queue == s.queue /\ s'.accepted == s.accepted /\
                    s'.terminated = s.terminated /\ s'.renders = s.renders /\ s'.next_seq = s.next_seq)) =
  match chain update fuel s.model env.msg with
  | TooLong -> ()
  | Computed model' effs _ ->
      let wanted = resources model' in
      let stops = stops_of s.running wanted in
      let starts = starts_of s.running wanted in
      let s0 = { s with updated = append s.updated [env] } in
      match decide { effects = effs; starts = starts; stops = stops } with
      | Allow ->
          stop_keys_spec stops ({ s0 with model = model' });
          start_wants_spec starts (stop_keys stops ({ s0 with model = model' }))
      | Park -> ()
      | Deny -> ()

/// **`step_whole_or_nothing`.** A step installs the chain's final model and hands over exactly the
/// chain's effects, in order, when the decision is allow; otherwise — a chain over its fuel, a parked
/// step, a denied step — the model and the handed list are exactly as they were.
let step_whole_or_nothing (#m #md #e: Type0)
                          (fuel: nat) (update: md -> m -> pair md (list (step m e))) (resources: md -> list want)
                          (decide: proposal e -> decision) (s: st m md e) (env: envelope m)
  : Lemma (ensures (let s' = process fuel update resources decide s env in
                    match chain update fuel s.model env.msg with
                    | TooLong -> s'.model == s.model /\ s'.handed == s.handed /\ s'.running == s.running
                    | Computed model' effs _ ->
                        let wanted = resources model' in
                        let p = { effects = effs; starts = starts_of s.running wanted; stops = stops_of s.running wanted } in
                        (decide p == Allow ==> s'.model == model' /\ s'.handed == append s.handed effs) /\
                        (decide p =!= Allow ==> s'.model == s.model /\ s'.handed == s.handed /\ s'.running == s.running))) =
  match chain update fuel s.model env.msg with
  | TooLong -> ()
  | Computed model' effs _ ->
      let wanted = resources model' in
      let stops = stops_of s.running wanted in
      let starts = starts_of s.running wanted in
      let s0 = { s with updated = append s.updated [env] } in
      match decide { effects = effs; starts = starts; stops = stops } with
      | Allow ->
          stop_keys_spec stops ({ s0 with model = model' });
          start_wants_spec starts (stop_keys stops ({ s0 with model = model' }))
      | Park -> ()
      | Deny -> ()

/// **`process_handed_prefix`.** Whatever a step does, what was handed before is a prefix of what is
/// handed after: effects are never retracted or reordered.
let process_handed_prefix (#m #md #e: Type0)
                          (fuel: nat) (update: md -> m -> pair md (list (step m e))) (resources: md -> list want)
                          (decide: proposal e -> decision) (s: st m md e) (env: envelope m)
  : Lemma (ensures is_prefix s.handed (process fuel update resources decide s env).handed) =
  step_whole_or_nothing fuel update resources decide s env;
  match chain update fuel s.model env.msg with
  | TooLong -> prefix_append s.handed []; append_nil s.handed
  | Computed model' effs _ ->
      let wanted = resources model' in
      let p = { effects = effs; starts = starts_of s.running wanted; stops = stops_of s.running wanted } in
      if decide p = Allow then prefix_append s.handed effs
      else begin prefix_append s.handed []; append_nil s.handed end

/// **`process_preserves_order`.** With the envelope in hand (popped from the queue's head), a step moves it
/// to the end of the updated list, so the order invariant holds again.
let process_preserves_order (#m #md #e: Type0)
                            (fuel: nat) (update: md -> m -> pair md (list (step m e))) (resources: md -> list want)
                            (decide: proposal e -> decision) (s: st m md e) (env: envelope m)
  : Lemma (requires not s.terminated /\ s.accepted == append s.updated (env :: s.queue))
          (ensures order_inv (process fuel update resources decide s env)) =
  process_updated fuel update resources decide s env;
  append_assoc s.updated [env] s.queue;
  append_one env s.queue

/// **`process_tok_inv`.** A step keeps every token below the next.
let process_tok_inv (#m #md #e: Type0)
                    (fuel: nat) (update: md -> m -> pair md (list (step m e))) (resources: md -> list want)
                    (decide: proposal e -> decision) (s: st m md e) (env: envelope m)
  : Lemma (requires tok_inv s) (ensures tok_inv (process fuel update resources decide s env)) =
  match chain update fuel s.model env.msg with
  | TooLong -> ()
  | Computed model' effs _ ->
      let wanted = resources model' in
      let stops = stops_of s.running wanted in
      let starts = starts_of s.running wanted in
      let s0 = { s with updated = append s.updated [env] } in
      match decide { effects = effs; starts = starts; stops = stops } with
      | Allow ->
          tok_inv_stop_keys stops ({ s0 with model = model' });
          tok_inv_start_wants starts (stop_keys stops ({ s0 with model = model' }))
      | Park -> ()
      | Deny -> ()

/// **`revoked_instance_refused`.** A committed step whose plan stops `key` leaves every token `key` ran
/// under revoked, and an envelope from that instance offered afterwards is refused and changes nothing.
let revoked_instance_refused (#m #md #e: Type0)
                             (bounded: bool) (capacity: nat)
                             (fuel: nat) (update: md -> m -> pair md (list (step m e))) (resources: md -> list want)
                             (decide: proposal e -> decision) (s: st m md e) (env: envelope m) (key t: nat) (late: m)
  : Lemma (requires tok_inv s /\ mem t (tokens_of_key key s.running) /\
                    (match chain update fuel s.model env.msg with
                     | TooLong -> False
                     | Computed model' effs _ ->
                         let wanted = resources model' in
                         mem key (stops_of s.running wanted) /\
                         decide { effects = effs; starts = starts_of s.running wanted; stops = stops_of s.running wanted } == Allow))
          (ensures (let s' = process fuel update resources decide s env in
                    not (mem t s'.live) /\ accept bounded capacity s' late (FromInstance t) == Pair s' false)) =
  match chain update fuel s.model env.msg with
  | TooLong -> ()
  | Computed model' effs _ ->
      let wanted = resources model' in
      let stops = stops_of s.running wanted in
      let starts = starts_of s.running wanted in
      let s0 = { s with updated = append s.updated [env] } in
      commit_revokes s0 model' effs stops starts key t;
      let s' = process fuel update resources decide s env in
      revoked_not_accepted bounded capacity s' late t

(* ───────────────────────────────────────────────────────────────────
   The drain.
   ─────────────────────────────────────────────────────────────────── *)

/// **`drain_steps_preserves_order`.** The loop keeps the order invariant at every step.
let rec drain_steps_preserves_order (#m #md #e: Type0)
                                    (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                                    (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (requires order_inv s)
          (ensures (let Pair s' _ = drain_steps fuel chain_fuel update resources decide s in order_inv s'))
          (decreases fuel) =
  if s.terminated then ()
  else
    match s.queue with
    | [] -> ()
    | env :: rest ->
        if fuel = 0 then ()
        else begin
          let popped = { s with queue = rest } in
          process_preserves_order chain_fuel update resources decide popped env;
          drain_steps_preserves_order (fuel - 1) chain_fuel update resources decide (process chain_fuel update resources decide popped env)
        end

/// **`drain_steps_finished`.** A finished loop that is not terminated left the queue empty.
let rec drain_steps_finished (#m #md #e: Type0)
                             (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                             (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (ensures (let Pair s' finished = drain_steps fuel chain_fuel update resources decide s in
                    (finished /\ not s'.terminated) ==> s'.queue == []))
          (decreases fuel) =
  if s.terminated then ()
  else
    match s.queue with
    | [] -> ()
    | env :: rest ->
        if fuel = 0 then ()
        else begin
          let popped = { s with queue = rest } in
          process_updated chain_fuel update resources decide popped env;
          drain_steps_finished (fuel - 1) chain_fuel update resources decide (process chain_fuel update resources decide popped env)
        end

/// **`exactly_once_in_order`** — the headline. Over any state satisfying the order invariant, a drain's loop
/// that finishes without terminating leaves every accepted envelope processed exactly once, in acceptance
/// order: the accepted list and the updated list are the same list.
let exactly_once_in_order (#m #md #e: Type0)
                          (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                          (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (requires order_inv s)
          (ensures (let Pair s' finished = drain_steps fuel chain_fuel update resources decide s in
                    (finished /\ not s'.terminated) ==> s'.accepted == s'.updated)) =
  drain_steps_preserves_order fuel chain_fuel update resources decide s;
  drain_steps_finished fuel chain_fuel update resources decide s;
  let Pair s' finished = drain_steps fuel chain_fuel update resources decide s in
  if finished && not s'.terminated then append_nil s'.updated

/// **`in_order_always`.** In every state the machine reaches from one satisfying the invariant, what has
/// reached `update` is a prefix of what was accepted: termination can truncate, never permute.
let in_order_always (#m #md #e: Type0) (s: st m md e)
  : Lemma (requires order_inv s) (ensures is_prefix s.updated s.accepted) =
  if s.terminated then () else prefix_append s.updated s.queue

/// **`drain_steps_renders`.** The loop renders nothing: `renders` is unchanged through every step.
let rec drain_steps_renders (#m #md #e: Type0)
                            (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                            (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (ensures (let Pair s' _ = drain_steps fuel chain_fuel update resources decide s in s'.renders = s.renders))
          (decreases fuel) =
  if s.terminated then ()
  else
    match s.queue with
    | [] -> ()
    | env :: rest ->
        if fuel = 0 then ()
        else begin
          let popped = { s with queue = rest } in
          process_updated chain_fuel update resources decide popped env;
          drain_steps_renders (fuel - 1) chain_fuel update resources decide (process chain_fuel update resources decide popped env)
        end

/// **`one_render_per_drain`.** A drain that does not end terminated renders exactly once, at its end,
/// whether it finished or yielded; a drain that ends terminated renders nothing.
let one_render_per_drain (#m #md #e: Type0)
                         (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                         (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (ensures (let s' = drain fuel chain_fuel update resources decide s in
                    (not s'.terminated ==> s'.renders = s.renders + 1) /\
                    (s'.terminated ==> s'.renders = s.renders))) =
  drain_steps_renders fuel chain_fuel update resources decide s

/// **`drain_steps_handed_prefix`.** Through any loop the effects handed before are a prefix of those handed
/// after.
let rec drain_steps_handed_prefix (#m #md #e: Type0)
                                  (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                                  (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (ensures (let Pair s' _ = drain_steps fuel chain_fuel update resources decide s in is_prefix s.handed s'.handed))
          (decreases fuel) =
  if s.terminated then begin prefix_append s.handed []; append_nil s.handed end
  else
    match s.queue with
    | [] -> prefix_append s.handed []; append_nil s.handed
    | env :: rest ->
        if fuel = 0 then begin prefix_append s.handed []; append_nil s.handed end
        else begin
          let popped = { s with queue = rest } in
          let s1 = process chain_fuel update resources decide popped env in
          process_handed_prefix chain_fuel update resources decide popped env;
          drain_steps_handed_prefix (fuel - 1) chain_fuel update resources decide s1;
          let Pair s' _ = drain_steps (fuel - 1) chain_fuel update resources decide s1 in
          prefix_trans s.handed s1.handed s'.handed
        end

/// **`effects_handed_once_in_order`.** Over a whole drain, every effect handed before is still there, in
/// place; what a committed step adds is exactly its chain's effects, once, in emission order
/// (`step_whole_or_nothing`).
let effects_handed_once_in_order (#m #md #e: Type0)
                                 (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                                 (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (ensures is_prefix s.handed (drain fuel chain_fuel update resources decide s).handed) =
  drain_steps_handed_prefix fuel chain_fuel update resources decide s

/// **`yield_drops_nothing`.** The loop over `fuel1 + fuel2` steps is the loop over `fuel1` and, if that
/// yielded, the loop over `fuel2` from the state it left: the same final state and the same verdict. The
/// state between the two IS the backlog the yield carries, and `order_inv` says what it holds.
let rec yield_drops_nothing (#m #md #e: Type0)
                            (fuel1 fuel2 chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                            (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (ensures (let Pair s1 finished = drain_steps fuel1 chain_fuel update resources decide s in
                    drain_steps (fuel1 + fuel2) chain_fuel update resources decide s ==
                    (if finished then Pair s1 true else drain_steps fuel2 chain_fuel update resources decide s1)))
          (decreases fuel1) =
  if s.terminated then ()
  else
    match s.queue with
    | [] -> ()
    | env :: rest ->
        if fuel1 = 0 then ()
        else yield_drops_nothing (fuel1 - 1) fuel2 chain_fuel update resources decide (process chain_fuel update resources decide { s with queue = rest } env)

(* ───────────────────────────────────────────────────────────────────
   Termination.
   ─────────────────────────────────────────────────────────────────── *)

/// **`terminate_spec`.** Termination of a running machine sets the flag, revokes every token, stops every
/// instance, discards the queue, resolves every parked step as refused, and leaves the model, the handed
/// list, the accepted and updated lists and the render count exactly as they were.
let terminate_spec (#m #md #e: Type0) (s: st m md e)
  : Lemma (requires not s.terminated)
          (ensures (let s' = terminate s in
                    s'.terminated /\ s'.live == [] /\ s'.running == [] /\ s'.queue == [] /\ s'.parked == [] /\
                    s'.model == s.model /\ s'.handed == s.handed /\ s'.accepted == s.accepted /\
                    s'.updated == s.updated /\ s'.renders = s.renders /\ term_inv s')) = ()

/// **`terminate_term_inv`.** Termination establishes the termination invariant, and keeps it.
let terminate_term_inv (#m #md #e: Type0) (s: st m md e)
  : Lemma (requires term_inv s) (ensures term_inv (terminate s)) = ()

/// **`terminate_preserves_order`.** Termination keeps what reached `update` a prefix of what was accepted.
let terminate_preserves_order (#m #md #e: Type0) (s: st m md e)
  : Lemma (requires order_inv s) (ensures order_inv (terminate s)) =
  if s.terminated then () else prefix_append s.updated s.queue

/// **`terminated_absorbing`.** After termination nothing is accepted (every offer is refused and changes
/// nothing), the drain processes nothing and renders nothing, nothing is handed over or started, and
/// terminating again changes nothing.
let terminated_absorbing (#m #md #e: Type0)
                         (bounded: bool) (capacity: nat)
                         (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                         (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e) (msg: m) (src: source)
  : Lemma (requires s.terminated)
          (ensures accept bounded capacity s msg src == Pair s false /\
                   drain_steps fuel chain_fuel update resources decide s == Pair s true /\
                   drain fuel chain_fuel update resources decide s == s /\
                   dispatch bounded capacity fuel chain_fuel update resources decide s msg src == Pair s false /\
                   terminate s == s) = ()

/// **`terminate_then_nothing`.** From any state, termination then any offer, drain or dispatch leaves the
/// terminated state exactly as termination left it: no token live, no instance running, nothing more
/// handed over, nothing rendered.
let terminate_then_nothing (#m #md #e: Type0)
                           (bounded: bool) (capacity: nat)
                           (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                           (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e) (msg: m) (src: source)
  : Lemma (requires term_inv s)
          (ensures (let s' = terminate s in
                    s'.terminated /\ s'.live == [] /\ s'.running == [] /\ s'.queue == [] /\ s'.parked == [] /\
                    accept bounded capacity s' msg src == Pair s' false /\
                    drain fuel chain_fuel update resources decide s' == s' /\
                    dispatch bounded capacity fuel chain_fuel update resources decide s' msg src == Pair s' false)) =
  if s.terminated then () else terminate_spec s;
  terminated_absorbing bounded capacity fuel chain_fuel update resources decide (terminate s) msg src

(* ───────────────────────────────────────────────────────────────────
   The whole machine: the invariants hold from the initial state through every operation.
   ─────────────────────────────────────────────────────────────────── *)

/// **`initial_inv`.** The initial state satisfies the three invariants.
let initial_inv (#m #md #e: Type0) (model: md)
  : Lemma (ensures order_inv (initial #m #md #e model) /\ tok_inv (initial #m #md #e model) /\
                   term_inv (initial #m #md #e model)) = ()

/// **`drain_steps_term_inv`.** The loop keeps the termination invariant: a terminated machine is left as
/// it is, and a step never terminates.
let rec drain_steps_term_inv (#m #md #e: Type0)
                             (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                             (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (requires term_inv s)
          (ensures (let Pair s' _ = drain_steps fuel chain_fuel update resources decide s in term_inv s'))
          (decreases fuel) =
  if s.terminated then ()
  else
    match s.queue with
    | [] -> ()
    | env :: rest ->
        if fuel = 0 then ()
        else begin
          let popped = { s with queue = rest } in
          process_updated chain_fuel update resources decide popped env;
          drain_steps_term_inv (fuel - 1) chain_fuel update resources decide (process chain_fuel update resources decide popped env)
        end

/// **`dispatch_term_inv`.** A dispatch keeps the termination invariant.
let dispatch_term_inv (#m #md #e: Type0)
                      (bounded: bool) (capacity: nat)
                      (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                      (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e) (msg: m) (src: source)
  : Lemma (requires term_inv s)
          (ensures (let Pair s' _ = dispatch bounded capacity fuel chain_fuel update resources decide s msg src in term_inv s')) =
  let Pair s1 ok = accept bounded capacity s msg src in
  if ok then drain_steps_term_inv fuel chain_fuel update resources decide s1

/// **`drain_preserves_order`.** A drain keeps the order invariant.
let drain_preserves_order (#m #md #e: Type0)
                          (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                          (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (requires order_inv s) (ensures order_inv (drain fuel chain_fuel update resources decide s)) =
  drain_steps_preserves_order fuel chain_fuel update resources decide s

/// **`dispatch_preserves_order`.** A dispatch — accept, then drain — keeps the order invariant.
let dispatch_preserves_order (#m #md #e: Type0)
                             (bounded: bool) (capacity: nat)
                             (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                             (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e) (msg: m) (src: source)
  : Lemma (requires order_inv s)
          (ensures (let Pair s' _ = dispatch bounded capacity fuel chain_fuel update resources decide s msg src in order_inv s')) =
  accept_preserves_order bounded capacity s msg src;
  let Pair s1 ok = accept bounded capacity s msg src in
  if ok then drain_preserves_order fuel chain_fuel update resources decide s1

/// **`drain_steps_tok_inv`.** The loop keeps every token below the next.
let rec drain_steps_tok_inv (#m #md #e: Type0)
                            (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                            (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e)
  : Lemma (requires tok_inv s)
          (ensures (let Pair s' _ = drain_steps fuel chain_fuel update resources decide s in tok_inv s'))
          (decreases fuel) =
  if s.terminated then ()
  else
    match s.queue with
    | [] -> ()
    | env :: rest ->
        if fuel = 0 then ()
        else begin
          let popped = { s with queue = rest } in
          process_tok_inv chain_fuel update resources decide popped env;
          drain_steps_tok_inv (fuel - 1) chain_fuel update resources decide (process chain_fuel update resources decide popped env)
        end

/// **`dispatch_tok_inv`.** A dispatch keeps every token below the next, so a revoked token is never
/// re-issued by any later start.
let dispatch_tok_inv (#m #md #e: Type0)
                     (bounded: bool) (capacity: nat)
                     (fuel chain_fuel: nat) (update: md -> m -> pair md (list (step m e)))
                     (resources: md -> list want) (decide: proposal e -> decision) (s: st m md e) (msg: m) (src: source)
  : Lemma (requires tok_inv s)
          (ensures (let Pair s' _ = dispatch bounded capacity fuel chain_fuel update resources decide s msg src in tok_inv s')) =
  let Pair s1 ok = accept bounded capacity s msg src in
  if ok then drain_steps_tok_inv fuel chain_fuel update resources decide s1

#pop-options
