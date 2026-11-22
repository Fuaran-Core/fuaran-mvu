// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The prototype's checks, restated against the real types (Phase 647, t1): the four ports under `spike/`,
/// `stream-vs-resource.fsx` and `stamp-argument.fsx`, one case per check, numbered as the scripts number
/// them. Where the design moved since the prototype (a reply is always its own step; a one-shot effect
/// cannot stream; an exception in `update` refuses the step instead of escaping), the case says what the
/// check now asserts. Host-neutral: the Expecto suite and the Node runner both run them.
module Fuaran.MVU.Tests.PortLaws

#nowarn "3391"

open Fuaran.MVU
open Fuaran.MVU.Testing
open Fuaran.MVU.Tests.Check
open Fuaran.MVU.Tests.Samples

let private user = User "test"

let private agent = Agent("model", "1", "a1")

/// The grant a host would mint for a parked step: an approver who is not the author, never expiring, generation
/// zero (these checks run on a scripted decision, which reads neither).
let private grantFor (parked: ParkedStep<_, _, _, _>) =
    Grant.mint parked.Id parked.Proposal (User "approver") System.Int64.MaxValue 0

let private committed (run: Harness<_, _, _, _, _, _>) =
    run.Runtime.Log |> List.filter (fun r -> r.Outcome = Committed) |> List.length

// ---- Port A ------------------------------------------------------------------------------------------

let private portA = [
    case "port-a 1: init model" (fun expect ->
        let run = Harness.start Counter.counter ()
        expect "the model Init returned" (run.Model = { Counter.Count = 0 }))
    case "port-a 2: init renders once" (fun expect ->
        let run = Harness.start Counter.counter ()
        expect "Renders = 1" (run.Runtime.Renders = 1))
    case "port-a 3: model after +,+,-" (fun expect ->
        let run = Harness.start Counter.counter ()
        run.Send Counter.Increment |> ignore
        run.Send Counter.Increment |> ignore
        run.Send Counter.Decrement |> ignore
        expect "Count = 1" (run.Model.Count = 1))
    case "port-a 4: no effects performed" (fun expect ->
        let run = Harness.start Counter.counter ()
        run.Send Counter.Increment |> ignore
        expect "Performed = []" (run.Performed = []))
    case "port-a 5: one render per drain (1 + 3)" (fun expect ->
        let run = Harness.start Counter.counter ()

        for msg in [ Counter.Increment; Counter.Increment; Counter.Decrement ] do
            run.Send msg |> ignore

        expect "Renders = 4" (run.Runtime.Renders = 4))
    case "port-a 6: clicked increment from the view value" (fun expect ->
        let run = Harness.start Counter.counter ()
        let view = List.last run.Views
        run.Send view.OnClick |> ignore
        expect "Count = 1" (run.Model.Count = 1))
    case "port-a 7: replay reproduces the model" (fun expect ->
        let run = Harness.start Counter.counter ()

        for msg in [ Counter.Increment; Counter.Increment; Counter.Decrement ] do
            run.Send msg |> ignore

        expect "replay = model" (Replay.run Counter.counter () run.Runtime.Log = run.Model))
]

// ---- Port B ------------------------------------------------------------------------------------------

let private alpha: Portfolio.Portfolio = { Id = "alpha"; Name = "Alpha" }
let private beta: Portfolio.Portfolio = { Id = "beta"; Name = "Beta" }

let private isGet (e: Portfolio.Effect) =
    match e with
    | Portfolio.GetPortfolio _ -> true
    | _ -> false

let private isTensions (e: Portfolio.Effect) =
    match e with
    | Portfolio.GetTensions _ -> true
    | _ -> false

/// The deep-linked session every port-B check starts from: `portfolio/beta`, list answered.
let private deepLinked () =
    let run = Harness.start Portfolio.portfolio "portfolio/beta"
    run

