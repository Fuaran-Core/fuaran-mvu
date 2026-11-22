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

/// Phase 646 — the fixed-capacity ring, modelled in F* and proved to be a bounded FIFO queue.
///
/// # What this module is
///
/// A hand-written model of `FixedRing<'item>` (`src/Fuaran.MVU.Kernel/Ring.fs`), clause for clause:
/// the two-state representation, `Push` with its refusal, and `Pop`. The ring is the queue for a host
/// that must refuse rather than grow — a server connection, a device with a single producer — so what
/// it promises is the order of every message on those hosts, and that a refused push costs nothing.
///
/// # What is taken, and what is new
///
/// This model is adapted from the ring of the ToolUp Analytics open-source SDK (toolup-forge,
/// Apache-2.0): its list model `ElmishRing.fst`, and the array ring `ElmishRingArray.fst` proved there
/// to refine it. TAKEN: the two-case state (`Writable`, `ReadWritable`), the cyclic index step `succ`
/// modelled as a case split so every lemma is linear arithmetic, the distance and window vocabulary
/// (`dist`, `iter`, `window`, `unread`), the well-formedness floor of two slots, the shape of a refused
/// push as "nothing changed" (`push_post` there), and the run-over-any-sequence statement with its
/// reference queue. REMOVED: the growth step `doubleSize`, and with it every placeholder a grow
/// manufactures, `order_across_grow`, and the capacity ceiling of 2^31 - 1 slots. NEW: the refusal at
/// the configured capacity, stated exactly — a push is refused if and only if the ring holds `capacity`
/// items; the popped slot is cleared, so an empty ring holds no reference at all, which strengthens
/// `wf` with a clear-outside-the-window clause; and `Pushed` verdicts in the run's output, so the
/// differential host sees every refusal.
///
/// # The abstraction function
///
/// `unread r` is the queue the ring `r` represents: the slots from the read head up to the write head,
/// cyclically. `capacity r` is the configured capacity, one less than the slot count, because the
/// two-head representation needs one spare slot to tell a full ring from an empty one.
///
/// # The theorems
///
///   * `ring_is_bounded_queue` — for any well-formed ring and any sequence of pushes and pops, the ring
///     answers exactly as the reference FIFO queue of its capacity does from the ring's unread contents
///     — every pop and every push verdict — and ends holding exactly what that queue holds. Nothing is
///     lost, nothing is invented, the order is the push order, and a push is refused exactly when the
///     queue is full.
///   * `push_refused_iff_full` — a push is refused if and only if the ring holds `capacity` items.
///   * `refused_push_unchanged` — a refused push leaves the ring exactly as it was.
///   * `placeholder_unobserved` — a pop never returns a placeholder slot.
///   * `cleared_outside_window` — after any run, every slot outside the unread window holds no item.
///
/// # The parameters, and what is assumed of each
///
/// The item type `a` is any `Type0`; nothing about it is used. The capacity is any integer; the
/// constructor floors it at `minimum_capacity`. Every theorem assumes the ring is well-formed (`wf`),
/// which `create` establishes and every operation preserves. Nothing is admitted or assumed.
module FixedRing

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

/// A backing-array slot: no item (`Array.zeroCreate`, a cleared slot), or a written one.
type slot (a: Type0) =
  | Placeholder : slot a
  | Written : value: a -> slot a

