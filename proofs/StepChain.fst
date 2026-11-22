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

/// Phase 647 — the chain of a step (the design's runtime semantics, rules 4 to 6), modelled in F*.
///
/// # What this module is
///
/// A model of `Chain.compute` and `Chain.continueFrom` (`src/Fuaran.MVU/Chain.fs`): the message of an
/// accepted envelope and every follow-up, processed first-in first-out — run `update`, append the
/// `Then` messages in order, record the `Do` effects in order — until no follow-up is left or the fuel is
/// spent. Nothing here performs or installs anything: the chain is a function of `update`, the model and
/// the message, which is what lets a preview, an approval and a replay compute exactly the step the
/// runtime commits (rule 6). The stamp a message reads, and the seed a follow-up reads, are not modelled:
/// they are inputs to `update` the theorems here do not depend on (the stamp laws test them).
///
/// # The parameters, and what is assumed of each
///
/// The message type `m`, the model type `md` and the effect type `e` are any `Type0`; nothing about them
/// is used. `update: md -> m -> pair md (list (step m e))` is total: the shipped runtime catches a throw
/// and refuses the step (rule 11), which the step machine's model takes as a refusal outcome. `fuel` is
/// the chain bound, in messages.
///
/// # The theorems
///
///   * `chain_messages_bounded` — a computed chain ran at most `fuel` messages through `update`.
///   * `chain_fuel_monotone` — a chain that fits under a bound is the same chain under any larger bound:
///     the bound refuses a chain or leaves it exactly as it is, never changes what it computes.
///   * `chain_whole` — the chain is computed whole: its effects are every `Do` of every message of the
///     chain, in processing order, as one list (stated by `chain_go_effects`, the inductive form).
///
/// Nothing is admitted or assumed.
module StepChain

#set-options "--fuel 0 --ifuel 0"

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns, so the extraction references
   `Prims` and nothing else.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a * 'b`.
type pair (a: Type0) (b: Type0) =
  | Pair : first: a -> second: b -> pair a b

/// F#: `Step<'msg, 'effect>`.
type step (m: Type0) (e: Type0) =
  | Do : payload: e -> step m e
  | Then : msg: m -> step m e

/// F#: `ChainOutcome<'model, 'effect>`, without the `Threw` arm (`update` is total here): the final
/// model, the effects in emission order and the number of messages run, or the fuel bound reached.
type outcome (md: Type0) (e: Type0) =
  | Computed : model: md -> effects: list e -> messages: nat -> outcome md e
  | TooLong : outcome md e

