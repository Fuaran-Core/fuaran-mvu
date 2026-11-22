// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.React

open Fable.React

/// How the mount attaches its root (DECISIONS.md D29.4).
type Attach =
    /// Render into an empty container: `createRoot`, then its first render.
    | Fresh
    /// Adopt server-rendered markup: `hydrateRoot` with the root element, and never a `render` after it. The root
    /// reads the store's first snapshot (the boot paint, the model the server rendered) as its server snapshot, so a
    /// boot step that moves the model before React hydrates is a later render of the moved model, not a mismatch.
    | Hydrate

module Hydration =

    /// The attachment a mount makes: what was asked, unless the mount replaces a hot-reloaded instance, whose
    /// unmount emptied the container, so there is no markup left to adopt and the root renders fresh.
    let choose (requested: Attach) (replacing: bool) : Attach = if replacing then Fresh else requested

    /// Attach `element` as the root of `container`. A hydrating root renders the element once, over the markup; a
    /// fresh root renders it into the empty container. Neither is rendered again: every later render is a publish
    /// the root's `useSyncExternalStore` reads, which is what keeps React from discarding adopted markup.
    let attach (api: ReactApi) (container: obj) (how: Attach) (element: ReactElement) : Root =
        match how with
        | Fresh ->
            let root = api.CreateRoot container
            root.Render element
            root
        | Hydrate -> api.HydrateRoot container element