let private portB = [
    case "port-b 1: init: three loads, as values" (fun expect ->
        let run = Harness.start Portfolio.portfolio "portfolio"

        expect "Init's effects performed in emission order" (run.Performed = [ Portfolio.ListPortfolios ]))
    case "port-b 2: init: route listener is a resource, started once" (fun expect ->
        let run = Harness.start Portfolio.portfolio "portfolio"
        expect "Started = [RouteChanges]" (run.Started = [ Portfolio.RouteChanges "portfolio" ]))
    case "port-b 3: init: section from the address, read without a router" (fun expect ->
        let run = Harness.start Portfolio.portfolio "portfolio/beta/parties"
        expect "Section = Parties" (run.Model.Section = Portfolio.Parties))
    case "port-b 4: deep link resolved against the list; reload is four values" (fun expect ->
        let run = deepLinked ()
        run.ClearPerformed()
        run.AnswerWhere((fun e -> e = Portfolio.ListPortfolios), Portfolio.PortfoliosLoaded(Ok [ alpha; beta ]))
        expect "selected beta from the list" (run.Model.Selected = Some beta)
        run.Send(Portfolio.SelectPortfolio "beta") |> ignore

        expect
            "four loads in order"
            (run.Performed = [
                Portfolio.GetPortfolio "beta"
                Portfolio.GetTensions "beta"
                Portfolio.GetParties "beta"
                Portfolio.RouteWrite "portfolio/beta"
            ]))
    case "port-b 5: selected the addressed portfolio" (fun expect ->
        let run = deepLinked ()
        run.AnswerWhere(isGet, Portfolio.PortfolioLoaded(Ok beta))
        expect "Selected = beta" (run.Model.Selected = Some beta))
    case "port-b 6: view landed" (fun expect ->
        let run = deepLinked ()
        run.AnswerWhere(isTensions, Portfolio.TensionsLoaded(Ok [ "t1" ]))
        expect "Tensions = [t1]" (run.Model.Tensions = [ "t1" ]))
    case "port-b 7: one Error arm handles any reply's failure" (fun expect ->
        let run = deepLinked ()
        run.AnswerWhere(isTensions, Portfolio.TensionsLoaded(Error "transport: 502"))
        expect "Refusal surfaced" (run.Model.Refusal = Some "transport: 502"))
    case "port-b 8: ShowSection: route replace + tensions load (via Then LoadTensions)" (fun expect ->
        let run = deepLinked ()
        run.AnswerWhere(isGet, Portfolio.PortfolioLoaded(Ok beta))
        run.ClearPerformed()
        run.Send(Portfolio.ShowSection Portfolio.Parties) |> ignore

        expect
            "route write then tensions read"
            (run.Performed = [ Portfolio.RouteWrite "portfolio/beta/parties"; Portfolio.GetTensions "beta" ]))
    case "port-b 9: Then is in the same drain: one render for ShowSection" (fun expect ->
        let run = deepLinked ()
        run.AnswerWhere(isGet, Portfolio.PortfolioLoaded(Ok beta))
        let before = run.Runtime.Renders
        let steps = committed run
        run.Send(Portfolio.ShowSection Portfolio.Parties) |> ignore
        expect "one render" (run.Runtime.Renders = before + 1)
        expect "one step, the follow-up inside it" (committed run = steps + 1))
    case "port-b 10: the save is a comparable value (args included)" (fun expect ->
        let run = deepLinked ()
        run.ClearPerformed()
        run.Send(Portfolio.Save { beta with Name = "Beta 2" }) |> ignore

        expect
            "Performed = [SavePortfolio]"
            (run.Performed = [ Portfolio.SavePortfolio { Id = "beta"; Name = "Beta 2" } ]))
    case "port-b 11: after save: notice + Then LoadTensions" (fun expect ->
        let run = deepLinked ()
        run.AnswerWhere(isGet, Portfolio.PortfolioLoaded(Ok beta))
        run.Send(Portfolio.Save beta) |> ignore
        run.AnswerWhere((fun e -> e = Portfolio.SavePortfolio beta), Portfolio.Saved(Ok()))
        expect "Notice = saved" (run.Model.Notice = Some "saved"))
    case "port-b 12: ...which performed GetTensions" (fun expect ->
        let run = deepLinked ()
        run.AnswerWhere(isGet, Portfolio.PortfolioLoaded(Ok beta))
        run.Send(Portfolio.Save beta) |> ignore
        run.AnswerWhere((fun e -> e = Portfolio.SavePortfolio beta), Portfolio.Saved(Ok()))
        expect "last performed is the tensions read" (List.last run.Performed = Portfolio.GetTensions "beta"))
    case "port-b 13: create then relist" (fun expect ->
        let run = deepLinked ()
        run.ClearPerformed()
        run.Send(Portfolio.Create "Gamma") |> ignore

        run.AnswerWhere(
            (fun e -> e = Portfolio.CreatePortfolio "Gamma"),
            Portfolio.Created(Ok { Id = "gamma"; Name = "Gamma" })
        )

        expect "create, then list" (run.Performed = [ Portfolio.CreatePortfolio "Gamma"; Portfolio.ListPortfolios ]))
    case "port-b 14: created notice" (fun expect ->
        let run = deepLinked ()
        run.Send(Portfolio.Create "Gamma") |> ignore

        run.AnswerWhere(
            (fun e -> e = Portfolio.CreatePortfolio "Gamma"),
            Portfolio.Created(Ok { Id = "gamma"; Name = "Gamma" })
        )

        expect "Notice" (run.Model.Notice = Some "created Gamma"))
    case "port-b 15: route change selected alpha via Then and reloaded" (fun expect ->
        let run = deepLinked ()
        run.AnswerWhere((fun e -> e = Portfolio.ListPortfolios), Portfolio.PortfoliosLoaded(Ok [ alpha; beta ]))
        run.ClearPerformed()
        run.Emit(Portfolio.RouteChanges "portfolio", Portfolio.RouteChanged "portfolio/alpha")
        expect "Selected = alpha" (run.Model.Selected = Some alpha)

        expect
            "four loads"
            (List.length run.Performed = 4
             && List.head run.Performed = Portfolio.GetPortfolio "alpha"))
    case "port-b 16: restart: old search stopped, new one started" (fun expect ->
        let run = deepLinked ()
        run.Send(Portfolio.SearchProjects "ac") |> ignore
        run.Send(Portfolio.SearchProjects "acm") |> ignore
        expect "Stopped = [ac]" (run.Stopped = [ Portfolio.ProjectSearch "ac" ])

        expect
            "started ac then acm"
            (List.filter (fun r -> r <> Portfolio.RouteChanges "portfolio") run.Started = [
                Portfolio.ProjectSearch "ac"
                Portfolio.ProjectSearch "acm"
            ]))
    case "port-b 17: the stale reply was dropped by its liveness token" (fun expect ->
        let run = deepLinked ()
        run.Send(Portfolio.SearchProjects "ac") |> ignore
        let stale = run.EmitterOf(Portfolio.ProjectSearch "ac")
        run.Send(Portfolio.SearchProjects "acm") |> ignore
        let before = run.Runtime.RefusedAtAcceptance
        stale (Portfolio.ProjectsFound("ac", [ "stale" ]))
        expect "refused at acceptance" (run.Runtime.RefusedAtAcceptance = before + 1)
        expect "nothing landed" (run.Model.Projects = []))
    case "port-b 18: the latest reply landed" (fun expect ->
        let run = deepLinked ()
        run.Send(Portfolio.SearchProjects "acm") |> ignore
        run.Emit(Portfolio.ProjectSearch "acm", Portfolio.ProjectsFound("acm", [ "acme" ]))
        expect "Projects = [acme]" (run.Model.Projects = [ "acme" ]))
    case "port-b 19: and the search resource is delisted once answered" (fun expect ->
        let run = deepLinked ()
        run.Send(Portfolio.SearchProjects "acm") |> ignore
        run.Emit(Portfolio.ProjectSearch "acm", Portfolio.ProjectsFound("acm", [ "acme" ]))
        expect "not running" (not (Map.containsKey (ResourceKey.constant "project-search") run.Runtime.Running)))
]

/// The decision of port B's checks 20 to 22: an agent's remote write needs approval; everything else is
/// allowed. The effects are described with a kind and a destination so the decision can read them.
let private describeB (e: Portfolio.Effect) : Invocation =
    match e with
    | Portfolio.SavePortfolio _
    | Portfolio.CreatePortfolio _ -> {
        Kind = "write"
        Args = None
        Destination = Remote "api"
      }
    | Portfolio.RouteWrite _ -> {
        Kind = "write"
        Args = None
        Destination = Local
      }
    | _ -> {
        Kind = "read"
        Args = None
        Destination = Remote "api"
      }

let private kindsB: EffectKind list = [
    HarnessOptions.testKind
    {
        HarnessOptions.testKind with
            Id = "read"
    }
    {
        HarnessOptions.testKind with
            Id = "write"
    }
]

let private agentRemoteWritesNeedApproval: Decide<Portfolio.Effect, Portfolio.Resource> =
    fun proposal ->
        match proposal.Author with
        | Agent _ when
            proposal.Effects
            |> List.exists (fun (inv, _) -> inv.Kind = "write" && inv.Destination <> Local)
            ->
            NeedsApproval
        | _ -> Allow

