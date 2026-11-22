// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Session

open Fuaran.Core

/// HMAC-SHA-256 (RFC 2104 over FIPS 180-4), on Core's portable SHA-256 (`Hash.sha256Bytes`), so a commitment
/// taken on a server checks in a browser byte for byte. Core ships an unkeyed digest only, and says so; the keyed
/// construction is this package's, held to RFC 4231's vectors on both hosts and to the platform's own
/// implementation on .NET (DECISIONS.md D28.3).
module Hmac =

    let private blockSize = 64

    /// The 32-byte HMAC-SHA-256 of `message` under `key`.
    let sha256 (key: byte[]) (message: byte[]) : byte[] =
        let key = if key.Length > blockSize then Hash.sha256Bytes key else key

        let padded = Array.zeroCreate<byte> blockSize
        Array.blit key 0 padded 0 key.Length
        let inner = Array.append (padded |> Array.map (fun b -> b ^^^ 0x36uy)) message

        let outer =
            Array.append (padded |> Array.map (fun b -> b ^^^ 0x5cuy)) (Hash.sha256Bytes inner)

        Hash.sha256Bytes outer

    let private hexChars = "0123456789abcdef"

    /// Bytes as lowercase hexadecimal, two characters each.
    let hex (bytes: byte[]) : string =
        let chars = Array.zeroCreate<char> (bytes.Length * 2)

        for i in 0 .. bytes.Length - 1 do
            let b = int bytes.[i]
            chars.[2 * i] <- hexChars.[b >>> 4]
            chars.[2 * i + 1] <- hexChars.[b &&& 0xF]

        System.String chars

/// The key a session's commitments are taken under. The host holds it: it is never in the log, never in an entry,
/// and this type prints nothing of it. At least 32 bytes, the output size of the hash it keys.
[<Sealed>]
type CommitmentKey private (bytes: byte[]) =

    /// The shortest key accepted, in bytes.
    static member MinimumLength = 32

    /// A key from the host's bytes, copied; refused when shorter than `MinimumLength`.
    static member OfBytes(bytes: byte[]) : Result<CommitmentKey, string> =
        if isNull bytes || bytes.Length < CommitmentKey.MinimumLength then
            Error(sprintf "a commitment key is at least %d bytes" CommitmentKey.MinimumLength)
        else
            Ok(CommitmentKey(Array.copy bytes))

    member internal _.Bytes = bytes

    override _.ToString() = "CommitmentKey(held by the host)"

/// A withheld value's commitment (DECISIONS.md D28.3): HMAC-SHA-256 under the host's key over Core's injective field
/// encoding (`Hash.canonicalFields`) of a domain tag, the step's sequence, the field's path and the value. The
/// sequence and the path make every occurrence distinct, so two equal values at two places commit differently and a
/// reader without the key learns neither the values nor that they are equal; the key makes a guessable value
/// uncheckable by anyone but its holder.
module Commitment =

    /// The domain tag every commitment's pre-image starts with.
    let tag = "fuaran-mvu-session/commitment/1"

    /// The commitment to `value` at field `path` of the step recorded at `sequence`, under `key`.
    let take (key: CommitmentKey) (sequence: int64) (path: string) (value: string) : string =
        Hash.canonicalFields [ tag; string sequence; path; value ]
        |> Hash.utf8Bytes
        |> Hmac.sha256 key.Bytes
        |> Hmac.hex

    /// Whether `value` is the value committed to at `path` of the step at `sequence`.
    let opens (key: CommitmentKey) (sequence: int64) (path: string) (value: string) (commitment: string) : bool =
        take key sequence path value = commitment
