// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// A small deterministic generator, the same on .NET and under Fable, so a differential case names its
/// seed and a failure is reproducible on either host. A 32-bit linear congruential step over `int`
/// arithmetic that both hosts perform identically (Fable's `int` is a 32-bit integer).
module Fuaran.MVU.Tests.Rand

type Rand(seed: int) =
    let mutable state = seed

    /// The next value in `[0, bound)`.
    member _.Next(bound: int) : int =
        // Numerical Recipes' constants; the multiplication wraps on both hosts.
        state <- state * 1664525 + 1013904223
        let positive = (state >>> 1) &&& 0x3FFFFFFF
        positive % bound

    /// A coin, true with probability `numerator / denominator`.
    member this.Chance(numerator: int, denominator: int) = this.Next denominator < numerator