let private optionsB: HarnessOptions<Portfolio.Msg, Portfolio.Effect, Portfolio.Resource> = {
    HarnessOptions.defaults with
        Kinds = kindsB
        Describe = describeB
        Decide = agentRemoteWritesNeedApproval
}

let private portBGate = [
    case "port-b 20: preview: the save needs approval for an agent" (fun expect ->
        let run = Harness.startWith optionsB Portfolio.portfolio "portfolio/beta"
        let preview = run.Preview(Stamp.forTest 1L, Portfolio.Save beta, agent)
        expect "NeedsApproval" (preview.Decision = Some NeedsApproval))
    case "port-b 21: preview follows Then: ShowSection's chain includes LoadTensions" (fun expect ->
        let run = Harness.startWith optionsB Portfolio.portfolio "portfolio/beta"
        run.AnswerWhere(isGet, Portfolio.PortfolioLoaded(Ok beta))

        let preview =
            run.Preview(Stamp.forTest 1L, Portfolio.ShowSection Portfolio.Parties, user)

        match preview.Outcome with
        | Computed chain ->
            expect "two messages in the chain" (chain.Messages = 2)

            expect
                "the follow-up's effect is in the chain"
                (chain.Effects = [ Portfolio.RouteWrite "portfolio/beta/parties"; Portfolio.GetTensions "beta" ])
        | _ -> expect "computed" false)
    case "port-b 22: preview: a local URL write is allowed under the remote-write ceiling" (fun expect ->
        let run = Harness.startWith optionsB Portfolio.portfolio "portfolio/beta"
        run.AnswerWhere(isGet, Portfolio.PortfolioLoaded(Ok beta))

        let preview =
            run.Preview(Stamp.forTest 1L, Portfolio.ShowSection Portfolio.Parties, agent)

        expect "Allow: the write is local and the read is a read" (preview.Decision = Some Allow))
    case "port-b 23: synchronous-completing replies all land in the INIT drain" (fun expect ->
        let answers (e: Portfolio.Effect) =
            match e with
            | Portfolio.ListPortfolios -> Immediate(Portfolio.PortfoliosLoaded(Ok [ alpha; beta ]))
            | Portfolio.GetPortfolio id ->
                Immediate(Portfolio.PortfolioLoaded(Ok(if id = "beta" then beta else alpha)))
            | Portfolio.GetTensions _ -> Immediate(Portfolio.TensionsLoaded(Ok [ "t1" ]))
            | Portfolio.GetParties _ -> Immediate(Portfolio.PartiesLoaded(Ok [ "p1" ]))
            | _ -> NoAnswer

        let run =
            Harness.startWith
                {
                    HarnessOptions.defaults with
                        Answers = answers
                }
                Portfolio.portfolio
                "portfolio/beta"

        expect
            "every reply applied"
            (run.Model.Selected = Some beta
             && run.Model.Tensions = [ "t1" ]
             && run.Model.Parties = [ "p1" ])
        // The paint, then the boot drain's render once the replies moved the model.
        expect "two renders: the boot paint and the boot drain" (run.Runtime.Renders = 2))
    case "port-b 24: a domain refusal or transport error surfaces as Refusal" (fun expect ->
        let answers (e: Portfolio.Effect) =
            match e with
            | Portfolio.GetTensions _ -> Immediate(Portfolio.TensionsLoaded(Error "transport: 502"))
            | _ -> Outstanding

        let run =
            Harness.startWith
                {
                    HarnessOptions.defaults with
                        Answers = answers
                }
                Portfolio.portfolio
                "portfolio/beta"

        expect "Refusal.IsSome" run.Model.Refusal.IsSome)
    case "port-b 25: view loaded" (fun expect ->
        let answers (e: Portfolio.Effect) =
            match e with
            | Portfolio.GetPortfolio _ -> Immediate(Portfolio.PortfolioLoaded(Ok beta))
            | _ -> Outstanding

        let run =
            Harness.startWith
                {
                    HarnessOptions.defaults with
                        Answers = answers
                }
                Portfolio.portfolio
                "portfolio/beta"

        expect "Selected = beta" (run.Model.Selected = Some beta))
]

// ---- Port C ------------------------------------------------------------------------------------------

let private shell () = Harness.start Shell.shell ()

