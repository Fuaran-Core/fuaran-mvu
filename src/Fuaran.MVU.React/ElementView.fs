// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.React

open Fable.React
open Fuaran.MVU

/// The mark a governed surface demands of a component's view (the design's §10, stage V7): a value a model can read
/// and a gate can check before anything is drawn. A tree will carry it when the governed surface lands. A React
/// element never can, because nothing in it can be read until React has drawn it (DECISIONS.md D29.5).
type IGovernedView = interface end

/// A view that is a React element: the element-view bridge (DECISIONS.md D29.5). It is how a view written against
/// React (forge's present views, built with Feliz) runs on the runtime until it is a tree. The element is built
/// against the mount's dispatch when React renders, not when the runtime does. Sealed, and it does not carry
/// `IGovernedView`, so `Governed.admit` refuses a component whose view is one at compile time.
[<Sealed; NoEquality; NoComparison>]
type ElementView<'msg>(build: ('msg -> unit) -> ReactElement) =

    /// The element, built against `dispatch`.
    member _.Build(dispatch: 'msg -> unit) : ReactElement = build dispatch

module ElementView =

    /// A view that is one element and dispatches nothing.
    let ofElement (element: ReactElement) : ElementView<'msg> = ElementView(fun _ -> element)

    /// A view in forge's present shape, `model -> dispatch -> element`, as a component's `View`.
    let ofView (view: 'model -> ('msg -> unit) -> ReactElement) : 'model -> ElementView<'msg> =
        fun model -> ElementView(view model)

    /// How the mount turns an element view into the element React renders.
    let toElement (view: ElementView<'msg>) (dispatch: 'msg -> unit) : ReactElement = view.Build dispatch

module Governed =

    /// The admission a governed-surface mount makes of a component: its view must carry `IGovernedView`. It checks
    /// nothing at run time; the constraint is the check, so a component whose view is an `ElementView` does not
    /// compile here (the gate builds that case and requires the refusal, DECISIONS.md D29.5).
    let admit<'flags, 'model, 'msg, 'effect, 'resource, 'view when 'view :> IGovernedView>
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        : Component<'flags, 'model, 'msg, 'effect, 'resource, 'view> =
        comp
