(*
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

/// Phase 646 — the keyed resource diff, modelled in F* and proved to plan and apply exactly what its
/// documentation says.
///
/// # What this module is
///
/// A model of `ResourceDiff.plan` and `ResourceDiff.apply` (`src/Fuaran.MVU.Kernel/Diff.fs`). The
/// runtime holds what is RUNNING (each key with the description it was started with), what is HELD
/// (each key whose start threw, with the description that threw), and a step ends with what the model
/// WANTS (each key with its description). The plan is: STOP a running key that is no longer wanted or
/// whose description changed; KEEP a running key whose description is unchanged; START a wanted key that
/// is not running under that description, unless it is held under it. A key stopped and started is a
/// RESTART. The plan is applied as every stop, then every start, each in key order.
///
/// # What is taken, and what is new
///
/// The shape descends from the subscription diff of Fable.Elmish and of the ToolUp Analytics open-source
/// SDK (toolup-forge, Apache-2.0; its model is `ElmishSub.fst`): a key-set comparison that stops what
/// vanished, keeps what stayed and starts what appeared, with the key and the start function opaque.
/// NEW here, and why that model's `never_both` lemma is replaced rather than kept: a DESCRIPTION per key,
/// so a changed description is a restart — a key both stopped and started — which `never_both` forbade;
/// the HELD set, so a start that threw is not retried under the same description; a RESULT, what is
/// running and held afterwards, so the plan-applied-twice and the identity-skip theorems can be stated;
/// and the two maps being SORTED, so every list the plan emits is in key order and every stop precedes
/// every start on every host.
///
/// # The representation and the abstraction
///
/// A map is a list of entries with distinct keys, in increasing key order under the host's comparison
/// (`sorted`); F#'s `Map` enumerates exactly so. The plan's three lists are filters of the two maps —
/// `stops` and `keeps` of what is running, `starts` of what is wanted — so each is in key order by
/// construction. The result of applying the plan is stated directly: what is running afterwards is the
/// wanted entries that were kept or whose start succeeded; what is held is the wanted entries held
/// before under that description, or whose start failed now. A start's outcome is a parameter `start`,
/// any function from a key and a description to a verdict.
///
/// # The theorems
///
///   * `start_spec`, `stop_spec`, `keep_spec`, `restart_spec` — each list holds exactly the entries the
///     documentation says, as an if-and-only-if over every key and description.
///   * `keys_after_are_wanted` — afterwards, a key is running or held exactly when it is wanted, and
///     never both; when nothing is held and every start succeeds, what runs IS what is wanted.
///   * `held_unchanged_not_started` — a key held under a description that is still wanted is not started.
///   * `plan_twice_empty` — the plan computed again, after applying it, against the same wanted map, is
///     empty: nothing to stop, nothing to start, and every running key kept.
///   * `sequence_shape` — the sequence a host performs is every stop then every start, and each half is in
///     key order.
///   * `identity_skip_agrees` — applying the plan again against the same wanted map changes nothing, so a
///     host that skips the diff when the model is the same instance agrees with the general path.
///
/// # The parameters, and what is assumed of each
///
/// The key type `k` and the description type `d` are any types with decidable equality; the key order
/// `lt` is any function `k -> k -> bool` of which `sequence_shape` assumes nothing and `sorted_distinct`
/// assumes irreflexivity (the host's `compare` is a strict total order, which is more). `start` is any
/// function `k -> d -> bool`. The maps are assumed to have distinct keys where a theorem says so, and
/// the held keys and the running keys are assumed disjoint (`separate`), which `keys_after_are_wanted`
/// shows is preserved. Nothing is admitted or assumed.
module KeyedDiff

#set-options "--fuel 0 --ifuel 0"

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns, so the extraction references
   `Prims` and nothing else.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a option`.
type opt (a: Type0) =
  | ONone : opt a
  | OSome : item: a -> opt a

/// F#: `'a * 'b`.
type pair (a: Type0) (b: Type0) =
  | Pair : first: a -> second: b -> pair a b

/// A map entry: a key and its description. F#: one `KeyValue` of a `Map<'k, 'd>` (the running map's
/// handle is the host's and is not modelled).
type entry (k: Type0) (d: Type0) =
  | Entry : key: k -> description: d -> entry k d

/// F#: `ResourceDiff.Plan` — the keys to stop, the keys to keep, the entries to start, each in key order.
type plan (k: Type0) (d: Type0) =
  | Plan : stops: list k -> keeps: list k -> starts: list (entry k d) -> plan k d

/// One act of the sequence a host performs.
type action (k: Type0) (d: Type0) =
  | Stop : key: k -> action k d
  | Start : key: k -> description: d -> action k d

(* ───────────────────────────────────────────────────────────────────
   Lists as maps.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

/// F#: `List.contains`.
let rec mem (#k: eqtype) (key: k) (keys: list k) : Tot bool (decreases keys) =
  match keys with
  | [] -> false
  | x :: rest -> x = key || mem key rest

/// F#: `Map.tryFind`.
let rec lookup (#k: eqtype) (#d: Type0) (key: k) (xs: list (entry k d)) : Tot (opt d) (decreases xs) =
  match xs with
  | [] -> ONone
  | Entry x v :: rest -> if x = key then OSome v else lookup key rest

/// F#: `Map.toList >> List.map fst`.
let rec keys_of (#k: Type0) (#d: Type0) (xs: list (entry k d)) : Tot (list k) (decreases xs) =
  match xs with
  | [] -> []
  | Entry key _ :: rest -> key :: keys_of rest

/// F#: `List.append`.
let rec append (#a: Type0) (xs ys: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> x :: append rest ys

/// The entries satisfying `p`, in order — the one shape every list of the plan takes.
let rec filter (#k: Type0) (#d: Type0) (p: entry k d -> bool) (xs: list (entry k d))
  : Tot (list (entry k d)) (decreases xs) =
  match xs with
  | [] -> []
  | e :: rest -> if p e then e :: filter p rest else filter p rest

/// Whether `key` is held under exactly `description`.
let held_at (#k: eqtype) (#d: eqtype) (held: list (entry k d)) (key: k) (description: d) : bool =
  lookup key held = OSome description

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The plan — `Diff.fs`, in its three lists.
   ─────────────────────────────────────────────────────────────────── *)

/// A running entry to stop: its key is no longer wanted, or is wanted under another description.
let stop_p (#k: eqtype) (#d: eqtype) (wanted: list (entry k d)) (e: entry k d) : bool =
  lookup (Entry?.key e) wanted <> OSome (Entry?.description e)

/// A running entry to keep: wanted under the same description.
let keep_p (#k: eqtype) (#d: eqtype) (wanted: list (entry k d)) (e: entry k d) : bool =
  lookup (Entry?.key e) wanted = OSome (Entry?.description e)

/// A wanted entry to start: not running under that description, and not held under it.
let start_p (#k: eqtype) (#d: eqtype) (running held: list (entry k d)) (e: entry k d) : bool =
  not (lookup (Entry?.key e) running = OSome (Entry?.description e) || held_at held (Entry?.key e) (Entry?.description e))

/// F#: `ResourceDiff.plan`.
let plan_of (#k: eqtype) (#d: eqtype) (running held wanted: list (entry k d)) : plan k d =
  Plan (keys_of (filter (stop_p wanted) running)) (keys_of (filter (keep_p wanted) running)) (filter (start_p running held) wanted)

#push-options "--fuel 1 --ifuel 1"

let rec stop_actions (#k: Type0) (#d: Type0) (keys: list k) : Tot (list (action k d)) (decreases keys) =
  match keys with
  | [] -> []
  | key :: rest -> Stop key :: stop_actions rest

let rec start_actions (#k: Type0) (#d: Type0) (xs: list (entry k d)) : Tot (list (action k d)) (decreases xs) =
  match xs with
  | [] -> []
  | Entry key description :: rest -> Start key description :: start_actions rest

#pop-options

/// F#: `ResourceDiff.apply`'s order of effects — every stop, then every start, each in the plan's order.
let sequence (#k: Type0) (#d: Type0) (p: plan k d) : list (action k d) =
  append (stop_actions (Plan?.stops p)) (start_actions (Plan?.starts p))

(* ───────────────────────────────────────────────────────────────────
   The plan applied — what runs and what is held afterwards.
   ─────────────────────────────────────────────────────────────────── *)

/// A wanted entry runs afterwards: it was kept, or it was started and the start succeeded.
let runs_after (#k: eqtype) (#d: eqtype) (start: k -> d -> bool) (running held: list (entry k d)) (e: entry k d) : bool =
  lookup (Entry?.key e) running = OSome (Entry?.description e) ||
  (not (held_at held (Entry?.key e) (Entry?.description e)) && start (Entry?.key e) (Entry?.description e))

/// A wanted entry is held afterwards: it was held under that description, or it was started and the
/// start failed.
let held_after (#k: eqtype) (#d: eqtype) (start: k -> d -> bool) (running held: list (entry k d)) (e: entry k d) : bool =
  held_at held (Entry?.key e) (Entry?.description e) ||
  (lookup (Entry?.key e) running <> OSome (Entry?.description e) && not (start (Entry?.key e) (Entry?.description e)))

/// F#: `ResourceDiff.apply` — (running, held) afterwards. `start key description` is the host's start,
/// `true` when the instance came up and `false` when it threw.
let apply (#k: eqtype) (#d: eqtype) (start: k -> d -> bool) (running held wanted: list (entry k d))
  : pair (list (entry k d)) (list (entry k d)) =
  Pair (filter (runs_after start running held) wanted) (filter (held_after start running held) wanted)

(* ───────────────────────────────────────────────────────────────────
   Specification vocabulary — ghost.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

/// Whether an entry is in a list.
[@@ noextract_to "FSharp"]
let rec mem_entry (#k: eqtype) (#d: eqtype) (e: entry k d) (xs: list (entry k d)) : Tot bool (decreases xs) =
  match xs with
  | [] -> false
  | x :: rest -> x = e || mem_entry e rest

/// Duplicate-free keys — what makes a list a map.
[@@ noextract_to "FSharp"]
let rec distinct (#k: eqtype) (keys: list k) : Tot bool (decreases keys) =
  match keys with
  | [] -> true
  | x :: rest -> not (mem x rest) && distinct rest

/// In key order: every key is below every later key. Pairwise, so no law of `lt` is needed to carry it
/// through a filter.
[@@ noextract_to "FSharp"]
let rec sorted (#k: eqtype) (lt: k -> k -> bool) (keys: list k) : Tot prop (decreases keys) =
  match keys with
  | [] -> True
  | x :: rest -> (forall (y: k). mem y rest ==> lt x y) /\ sorted lt rest

/// No key is both held and running.
[@@ noextract_to "FSharp"]
let separate (#k: eqtype) (#d: eqtype) (held running: list (entry k d)) : prop =
  forall (key: k). not (mem key (keys_of held) && mem key (keys_of running))

(* ───────────────────────────────────────────────────────────────────
   Lemmas about maps and filters.
   ─────────────────────────────────────────────────────────────────── *)

/// A key is among the keys exactly when some entry carries it.
let rec mem_keys_of (#k: eqtype) (#d: eqtype) (xs: list (entry k d))
  : Lemma (ensures (forall (key: k). mem key (keys_of xs) <==> (exists (v: d). mem_entry (Entry key v) xs))) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> mem_keys_of rest

/// A lookup that answers names an entry of the list.
let rec lookup_mem (#k: eqtype) (#d: eqtype) (key: k) (xs: list (entry k d))
  : Lemma (ensures (forall (v: d). lookup key xs = OSome v ==> mem_entry (Entry key v) xs) /\
                   (ONone? (lookup key xs) ==> not (mem key (keys_of xs))))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> lookup_mem key rest

/// On a map, an entry is in the list exactly when the lookup of its key answers its description.
let rec lookup_spec (#k: eqtype) (#d: eqtype) (xs: list (entry k d))
  : Lemma (requires distinct (keys_of xs))
          (ensures (forall (key: k) (v: d). mem_entry (Entry key v) xs <==> lookup key xs = OSome v))
          (decreases xs) =
  match xs with
  | [] -> ()
  | Entry x _ :: rest ->
      lookup_spec rest;
      mem_keys_of rest

/// What a filter holds: the entries of its input that satisfy the predicate.
let rec filter_mem (#k: eqtype) (#d: eqtype) (p: entry k d -> bool) (xs: list (entry k d))
  : Lemma (ensures (forall (e: entry k d). mem_entry e (filter p xs) <==> (mem_entry e xs /\ p e))) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> filter_mem p rest

/// A filter's keys are among its input's.
let rec filter_keys (#k: eqtype) (#d: eqtype) (p: entry k d -> bool) (xs: list (entry k d))
  : Lemma (ensures (forall (key: k). mem key (keys_of (filter p xs)) ==> mem key (keys_of xs))) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> filter_keys p rest

/// A filter of a map is a map.
let rec filter_distinct (#k: eqtype) (#d: eqtype) (p: entry k d -> bool) (xs: list (entry k d))
  : Lemma (requires distinct (keys_of xs)) (ensures distinct (keys_of (filter p xs))) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest ->
      filter_distinct p rest;
      filter_keys p rest

/// A filter of a sorted map is sorted.
let rec filter_sorted (#k: eqtype) (#d: eqtype) (lt: k -> k -> bool) (p: entry k d -> bool) (xs: list (entry k d))
  : Lemma (requires sorted lt (keys_of xs)) (ensures sorted lt (keys_of (filter p xs))) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest ->
      filter_sorted lt p rest;
      filter_keys p rest

/// A filter whose predicate is always true is the identity.
let rec filter_all (#k: eqtype) (#d: eqtype) (p: entry k d -> bool) (xs: list (entry k d))
  : Lemma (requires (forall (e: entry k d). mem_entry e xs ==> p e)) (ensures filter p xs == xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> filter_all p rest

/// Two predicates that agree on the input filter it alike.
let rec filter_ext (#k: eqtype) (#d: eqtype) (p q: entry k d -> bool) (xs: list (entry k d))
  : Lemma (requires (forall (e: entry k d). mem_entry e xs ==> p e = q e)) (ensures filter p xs == filter q xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> filter_ext p q rest

/// A filter that keeps nothing is empty.
let rec filter_none (#k: eqtype) (#d: eqtype) (p: entry k d -> bool) (xs: list (entry k d))
  : Lemma (requires (forall (e: entry k d). mem_entry e xs ==> not (p e))) (ensures filter p xs == []) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> filter_none p rest

/// Under an irreflexive order, a sorted key list is duplicate-free.
let rec sorted_distinct (#k: eqtype) (lt: k -> k -> bool) (keys: list k)
  : Lemma (requires sorted lt keys /\ (forall (x: k). not (lt x x))) (ensures distinct keys) (decreases keys) =
  match keys with
  | [] -> ()
  | _ :: rest -> sorted_distinct lt rest

/// `keys_of` of the plan's filters, read back through `mem`.
let rec mem_keys_filter (#k: eqtype) (#d: eqtype) (p: entry k d -> bool) (xs: list (entry k d))
  : Lemma (ensures (forall (key: k). mem key (keys_of (filter p xs)) <==> (exists (v: d). mem_entry (Entry key v) xs /\ p (Entry key v))))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> mem_keys_filter p rest

(* ───────────────────────────────────────────────────────────────────
   The theorems — the plan.
   ─────────────────────────────────────────────────────────────────── *)

/// **`start_spec`.** `starts` holds exactly the wanted entries whose key is not running under that
/// description and not held under it.
let start_spec (#k: eqtype) (#d: eqtype) (running held wanted: list (entry k d))
  : Lemma (ensures (forall (key: k) (v: d).
                      mem_entry (Entry key v) (Plan?.starts (plan_of running held wanted)) <==>
                      (mem_entry (Entry key v) wanted /\ lookup key running <> OSome v /\ not (held_at held key v)))) =
  filter_mem (start_p running held) wanted

/// **`stop_spec`.** `stops` holds exactly the keys of running entries whose key is no longer wanted, or is
/// wanted under another description.
let stop_spec (#k: eqtype) (#d: eqtype) (running held wanted: list (entry k d))
  : Lemma (ensures (forall (key: k).
                      mem key (Plan?.stops (plan_of running held wanted)) <==>
                      (exists (v: d). mem_entry (Entry key v) running /\ lookup key wanted <> OSome v))) =
  mem_keys_filter (stop_p wanted) running

/// **`keep_spec`.** `keeps` holds exactly the keys of running entries wanted under the same description.
let keep_spec (#k: eqtype) (#d: eqtype) (running held wanted: list (entry k d))
  : Lemma (ensures (forall (key: k).
                      mem key (Plan?.keeps (plan_of running held wanted)) <==>
                      (exists (v: d). mem_entry (Entry key v) running /\ lookup key wanted = OSome v))) =
  mem_keys_filter (keep_p wanted) running

/// **`restart_spec`.** On maps, a key is both stopped and started exactly when it is running under one
/// description and wanted under another it is not held under.
let restart_spec (#k: eqtype) (#d: eqtype) (running held wanted: list (entry k d))
  : Lemma (requires distinct (keys_of running) /\ distinct (keys_of wanted))
          (ensures (forall (key: k).
                      (mem key (Plan?.stops (plan_of running held wanted)) /\
                       mem key (keys_of (Plan?.starts (plan_of running held wanted)))) <==>
                      (exists (v0 v1: d). mem_entry (Entry key v0) running /\ mem_entry (Entry key v1) wanted /\
                                          v0 <> v1 /\ not (held_at held key v1)))) =
  stop_spec running held wanted;
  mem_keys_filter (start_p running held) wanted;
  lookup_spec running;
  lookup_spec wanted;
  introduce forall (key: k). (mem key (Plan?.stops (plan_of running held wanted)) /\
                              mem key (keys_of (Plan?.starts (plan_of running held wanted)))) ==>
                             (exists (v0 v1: d). mem_entry (Entry key v0) running /\ mem_entry (Entry key v1) wanted /\
                                                 v0 <> v1 /\ not (held_at held key v1))
  with introduce _ ==> _
  with begin
    eliminate exists (v0: d). mem_entry (Entry key v0) running /\ lookup key wanted <> OSome v0
    with begin
      eliminate exists (v1: d). mem_entry (Entry key v1) wanted /\ start_p running held (Entry key v1)
      with ()
    end
  end

/// **`held_unchanged_not_started`.** A key held under a description that is still wanted is not in
/// `starts`.
let held_unchanged_not_started (#k: eqtype) (#d: eqtype) (running held wanted: list (entry k d)) (key: k) (v: d)
  : Lemma (requires held_at held key v)
          (ensures not (mem_entry (Entry key v) (Plan?.starts (plan_of running held wanted)))) =
  start_spec running held wanted

/// **`sequence_shape`.** The sequence a host performs is every stop, then every start; and when the two
/// maps are in key order, so is each half.
let sequence_shape (#k: eqtype) (#d: eqtype) (lt: k -> k -> bool) (running held wanted: list (entry k d))
  : Lemma (requires sorted lt (keys_of running) /\ sorted lt (keys_of wanted))
          (ensures (let p = plan_of running held wanted in
                    sequence p == append (stop_actions (Plan?.stops p)) (start_actions (Plan?.starts p)) /\
                    sorted lt (Plan?.stops p) /\ sorted lt (Plan?.keeps p) /\ sorted lt (keys_of (Plan?.starts p)))) =
  filter_sorted lt (stop_p wanted) running;
  filter_sorted lt (keep_p wanted) running;
  filter_sorted lt (start_p running held) wanted

(* ───────────────────────────────────────────────────────────────────
   The theorems — the plan applied.
   ─────────────────────────────────────────────────────────────────── *)

/// `lookup_mem`, for every key at once.
let lookup_mem_all (#k: eqtype) (#d: eqtype) (xs: list (entry k d))
  : Lemma (ensures (forall (key: k) (v: d). lookup key xs = OSome v ==> mem_entry (Entry key v) xs) /\
                   (forall (key: k). ONone? (lookup key xs) ==> not (mem key (keys_of xs)))) =
  introduce forall (key: k). (forall (v: d). lookup key xs = OSome v ==> mem_entry (Entry key v) xs) /\
                             (ONone? (lookup key xs) ==> not (mem key (keys_of xs)))
  with lookup_mem key xs

/// What holds of every wanted entry afterwards: it runs exactly when it was kept or started
/// successfully, and is held exactly otherwise — never both, under `separate`.
let after_spec (#k: eqtype) (#d: eqtype) (start: k -> d -> bool) (running held wanted: list (entry k d))
  : Lemma (requires distinct (keys_of wanted) /\ separate held running)
          (ensures (let Pair running' held' = apply start running held wanted in
                    distinct (keys_of running') /\ distinct (keys_of held') /\
                    (forall (key: k) (v: d). mem_entry (Entry key v) wanted ==>
                       (lookup key running' = OSome v <==> runs_after start running held (Entry key v)) /\
                       (held_at held' key v <==> not (runs_after start running held (Entry key v)))) /\
                    (forall (e: entry k d). mem_entry e running' ==> mem_entry e wanted) /\
                    (forall (e: entry k d). mem_entry e held' ==> mem_entry e wanted))) =
  let Pair running' held' = apply start running held wanted in
  filter_mem (runs_after start running held) wanted;
  filter_mem (held_after start running held) wanted;
  filter_distinct (runs_after start running held) wanted;
  filter_distinct (held_after start running held) wanted;
  lookup_spec running';
  lookup_spec held';
  lookup_mem_all held;
  lookup_mem_all running;
  mem_keys_of held;
  mem_keys_of running

/// **`keys_after_are_wanted`.** Afterwards, a key is running or held exactly when it is wanted, and never
/// both; and when nothing is held and every start succeeds, what runs is exactly what is wanted.
let keys_after_are_wanted (#k: eqtype) (#d: eqtype) (start: k -> d -> bool) (running held wanted: list (entry k d))
  : Lemma (requires distinct (keys_of wanted) /\ separate held running)
          (ensures (let Pair running' held' = apply start running held wanted in
                    (forall (key: k). (mem key (keys_of running') \/ mem key (keys_of held')) <==> mem key (keys_of wanted)) /\
                    separate held' running' /\
                    ((held == [] /\ (forall (key: k) (v: d). start key v)) ==> running' == wanted))) =
  let Pair running' held' = apply start running held wanted in
  after_spec start running held wanted;
  mem_keys_of running';
  mem_keys_of held';
  mem_keys_of wanted;
  mem_keys_of held;
  mem_keys_of running;
  lookup_spec wanted;
  lookup_spec running';
  lookup_spec held';
  lookup_mem_all held;
  lookup_mem_all running;
  filter_mem (runs_after start running held) wanted;
  filter_mem (held_after start running held) wanted;
  introduce (held == [] /\ (forall (key: k) (v: d). start key v)) ==> running' == wanted
  with filter_all (runs_after start running held) wanted

/// **`plan_twice_empty`.** The plan computed again after applying it, against the same wanted map, is
/// empty: nothing to stop, nothing to start, and every running key kept.
let plan_twice_empty (#k: eqtype) (#d: eqtype) (start: k -> d -> bool) (running held wanted: list (entry k d))
  : Lemma (requires distinct (keys_of wanted) /\ separate held running)
          (ensures (let Pair running' held' = apply start running held wanted in
                    let Plan stops keeps starts = plan_of running' held' wanted in
                    stops == [] /\ starts == [] /\ keeps == keys_of running')) =
  let Pair running' held' = apply start running held wanted in
  after_spec start running held wanted;
  lookup_spec wanted;
  lookup_spec running';
  filter_none (stop_p wanted) running';
  filter_all (keep_p wanted) running';
  filter_none (start_p running' held') wanted

/// **`identity_skip_agrees`.** Applying the plan again against the same wanted map changes nothing:
/// what runs and what is held are exactly as before. A host that skips the diff when the model is the
/// same instance — so the wanted map is the one it last applied — therefore agrees with the general path.
let identity_skip_agrees (#k: eqtype) (#d: eqtype) (start: k -> d -> bool) (running held wanted: list (entry k d))
  : Lemma (requires distinct (keys_of wanted) /\ separate held running)
          (ensures (let Pair running' held' = apply start running held wanted in
                    apply start running' held' wanted == Pair running' held')) =
  let Pair running' held' = apply start running held wanted in
  after_spec start running held wanted;
  lookup_mem_all held;
  lookup_mem_all running;
  mem_keys_of held;
  mem_keys_of running;
  filter_ext (runs_after start running' held') (runs_after start running held) wanted;
  filter_ext (held_after start running' held') (held_after start running held) wanted

#pop-options
