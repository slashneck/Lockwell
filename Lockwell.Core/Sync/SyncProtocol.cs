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
/// Shared constants and key-derivation for the sync protocol.
///
/// Route B of docs/SYNC-PROTOCOL.md section 2.1: built on primitives .NET already
/// ships, rather than pulling in a Noise library. .NET 8 has no X25519, so key
/// agreement is P-256 ECDH. The handshake keeps Noise's XK/KK *structure* -- the peer's
/// static key is known in advance, ephemerals give forward secrecy, and a pre-shared
/// secret binds pairing to one specific QR -- without claiming to be Noise.
///
/// Everything derived here goes through HKDF with the running transcript hash, so no
/// two sessions between the same devices ever produce the same keys.
/// </summary>
public static class SyncProtocol
{
    public const int Version = 1;

    /// <summary>Domain separation, so these keys can never collide with vault keys.</summary>
    private const string Label = "lockwell-sync-v1";

    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>One frame of transport payload. Large files are split across many.</summary>
    public const int MaxFramePayload = 1024 * 1024;

    /// <summary>
    /// Port the listening device opens while pairing or accepting a transfer. In the
    /// IANA dynamic range, so it needs no registration and is unlikely to collide.
    /// The listener is only open while the user has a linking screen in front of them.
    /// </summary>
    public const int DefaultPort = 51820;

    /// <summary>
    /// Mix a new input into the running transcript. Every handshake message updates
    /// this, so both sides end up with a hash covering everything that was said -- if
    /// an attacker altered any message, the two sides derive different keys and the
    /// first authenticated frame fails.
    /// </summary>
    public static byte[] MixHash(byte[] transcript, params byte[][] inputs)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(transcript, 0, transcript.Length, null, 0);
        for (int i = 0; i < inputs.Length; i++)
        {
            byte[] input = inputs[i];
            if (i == inputs.Length - 1)
                sha.TransformFinalBlock(input, 0, input.Length);
            else
                sha.TransformBlock(input, 0, input.Length, null, 0);
        }
        return sha.Hash!;
    }

    public static byte[] InitialTranscript() =>
        SHA256.HashData(Encoding.UTF8.GetBytes(Label));

    /// <summary>
    /// Turn shared secrets plus the transcript into the two directional transport keys.
    /// Both sides run this identically and must arrive at the same pair.
    /// </summary>
    public static (byte[] InitiatorToResponder, byte[] ResponderToInitiator) DeriveTransportKeys(
        byte[] transcript, params byte[][] secrets)
    {
        // Concatenate every shared secret contributed by the handshake.
        int total = secrets.Sum(s => s.Length);
        byte[] combined = new byte[total];
        int at = 0;
        foreach (byte[] s in secrets)
        {
            Buffer.BlockCopy(s, 0, combined, at, s.Length);
            at += s.Length;
        }

        try
        {
            byte[] material = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: combined,
                outputLength: KeySize * 2,
                salt: transcript,
                info: Encoding.UTF8.GetBytes(Label + ":transport"));

            byte[] i2r = material[..KeySize];
            byte[] r2i = material[KeySize..];
            CryptographicOperations.ZeroMemory(material);
            return (i2r, r2i);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    /// <summary>
    /// Nonce for a transport frame: the frame counter, big-endian, left-padded.
    /// Counters never reset inside a session, so a nonce is never reused under a key.
    /// </summary>
    public static byte[] NonceFor(ulong counter)
    {
        byte[] nonce = new byte[NonceSize];
        BinaryPrimitivesWriteUInt64BigEndian(nonce.AsSpan(NonceSize - 8), counter);
        return nonce;
    }

    private static void BinaryPrimitivesWriteUInt64BigEndian(Span<byte> destination, ulong value)
    {
        for (int i = 7; i >= 0; i--)
        {
            destination[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }

    /// <summary>
    /// The short code both devices show for a human to compare, derived from the finished
    /// handshake transcript.
    ///
    /// Six digits in two groups. Short enough to read across a room, and short does not
    /// weaken it the way a short secret would: an attacker cannot search for a transcript
    /// that produces a chosen code, because they do not control both sides of the exchange
    /// and each attempt costs a full handshake with a fresh ephemeral key. A machine in the
    /// middle has to run two separate handshakes and would need both to land on the same
    /// six digits by chance, which is a one-in-a-million shot it gets exactly one try at
    /// with a human watching.
    /// </summary>
    public static string SessionCodeFrom(byte[] transcript)
    {
        byte[] material = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: transcript,
            outputLength: 8,
            info: Encoding.UTF8.GetBytes(Label + ":confirm"));

        try
        {
            // Big-endian so both platforms read the same bytes the same way.
            ulong value = 0;
            foreach (byte b in material) value = (value << 8) | b;

            uint digits = (uint)(value % 1_000_000);
            string text = digits.ToString("D6");
            return text[..3] + " " + text[3..];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    /// <summary>Constant-time comparison, for anything an attacker could probe by timing.</summary>
    public static bool ConstantTimeEquals(byte[] a, byte[] b) =>
        CryptographicOperations.FixedTimeEquals(a, b);
}
