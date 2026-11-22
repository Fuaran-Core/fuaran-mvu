// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU

/// What the runtime fixed when it accepted a message, read by `update` as its first argument: the time
/// of acceptance, the message's sequence number and a seed for fresh ids. Reading them is pure, because
/// they travel with the message and are recorded with it (DECISIONS.md D21, D24): a preview, the
/// computation repeated when an approval arrives and a replay all read the same stamp. A follow-up reads
/// its step's time and sequence and a seed of its own, derived from the emitting message's seed and the
/// follow-up's position in that message's steps; a lifted child reads a seed split from its parent's by
/// the child's key. `Init` takes no stamp.
type Stamp = {
    /// The time of acceptance, as the host's clock reads it: milliseconds since an epoch the host chooses.
    At: int64
    /// The accepted message's sequence number, from one upwards; the boot stamp is zero.
    Sequence: int64
    /// The seed for fresh ids in this `update` call.
    Seed: uint64
}

/// The seed function (DECISIONS.md D26). Every derivation is one 64-bit mixing step over a combination
/// that is distinct per purpose, so a child, a follow-up and a fresh id never derive the same value from
/// one seed by accident. The mixer is the SplitMix64 finaliser: a bijection on 64-bit words whose output
/// bits each depend on every input bit. 64-bit arithmetic wraps on both hosts (a JavaScript BigInt under
/// Fable).
module Seed =

    let private goldenGamma = 0x9E3779B97F4A7C15UL

    /// The SplitMix64 finaliser.
    let mix (x: uint64) : uint64 =
        let z = x + goldenGamma
        let z = (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
        let z = (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
        z ^^^ (z >>> 31)

    /// A 64-bit hash of a key's characters (FNV-1a over UTF-16 code units), for a child's key.
    let private hashKey (key: string) : uint64 =
        let mutable h = 0xCBF29CE484222325UL

        for c in key do
            h <- (h ^^^ uint64 (int c)) * 0x100000001B3UL

        h

    /// The seed of the message accepted at `sequence`, from the runtime's root seed.
    let ofSequence (root: uint64) (sequence: int64) : uint64 =
        mix (root ^^^ (uint64 sequence * 0xD6E8FEB86659FD93UL))

    /// The seed a lifted child reads: the parent's, split by the child's key.
    let forChild (seed: uint64) (key: string) : uint64 = mix (seed ^^^ hashKey key)

    /// The seed a follow-up reads: the emitting message's, combined with the follow-up's position in that
    /// message's steps (zero-based).
    let forFollowUp (seed: uint64) (position: int) : uint64 =
        mix (seed ^^^ ((uint64 position + 1UL) * 0xA0761D6478BD642FUL))

    /// The `counter`-th fresh value of a seed.
    let fresh (seed: uint64) (counter: int) : uint64 =
        mix (seed + (uint64 counter + 1UL) * goldenGamma)

/// Functions over stamps.
module Stamp =

    let private hex = "0123456789abcdef"

    /// A 64-bit word as sixteen lowercase hexadecimal characters.
    let private render (x: uint64) : string =
        let chars = Array.zeroCreate<char> 16

        for i in 0..15 do
            let nibble = int ((x >>> (60 - 4 * i)) &&& 0xFUL)
            chars.[i] <- hex.[nibble]

        System.String chars

    /// A stamp a test supplies: the time given, sequence one, and a fixed seed.
    let forTest (at: int64) : Stamp = {
        At = at
        Sequence = 1L
        Seed = Seed.mix 0UL
    }

    /// The `counter`-th fresh id of a stamp: a 64-bit value rendered as sixteen lowercase hexadecimal
    /// characters. The same stamp and counter give the same id; a different counter, a different id.
    let freshId (counter: int) (stamp: Stamp) : string = render (Seed.fresh stamp.Seed counter)

    /// The stamp a lifted child reads: the same time and sequence, the seed split by the child's key.
    let forChild (key: string) (stamp: Stamp) : Stamp = {
        stamp with
            Seed = Seed.forChild stamp.Seed key
    }

    /// The stamp a follow-up reads: the same time and sequence, a seed of its own.
    let forFollowUp (position: int) (stamp: Stamp) : Stamp = {
        stamp with
            Seed = Seed.forFollowUp stamp.Seed position
    }
