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

/// Phase 646 — the drain buffer, modelled in F* and proved to be a FIFO queue.
///
/// # What this module is
///
/// A hand-written model of `DrainBuffer<'item>` (`src/Fuaran.MVU.Kernel/Drain.fs`), clause for
/// clause: the backing array as a list of slots, the read and write cursors, `Push` with its
/// compaction and growth, `Pop` and `Take`. The drain buffer is the queue every single-threaded host
/// drains messages from, so what it promises is the order of every message on those hosts.
///
/// # The representation
///
/// The array is a `list (slot a)`. `Placeholder` is a slot that holds no item — the constructor's
/// `Array.zeroCreate`, the slots a compaction clears, and the slot `Pop` clears after reading it —
/// and `Written v` is a slot the write cursor has filled. The unread contents are the slots from the
/// read cursor up to the write cursor, in order (`unread`), and that list is the queue the buffer
/// stands for.
///
/// # The abstraction function
///
/// `unread d` is the queue the buffer `d` represents. Every theorem is stated against it: a push
/// appends to it, a pop takes its head, and over any sequence of operations the buffer answers exactly
/// as the reference queue does (`drain_is_queue`). The differential host (`tests/Fuaran.MVU.Tests`)
/// runs the EXTRACTION of this module beside the shipped class over generated sequences on .NET and
/// under Fable, and requires the same answers in the same order.
///
/// # The theorems
///
///   * `drain_is_queue` — for any well-formed buffer and any sequence of pushes and pops, the buffer
///     produces exactly the outputs a reference FIFO queue produces from the buffer's unread contents,
///     and ends holding exactly what the queue holds: nothing is lost, nothing is invented, and the
///     order is the push order, through every compaction and every growth.
///   * `reset_agrees_with_empty` — the two cursors are both zero exactly when the queue is empty.
///   * `run_append` — a run that stops after some operations and resumes with the rest is the same run:
///     the state and the outputs are those of the uninterrupted run, so a backlog carried across a
///     yield is exactly the queue's contents and nothing is dropped.
///   * `cleared_outside_window` — after any run, every slot outside the unread window holds no item.
///   * `placeholder_unobserved` — a pop never returns a placeholder slot.
///   * `take_is_pop` — `Take` on a non-empty buffer is `Pop`.
///
/// # The parameters, and what is assumed of each
///
/// The item type `a` is any `Type0`; nothing about it is used. The capacity `size` is any integer; the
/// constructor floors it at `minimum_capacity`. Every theorem assumes the buffer is well-formed (`wf`),
/// which `create` establishes and every operation preserves. Nothing is admitted or assumed.
module Drain

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
   The array, as a list — the operations the buffer performs on it.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