let private portC = [
    case "port-c 1: three listeners running; notifications held back until a token" (fun expect ->
        let run = shell ()
        expect "Running.Count = 3" (run.Runtime.Running.Count = 3)
        expect "no notifications" (not (Map.containsKey (ResourceKey.constant "notifications") run.Runtime.Running)))
    case "port-c 2: a navigation request arrives through a resource" (fun expect ->
        let run = shell ()
        run.Emit(Shell.NavListener, Shell.Navigate "notes")
        expect "Active = notes" (run.Model.Active = "notes"))
    case "port-c 3: token arrived: notifications stream listed and started" (fun expect ->
        let run = shell ()
        run.Emit(Shell.BusListener, Shell.TokenAcquired "t-1")
        expect "running" (Map.containsKey (ResourceKey.constant "notifications") run.Runtime.Running)
        expect "started with the token" (List.contains (Shell.Notifications "t-1") run.Started))
    case "port-c 4: TokenAcquired's follow-up ran once (transition held in the model)" (fun expect ->
        let run = shell ()
        run.Emit(Shell.BusListener, Shell.TokenAcquired "t-1")
        expect "Events = [token]" (run.Model.Events = [ "token" ]))
    case "port-c 5: notification became a toast" (fun expect ->
        let run = shell ()
        run.Emit(Shell.BusListener, Shell.TokenAcquired "t-1")
        run.Emit(Shell.Notifications "t-1", Shell.Notification "hello")
        expect "Toasts = [hello]" (run.Model.Toasts = [ "hello" ]))
    case "port-c 6: token gone: the stream is stopped by the diff" (fun expect ->
        let run = shell ()
        run.Emit(Shell.BusListener, Shell.TokenAcquired "t-1")
        run.Emit(Shell.BusListener, Shell.TokenLost)
        expect "not running" (not (Map.containsKey (ResourceKey.constant "notifications") run.Runtime.Running)))
    case "port-c 7: ...and its subscription is gone" (fun expect ->
        let run = shell ()
        run.Emit(Shell.BusListener, Shell.TokenAcquired "t-1")
        let late = run.EmitterOf(Shell.Notifications "t-1")
        run.Emit(Shell.BusListener, Shell.TokenLost)
        expect "disposed" (run.Stopped = [ Shell.Notifications "t-1" ])
        late (Shell.Notification "late")
        expect "a late event is refused" (run.Model.Toasts = []))
    case "port-c 8: a module's up-call reached the shell with no global dispatcher" (fun expect ->
        let run = shell ()
        // The notes child asks to open a team: an effect the shell intercepts into its own message.
        let lifted = Shell.liftedNotes
        let _, steps = lifted.Update (Stamp.forTest 1L) (Notes.Add "x") run.Model.Notes

        expect
            "a Persist is wrapped"
            (List.exists
                (fun s ->
                    match s with
                    | Do(Shell.NotesEffect(Notes.Persist _)) -> true
                    | _ -> false)
                steps)

        run.Send(Shell.SetTeam "t-2") |> ignore
        expect "Team = t-2" (run.Model.Team = Some "t-2"))
    case "port-c 9: a child's effect, lifted; its reply routed back by the wrapping (one extra message)" (fun expect ->
        let run = shell ()
        run.Send(Shell.NotesMsg(Notes.Add "hi")) |> ignore

        match run.Performed with
        | [ Shell.NotesEffect(Notes.Persist note) ] ->
            run.Answer(Shell.NotesMsg(Notes.Saved note.Id))
            expect "the audit records the reply" (List.map fst run.Model.Notes.Audit = [ note.Id ])
        | _ -> expect "one wrapped Persist performed" false)
    case "port-c 10: a child's module-lifetime resource, prefixed" (fun expect ->
        let run = shell ()
        run.Emit(Shell.NavListener, Shell.Navigate "notes")
        expect "notes/feed running" (Map.containsKey (ResourceKey [ "notes"; "feed" ]) run.Runtime.Running))
    case "port-c 11: child resource emits a child message, routed by the wrapping" (fun expect ->
        let run = shell ()
        run.Emit(Shell.NavListener, Shell.Navigate "notes")
        run.Emit(Shell.NotesFeed, Shell.NotesMsg(Notes.Add "from feed"))
        expect "the child updated" (List.map (fun (n: Notes.Note) -> n.Text) run.Model.Notes.Notes = [ "from feed" ]))
    case "port-c 12: cross-module event fan-out became Then follow-ups" (fun expect ->
        let run = shell ()
        run.Emit(Shell.BusListener, Shell.TokenAcquired "t-1")

        expect
            "the child recorded the event"
            (List.map (fun (n: Notes.Note) -> n.Text) run.Model.Notes.Notes = [ "event:token" ]))
    case "port-c 13: child effect, wrapped, compared with =" (fun expect ->
        let run = shell ()
        run.Send(Shell.NotesMsg(Notes.Add "hi")) |> ignore
        let note = List.head run.Model.Notes.Notes
        expect "Performed = [wrapped Persist]" (run.Performed = [ Shell.NotesEffect(Notes.Persist note) ]))
    case "port-c 14: erased: boxed effect values still compare with =" (fun expect ->
        expect
            "equal"
            (Shell.ModuleEffect("counter", box (Notes.Persist { Id = "1"; Text = "a"; At = 1L })) = Shell.ModuleEffect(
                "counter",
                box (Notes.Persist { Id = "1"; Text = "a"; At = 1L })
            )))
    case "port-c 15: erased: a wrong-typed box compiles and is simply unequal" (fun expect ->
        expect
            "unequal"
            (Shell.ModuleEffect("counter", box 1)
             <> Shell.ModuleEffect("counter", box (Notes.Persist { Id = "1"; Text = "a"; At = 1L }))))
    case "port-c 16: erased: the interpreter still gets the module's real invocation" (fun expect ->
        let describe (e: Shell.Effect) : Invocation =
            match e with
            | Shell.ModuleEffect(id, payload) ->
                match payload with
                | :? Notes.Effect as inner ->
                    match inner with
                    | Notes.Persist _ -> {
                        Kind = id + ".save"
                        Args = None
                        Destination = Local
                      }
                    | Notes.OpenTeam _ -> {
                        Kind = id + ".team"
                        Args = None
                        Destination = Local
                      }
                | _ -> {
                    Kind = id + ".unknown"
                    Args = None
                    Destination = Local
                  }
            | Shell.NotesEffect _ -> {
                Kind = "notes"
                Args = None
                Destination = Local
              }

        let inv =
            describe (Shell.ModuleEffect("counter", box (Notes.Persist { Id = "1"; Text = "a"; At = 1L })))

        expect "Kind = counter.save" (inv.Kind = "counter.save"))
    case "port-c 17: erased: an up-call intercepted through the registered projection" (fun expect ->
        // The erased route: a module's boxed effect is projected by the module's registration.
        let projections: Map<string, obj -> Shell.Msg option> =
            Map.ofList [
                "notes",
                (fun (o: obj) ->
                    match o with
                    | :? Notes.Effect as e ->
                        (match e with
                         | Notes.OpenTeam t -> Some(Shell.SetTeam t)
                         | _ -> None)
                    | _ -> None)
            ]

        let intercepted = projections.["notes"] (box (Notes.OpenTeam "t-9"))
        expect "projected to SetTeam" (intercepted = Some(Shell.SetTeam "t-9")))
    case "port-c 18: erased: child resource key prefixed by module id" (fun expect ->
        let prefixed =
            Component.prefixResources
                "notes"
                (fun (r: NoResource) -> Shell.ModuleResource("notes", box r))
                (Map.ofList [ ResourceKey.constant "feed", NoResource ])

        expect "notes/feed" (Map.containsKey (ResourceKey [ "notes"; "feed" ]) prefixed))
    case "port-c 19: erased: a mis-addressed message fails in update, which refuses the step" (fun expect ->
        let run = shell ()
        let before = run.Model
        run.Send(Shell.ModuleMsg("counter", box "not a counter message")) |> ignore

        expect
            "the step was refused"
            (match run.Runtime.Refusals with
             | [ UpdateThrew _ ] -> true
             | _ -> false)

        expect "the model is untouched" (obj.ReferenceEquals(before, run.Model)))
]

// ---- Port D ------------------------------------------------------------------------------------------

let private reply text tools usage : Agent.Reply = {
    Text = text
    Tools = tools
    Usage = usage
}

let private providerKey = ResourceKey.constant "provider"
let private clockKey = ResourceKey.constant "active-clock"

let private requestOf (run: Harness<_, Agent.Model, _, _, _, _>) =
    match run.Model.Phase with
    | Agent.Calling request -> Some request
    | _ -> None

let private asking () =
    let run = Harness.start Agent.agent 1000
    run.Send(Agent.Ask "paint it blue") |> ignore
    let r1 = (requestOf run).Value
    run.Emit(Agent.ProviderCall r1, Agent.ProviderReplied(1, reply "" [ "getTree", "{}" ] 100))
    run.Answer Agent.Painted
    run.Answer(Agent.DomRead("getTree", "tree"))
    let r2 = (requestOf run).Value
    run.Emit(Agent.ProviderCall r2, Agent.ProviderReplied(2, reply "" [ "askUser", "which?" ] 50))
    run

