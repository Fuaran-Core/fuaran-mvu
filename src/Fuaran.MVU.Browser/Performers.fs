// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Browser

open System
open Fuaran.ClientEffects
open Fuaran.MVU

/// The browser as the performers use it (DECISIONS.md D30.5): every call they make into the DOM, as a value, so the
/// laws run against a stand-in on .NET and under Node, and an application passes `Live.dom ()`. `Resolve` is how a
/// node address becomes an element (D30.4): the live binding looks the address up as an element id, and a view that
/// addresses its nodes another way replaces it.
type Dom = {
    /// Write text to the clipboard.
    WriteClipboard: string -> unit
    /// Navigate this browsing context to a URL: a full page load.
    Assign: string -> unit
    /// Open a URL in a fresh browsing context with no opener and no referrer.
    OpenBlank: string -> unit
    /// Push a history entry for a route, with no reload.
    PushState: string -> unit
    /// The element a node address names, if any.
    Resolve: string -> obj option
    /// Move focus to an element.
    Focus: obj -> unit
    /// Download a URL under a suggested file name.
    Download: string -> string -> unit
    /// Read the file selected at an element with an encoding (`Text`, `Base64`, `DataUrl`), answering once.
    ReadFile: obj -> string -> (Result<string, FileReadFailure> -> unit) -> unit
    /// Open the reader's print dialogue.
    Print: unit -> unit
    /// Ask the reader a question; `true` for OK.
    Confirm: string -> bool
    /// What a key holds in a storage area.
    StorageGet: StorageArea -> string -> string option
    /// Write a key in a storage area.
    StorageSet: StorageArea -> string -> string -> unit
    /// Remove a key from a storage area.
    StorageRemove: StorageArea -> string -> unit
    /// Run a callback once, after a delay in milliseconds, in a task of its own.
    Delay: int -> (unit -> unit) -> unit
    /// Run a callback every interval in milliseconds until the answer is disposed.
    Repeat: int -> (unit -> unit) -> IDisposable
    /// Tell the developer something went wrong: the browser host's reports go here.
    Error: string -> unit
}