/// F#: `items.Length`.
let rec length (#a: Type0) (xs: list a) : Tot nat (decreases xs) =
  match xs with
  | [] -> 0
  | _ :: rest -> 1 + length rest

/// F#: `items.[i]`. Total: an out-of-range read yields a placeholder, which `wf` proves is never
/// what the buffer reads.
let rec nth (#a: Type0) (xs: list (slot a)) (i: nat) : Tot (slot a) (decreases xs) =
  match xs with
  | [] -> Placeholder
  | x :: rest -> if i = 0 then x else nth rest (i - 1)

/// F#: `items.[i] <- v`. Total: an out-of-range write is dropped.
let rec set (#a: Type0) (xs: list (slot a)) (i: nat) (v: slot a) : Tot (list (slot a)) (decreases xs) =
  match xs with
  | [] -> []
  | x :: rest -> if i = 0 then v :: rest else x :: set rest (i - 1) v

/// F#: `Array.blit` into a fresh or cleared tail — the moved items followed by the placeholders.
let rec append (#a: Type0) (xs ys: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> x :: append rest ys

/// F#: the slots from the read cursor on.
let rec skip (#a: Type0) (n: nat) (xs: list a) : Tot (list a) (decreases xs) =
  if n = 0 then xs
  else
    match xs with
    | [] -> []
    | _ :: rest -> skip (n - 1) rest

/// F#: the first `n` of them.
let rec take (#a: Type0) (n: nat) (xs: list a) : Tot (list a) (decreases xs) =
  if n = 0 then []
  else
    match xs with
    | [] -> []
    | x :: rest -> x :: take (n - 1) rest

/// F#: `Array.zeroCreate n`, and the cleared tail of a compaction.
let rec replicate (#a: Type0) (n: nat) (v: a) : Tot (list a) (decreases n) =
  if n = 0 then [] else v :: replicate (n - 1) v

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The buffer — `Drain.fs`, clause for clause.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: the three mutable fields of `DrainBuffer<'item>`: `items`, `read`, `write`.
type drain (a: Type0) =
  | Drain : items: list (slot a) -> read: nat -> write: nat -> drain a

/// F#: `DrainBuffer.MinimumCapacity`.
let minimum_capacity : nat = 2

/// F#: `max capacity MinimumCapacity`.
let capacity_of (size: int) : (n: nat{n >= minimum_capacity}) =
  if size > minimum_capacity then size else minimum_capacity

/// F#: `DrainBuffer(capacity)`.
let create (#a: Type0) (size: int) : drain a = Drain (replicate (capacity_of size) Placeholder) 0 0

/// F#: `Push`. When the write cursor is at the end, the live items move to the front of the same
/// array (its tail cleared) if at most half of it is live, else into an array twice the size; then
/// the item is written at the write cursor.
let push (#a: Type0) (x: a) (d: drain a) : drain a =
  let Drain items r w = d in
  let n = length items in
  if w = n && r <= w then
    let live = w - r in
    let moved = take live (skip r items) in
    let items' =
      if live + live <= n then append moved (replicate (n - live) Placeholder)
      else append moved (replicate (n + n - live) Placeholder) in
    Drain (set items' live (Written x)) 0 (live + 1)
  else Drain (set items w (Written x)) r (w + 1)

/// F#: `Pop`. The read slot is cleared; both cursors reset when the buffer becomes empty.
let pop (#a: Type0) (d: drain a) : pair (drain a) (opt (slot a)) =
  let Drain items r w = d in
  if r = w then Pair d ONone
  else
    let x = nth items r in
    let items' = set items r Placeholder in
    if r + 1 = w then Pair (Drain items' 0 0) (OSome x) else Pair (Drain items' (r + 1) w) (OSome x)

/// F#: `Take` — the read without the option, for a caller that has checked `IsEmpty`.
let take_item (#a: Type0) (d: drain a) : pair (drain a) (slot a) =
  let Drain items r w = d in
  let x = nth items r in
  let items' = set items r Placeholder in
  if r + 1 = w then Pair (Drain items' 0 0) x else Pair (Drain items' (r + 1) w) x

(* ───────────────────────────────────────────────────────────────────
   The differential host's driver — one op at a time, outputs collected.
   Extracted, so both hosts run THIS over a generated sequence.
   ─────────────────────────────────────────────────────────────────── *)

/// One operation on the buffer.
type op (a: Type0) =
  | Push : item: a -> op a
  | Pop : op a

#push-options "--fuel 1 --ifuel 1"

/// Run a sequence of operations; every `Pop` contributes its result to the output list, in order.
let rec run (#a: Type0) (d: drain a) (ops: list (op a))
  : Tot (pair (drain a) (list (opt (slot a)))) (decreases ops) =
  match ops with
  | [] -> Pair d []
  | Push item :: rest -> run (push item d) rest
  | Pop :: rest ->
      let Pair d' out = pop d in
      let Pair d'' outs = run d' rest in
      Pair d'' (out :: outs)

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The specification — ghost. What the buffer MEANS.
   ─────────────────────────────────────────────────────────────────── *)

/// The unread contents, oldest first — the queue the buffer stands for.
[@@ noextract_to "FSharp"]
let unread (#a: Type0) (d: drain a) : list (slot a) =
  let Drain items r w = d in
  if r <= w then take (w - r) (skip r items) else []

/// Every slot is a written one.
[@@ noextract_to "FSharp"]
let all_written (#a: Type0) (xs: list (slot a)) : prop =
  forall (i: nat). i < length xs ==> Written? (nth xs i)

/// Well-formed: at least two slots, the cursors in range and in order, both zero when they meet,
/// every unread slot written, and every slot outside the unread window clear.
[@@ noextract_to "FSharp"]
let wf (#a: Type0) (d: drain a) : prop =
  let Drain items r w = d in
  let n = length items in
  n >= minimum_capacity /\ r <= w /\ w <= n /\ (r = w ==> r = 0) /\
  all_written (unread d) /\
  (forall (i: nat). i < n /\ (i < r \/ i >= w) ==> nth items i == Placeholder)

(* ───────────────────────────────────────────────────────────────────
   List lemmas — pointwise, through `nth`, closed by extensionality.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

let rec length_append (#a: Type0) (xs ys: list a)
  : Lemma (ensures length (append xs ys) = length xs + length ys) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> length_append rest ys

let rec length_replicate (#a: Type0) (n: nat) (v: a)
  : Lemma (ensures length (replicate n v) = n) (decreases n) =
  if n = 0 then () else length_replicate (n - 1) v

let rec length_set (#a: Type0) (xs: list (slot a)) (i: nat) (v: slot a)
  : Lemma (ensures length (set xs i v) = length xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else length_set rest (i - 1) v

let rec length_skip (#a: Type0) (n: nat) (xs: list a)
  : Lemma (requires n <= length xs) (ensures length (skip n xs) = length xs - n) (decreases xs) =
  if n = 0 then ()
  else
    match xs with
    | [] -> ()
    | _ :: rest -> length_skip (n - 1) rest

let rec length_take (#a: Type0) (n: nat) (xs: list a)
  : Lemma (requires n <= length xs) (ensures length (take n xs) = n) (decreases xs) =
  if n = 0 then ()
  else
    match xs with
    | [] -> ()
    | _ :: rest -> length_take (n - 1) rest

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

let rec nth_append (#a: Type0) (xs ys: list (slot a)) (i: nat)
  : Lemma (ensures nth (append xs ys) i == (if i < length xs then nth xs i else nth ys (i - length xs)))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else nth_append rest ys (i - 1)

let rec nth_skip (#a: Type0) (n: nat) (xs: list (slot a)) (i: nat)
  : Lemma (ensures nth (skip n xs) i == nth xs (n + i)) (decreases xs) =
  if n = 0 then ()
  else
    match xs with
    | [] -> ()
    | _ :: rest -> nth_skip (n - 1) rest i

let rec nth_take (#a: Type0) (n: nat) (xs: list (slot a)) (i: nat)
  : Lemma (requires i < n) (ensures nth (take n xs) i == nth xs i) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else nth_take (n - 1) rest (i - 1)

let rec nth_replicate (#a: Type0) (n i: nat)
  : Lemma (ensures nth (replicate n (Placeholder #a)) i == Placeholder) (decreases n) =
  if n = 0 then ()
  else if i = 0 then ()
  else nth_replicate #a (n - 1) (i - 1)

#push-options "--fuel 2"
let length_one (#a: Type0) (v: a) : Lemma (length [v] = 1) = ()
#pop-options

/// Two slot lists of the same length that agree at every index are the same list.
let rec ext (#a: Type0) (xs ys: list (slot a))
  : Lemma (requires length xs = length ys /\ (forall (i: nat). i < length xs ==> nth xs i == nth ys i))
          (ensures xs == ys) (decreases xs) =
  match xs, ys with
  | [], [] -> ()
  | x :: xr, y :: yr ->
      assert (nth xs 0 == nth ys 0);
      assert (forall (i: nat). i < length xr ==> nth xr i == nth xs (i + 1));
      assert (forall (i: nat). i < length yr ==> nth yr i == nth ys (i + 1));
      ext xr yr

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The unread window, pointwise.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

/// The unread contents have one slot per unread position, each the array's slot at that position
/// from the read cursor.
let unread_spec (#a: Type0) (d: drain a)
  : Lemma (requires (let Drain items r w = d in r <= w /\ w <= length items))
          (ensures (let Drain items r w = d in
                    length (unread d) = w - r /\
                    (forall (i: nat). i < w - r ==> nth (unread d) i == nth items (r + i)))) =
  let Drain items r w = d in
  length_skip r items;
  length_take (w - r) (skip r items);
  introduce forall (i: nat). i < w - r ==> nth (unread d) i == nth items (r + i)
  with introduce _ ==> _
  with begin
    nth_take (w - r) (skip r items) i;
    nth_skip r items i
  end

(* ───────────────────────────────────────────────────────────────────
   The theorems.
   ─────────────────────────────────────────────────────────────────── *)

/// **`create_wf`.** Every buffer the constructor builds is well-formed and empty, whatever capacity
/// was asked for.
let create_wf (#a: Type0) (size: int)
  : Lemma (wf (create #a size) /\ unread (create #a size) == []) =
  let n = capacity_of size in
  length_replicate n (Placeholder #a);
  introduce forall (i: nat). i < n ==> nth (replicate n (Placeholder #a)) i == Placeholder
  with introduce _ ==> _ with nth_replicate #a n i;
  unread_spec (create #a size)

/// The slots of the compacted or grown array: the moved window first, placeholders after.
let compacted_spec (#a: Type0) (items: list (slot a)) (r w k: nat)
  : Lemma (requires r <= w /\ w <= length items)
          (ensures (let moved = take (w - r) (skip r items) in
                    let items' = append moved (replicate k (Placeholder #a)) in
                    length items' = (w - r) + k /\
                    (forall (i: nat). i < w - r ==> nth items' i == nth items (r + i)) /\
                    (forall (i: nat). w - r <= i /\ i < (w - r) + k ==> nth items' i == Placeholder))) =
  let live = w - r in
  let moved = take live (skip r items) in
  let tail = replicate k (Placeholder #a) in
  length_skip r items;
  length_take live (skip r items);
  length_replicate k (Placeholder #a);
  length_append moved tail;
  introduce forall (i: nat). i < live ==> nth (append moved tail) i == nth items (r + i)
  with introduce _ ==> _
  with begin
    nth_append moved tail i;
    nth_take live (skip r items) i;
    nth_skip r items i
  end;
  introduce forall (i: nat). live <= i /\ i < live + k ==> nth (append moved tail) i == Placeholder
  with introduce _ ==> _
  with begin
    nth_append moved tail i;
    nth_replicate #a k (i - live)
  end

/// The written slot: one index moved, every other the same.
let set_spec (#a: Type0) (xs: list (slot a)) (i: nat) (v: slot a)
  : Lemma (requires i < length xs)
          (ensures length (set xs i v) = length xs /\ nth (set xs i v) i == v /\
                   (forall (j: nat). j <> i ==> nth (set xs i v) j == nth xs j)) =
  length_set xs i v;
  nth_set_same xs i v;
  introduce forall (j: nat). j <> i ==> nth (set xs i v) j == nth xs j
  with introduce _ ==> _ with nth_set_other xs i j v

/// A push on a well-formed buffer appends its item to the unread contents and leaves the buffer
/// well-formed — through a compaction and through a growth included.
let push_spec (#a: Type0) (x: a) (d: drain a)
  : Lemma (requires wf d)
          (ensures wf (push x d) /\ unread (push x d) == append (unread d) [Written x]) =
  let Drain items r w = d in
  let n = length items in
  unread_spec d;
  let before = unread d in
  length_one (Written x);
  length_append before [Written x];
  introduce forall (i: nat). i < length before ==> nth (append before [Written x]) i == nth items (r + i)
  with introduce _ ==> _ with nth_append before [Written x] i;
  nth_append before [Written x] (w - r);
  if w = n then begin
    let live = w - r in
    let k = if live + live <= n then n - live else n + n - live in
    let items' = append (take live (skip r items)) (replicate k (Placeholder #a)) in
    compacted_spec items r w k;
    set_spec items' live (Written x);
    let d' = Drain (set items' live (Written x)) 0 (live + 1) in
    unread_spec d';
    ext (unread d') (append before [Written x])
  end
  else begin
    set_spec items w (Written x);
    let d' = Drain (set items w (Written x)) r (w + 1) in
    unread_spec d';
    ext (unread d') (append before [Written x])
  end

/// A pop on a well-formed buffer yields the oldest unread slot and leaves the rest, well-formed, the
/// read slot cleared; on an empty buffer it yields nothing and changes nothing.
let pop_spec (#a: Type0) (d: drain a)
  : Lemma (requires wf d)
          (ensures (match unread d with
                    | [] -> pop d == Pair d ONone
                    | s :: rest -> (let Pair d' out = pop d in wf d' /\ out == OSome s /\ unread d' == rest))) =
  let Drain items r w = d in
  unread_spec d;
  if r = w then ()
  else begin
    let before = unread d in
    set_spec items r (Placeholder #a);
    let items' = set items r (Placeholder #a) in
    let d' = if r + 1 = w then Drain items' 0 0 else Drain items' (r + 1) w in
    unread_spec d';
    assert (Cons? before);
    let s :: rest = before in
    assert (s == nth items r);
    assert (length rest = w - r - 1);
    introduce forall (i: nat). i < length rest ==> nth rest i == nth before (i + 1)
    with ();
    ext (unread d') rest
  end

/// The reference: a FIFO queue over the same operations.
[@@ noextract_to "FSharp"]
let rec queue_run (#a: Type0) (q: list (slot a)) (ops: list (op a))
  : Tot (pair (list (slot a)) (list (opt (slot a)))) (decreases ops) =
  match ops with
  | [] -> Pair q []
  | Push item :: rest -> queue_run (append q [Written item]) rest
  | Pop :: rest ->
      (match q with
       | [] -> (let Pair q' outs = queue_run q rest in Pair q' (ONone :: outs))
       | s :: q' -> (let Pair q'' outs = queue_run q' rest in Pair q'' (OSome s :: outs)))

/// **`drain_is_queue`** — the headline. Over ANY operation sequence a well-formed buffer produces
/// exactly the outputs the reference queue produces from its unread contents, and ends well-formed
/// holding exactly what the queue holds: every pushed item is popped exactly once, in push order,
/// however many times the buffer compacts or grows on the way.
let rec drain_is_queue (#a: Type0) (d: drain a) (ops: list (op a))
  : Lemma (requires wf d)
          (ensures (let Pair d' outs = run d ops in
                    let Pair q' qouts = queue_run (unread d) ops in
                    wf d' /\ unread d' == q' /\ outs == qouts))
          (decreases ops) =
  match ops with
  | [] -> ()
  | Push item :: rest ->
      push_spec item d;
      drain_is_queue (push item d) rest
  | Pop :: rest ->
      pop_spec d;
      let Pair d' _ = pop d in
      drain_is_queue d' rest

/// **`run_append`** — nothing is dropped across a yield. A run over `ops1` that stops, and resumes
/// over `ops2` from the state it left, is the run over the whole sequence: same final state, and the
/// outputs of the two parts in order. The state between the two IS the backlog the yield carries.
let rec run_append (#a: Type0) (d: drain a) (ops1 ops2: list (op a))
  : Lemma (ensures (let Pair d1 outs1 = run d ops1 in
                    let Pair d2 outs2 = run d1 ops2 in
                    run d (append ops1 ops2) == Pair d2 (append outs1 outs2)))
          (decreases ops1) =
  match ops1 with
  | [] -> ()
  | Push item :: rest -> run_append (push item d) rest ops2
  | Pop :: rest ->
      let Pair d' _ = pop d in
      run_append d' rest ops2

/// **`reset_agrees_with_empty`.** On a well-formed buffer the two cursors are both zero exactly when
/// the unread contents are empty: a reset happens when, and only when, the queue is empty.
let reset_agrees_with_empty (#a: Type0) (d: drain a)
  : Lemma (requires wf d)
          (ensures (let Drain _ r w = d in (unread d == []) <==> (r = 0 /\ w = 0))) =
  unread_spec d

/// **`cleared_outside_window`.** After any run from a well-formed buffer, every slot outside the
/// unread window holds no item: the buffer keeps no reference to an item it has handed over.
let cleared_outside_window (#a: Type0) (d: drain a) (ops: list (op a))
  : Lemma (requires wf d)
          (ensures (let Pair (Drain items r w) _ = run d ops in
                    forall (i: nat). i < length items /\ (i < r \/ i >= w) ==> nth items i == Placeholder)) =
  drain_is_queue d ops

/// **`placeholder_unobserved`.** A pop on a well-formed buffer never returns a placeholder.
let placeholder_unobserved (#a: Type0) (d: drain a)
  : Lemma (requires wf d) (ensures (let Pair _ out = pop d in out =!= OSome (Placeholder #a))) =
  pop_spec d;
  unread_spec d

/// **`take_is_pop`.** On a non-empty well-formed buffer, `Take` is `Pop` without the option.
let take_is_pop (#a: Type0) (d: drain a)
  : Lemma (requires wf d /\ Cons? (unread d))
          (ensures (let Pair d1 x = take_item d in let Pair d2 out = pop d in d1 == d2 /\ out == OSome x)) =
  unread_spec d

#pop-options