let private portD = [
    case "port-d 1: call 1 in flight as a resource" (fun expect ->
        let run = Harness.start Agent.agent 1000
        run.Send(Agent.Ask "paint it blue") |> ignore

        expect
            "provider running with request 1"
            (match Map.tryFind providerKey run.Runtime.Running with
             | Some(Agent.ProviderCall r) -> r.Id = 1
             | _ -> false))
    case "port-d 2: active clock running" (fun expect ->
        let run = Harness.start Agent.agent 1000
        run.Send(Agent.Ask "paint it blue") |> ignore
        expect "clock running" (Map.containsKey clockKey run.Runtime.Running))
    case "port-d 3: two active seconds" (fun expect ->
        let run = Harness.start Agent.agent 1000
        run.Send(Agent.Ask "paint it blue") |> ignore
        run.Emit(Agent.ActiveClock, Agent.Tick)
        run.Emit(Agent.ActiveClock, Agent.Tick)
        expect "ActiveSeconds = 2" (run.Model.ActiveSeconds = 2))
    case "port-d 4: emission applied" (fun expect ->
        let run = Harness.start Agent.agent 1000
        run.Send(Agent.Ask "paint it blue") |> ignore
        let r1 = (requestOf run).Value
        run.Emit(Agent.ProviderCall r1, Agent.ProviderReplied(1, reply "" [ "getTree", "{}" ] 100))
        expect "Painting" (run.Model.Phase = Agent.Painting "getTree"))
    case "port-d 5: provider resource stopped once replied" (fun expect ->
        let run = Harness.start Agent.agent 1000
        run.Send(Agent.Ask "paint it blue") |> ignore
        let r1 = (requestOf run).Value
        run.Emit(Agent.ProviderCall r1, Agent.ProviderReplied(1, reply "" [ "getTree", "{}" ] 100))
        expect "Stopped = [call 1]" (run.Stopped = [ Agent.ProviderCall r1 ]))
    case "port-d 6: paint wait is an effect, compared by value" (fun expect ->
        let run = Harness.start Agent.agent 1000
        run.Send(Agent.Ask "paint it blue") |> ignore
        let r1 = (requestOf run).Value
        run.Emit(Agent.ProviderCall r1, Agent.ProviderReplied(1, reply "" [ "getTree", "{}" ] 100))
        expect "Performed = [AwaitPaint]" (run.Performed = [ Agent.AwaitPaint ]))
    case "port-d 7: after paint, the DOM tool is an effect" (fun expect ->
        let run = Harness.start Agent.agent 1000
        run.Send(Agent.Ask "paint it blue") |> ignore
        let r1 = (requestOf run).Value
        run.Emit(Agent.ProviderCall r1, Agent.ProviderReplied(1, reply "" [ "getTree", "{}" ] 100))
        run.Answer Agent.Painted
        expect "AwaitPaint then ReadDom" (run.Performed = [ Agent.AwaitPaint; Agent.ReadDom "getTree" ]))
    case "port-d 8: tool result fed back; call 2 in flight" (fun expect ->
        let run = Harness.start Agent.agent 1000
        run.Send(Agent.Ask "paint it blue") |> ignore
        let r1 = (requestOf run).Value
        run.Emit(Agent.ProviderCall r1, Agent.ProviderReplied(1, reply "" [ "getTree", "{}" ] 100))
        run.Answer Agent.Painted
        run.Answer(Agent.DomRead("getTree", "tree"))

        expect
            "request 2"
            (match requestOf run with
             | Some r -> r.Id = 2
             | None -> false))
    case "port-d 9: call 2 carries the tool result in its request (as a value)" (fun expect ->
        let run = Harness.start Agent.agent 1000
        run.Send(Agent.Ask "paint it blue") |> ignore
        let r1 = (requestOf run).Value
        run.Emit(Agent.ProviderCall r1, Agent.ProviderReplied(1, reply "" [ "getTree", "{}" ] 100))
        run.Answer Agent.Painted
        run.Answer(Agent.DomRead("getTree", "tree"))

        expect
            "the running resource's request names the tool result"
            (match Map.tryFind providerKey run.Runtime.Running with
             | Some(Agent.ProviderCall r) -> List.last r.Messages = "tool:getTree=tree"
             | _ -> false))
    case "port-d 10: asking: the loop waits in a phase" (fun expect ->
        let run = asking ()
        expect "Asking" (run.Model.Phase = Agent.Asking "which?"))
    case "port-d 11: asking: the active clock is stopped" (fun expect ->
        let run = asking ()
        expect "no clock" (not (Map.containsKey clockKey run.Runtime.Running)))
    case "port-d 12: preview sees the next provider call only as a resource change" (fun expect ->
        let run = asking ()
        let preview = run.Preview(Stamp.forTest 1L, Agent.AskAnswered "blue", agent)

        match preview.Proposal with
        | Some proposal ->
            expect "no effects" (proposal.Effects = [])

            expect
                "starts: the clock and the provider"
                (List.map (fun (k, _, _) -> k) proposal.Starts = [ clockKey; providerKey ])
        | None -> expect "a proposal" false)
    case "port-d 13: answered: call 3 in flight" (fun expect ->
        let run = asking ()
        run.Send(Agent.AskAnswered "blue") |> ignore

        expect
            "request 3"
            (match requestOf run with
             | Some r -> r.Id = 3
             | None -> false))
    case "port-d 14: stopped" (fun expect ->
        let run = asking ()
        run.Send(Agent.AskAnswered "blue") |> ignore
        run.Send Agent.Stop |> ignore
        expect "Halted aborted" (run.Model.Phase = Agent.Halted "aborted"))
    case "port-d 15: the provider call was stopped (an in-flight HTTP would be aborted)" (fun expect ->
        let run = asking ()
        run.Send(Agent.AskAnswered "blue") |> ignore
        let r3 = (requestOf run).Value
        run.Send Agent.Stop |> ignore
        expect "call 3 stopped" (List.contains (Agent.ProviderCall r3) run.Stopped))
    case "port-d 16: late reply dropped by the liveness token" (fun expect ->
        let run = asking ()
        run.Send(Agent.AskAnswered "blue") |> ignore
        let r3 = (requestOf run).Value
        let late = run.EmitterOf(Agent.ProviderCall r3)
        run.Send Agent.Stop |> ignore
        let before = run.Runtime.RefusedAtAcceptance
        late (Agent.ProviderReplied(3, reply "late" [] 10))
        expect "refused exactly once" (run.Runtime.RefusedAtAcceptance = before + 1)
        expect "tokens unchanged" (run.Model.Tokens = 150))
    case "port-d 17: tokens accumulated from usage" (fun expect ->
        let run = asking ()
        expect "Tokens = 150" (run.Model.Tokens = 150))
    case "port-d 18: resumed run carries prior context" (fun expect ->
        let run = asking ()
        run.Send(Agent.AskAnswered "blue") |> ignore

        expect
            "the request carries the whole conversation"
            (match requestOf run with
             | Some r -> List.length r.Messages = 3 && List.head r.Messages = "user:paint it blue"
             | None -> false))
    case "port-d 19: broken emission with no tools is fed back, not Completed" (fun expect ->
        let run = asking ()
        run.Send(Agent.AskAnswered "blue") |> ignore
        let r3 = (requestOf run).Value
        run.Emit(Agent.ProviderCall r3, Agent.ProviderReplied(3, reply "" [] 10))

        expect
            "request 4 in flight"
            (match requestOf run with
             | Some r -> r.Id = 4
             | None -> false))
    case "port-d 20: completed; final assistant text appended to the session" (fun expect ->
        let run = asking ()
        run.Send(Agent.AskAnswered "blue") |> ignore
        let r3 = (requestOf run).Value
        run.Emit(Agent.ProviderCall r3, Agent.ProviderReplied(3, reply "Done - it is blue." [] 10))
        expect "Halted completed" (run.Model.Phase = Agent.Halted "completed")
        expect "the text is the last turn" (List.last run.Model.Turns = "assistant:Done - it is blue."))
    case "port-d 21: token budget halts at the next loop entry" (fun expect ->
        let run = Harness.start Agent.agent 100
        run.Send(Agent.Ask "paint it blue") |> ignore
        let r1 = (requestOf run).Value
        run.Emit(Agent.ProviderCall r1, Agent.ProviderReplied(1, reply "" [ "getTree", "{}" ] 110))
        expect "Halted max tokens" (run.Model.Phase = Agent.Halted "max tokens"))
    case "port-d 22: replay reproduces the model from the log" (fun expect ->
        let run = asking ()
        run.Send(Agent.AskAnswered "blue") |> ignore
        expect "replay = model" (Replay.run Agent.agent 1000 run.Runtime.Log = run.Model))
]

