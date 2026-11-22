// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Browser

open System
open Fuaran.MVU

/// The browser host (DECISIONS.md D30.7). The browser's host premise is the re-entrancy latch, which the runtime owns
/// (D26.2): a dispatch made while a step runs is queued on the kernel's growing queue (`DrainBuffer`) and processed in
/// the same drain. What this host adds is where a yielded drain's continuation runs and where the reports go.
module BrowserHost =

    /// A report as a sentence for the developer, or `None` for one that is the runtime working as designed: a yield,
    /// and an envelope refused at acceptance (a late answer from a revoked source, a dispatch after termination).
    let describe (report: Report<'msg, 'effect, 'resource>) : string option =
        match report with
        | Report.PerformerThrew(cause, effect, ex) ->
            Some(sprintf "Fuaran.MVU: the performer of %A (step %d) threw: %s" effect cause ex.Message)
        | Report.StepRefused(step, refusal) ->
            Some(sprintf "Fuaran.MVU: step %d was refused: %A" (StepOf.sequence step) refusal)
        | ResourceLost(key, resource, ex) ->
            Some(sprintf "Fuaran.MVU: the resource %A at %A could not start: %s" resource key ex.Message)
        | StopThrew(key, ex) -> Some(sprintf "Fuaran.MVU: stopping the resource at %A threw: %s" key ex.Message)
        | ResourcesThrew ex -> Some(sprintf "Fuaran.MVU: the component's resources threw: %s" ex.Message)
        | EnvelopeNotAccepted _
        | Yielded _ -> None

    /// A host for the browser: the runtime on a growing queue (the kernel's `DrainBuffer`), under the latch; a yielded
    /// drain's continuation run in a task of its own (`Dom.Delay 0`), which the runtime schedules once its latch is
    /// released, at most one pending, and opening a drain only if the queue still holds work (a drain opened since
    /// the yield may have taken it, and an empty drain would render again); every report `describe` words written to
    /// `Dom.Error`. The rest is `Host.create`'s: the allow-all decision, until the application sets the gate's, the
    /// default fuel, and no record sink.
    let create
        (dom: Dom)
        (clock: unit -> int64)
        (seed: uint64)
        (render: 'view -> unit)
        : Host<'model, 'msg, 'effect, 'resource, 'view> =
        let queue = Queue.growing 16
        let mutable pending = false

        {
            Host.create clock seed render with
                Queue = queue
                Schedule =
                    fun continuation ->
                        if not pending then
                            pending <- true

                            dom.Delay 0 (fun () ->
                                pending <- false

                                if not (Queue.isEmpty queue) then
                                    continuation ())
                Report =
                    fun report ->
                        match describe report with
                        | Some sentence -> dom.Error sentence
                        | None -> ()
        }

    /// Mount a component on a host: the runtime built and started (its boot paint, the boot step, the boot drain).
    let mount
        (host: Host<'model, 'msg, 'effect, 'resource, 'view>)
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (interpreter: Interpreter<'msg, 'effect, 'resource>)
        (flags: 'flags)
        : Runtime<'flags, 'model, 'msg, 'effect, 'resource, 'view> =
        let runtime = Runtime(comp, interpreter, host, flags)
        runtime.Start()
        runtime

/// The live binding to the browser (DECISIONS.md D30.5, D30.8): the only code in this package that touches the DOM,
/// and so the only part that runs under Fable alone. Every function is inline, so the DOM's globals are used in the
/// module that calls them; this package's own modules use none, and the laws run under Node with a stand-in.
module Live =

    open Fable.Core.JsInterop

    /// The browser's DOM. A node address is an element id (`document.getElementById`); a view that addresses its
    /// nodes another way replaces `Resolve`.
    let inline dom () : Dom =
        let storageOf (area: StorageArea) : obj =
            match area with
            | StorageArea.Local -> emitJsExpr () "localStorage"
            | StorageArea.Session -> emitJsExpr () "sessionStorage"

        {
            WriteClipboard = fun text -> emitJsStatement text "navigator.clipboard.writeText($0).catch(function () {})"
            Assign = fun url -> emitJsStatement url "window.location.assign($0)"
            OpenBlank = fun url -> emitJsStatement url "window.open($0, '_blank', 'noopener,noreferrer')"
            PushState = fun route -> emitJsStatement route "window.history.pushState(null, '', $0)"
            Resolve =
                fun nodeId ->
                    let element: obj = emitJsExpr nodeId "document.getElementById($0)"
                    if isNull element then None else Some element
            Focus = fun element -> emitJsStatement element "$0.focus()"
            Download =
                fun url name ->
                    emitJsStatement
                        (url, name)
                        "var a = document.createElement('a'); a.href = $0; a.download = $1; a.rel = 'noopener'; document.body.appendChild(a); a.click(); a.remove()"
            ReadFile =
                fun element encoding deliver ->
                    let file: obj =
                        emitJsExpr element "($0.files && $0.files.length > 0) ? $0.files[0] : null"

                    if isNull file then
                        deliver (Error NoFile)
                    else
                        let onBody (body: string) = deliver (Ok body)
                        let onError (reason: string) = deliver (Error(ReadFailed reason))

                        emitJsStatement
                            (file, encoding, onBody, onError)
                            "var r = new FileReader(); r.onload = function () { var v = r.result; $2($1 === 'Base64' ? v.substring(v.indexOf(',') + 1) : v); }; r.onerror = function () { $3(String(r.error)); }; if ($1 === 'Text') { r.readAsText($0); } else { r.readAsDataURL($0); }"
            Print = fun () -> emitJsStatement () "window.print()"
            Confirm = fun prompt -> emitJsExpr prompt "window.confirm($0)"
            StorageGet =
                fun area key ->
                    let value: obj = emitJsExpr (storageOf area, key) "$0.getItem($1)"
                    if isNull value then None else Some(unbox<string> value)
            StorageSet = fun area key value -> emitJsStatement (storageOf area, key, value) "$0.setItem($1, $2)"
            StorageRemove = fun area key -> emitJsStatement (storageOf area, key) "$0.removeItem($1)"
            Delay = fun milliseconds callback -> emitJsStatement (callback, milliseconds) "setTimeout($0, $1)"
            Repeat =
                fun milliseconds callback ->
                    let id: obj = emitJsExpr (callback, milliseconds) "setInterval($0, $1)"

                    { new IDisposable with
                        member _.Dispose() = emitJsStatement id "clearInterval($0)"
                    }
            Error = fun message -> emitJsStatement message "console.error($0)"
        }

    /// A browser host over the live DOM, with the browser's clock (`Date.now()`) and a seed drawn from
    /// `crypto.getRandomValues`.
    let inline host (render: 'view -> unit) : Host<'model, 'msg, 'effect, 'resource, 'view> =
        let clock () : int64 =
            int64 (emitJsExpr<float> () "Date.now()")

        let seed: uint64 =
            let parts: float[] =
                emitJsExpr () "Array.from(crypto.getRandomValues(new Uint32Array(2)))"

            (uint64 parts.[0] <<< 32) ||| uint64 parts.[1]

        BrowserHost.create (dom ()) clock seed render
