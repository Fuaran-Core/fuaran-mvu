// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.React

open System

/// What a mounted instance hands the instance that replaces it on a hot reload: its model, the last sequence it
/// used, and the shape of its model's type.
type Carried = {
    Model: obj
    Sequence: int64
    Shape: string
}

/// Hot reload (DECISIONS.md D29.7). A mount that names a key registers itself under it; a later mount under the same
/// key (the application's entry module run again by the bundler) unmounts the registered instance and takes its
/// model, and resumes from it when the model's type has the shape it had, or starts fresh from `Init` when it does
/// not. Compiled only under `DEBUG`: Fable defines it in watch mode (the development server) and not in a build,
/// whose default configuration is Release, so a production bundle carries none of this. In a build without
/// `DEBUG` every function here is the inert one at the bottom, and `Marker`, the string the gate looks for in a
/// production bundle, appears nowhere in it.
module HotReload =

#if DEBUG
    open System.Collections.Generic
    open Microsoft.FSharp.Reflection

    /// The registry's name, and the string a production bundle must not carry.
    [<Literal>]
    let Marker = "fuaran-mvu-react:hot-reload"

    /// Whether this build carries hot reload.
    let enabled = true

    // The registry: key to the registered instance's dispose. In the browser it lives on `globalThis`, so it
    // survives a reload that evaluates this module again.
    let private registry: Dictionary<string, unit -> Carried> =
#if FABLE_COMPILER
        let slot: obj = Fable.Core.JsInterop.emitJsExpr Marker "globalThis[$0]"

        if isNull slot then
            let fresh = Dictionary<string, unit -> Carried>()
            Fable.Core.JsInterop.emitJsStatement (Marker, fresh) "globalThis[$0] = $1"
            fresh
        else
            unbox slot
#else
        Dictionary<string, unit -> Carried>()
#endif

    /// The shape of a model's type: its name, and for a record its fields, for a union its cases with their fields,
    /// each field's type in turn (to a fixed depth, a recursive type cut at its name). Two builds whose model types
    /// have one shape can carry a model from one to the other.
    let shapeOf (t: Type) : string =
        let rec shape (depth: int) (seen: string list) (t: Type) : string =
            let name = t.FullName

            if depth > 6 || List.contains name seen then
                name
            elif FSharpType.IsRecord t then
                let fields =
                    FSharpType.GetRecordFields t
                    |> Array.map (fun f -> f.Name + ":" + shape (depth + 1) (name :: seen) f.PropertyType)

                name + "{" + String.Join(";", fields) + "}"
            elif FSharpType.IsUnion t then
                let cases =
                    FSharpType.GetUnionCases t
                    |> Array.map (fun c ->
                        let fields =
                            c.GetFields()
                            |> Array.map (fun f -> f.Name + ":" + shape (depth + 1) (name :: seen) f.PropertyType)

                        c.Name + "(" + String.Join(";", fields) + ")")

                name + "<" + String.Join("|", cases) + ">"
            else
                name

        shape 0 [] t

    /// Take the instance registered under `key`: unmount it and answer what it carried, or `None` if nothing is
    /// registered.
    let take (key: string) : Carried option =
        match registry.TryGetValue key with
        | true, dispose ->
            registry.Remove key |> ignore
            Some(dispose ())
        | _ -> None

    /// Register an instance's dispose under `key`.
    let register (key: string) (dispose: unit -> Carried) : unit = registry[key] <- dispose

    /// Drop the registration under `key` if it is still `dispose`'s: an instance unmounted by the application.
    let forget (key: string) (dispose: unit -> Carried) : unit =
        match registry.TryGetValue key with
        | true, registered when obj.ReferenceEquals(registered, dispose) -> registry.Remove key |> ignore
        | _ -> ()
#else
    /// Whether this build carries hot reload: not in this one.
    let enabled = false

    /// No shape is read in a build without hot reload.
    let shapeOf (_: Type) : string = ""

    /// Nothing is ever registered.
    let take (_: string) : Carried option = None

    let register (_: string) (_: unit -> Carried) : unit = ()

    let forget (_: string) (_: unit -> Carried) : unit = ()
#endif