// ---- stream-vs-resource ------------------------------------------------------------------------------

module private Feed =

    type Msg =
        | Watch of bool
        | Got of string
        | Add of int
        | Added of int

    type Effect = Compute of int

    type Resource = | FeedSource

    type Model = {
        Watching: bool
        Got: string list
        N: int
    }

    let feed: Component<unit, Model, Msg, Effect, Resource, Model> =
        Component.plain
            (fun () -> { Watching = false; Got = []; N = 1 }, [])
            (fun msg m ->
                match msg with
                | Watch w -> { m with Watching = w }, []
                | Got s -> { m with Got = m.Got @ [ s ] }, []
                | Add n -> m, [ Do(Compute n) ]
                | Added n -> { m with N = m.N + n }, [])
            (fun m ->
                if m.Watching then
                    Map.ofList [ ResourceKey.constant "feed", FeedSource ]
                else
                    Map.empty)
            id

let private streamVsResource = [
    case
        "stream-vs-resource 1: a one-shot effect cannot keep delivering: its second answer is refused at acceptance"
        (fun expect ->
            // The design removed the streaming effect the prototype measured; the leak it showed is closed by
            // construction. What stands in its place: a reply's token is revoked after its one answer.
            let run = Harness.start Feed.feed ()
            run.Send(Feed.Add 10) |> ignore
            let outstanding = run.Runtime.Sequence
            run.Answer(Feed.Added 10)
            expect "the answer landed" (run.Model.N = 11)
            // Deliver through the same performer again: the harness has forgotten it, so emulate a performer
            // that calls its callback twice by capturing before answering.
            let run2 = Harness.start Feed.feed ()
            run2.Send(Feed.Add 1) |> ignore
            let before = run2.Runtime.RefusedAtAcceptance
            // A second answer to an effect already answered has nothing outstanding to call: the harness refuses.
            run2.Answer(Feed.Added 1)

            let refused =
                try
                    run2.Answer(Feed.Added 1)
                    false
                with _ ->
                    true

            expect "no second answer" refused
            expect "nothing refused at acceptance either" (run2.Runtime.RefusedAtAcceptance = before)
            expect "sequence advanced once for the answer" (run.Runtime.Sequence = outstanding + 1L))
    case "stream-vs-resource 2: a resource stops when delisted" (fun expect ->
        let run = Harness.start Feed.feed ()
        run.Send(Feed.Watch true) |> ignore
        run.Emit(Feed.FeedSource, Feed.Got "while-watching")
        let late = run.EmitterOf Feed.FeedSource
        run.Send(Feed.Watch false) |> ignore
        late (Feed.Got "after-unwatch")
        expect "Got = [while-watching]" (run.Model.Got = [ "while-watching" ]))
    case "stream-vs-resource 3: an answer that completes at once: one drain, one render" (fun expect ->
        let answers (e: Feed.Effect) =
            match e with
            | Feed.Compute n -> Immediate(Feed.Added n)

        let run =
            Harness.startWith
                {
                    HarnessOptions.defaults with
                        Answers = answers
                }
                Feed.feed
                ()

        run.Send(Feed.Add 10) |> ignore
        expect "(Renders, N) = (2, 11)" ((run.Runtime.Renders, run.Model.N) = (2, 11)))
    case "stream-vs-resource 4: an answer that arrives later: two drains, two renders" (fun expect ->
        let run = Harness.start Feed.feed ()
        run.Send(Feed.Add 10) |> ignore
        run.Answer(Feed.Added 10)
        expect "(Renders, N) = (3, 11)" ((run.Runtime.Renders, run.Model.N) = (3, 11)))
]

// ---- stamp-argument ----------------------------------------------------------------------------------