(* ───────────────────────────────────────────────────────────────────
   The array, as a list.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

/// F#: `items.Length`.
let rec length (#a: Type0) (xs: list a) : Tot nat (decreases xs) =
  match xs with
  | [] -> 0
  | _ :: rest -> 1 + length rest

/// F#: `items.[i]`. Total: an out-of-range read yields a placeholder, which `wf` proves is never
/// what the ring reads.
let rec nth (#a: Type0) (xs: list (slot a)) (i: nat) : Tot (slot a) (decreases xs) =
  match xs with
  | [] -> Placeholder
  | x :: rest -> if i = 0 then x else nth rest (i - 1)

/// F#: `items.[i] <- v`. Total: an out-of-range write is dropped.
let rec set (#a: Type0) (xs: list (slot a)) (i: nat) (v: slot a) : Tot (list (slot a)) (decreases xs) =
  match xs with
  | [] -> []
  | x :: rest -> if i = 0 then v :: rest else x :: set rest (i - 1) v

/// F#: `List.append`, in the specification.
let rec append (#a: Type0) (xs ys: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> x :: append rest ys

/// F#: `Array.zeroCreate n`.
let rec replicate (#a: Type0) (n: nat) (v: a) : Tot (list a) (decreases n) =
  if n = 0 then [] else v :: replicate (n - 1) v

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The index step.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `if i + 1 >= items.Length then 0 else i + 1` — the ring's own spelling of `(i + 1) % n`.
let succ (n: nat) (i: nat) : nat = if i + 1 >= n then 0 else i + 1

(* ───────────────────────────────────────────────────────────────────
   The ring — `Ring.fs`, clause for clause.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: the mutable state of `FixedRing<'item>`. `readable = false` is `Writable items wix` (nothing
/// unread; `wix` is where the next write lands); `readable = true` is `ReadWritable items wix rix`
/// (unread slots from `rix` up to but excluding `wix`, cyclically).
type ring (a: Type0) =
  | Writable : items: list (slot a) -> ix: nat -> ring a
  | ReadWritable : items: list (slot a) -> wix: nat -> rix: nat -> ring a

/// F#: `FixedRing.MinimumCapacity` — a ring holds at least one item.
let minimum_capacity : nat = 1

/// F#: `max capacity MinimumCapacity`.
let capacity_of (size: int) : (n: nat{n >= minimum_capacity}) =
  if size > minimum_capacity then size else minimum_capacity

/// F#: `FixedRing(capacity)` — `capacity + 1` slots, the spare one telling full from empty.
let create (#a: Type0) (size: int) : ring a = Writable (replicate (capacity_of size + 1) Placeholder) 0

#push-options "--ifuel 1"

/// The backing array of either state.
let items_of (#a: Type0) (r: ring a) : list (slot a) =
  match r with
  | Writable items _ -> items
  | ReadWritable items _ _ -> items

/// F#: `Capacity` — the slot count less the spare slot.
let capacity (#a: Type0) (r: ring a) : int = length (items_of r) - 1

/// F#: `Pop`. The read slot is cleared.
let pop (#a: Type0) (r: ring a) : pair (ring a) (opt (slot a)) =
  match r with
  | ReadWritable items wix rix ->
      let items' = set items rix Placeholder in
      let rix' = succ (length items) rix in
      if rix' = wix then Pair (Writable items' wix) (OSome (nth items rix))
      else Pair (ReadWritable items' wix rix') (OSome (nth items rix))
  | Writable _ _ -> Pair r ONone

/// F#: `Push` — the ring and the verdict: `true` and the item appended, or `false` and nothing changed.
let push (#a: Type0) (item: a) (r: ring a) : pair (ring a) bool =
  match r with
  | Writable items ix ->
      let items' = set items ix (Written item) in
      Pair (ReadWritable items' (succ (length items) ix) ix) true
  | ReadWritable items wix rix ->
      let wix' = succ (length items) wix in
      if wix' = rix then Pair r false
      else Pair (ReadWritable (set items wix (Written item)) wix' rix) true

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The differential host's driver — one op at a time, outputs collected.
   Extracted, so both hosts run THIS over a generated sequence.
   ─────────────────────────────────────────────────────────────────── *)

/// One operation on the ring.
type op (a: Type0) =
  | Push : item: a -> op a
  | Pop : op a

/// What an operation answers: the popped slot, or the push verdict.
type out (a: Type0) =
  | Popped : result: opt (slot a) -> out a
  | Pushed : accepted: bool -> out a

#push-options "--fuel 1 --ifuel 1"

/// Run a sequence of operations; every operation contributes its answer to the output list, in order.
let rec run (#a: Type0) (r: ring a) (ops: list (op a)) : Tot (pair (ring a) (list (out a))) (decreases ops) =
  match ops with
  | [] -> Pair r []
  | Push item :: rest ->
      let Pair r' ok = push item r in
      let Pair r'' outs = run r' rest in
      Pair r'' (Pushed ok :: outs)
  | Pop :: rest ->
      let Pair r' o = pop r in
      let Pair r'' outs = run r' rest in
      Pair r'' (Popped o :: outs)

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The specification — ghost. What the ring MEANS.
   ─────────────────────────────────────────────────────────────────── *)

/// The number of steps from `from` forward to `to_`, cyclically.
[@@ noextract_to "FSharp"]
let dist (n: nat) (from: nat{from < n}) (to_: nat) : nat =
  if to_ >= from then to_ - from else to_ + n - from

#push-options "--fuel 1 --ifuel 1"

/// `count` steps of `succ` from `start`.
[@@ noextract_to "FSharp"]
let rec iter (n: nat) (start: nat) (count: nat) : Tot nat (decreases count) =
  if count = 0 then start else iter n (succ n start) (count - 1)

/// The `count` slots read cyclically from `start`.
[@@ noextract_to "FSharp"]
let rec window (#a: Type0) (items: list (slot a)) (start: nat) (count: nat) : Tot (list (slot a)) (decreases count) =
  if count = 0 then [] else nth items start :: window items (succ (length items) start) (count - 1)

/// Whether `i` is one of the `count` indices read cyclically from `start`.
[@@ noextract_to "FSharp"]
let rec in_window (n: nat) (start: nat) (count: nat) (i: nat) : Tot bool (decreases count) =
  if count = 0 then false else start = i || in_window n (succ n start) (count - 1) i

/// Every slot is a written one.
[@@ noextract_to "FSharp"]
let rec all_written (#a: Type0) (xs: list (slot a)) : Tot bool (decreases xs) =
  match xs with
  | [] -> true
  | Placeholder :: _ -> false
  | Written _ :: rest -> all_written rest

#pop-options

#push-options "--ifuel 1"

/// The unread contents of a ring, oldest first — the queue it stands for.
[@@ noextract_to "FSharp"]
let unread (#a: Type0) (r: ring a) : list (slot a) =
  match r with
  | Writable _ _ -> []
  | ReadWritable items wix rix ->
      if rix < length items then window items rix (dist (length items) rix wix) else []

/// Well-formed: at least two slots; in the empty state every slot clear; in the readable state the two
/// heads in range and apart, every unread slot written, and every slot outside the unread window clear.
[@@ noextract_to "FSharp"]
let wf (#a: Type0) (r: ring a) : prop =
  match r with
  | Writable items ix ->
      length items >= 2 /\ ix < length items /\
      (forall (i: nat). i < length items ==> nth items i == Placeholder)
  | ReadWritable items wix rix ->
      let n = length items in
      n >= 2 /\ wix < n /\ rix < n /\ wix <> rix /\ all_written (unread r) /\
      (forall (i: nat). i < n /\ not (in_window n rix (dist n rix wix) i) ==> nth items i == Placeholder)

#pop-options

(* ───────────────────────────────────────────────────────────────────
   List lemmas.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 2 --ifuel 1"

let rec length_replicate (#a: Type0) (n: nat) (v: a)
  : Lemma (ensures length (replicate n v) = n) (decreases n) =
  if n = 0 then () else length_replicate (n - 1) v

let rec nth_replicate (#a: Type0) (n i: nat)
  : Lemma (ensures nth (replicate n (Placeholder #a)) i == Placeholder) (decreases n) =
  if n = 0 then ()
  else if i = 0 then ()
  else nth_replicate #a (n - 1) (i - 1)

let rec length_set (#a: Type0) (xs: list (slot a)) (i: nat) (v: slot a)
  : Lemma (ensures length (set xs i v) = length xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else length_set rest (i - 1) v

let rec nth_set_same (#a: Type0) (xs: list (slot a)) (i: nat) (v: slot a)
  : Lemma (requires i < length xs) (ensures nth (set xs i v) i == v) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else nth_set_same rest (i - 1) v

let rec nth_set_other (#a: Type0) (xs: list (slot a)) (i j: nat) (v: slot a)
  : Lemma (requires i <> j) (ensures nth (set xs i v) j == nth xs j) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 || j = 0 then () else nth_set_other rest (i - 1) (j - 1) v

let rec all_written_append (#a: Type0) (xs ys: list (slot a))
  : Lemma (ensures all_written (append xs ys) = (all_written xs && all_written ys)) (decreases xs) =
  match xs with
  | [] -> ()
  | Placeholder :: _ -> ()
  | Written _ :: rest -> all_written_append rest ys

let rec length_window (#a: Type0) (items: list (slot a)) (start: nat) (count: nat)
  : Lemma (ensures length (window items start count) = count) (decreases count) =
  if count = 0 then () else length_window items (succ (length items) start) (count - 1)

(* ───────────────────────────────────────────────────────────────────
   Index lemmas — all linear.
   ─────────────────────────────────────────────────────────────────── *)

/// Stepping the origin forward shortens the distance by one.
let dist_step (n: nat) (from: nat{from < n}) (to_: nat)
  : Lemma (requires to_ < n /\ from <> to_)
          (ensures succ n from < n /\ dist n from to_ = 1 + dist n (succ n from) to_) = ()

/// Stepping the target forward lengthens the distance by one, until it would land on the origin.
let dist_succ (n: nat) (from: nat{from < n}) (to_: nat)
  : Lemma (requires to_ < n /\ succ n to_ <> from)
          (ensures dist n from (succ n to_) = dist n from to_ + 1) = ()

/// The window is full — `n - 1` slots — exactly when the next write would land on the read head.
let dist_full (n: nat) (from: nat{from < n}) (to_: nat)
  : Lemma (requires n >= 2 /\ to_ < n) (ensures (dist n from to_ = n - 1) <==> (succ n to_ = from)) = ()

/// The step back: from the slot after `from`, `from` is `n - 1` steps away.
let dist_back (n: nat) (from: nat{from < n})
  : Lemma (requires n >= 2) (ensures succ n from < n /\ dist n (succ n from) from = n - 1) = ()

let rec iter_in_range (n: nat) (start: nat) (count: nat)
  : Lemma (requires start < n) (ensures iter n start count < n) (decreases count) =
  if count = 0 then () else iter_in_range n (succ n start) (count - 1)

/// Walking `dist from to` steps from `from` lands on `to`.
let rec iter_dist (n: nat) (from: nat{from < n}) (to_: nat)
  : Lemma (requires to_ < n) (ensures iter n from (dist n from to_) = to_) (decreases (dist n from to_)) =
  if from = to_ then ()
  else begin
    dist_step n from to_;
    iter_dist n (succ n from) to_
  end

/// An index is in the window exactly when it is nearer than the window's length.
let rec in_window_iff_dist (n: nat) (start: nat{start < n}) (count: nat) (i: nat)
  : Lemma (requires i < n) (ensures in_window n start count i = (dist n start i < count)) (decreases count) =
  if count = 0 then ()
  else if start = i then ()
  else begin
    dist_step n start i;
    in_window_iff_dist n (succ n start) (count - 1) i
  end

(* ───────────────────────────────────────────────────────────────────
   Window lemmas.
   ─────────────────────────────────────────────────────────────────── *)

/// A write outside the window leaves the window unchanged.
let rec window_set_outside (#a: Type0) (items: list (slot a)) (start: nat) (count: nat) (i: nat) (v: slot a)
  : Lemma (requires not (in_window (length items) start count i))
          (ensures window (set items i v) start count == window items start count) (decreases count) =
  length_set items i v;
  if count = 0 then ()
  else begin
    nth_set_other items i start v;
    window_set_outside items (succ (length items) start) (count - 1) i v
  end

/// One more slot on the window is the slot at the window's end.
let rec window_snoc (#a: Type0) (items: list (slot a)) (start: nat) (count: nat)
  : Lemma (ensures window items start (count + 1) == append (window items start count) [nth items (iter (length items) start count)])
          (decreases count) =
  if count = 0 then () else window_snoc items (succ (length items) start) (count - 1)

(* ───────────────────────────────────────────────────────────────────
   The theorems.
   ─────────────────────────────────────────────────────────────────── *)

/// **`create_wf`.** Every ring the constructor builds is well-formed, empty, and of the configured
/// capacity (floored), whatever capacity was asked for.
let create_wf (#a: Type0) (size: int)
  : Lemma (wf (create #a size) /\ unread (create #a size) == [] /\ capacity (create #a size) = capacity_of size) =
  let n = capacity_of size + 1 in
  length_replicate n (Placeholder #a);
  introduce forall (i: nat). i < n ==> nth (replicate n (Placeholder #a)) i == Placeholder
  with introduce _ ==> _ with nth_replicate #a n i

/// A push on a well-formed ring that is not full appends its item to the unread contents and is
/// accepted; on a full ring it is refused and changes nothing. Either way the ring stays well-formed
/// and its capacity does not move.
let push_spec (#a: Type0) (item: a) (r: ring a)
  : Lemma (requires wf r)
          (ensures (let Pair r' ok = push item r in
                    wf r' /\ capacity r' = capacity r /\
                    (if ok then unread r' == append (unread r) [Written item]
                     else (r' == r /\ length (unread r) = capacity r)) /\
                    (ok <==> length (unread r) < capacity r))) =
  match r with
  | Writable items ix ->
      let n = length items in
      let items' = set items ix (Written item) in
      length_set items ix (Written item);
      nth_set_same items ix (Written item);
      dist_succ n ix ix;
      let r' = ReadWritable items' (succ n ix) ix in
      assert (dist n ix (succ n ix) = 1);
      assert (unread r' == [nth items' ix]);
      introduce forall (i: nat). i < n /\ not (in_window n ix 1 i) ==> nth items' i == Placeholder
      with introduce _ ==> _ with nth_set_other items ix i (Written item)
  | ReadWritable items wix rix ->
      let n = length items in
      let d = dist n rix wix in
      length_window items rix d;
      dist_full n rix wix;
      let wix' = succ n wix in
      if wix' = rix then ()
      else begin
        let items' = set items wix (Written item) in
        length_set items wix (Written item);
        nth_set_same items wix (Written item);
        in_window_iff_dist n rix d wix;
        window_set_outside items rix d wix (Written item);
        iter_dist n rix wix;
        window_snoc items' rix d;
        all_written_append (unread r) [Written item];
        dist_succ n rix wix;
        let r' = ReadWritable items' wix' rix in
        assert (unread r' == append (unread r) [Written item]);
        introduce forall (i: nat). i < n /\ not (in_window n rix (d + 1) i) ==> nth items' i == Placeholder
        with introduce _ ==> _
        with begin
          in_window_iff_dist n rix (d + 1) i;
          in_window_iff_dist n rix d i;
          nth_set_other items wix i (Written item)
        end
      end

/// A pop on a well-formed ring yields the oldest unread slot and leaves the rest, well-formed, the
/// read slot cleared; on an empty ring it yields nothing and changes nothing. The capacity does not move.
let pop_spec (#a: Type0) (r: ring a)
  : Lemma (requires wf r)
          (ensures (let Pair r' out = pop r in
                    capacity r' = capacity r /\
                    (match unread r with
                     | [] -> r' == r /\ out == ONone
                     | s :: rest -> wf r' /\ out == OSome s /\ unread r' == rest))) =
  match r with
  | Writable _ _ -> ()
  | ReadWritable items wix rix ->
      let n = length items in
      let d = dist n rix wix in
      let items' = set items rix Placeholder in
      length_set items rix (Placeholder #a);
      nth_set_same items rix (Placeholder #a);
      dist_step n rix wix;
      let rix' = succ n rix in
      let d' = dist n rix' wix in
      dist_back n rix;
      in_window_iff_dist n rix' d' rix;
      window_set_outside items rix' d' rix (Placeholder #a);
      introduce forall (i: nat). i < n /\ i <> rix /\ not (in_window n rix' d' i) ==> nth items' i == Placeholder
      with introduce _ ==> _
      with begin
        in_window_iff_dist n rix' d' i;
        in_window_iff_dist n rix d i;
        nth_set_other items rix i (Placeholder #a)
      end

/// The reference: a FIFO queue of capacity `cap` over the same operations, refusing a push when full.
[@@ noextract_to "FSharp"]
let rec queue_run (#a: Type0) (cap: int) (q: list (slot a)) (ops: list (op a))
  : Tot (pair (list (slot a)) (list (out a))) (decreases ops) =
  match ops with
  | [] -> Pair q []
  | Push item :: rest ->
      if length q < cap then
        (let Pair q' outs = queue_run cap (append q [Written item]) rest in Pair q' (Pushed true :: outs))
      else (let Pair q' outs = queue_run cap q rest in Pair q' (Pushed false :: outs))
  | Pop :: rest ->
      (match q with
       | [] -> (let Pair q' outs = queue_run cap q rest in Pair q' (Popped ONone :: outs))
       | s :: q' -> (let Pair q'' outs = queue_run cap q' rest in Pair q'' (Popped (OSome s) :: outs)))

/// **`ring_is_bounded_queue`** — the headline. Over ANY operation sequence a well-formed ring produces
/// exactly the outputs the reference queue of its capacity produces from its unread contents — every
/// popped slot and every push verdict — and ends well-formed, at the same capacity, holding exactly what
/// the queue holds. Every accepted item is popped exactly once, in push order, and a push is refused
/// exactly when the queue is full.
let rec ring_is_bounded_queue (#a: Type0) (r: ring a) (ops: list (op a))
  : Lemma (requires wf r)
          (ensures (let Pair r' outs = run r ops in
                    let Pair q' qouts = queue_run (capacity r) (unread r) ops in
                    wf r' /\ capacity r' = capacity r /\ unread r' == q' /\ outs == qouts))
          (decreases ops) =
  match ops with
  | [] -> ()
  | Push item :: rest ->
      push_spec item r;
      let Pair r' _ = push item r in
      ring_is_bounded_queue r' rest
  | Pop :: rest ->
      pop_spec r;
      let Pair r' _ = pop r in
      ring_is_bounded_queue r' rest

/// **`push_refused_iff_full`.** A push on a well-formed ring is refused if and only if the ring holds
/// exactly `capacity` items.
let push_refused_iff_full (#a: Type0) (item: a) (r: ring a)
  : Lemma (requires wf r)
          (ensures (let Pair _ ok = push item r in (not ok) <==> (length (unread r) = capacity r))) =
  push_spec item r

/// **`refused_push_unchanged`.** A refused push leaves the ring exactly as it was: the same slots, the
/// same heads, the same state.
let refused_push_unchanged (#a: Type0) (item: a) (r: ring a)
  : Lemma (requires wf r) (ensures (let Pair r' ok = push item r in ok \/ r' == r)) =
  push_spec item r

/// **`placeholder_unobserved`.** A pop on a well-formed ring never returns a placeholder.
let placeholder_unobserved (#a: Type0) (r: ring a)
  : Lemma (requires wf r) (ensures (let Pair _ out = pop r in out =!= OSome (Placeholder #a))) =
  pop_spec r

/// **`cleared_outside_window`.** After any run from a well-formed ring, every slot outside the unread
/// window holds no item — and an empty ring holds none at all.
let cleared_outside_window (#a: Type0) (r: ring a) (ops: list (op a))
  : Lemma (requires wf r)
          (ensures (let Pair r' _ = run r ops in
                    match r' with
                    | Writable items _ -> forall (i: nat). i < length items ==> nth items i == Placeholder
                    | ReadWritable items wix rix ->
                        (rix < length items) /\
                        (forall (i: nat). i < length items /\ not (in_window (length items) rix (dist (length items) rix wix) i)
                           ==> nth items i == Placeholder))) =
  ring_is_bounded_queue r ops

#pop-options
