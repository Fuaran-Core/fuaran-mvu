// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU

/// One step an `update` asks for: perform an effect, or process a follow-up message next. `Then` lives
/// in the step type rather than in the application's effect type, so a built-in case never has to be
/// added to a union the application owns. The implicit conversion lets an effect stand where a step is
/// expected (`[ LoadTree id ]` reads as `[ Do (LoadTree id) ]`); it applies per element, where the
/// expected type is known, and never to a whole list already inferred as a list of effects.
type Step<'msg, 'effect> =
    | Do of 'effect
    | Then of 'msg

    /// An effect where a step is expected is `Do` of it.
    static member op_Implicit(effect: 'effect) : Step<'msg, 'effect> = Do effect

/// Functions over steps: the mappings a parent applies when it lifts a child, and the two projections.
module Step =

    /// Map a step's message and effect.
    let map (fm: 'a -> 'b) (fe: 'e -> 'f) (step: Step<'a, 'e>) : Step<'b, 'f> =
        match step with
        | Do effect -> Do(fe effect)
        | Then msg -> Then(fm msg)

    /// Map every step of a list.
    let mapAll (fm: 'a -> 'b) (fe: 'e -> 'f) (steps: Step<'a, 'e> list) : Step<'b, 'f> list = List.map (map fm fe) steps

    /// Map a step through a function that may turn a child's effect into a parent's step of either case:
    /// an up-call from a child becomes a parent `Then` while it is lifted.
    let bind (fm: 'a -> 'b) (fe: 'e -> Step<'b, 'f>) (step: Step<'a, 'e>) : Step<'b, 'f> =
        match step with
        | Do effect -> fe effect
        | Then msg -> Then(fm msg)

    /// The effects of a step list, in order.
    let effects (steps: Step<'msg, 'effect> list) : 'effect list =
        steps
        |> List.choose (fun step ->
            match step with
            | Do effect -> Some effect
            | Then _ -> None)

    /// The follow-up messages of a step list, in order.
    let followUps (steps: Step<'msg, 'effect> list) : 'msg list =
        steps
        |> List.choose (fun step ->
            match step with
            | Then msg -> Some msg
            | Do _ -> None)

/// The effect type of a component that performs nothing. F# has no empty union, and an unconstrained
/// type parameter at a use site is an error (FS0030); this one-case union is the closed type to name.
type NoEffect = | NoEffect

/// The resource type of a component that holds nothing.
type NoResource = | NoResource