(* ───────────────────────────────────────────────────────────────────
   Lists — the model's own helpers.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

let rec length (#a: Type0) (xs: list a) : Tot nat (decreases xs) =
  match xs with
  | [] -> 0
  | _ :: rest -> 1 + length rest

let rec append (#a: Type0) (xs ys: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> x :: append rest ys

/// F#: `Step.effects` — the `Do` payloads, in order.
let rec effects_of (#m #e: Type0) (steps: list (step m e)) : Tot (list e) (decreases steps) =
  match steps with
  | [] -> []
  | Do x :: rest -> x :: effects_of rest
  | Then _ :: rest -> effects_of rest

/// F#: `Step.followUps` — the `Then` messages, in order.
let rec follow_ups_of (#m #e: Type0) (steps: list (step m e)) : Tot (list m) (decreases steps) =
  match steps with
  | [] -> []
  | Do _ :: rest -> follow_ups_of rest
  | Then x :: rest -> x :: follow_ups_of rest

(* ───────────────────────────────────────────────────────────────────
   The chain — `Chain.fs`, clause for clause.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Chain.continueFrom`'s loop. `pending` are the follow-ups not yet run, oldest first; `effs` the
/// effects so far; `count` the messages run; `fuel` what remains of the bound.
let rec chain_go (#m #md #e: Type0)
                 (update: md -> m -> pair md (list (step m e)))
                 (fuel: nat) (pending: list m) (model: md) (effs: list e) (count: nat)
  : Tot (outcome md e) (decreases fuel) =
  match pending with
  | [] -> Computed model effs count
  | x :: rest ->
      if fuel = 0 then TooLong
      else
        let Pair model' steps = update model x in
        chain_go update (fuel - 1) (append rest (follow_ups_of steps)) model' (append effs (effects_of steps)) (count + 1)

/// F#: `Chain.compute` — `update` on the message, then its follow-ups. A bound of zero refuses at once.
let chain (#m #md #e: Type0)
          (update: md -> m -> pair md (list (step m e)))
          (fuel: nat) (model: md) (msg: m) : outcome md e =
  if fuel = 0 then TooLong
  else
    let Pair model' steps = update model msg in
    chain_go update (fuel - 1) (follow_ups_of steps) model' (effects_of steps) 1

/// F#: `Chain.continueFrom update stamp fuel 0 model steps` — the boot's chain from `Init`'s steps.
let chain_from (#m #md #e: Type0)
               (update: md -> m -> pair md (list (step m e)))
               (fuel: nat) (model: md) (steps: list (step m e)) : outcome md e =
  chain_go update fuel (follow_ups_of steps) model (effects_of steps) 0

#pop-options

(* ───────────────────────────────────────────────────────────────────
   The theorems.
   ─────────────────────────────────────────────────────────────────── *)

#push-options "--fuel 1 --ifuel 1"

let rec append_assoc (#a: Type0) (xs ys zs: list a)
  : Lemma (ensures append (append xs ys) zs == append xs (append ys zs)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> append_assoc rest ys zs

let rec append_nil (#a: Type0) (xs: list a) : Lemma (ensures append xs [] == xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> append_nil rest

/// **`chain_go_bounded`.** A computed continuation ran at most `fuel` further messages, and at least the
/// `count` already run.
let rec chain_go_bounded (#m #md #e: Type0)
                         (update: md -> m -> pair md (list (step m e)))
                         (fuel: nat) (pending: list m) (model: md) (effs: list e) (count: nat)
  : Lemma (ensures (match chain_go update fuel pending model effs count with
                    | Computed _ _ n -> count <= n /\ n <= count + fuel
                    | TooLong -> True))
          (decreases fuel) =
  match pending with
  | [] -> ()
  | x :: rest ->
      if fuel = 0 then ()
      else
        let Pair model' steps = update model x in
        chain_go_bounded update (fuel - 1) (append rest (follow_ups_of steps)) model' (append effs (effects_of steps)) (count + 1)

/// **`chain_messages_bounded`.** A computed chain ran at most `fuel` messages through `update`: the
/// bound is what it says.
let chain_messages_bounded (#m #md #e: Type0)
                           (update: md -> m -> pair md (list (step m e)))
                           (fuel: nat) (model: md) (msg: m)
  : Lemma (ensures (match chain update fuel model msg with
                    | Computed _ _ n -> 1 <= n /\ n <= fuel
                    | TooLong -> True)) =
  if fuel = 0 then ()
  else
    let Pair model' steps = update model msg in
    chain_go_bounded update (fuel - 1) (follow_ups_of steps) model' (effects_of steps) 1

/// **`chain_go_monotone`.** A continuation that fits under `fuel` is the same under any larger bound.
let rec chain_go_monotone (#m #md #e: Type0)
                          (update: md -> m -> pair md (list (step m e)))
                          (fuel more: nat) (pending: list m) (model: md) (effs: list e) (count: nat)
  : Lemma (requires Computed? (chain_go update fuel pending model effs count))
          (ensures chain_go update (fuel + more) pending model effs count ==
                   chain_go update fuel pending model effs count)
          (decreases fuel) =
  match pending with
  | [] -> ()
  | x :: rest ->
      let Pair model' steps = update model x in
      chain_go_monotone update (fuel - 1) more (append rest (follow_ups_of steps)) model' (append effs (effects_of steps)) (count + 1)

/// **`chain_fuel_monotone`.** A chain that fits under a bound is exactly the same chain under any larger
/// bound: the bound never changes what a chain computes, it only refuses chains that do not fit.
let chain_fuel_monotone (#m #md #e: Type0)
                        (update: md -> m -> pair md (list (step m e)))
                        (fuel more: nat) (model: md) (msg: m)
  : Lemma (requires Computed? (chain update fuel model msg))
          (ensures chain update (fuel + more) model msg == chain update fuel model msg) =
  let Pair model' steps = update model msg in
  chain_go_monotone update (fuel - 1) more (follow_ups_of steps) model' (effects_of steps) 1

/// The effects a continuation computes, as a list: the effects so far, then every `Do` of every message
/// run, in processing order. A specification helper, not extracted.
[@@ noextract_to "FSharp"]
let rec effects_run (#m #md #e: Type0)
                    (update: md -> m -> pair md (list (step m e)))
                    (fuel: nat) (pending: list m) (model: md)
  : Tot (list e) (decreases fuel) =
  match pending with
  | [] -> []
  | x :: rest ->
      if fuel = 0 then []
      else
        let Pair model' steps = update model x in
        append (effects_of steps) (effects_run update (fuel - 1) (append rest (follow_ups_of steps)) model')

/// **`chain_go_effects`.** A computed continuation's effects are the effects so far followed by the
/// effects of every message it ran, in order: nothing is dropped, reordered or added along the chain.
let rec chain_go_effects (#m #md #e: Type0)
                         (update: md -> m -> pair md (list (step m e)))
                         (fuel: nat) (pending: list m) (model: md) (effs: list e) (count: nat)
  : Lemma (ensures (match chain_go update fuel pending model effs count with
                    | Computed _ out _ -> out == append effs (effects_run update fuel pending model)
                    | TooLong -> True))
          (decreases fuel) =
  match pending with
  | [] -> append_nil effs
  | x :: rest ->
      if fuel = 0 then ()
      else
        let Pair model' steps = update model x in
        chain_go_effects update (fuel - 1) (append rest (follow_ups_of steps)) model' (append effs (effects_of steps)) (count + 1);
        append_assoc effs (effects_of steps) (effects_run update (fuel - 1) (append rest (follow_ups_of steps)) model')

/// **`chain_whole`.** A computed chain's effects are the first message's effects followed by those of
/// every follow-up it ran, in processing order, as one list — the list the step machine hands over
/// whole or not at all.
let chain_whole (#m #md #e: Type0)
                (update: md -> m -> pair md (list (step m e)))
                (fuel: nat) (model: md) (msg: m)
  : Lemma (requires fuel > 0)
          (ensures (match chain update fuel model msg with
                    | Computed _ out _ ->
                        (let Pair model' steps = update model msg in
                         out == append (effects_of steps) (effects_run update (fuel - 1) (follow_ups_of steps) model'))
                    | TooLong -> True)) =
  let Pair model' steps = update model msg in
  chain_go_effects update (fuel - 1) (follow_ups_of steps) model' (effects_of steps) 1

#pop-options