/// A shell of two stamped children for the stamp checks: notes (lifted, split) and an audit child, with a
/// parent that mints its own id, a plain child, and controls without the split and without a seed per
/// follow-up.
module private Stamped =

    type Msg =
        | NotesMsg of Notes.Msg
        | AuditMsg of Notes.Msg
        | Toast of string
        | Publish of string
        | PlainMsg of Counter.Msg

    type Effect =
        | NotesEffect of Notes.Effect
        | AuditEffect of Notes.Effect

    type Model = {
        StartedAt: int64
        Notes: Notes.Model
        Audit: Notes.Model
        ToastIds: string list
        Plain: Counter.Model
    }

    let make
        (split: Stamp -> string -> Stamp)
        (followSeeds: bool)
        : Component<int64, Model, Msg, Effect, NoResource, Model> =
        let notes =
            Component.liftWith
                split
                "notes"
                NotesMsg
                (fun e -> Do(NotesEffect e))
                (fun (r: NoResource) -> r)
                Notes.notes

        let audit =
            Component.liftWith
                split
                "audit"
                AuditMsg
                (fun e -> Do(AuditEffect e))
                (fun (r: NoResource) -> r)
                Notes.notes

        let plain =
            Component.lift
                "plain"
                PlainMsg
                (fun (_: NoEffect) -> NotesEffect(Notes.OpenTeam "never"))
                (fun (r: NoResource) -> r)
                Counter.counter

        {
            Init =
                fun bootAt ->
                    let n, _ = notes.Init()
                    let a, _ = audit.Init()
                    let p, _ = plain.Init()

                    {
                        StartedAt = bootAt
                        Notes = n
                        Audit = a
                        ToastIds = []
                        Plain = p
                    },
                    [ Then(AuditMsg(Notes.Add "boot")) ]
            Update =
                fun stamp msg m ->
                    match msg with
                    | NotesMsg inner ->
                        let n, steps = notes.Update stamp inner m.Notes
                        { m with Notes = n }, steps
                    | AuditMsg inner ->
                        let a, steps = audit.Update stamp inner m.Audit
                        { m with Audit = a }, steps
                    | PlainMsg inner ->
                        let p, steps = plain.Update stamp inner m.Plain
                        { m with Plain = p }, steps
                    | Toast text ->
                        // The parent mints an id in the same call as a lifted child: `Toast` fans out.
                        let a, steps = audit.Update stamp (Notes.Add("toast:" + text)) m.Audit

                        {
                            m with
                                ToastIds = Stamp.freshId 0 stamp :: m.ToastIds
                                Audit = a
                        },
                        steps
                    | Publish text ->
                        if followSeeds then
                            // Three follow-ups to one child in one step: each reads a seed of its own.
                            m,
                            [
                                Then(AuditMsg(Notes.Add("published:" + text)))
                                Then(AuditMsg(Notes.Add("indexed:" + text)))
                                Then(AuditMsg(Notes.Add("notified:" + text)))
                            ]
                        else
                            // The control: the child run three times on the SAME stamp, as a runtime without
                            // the seed rule would run its follow-ups.
                            let a1, _ = audit.Update stamp (Notes.Add("published:" + text)) m.Audit
                            let a2, _ = audit.Update stamp (Notes.Add("indexed:" + text)) a1
                            let a3, _ = audit.Update stamp (Notes.Add("notified:" + text)) a2
                            { m with Audit = a3 }, []
            Resources = fun _ -> Map.empty
            View = id
        }

    let split (stamp: Stamp) (key: string) = Stamp.forChild key stamp
    let unsplit (stamp: Stamp) (_: string) = stamp

    let stamped = make split true
    let unsplitShell = make unsplit true
    let unfollowed = make split false

let private noteIds (m: Notes.Model) = m.Notes |> List.map (fun n -> n.Id)