/// The performers, one per standard kind (DECISIONS.md D30.3). Each is a one-shot answer (`Reply`, delivered once) or
/// none (`Signal`); the repeating timer is a resource. A performer is handed out only for a step the gate has decided
/// and the runtime has committed: registering it permits nothing.
module Performers =

    /// A callback that delivers at most once, whatever its caller does.
    let private once (deliver: 'msg -> unit) : 'msg -> unit =
        let mutable delivered = false

        fun msg ->
            if not delivered then
                delivered <- true
                deliver msg

    /// Refuse a URL whose scheme runs script (`javascript:`), which a navigation or a download would execute in the
    /// page. The refusal throws, so the runtime reports the performer and goes on with the step's next effect.
    let private refuseScript (what: string) (url: string) =
        if url.Trim().ToLowerInvariant().StartsWith "javascript:" then
            invalidOp (sprintf "%s: a javascript: URL is refused" what)

    /// The clipboard: write the text. No answer.
    let writeToClipboard (dom: Dom) (text: string) : Performance<'msg> =
        Signal(fun () -> dom.WriteClipboard text)

    /// Navigation: this browsing context, or a fresh one with no opener and no referrer. No answer.
    let navigate (dom: Dom) (route: string) (target: NavigateTarget) : Performance<'msg> =
        Signal(fun () ->
            refuseScript "Navigate" route

            match target with
            | NavigateTarget.Self -> dom.Assign route
            | NavigateTarget.Blank -> dom.OpenBlank route)

    /// History: push an entry for the route, with no reload. No answer.
    let pushState (dom: Dom) (route: string) : Performance<'msg> = Signal(fun () -> dom.PushState route)

    /// Focus: move focus to the addressed element; an address that resolves to nothing does nothing. No answer.
    let focus (dom: Dom) (nodeId: string) : Performance<'msg> =
        Signal(fun () ->
            match dom.Resolve nodeId with
            | Some element -> dom.Focus element
            | None -> ())

    /// Download: the URL under the suggested name. No answer.
    let download (dom: Dom) (url: string) (name: string) : Performance<'msg> =
        Signal(fun () ->
            refuseScript "Download" url
            dom.Download url name)

    /// File read: one answer, the body of the file selected at the addressed element, or why it was not read.
    let readFileBody
        (dom: Dom)
        (answer: StandardAnswer -> 'msg)
        (nodeId: string)
        (encoding: string)
        : Performance<'msg> =
        Reply(fun deliver ->
            let deliver = once (fun body -> deliver (answer (FileBody(nodeId, encoding, body))))

            if not (List.contains encoding ClientEffect.readEncodings) then
                deliver (Error UnknownEncoding)
            else
                match dom.Resolve nodeId with
                | None -> deliver (Error NoNode)
                | Some element -> dom.ReadFile element encoding deliver)

    /// Print: the reader's own dialogue. No answer.
    let print (dom: Dom) : Performance<'msg> = Signal(fun () -> dom.Print())

    /// Confirm: one answer, the reader's yes or no, carrying the confirmation's token back.
    let confirm (dom: Dom) (answer: StandardAnswer -> 'msg) (prompt: string) (token: string) : Performance<'msg> =
        Reply(fun deliver -> deliver (answer (Confirmed(token, dom.Confirm prompt))))

    /// Storage read: one answer, what the key holds.
    let readStorage (dom: Dom) (answer: StandardAnswer -> 'msg) (area: StorageArea) (key: string) : Performance<'msg> =
        Reply(fun deliver -> deliver (answer (Stored(area, key, dom.StorageGet area key))))

    /// Storage write. No answer.
    let writeStorage (dom: Dom) (area: StorageArea) (key: string) (value: string) : Performance<'msg> =
        Signal(fun () -> dom.StorageSet area key value)

    /// Storage removal. No answer.
    let removeStorage (dom: Dom) (area: StorageArea) (key: string) : Performance<'msg> =
        Signal(fun () -> dom.StorageRemove area key)

    /// The one-shot timer: one answer, after the delay.
    let after (dom: Dom) (answer: StandardAnswer -> 'msg) (label: string) (milliseconds: int) : Performance<'msg> =
        Reply(fun deliver ->
            let deliver = once deliver
            dom.Delay milliseconds (fun () -> deliver (answer (Elapsed label))))

    /// The interpreter's line for a standard effect: its invocation and its performer, from one function so the two
    /// cannot disagree.
    let handle (dom: Dom) (answer: StandardAnswer -> 'msg) (effect: StandardEffect) : Handled<'msg> = {
        Invocation = Standard.invocation effect
        Perform =
            fun () ->
                match effect with
                | Client(WriteToClipboard text) -> writeToClipboard dom text
                | Client(Navigate(route, target)) -> navigate dom route target
                | Client(PushState route) -> pushState dom route
                | Client(Focus nodeId) -> focus dom nodeId
                | Client(Download(url, name)) -> download dom url name
                | Client(ReadFileBody(nodeId, encoding)) -> readFileBody dom answer nodeId encoding
                | Client Print -> print dom
                | Client(Confirm(prompt, token)) -> confirm dom answer prompt token
                | ReadStorage(area, key) -> readStorage dom answer area key
                | WriteStorage(area, key, value) -> writeStorage dom area key value
                | RemoveStorage(area, key) -> removeStorage dom area key
                | After(label, milliseconds) -> after dom answer label milliseconds
    }

    /// The interpreter's line for the standard resource: the repeating timer, started with its emitter and stopped
    /// when the model stops listing it.
    let start
        (dom: Dom)
        (answer: StandardAnswer -> 'msg)
        (key: ResourceKey)
        (resource: StandardResource)
        : Started<'msg> =
        let (Every(label, milliseconds)) = resource

        {
            Invocation = Standard.resourceInvocation key resource
            Start = fun emit -> dom.Repeat milliseconds (fun () -> emit (answer (Ticked label)))
        }

/// The standard vocabulary as an interpreter (DECISIONS.md D30.6, and the design's §26 question 10).
module StandardInterpreter =

    /// The interpreter of an application whose effects are the standard vocabulary and nothing else: it writes
    /// nothing for them but the one function from a standard answer to its message.
    let create (dom: Dom) (answer: StandardAnswer -> 'msg) : Interpreter<'msg, StandardEffect, StandardResource> =
        Interpreter.create Standard.kinds (Performers.handle dom answer) (Performers.start dom answer)

    /// Embed the standard vocabulary as one case of an application's effect type (and, if it wants the repeating
    /// timer, of its resource type): the application's own interpreter with the standard kinds registered before its
    /// own, a standard effect handed to the standard performers, and everything else to its own. `effectCase` and
    /// `resourceCase` read the case back (`function Browser e -> Some e | _ -> None`); they are the interpreter's
    /// whole line for the standard vocabulary. The own interpreter's hooks are kept.
    let embed
        (dom: Dom)
        (answer: StandardAnswer -> 'msg)
        (effectCase: 'effect -> StandardEffect option)
        (resourceCase: 'resource -> StandardResource option)
        (own: Interpreter<'msg, 'effect, 'resource>)
        : Interpreter<'msg, 'effect, 'resource> =
        {
            own with
                Kinds = Standard.kinds @ own.Kinds
                Effect =
                    fun effect ->
                        match effectCase effect with
                        | Some standard -> Performers.handle dom answer standard
                        | None -> own.Effect effect
                Resource =
                    fun key resource ->
                        match resourceCase resource with
                        | Some standard -> Performers.start dom answer key standard
                        | None -> own.Resource key resource
        }
