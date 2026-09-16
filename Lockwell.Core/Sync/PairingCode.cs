// Lockwell - local-only encrypted vault
// Copyright (C) 2026 Lockwell
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System.Security.Cryptography;
using System.Text;

namespace Lockwell.Sync;

/// <summary>
/// The short code a person reads off one screen and types into another.
///
/// Six digits, because this is the last thing anyone has to type and every extra
/// character is a chance to get it wrong. Six digits is twenty bits, which sounds alarming
/// until you look at what the code is actually holding up.
///
/// It is not what keeps the transfer secret. Confidentiality comes from the ECDH exchange
/// between the two devices' keys, and someone who learned the code still has neither
/// private key, so they still cannot read anything. Guessing the code offline from a
/// recorded handshake buys an attacker nothing on its own.
///
/// It is not what proves who you connected to either. That is the fingerprint both screens
/// show at the end, which is derived from the finished handshake: anyone standing in the
/// middle produces a different fingerprint on each side, and the human comparing them is
/// what catches it.
///
/// What the code does is stop a stranger on the same network from starting a pairing at
/// all, so the only pairing request you ever see is one you began yourself. That is a
/// job six digits can do, on two conditions, and both are enforced rather than assumed:
///
///   - Guesses are limited. See <see cref="PairingSession"/>: after a few wrong answers
///     the code is thrown away and a new one generated, so an online guessing loop never
///     gets more than a handful of tries at any one code.
///   - The code is stretched with Argon2id before it becomes the pairing secret, so each
///     attempt costs the machine answering it real time and memory.
///
/// The previous version used ten characters from a 32-symbol alphabet. That was more
/// entropy than the design needs and more typing than anyone wants.
/// </summary>
public static class PairingCode
{
    /// <summary>Digits only: a phone shows a number pad for it, and nothing looks like anything else.</summary>
    private const string Alphabet = "0123456789";

    public const int Length = 6;

    /// <summary>How many wrong answers one code tolerates before it is replaced.</summary>
    public const int MaxAttempts = 3;

    /// <summary>A fresh code, split in the middle so it reads as two halves.</summary>
    public static string Generate()
    {
        var sb = new StringBuilder(Length + 1);
        for (int i = 0; i < Length; i++)
        {
            if (i > 0 && i % 3 == 0) sb.Append(' ');
            sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strip formatting and fix the characters people reliably mistype, so "typed it
    /// correctly but it says wrong code" is not a thing.
    ///
    /// Letters that look like digits are still folded in, because a code read aloud or
    /// remembered from the old format can arrive with an O where a zero belongs.
    /// </summary>
    public static string Normalise(string input)
    {
        var sb = new StringBuilder(Length);
        foreach (char raw in input.Trim().ToUpperInvariant())
        {
            char c = raw switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                'S' => '5',
                'B' => '8',
                _ => raw,
            };
            if (Alphabet.IndexOf(c) >= 0) sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool LooksValid(string input) => Normalise(input).Length == Length;

    /// <summary>
    /// Turn the typed code into the secret the handshake folds in.
    ///
    /// Argon2id rather than a plain hash: the code is short, so the only thing making an
    /// attempt expensive is the derivation itself. Salted with the offering device's
    /// public key, so a code derived for one PC is meaningless against another.
    /// </summary>
    public static byte[] ToSecret(string code, byte[] devicePublicKey)
    {
        string normalised = Normalise(code);
        byte[] codeBytes = Encoding.UTF8.GetBytes(normalised);

        // The salt must be the same on both sides and unique per device.
        byte[] salt = SHA256.HashData(devicePublicKey)[..16];

        try
        {
            using var argon = new Konscious.Security.Cryptography.Argon2id(codeBytes)
            {
                Salt = salt,
                MemorySize = 64 * 1024, // 64 MiB: heavy for a guesser, instant once
                Iterations = 3,
                DegreeOfParallelism = 2,
            };
            return argon.GetBytes(32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(codeBytes);
        }
    }
}

/// <summary>
/// One run of "this PC is waiting for a device", and the guess counting that makes a
/// short code safe.
///
/// This exists because of a mistake worth remembering. The listener originally accepted a
/// single connection and then stopped, and the code's length was chosen on that basis. When
/// the listener was later changed to keep waiting -- a real usability fix, because one typo
/// used to end the whole attempt -- that quietly removed the thing the code's length
/// depended on and turned pairing into an unlimited online guessing target.
///
/// So the limit lives here, next to the code, instead of being an accident of how the
/// listener happens to be written. Wrong answers are counted; after
/// <see cref="PairingCode.MaxAttempts"/> the code is discarded and a new one generated, so
/// no single code is ever worth grinding at.
/// </summary>
public sealed class PairingSession
{
    private readonly byte[] _publicKey;

    public PairingSession(byte[] devicePublicKey)
    {
        _publicKey = devicePublicKey;
        Code = PairingCode.Generate();
    }

    /// <summary>The code currently on screen.</summary>
    public string Code { get; private set; }

    /// <summary>Wrong answers against the current code.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>How many codes have been burned through this session.</summary>
    public int Rotations { get; private set; }

    /// <summary>The secret for the code currently on screen.</summary>
    public byte[] Secret() => PairingCode.ToSecret(Code, _publicKey);

    /// <summary>
    /// Record a wrong answer. Returns true when the code was replaced, so the caller knows
    /// to show the new one.
    /// </summary>
    public bool RecordFailure()
    {
        FailedAttempts++;
        if (FailedAttempts < PairingCode.MaxAttempts) return false;

        Code = PairingCode.Generate();
        FailedAttempts = 0;
        Rotations++;
        return true;
    }

    /// <summary>A correct answer clears the count: a typo before success is not suspicious.</summary>
    public void RecordSuccess() => FailedAttempts = 0;

    /// <summary>Attempts left before this code is replaced.</summary>
    public int AttemptsRemaining => PairingCode.MaxAttempts - FailedAttempts;
}