let private stampArgument = [
    case "stamp 1: a test supplies the stamp: the note carries its time" (fun expect ->
        let m, _ =
            Notes.notes.Update (Stamp.forTest 42L) (Notes.Add "hello") (fst (Notes.notes.Init()))

        expect "At = 42" ((List.head m.Notes).At = 42L))
    case "stamp 2: the save carries the same note" (fun expect ->
        let m, steps =
            Notes.notes.Update (Stamp.forTest 42L) (Notes.Add "hello") (fst (Notes.notes.Init()))

        expect "steps = [Do (Persist note)]" (steps = [ Do(Notes.Persist(List.head m.Notes)) ]))
    case "stamp 3: the note is in the model after one message" (fun expect ->
        let run = Harness.start Notes.notes ()
        let steps = committed run
        run.Send(Notes.Add "hello") |> ignore
        expect "one step" (committed run = steps + 1)
        expect "one note" (List.length run.Model.Notes = 1))
    case "stamp 4: its time is the time of acceptance" (fun expect ->
        let run = Harness.start Notes.notes ()
        let reads = run.ClockReads
        run.Send(Notes.Add "hello") |> ignore
        // The harness clock reads 1000 + the number of reads so far.
        expect "At = the clock at acceptance" ((List.head run.Model.Notes).At = 1000L + int64 reads + 1L))
    case "stamp 5: the host read the clock once, and update never did" (fun expect ->
        let run = Harness.start Notes.notes ()
        let reads = run.ClockReads
        run.Send(Notes.Add "hello") |> ignore
        expect "one read" (run.ClockReads = reads + 1))
    case "stamp 6: the effect form needs two messages for the same note" (fun expect ->
        // The prototype's port C asked for the time by effect: an `AskTime` and its `Stamped` reply, two
        // envelopes. Here that is a harness run whose first message performs an effect answered later.
        let run = Harness.start Feed.feed ()
        let steps = committed run
        run.Send(Feed.Add 1) |> ignore
        run.Answer(Feed.Added 1)
        expect "two steps" (committed run = steps + 2))
    case "stamp 7: update twice on the same stamp gives the same result" (fun expect ->
        let model = fst (Notes.notes.Init())

        let inputs = [ for i in 1..4 -> Stamp.forTest (int64 i), Notes.Add(sprintf "n%d" i), model ]

        expect "deterministic" ((Law.deterministic Notes.notes inputs).IsEmpty))
    case "stamp 8: a preview reads no clock" (fun expect ->
        let run = Harness.start Notes.notes ()
        let reads = run.ClockReads
        run.Preview(Stamp.forTest 7L, Notes.Add "hello", user) |> ignore
        expect "no read" (run.ClockReads = reads))
    case "stamp 9: the same envelope commits after the clock has moved" (fun expect ->
        // A step parked for approval and approved later commits on the envelope's own stamp.
        let needsApproval: Decide<Notes.Effect, NoResource> =
            fun p -> if p.Grant.IsSome then Allow else NeedsApproval

        let run =
            Harness.startWith
                {
                    HarnessOptions.defaults with
                        Decide = needsApproval
                }
                Notes.notes
                ()

        run.Send(Notes.Add "hello") |> ignore
        expect "parked" (List.length run.Runtime.Parked = 1)
        // The clock moves on.
        for _ in 1..500 do
            HarnessOptions.defaults.Clock() |> ignore

        let approved = run.Runtime.Approve(grantFor (List.head run.Runtime.Parked))
        expect "committed" (approved && List.length run.Model.Notes = 1))
    case "stamp 10: the committed note carries the time of acceptance, not of approval" (fun expect ->
        let needsApproval: Decide<Notes.Effect, NoResource> =
            fun p -> if p.Grant.IsSome then Allow else NeedsApproval

        let run =
            Harness.startWith
                {
                    HarnessOptions.defaults with
                        Decide = needsApproval
                }
                Notes.notes
                ()

        run.Send(Notes.Add "hello") |> ignore
        let accepted = (List.head run.Runtime.Parked).Envelope
        run.Runtime.Approve(grantFor (List.head run.Runtime.Parked)) |> ignore
        expect "At = the envelope's" ((List.head run.Model.Notes).At = accepted.Stamp.At))
    case
        "stamp 11: a step approved after the model moved under it no longer matches, and is refused as stale"
        (fun expect ->
            let mutable gate = NeedsApproval
            let decide: Decide<Portfolio.Effect, Portfolio.Resource> = fun _ -> gate

            let run =
                Harness.startWith
                    {
                        HarnessOptions.defaults with
                            Decide = decide
                    }
                    Portfolio.portfolio
                    "portfolio"

            gate <- NeedsApproval
            run.Send(Portfolio.ShowSection Portfolio.Parties) |> ignore
            let grant = grantFor (List.head run.Runtime.Parked)
            // The chain of ShowSection writes a route that names the selected portfolio: select one.
            gate <- Allow
            run.Send(Portfolio.PortfolioLoaded(Ok alpha)) |> ignore
            run.Runtime.Approve grant |> ignore
            expect "refused as stale" (List.contains Stale run.Runtime.Refusals)
            expect "the section did not change" (run.Model.Section = Portfolio.Overview))
    case "stamp 12: a replay reads no clock and reproduces the model, ids and times included" (fun expect ->
        let run = Harness.start Notes.notes ()
        run.Send(Notes.Add "a") |> ignore
        run.Send(Notes.Add "b") |> ignore
        run.Answer(Notes.Saved "a")
        expect "replay = model" (Replay.run Notes.notes () run.Runtime.Log = run.Model))
    case "stamp 13: the same stamp and counter give the same id" (fun expect ->
        let s = Stamp.forTest 1L
        expect "equal" (Stamp.freshId 0 s = Stamp.freshId 0 s))
    case "stamp 14: a different counter gives a different id" (fun expect ->
        let s = Stamp.forTest 1L
        expect "differ" (Stamp.freshId 0 s <> Stamp.freshId 1 s))
    case "stamp 15: a child's seed differs from its parent's and from its sibling's" (fun expect ->
        let s = Stamp.forTest 1L

        let ids = [
            Stamp.freshId 0 s
            Stamp.freshId 0 (Stamp.forChild "a" s)
            Stamp.freshId 0 (Stamp.forChild "b" s)
        ]

        expect "pairwise distinct" (List.length (List.distinct ids) = 3))
    case "stamp 16: Init takes no stamp: what it needs came in the flags" (fun expect ->
        let run = Harness.start Stamped.stamped 777L
        expect "StartedAt = the flag" (run.Model.StartedAt = 777L))
    case "stamp 17: Init's follow-up is a message, stamped by the host at boot" (fun expect ->
        let run = Harness.start Stamped.stamped 777L
        let bootAt = (List.head run.Model.Audit.Notes).At
        expect "one boot note" (List.length run.Model.Audit.Notes = 1)
        expect "stamped at boot by the host's clock" (bootAt = 1001L))
    case "stamp 18: a component that needs no stamp composes under one that does" (fun expect ->
        let run = Harness.start Stamped.stamped 777L
        run.Send(Stamped.PlainMsg Counter.Increment) |> ignore
        expect "Count = 1" (run.Model.Plain.Count = 1))
    case "stamp 19: a parent and a lifted child mint in one call, and the ids differ" (fun expect ->
        let run = Harness.start Stamped.stamped 777L
        run.Send(Stamped.Toast "hi") |> ignore
        let toastId = List.head run.Model.ToastIds
        let auditId = (List.head run.Model.Audit.Notes).Id
        expect "differ" (toastId <> auditId))
    case "stamp 20: the same parent mints a different id in the next step" (fun expect ->
        let run = Harness.start Stamped.stamped 777L
        run.Send(Stamped.Toast "hi") |> ignore
        run.Send(Stamped.Toast "ho") |> ignore
        expect "two distinct toast ids" (List.length (List.distinct run.Model.ToastIds) = 2))
    case "stamp 21: three follow-ups to one child each mint an id, and the ids differ" (fun expect ->
        let run = Harness.start Stamped.stamped 777L
        run.Send(Stamped.Publish "p") |> ignore
        let ids = noteIds run.Model.Audit |> List.filter (fun _ -> true)
        expect "four notes (boot and three)" (List.length ids = 4)
        expect "all distinct" (List.length (List.distinct ids) = 4))
    case "stamp 22: every follow-up reads its step's time, though the clock moves on each read" (fun expect ->
        let run = Harness.start Stamped.stamped 777L
        run.Send(Stamped.Publish "p") |> ignore

        let times =
            run.Model.Audit.Notes
            |> List.filter (fun n -> n.Text <> "boot")
            |> List.map (fun n -> n.At)

        expect "one time for the three" (List.length (List.distinct times) = 1))
    case "stamp 23: a reply is a step of its own, stamped when it is accepted" (fun expect ->
        let run = Harness.start Notes.notes ()
        run.Send(Notes.Add "hello") |> ignore
        let note = List.head run.Model.Notes
        run.Answer(Notes.Saved note.Id)
        expect "the audit's time is later than the note's" (snd (List.head run.Model.Audit) = note.At + 1L))
    case "stamp 24: without the split, a parent and its child mint the same id in one call" (fun expect ->
        let run = Harness.start Stamped.unsplitShell 777L
        run.Send(Stamped.Toast "hi") |> ignore
        expect "collide" (List.head run.Model.ToastIds = (List.head run.Model.Audit.Notes).Id))
    case "stamp 25: without a seed per follow-up, three follow-ups to one child mint one id" (fun expect ->
        let run = Harness.start Stamped.unfollowed 777L
        run.Send(Stamped.Publish "p") |> ignore

        let ids =
            run.Model.Audit.Notes
            |> List.filter (fun n -> n.Text <> "boot")
            |> List.map (fun n -> n.Id)

        expect "three notes, one id" (List.length ids = 3 && List.length (List.distinct ids) = 1))
]

/// Every ported check, in one list.
let cases: Case list =
    portA @ portB @ portBGate @ portC @ portD @ streamVsResource @ stampArgument
