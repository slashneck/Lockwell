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

/// <summary>Why a handshake was refused. Deliberately coarse on the wire.</summary>
public enum HandshakeFailure
{
    None = 0,
    VersionMismatch,
    Malformed,
    BadPairingSecret,
    UnknownPeer,
    Timeout,
}

public sealed class HandshakeException : Exception
{
    public HandshakeFailure Reason { get; }

    public HandshakeException(HandshakeFailure reason, string message) : base(message) =>
        Reason = reason;
}

/// <summary>What kind of connection is being established.</summary>
public enum HandshakeMode
{
    /// <summary>First contact. A pairing secret from the QR is required.</summary>
    Pair,

    /// <summary>Both devices already know each other. No user interaction.</summary>
    Reconnect,
}

/// <summary>The outcome of a successful handshake.</summary>
public sealed class HandshakeResult
{
    /// <summary>The peer's long-term public key, now cryptographically proven.</summary>
    public required byte[] PeerPublicKey { get; init; }

    /// <summary>Key for data this side sends.</summary>
    public required byte[] SendKey { get; init; }

    /// <summary>Key for data this side receives.</summary>
    public required byte[] ReceiveKey { get; init; }

    /// <summary>
    /// A short code derived from the finished handshake, identical on both devices.
    ///
    /// This is the number a human compares, and getting it from the transcript rather than
    /// from either device's identity is the whole point. Both sides mix the same inputs --
    /// both ephemeral keys, both static keys, the pairing secret -- so they arrive at the
    /// same value only if they genuinely talked to each other. Anyone sitting in the middle
    /// runs two separate handshakes and cannot make both ends produce the same number, so a
    /// mismatch is exactly the signal it is supposed to be.
    ///
    /// It replaces showing the peer's identity fingerprint, which was a real bug: each side
    /// displayed the *other* device's fingerprint, so the two screens showed different keys
    /// and could never match no matter how correct the pairing was. The step looked like a
    /// security check and verified nothing.
    /// </summary>
    public required string SessionCode { get; init; }

    /// <summary>The peer's identity fingerprint. Useful in a device list; not for comparing.</summary>
    public string PeerFingerprint => DeviceIdentity.FingerprintOf(PeerPublicKey);
}

/// <summary>
/// The pairing and reconnect handshake.
///
/// Three messages, following the structure of Noise's XK pattern (see
/// docs/SYNC-PROTOCOL.md section 4.3) built on primitives .NET already ships:
///
///   1. initiator -> responder   ephemeral public key
///   2. responder -> initiator   ephemeral public key
///   3. initiator -> responder   its static public key, encrypted
///
/// The properties this buys:
///   - The responder is authenticated because step 2's shared secret cannot be computed
///     without the responder's private key, which the initiator learned about from the
///     QR. A substituted PC cannot answer.
///   - The initiator is authenticated because the final key needs its static private key.
///   - Fresh ephemerals mean every session is forward-secret: a device compromised
///     tomorrow does not decrypt a conversation recorded today.
///   - The pairing secret is folded into the final derivation, binding the session to
///     one specific QR code. Without it, anyone who learned the PC's public key could
///     attempt to pair.
///   - Every message is mixed into a running transcript hash, so altering any of them
///     makes the two sides derive different keys and the first real frame fails.
///
/// Pair and Reconnect run the identical exchange. They differ only in policy: pairing
/// requires the secret and hands the new peer up for confirmation, while reconnect
/// requires the peer to already be trusted.
/// </summary>
public static class Handshake
{
    private const int MaxMessage = 4096;
    private static readonly byte[] EmptySecret = Array.Empty<byte>();

    /// <summary>Run the initiator side (the device that scanned the QR, or is calling out).</summary>
    public static async Task<HandshakeResult> InitiateAsync(
        Stream stream,
        DeviceIdentity self,
        byte[] responderPublicKey,
        HandshakeMode mode,
        byte[]? pairingSecret = null,
        CancellationToken cancellationToken = default)
    {
        RequireSecretForMode(mode, pairingSecret);

        var ephemeral = DeviceIdentity.Create();
        try
        {
            // --- message 1: our ephemeral ---
            await WriteMessageAsync(stream, Frame(SyncProtocol.Version, ephemeral.PublicKey), cancellationToken);

            // --- message 2: their ephemeral ---
            byte[] msg2 = await ReadMessageAsync(stream, cancellationToken);
            (int version, byte[] peerEphemeral) = Unframe(msg2);
            if (version != SyncProtocol.Version)
                throw new HandshakeException(HandshakeFailure.VersionMismatch,
                    $"Peer speaks protocol version {version}, this device speaks {SyncProtocol.Version}.");

            // Both sides can now reach the same two secrets.
            byte[] dhEphemeral = ephemeral.Agree(peerEphemeral);
            byte[] dhToResponderStatic = ephemeral.Agree(responderPublicKey);

            byte[] transcript = BuildTranscript(responderPublicKey, ephemeral.PublicKey, peerEphemeral);
            byte[] handshakeKey = DeriveHandshakeKey(transcript, dhEphemeral, dhToResponderStatic);

            // --- message 3: our static key, encrypted ---
            byte[] sealedStatic = Seal(handshakeKey, self.PublicKey, transcript);
            await WriteMessageAsync(stream, sealedStatic, cancellationToken);

            byte[] dhStaticToEphemeral = self.Agree(peerEphemeral);
            byte[] secret = pairingSecret ?? EmptySecret;

            byte[] finalTranscript = SyncProtocol.MixHash(transcript, self.PublicKey, secret);
            var keys = SyncProtocol.DeriveTransportKeys(
                finalTranscript, dhEphemeral, dhToResponderStatic, dhStaticToEphemeral, secret);

            Wipe(handshakeKey, dhEphemeral, dhToResponderStatic, dhStaticToEphemeral);

            return new HandshakeResult
            {
                PeerPublicKey = responderPublicKey,
                SendKey = keys.InitiatorToResponder,
                ReceiveKey = keys.ResponderToInitiator,
                SessionCode = SyncProtocol.SessionCodeFrom(finalTranscript),
            };
        }
        finally
        {
            ephemeral.Dispose();
        }
    }

    /// <summary>
    /// Run the responder side (the device showing the QR, or listening for a known peer).
    ///
    /// <paramref name="isTrusted"/> is consulted in Reconnect mode only, and decides
    /// whether the peer that just proved its identity is one we have paired with.
    /// </summary>
    public static async Task<HandshakeResult> RespondAsync(
        Stream stream,
        DeviceIdentity self,
        HandshakeMode mode,
        byte[]? pairingSecret = null,
        Func<byte[], bool>? isTrusted = null,
        CancellationToken cancellationToken = default)
    {
        RequireSecretForMode(mode, pairingSecret);

        var ephemeral = DeviceIdentity.Create();
        try
        {
            // --- message 1: their ephemeral ---
            byte[] msg1 = await ReadMessageAsync(stream, cancellationToken);
            (int version, byte[] peerEphemeral) = Unframe(msg1);
            if (version != SyncProtocol.Version)
                throw new HandshakeException(HandshakeFailure.VersionMismatch,
                    $"Peer speaks protocol version {version}, this device speaks {SyncProtocol.Version}.");

            // --- message 2: our ephemeral ---
            await WriteMessageAsync(stream, Frame(SyncProtocol.Version, ephemeral.PublicKey), cancellationToken);

            byte[] dhEphemeral = ephemeral.Agree(peerEphemeral);
            byte[] dhToResponderStatic = self.Agree(peerEphemeral);

            byte[] transcript = BuildTranscript(self.PublicKey, peerEphemeral, ephemeral.PublicKey);
            byte[] handshakeKey = DeriveHandshakeKey(transcript, dhEphemeral, dhToResponderStatic);

            // --- message 3: their static key, encrypted ---
            byte[] msg3 = await ReadMessageAsync(stream, cancellationToken);
            byte[] peerStatic = Open(handshakeKey, msg3, transcript)
                ?? throw new HandshakeException(HandshakeFailure.Malformed,
                    "The peer's identity message did not authenticate.");

            if (peerStatic.Length != DeviceIdentity.PublicKeySize)
                throw new HandshakeException(HandshakeFailure.Malformed, "Peer sent a malformed identity key.");

            // Policy: on reconnect, an unrecognised device gets nothing.
            if (mode == HandshakeMode.Reconnect && !(isTrusted?.Invoke(peerStatic) ?? false))
                throw new HandshakeException(HandshakeFailure.UnknownPeer,
                    "This device is not paired with us.");

            byte[] dhStaticToEphemeral = ephemeral.Agree(peerStatic);
            byte[] secret = pairingSecret ?? EmptySecret;

            byte[] finalTranscript = SyncProtocol.MixHash(transcript, peerStatic, secret);
            var keys = SyncProtocol.DeriveTransportKeys(
                finalTranscript, dhEphemeral, dhToResponderStatic, dhStaticToEphemeral, secret);

            Wipe(handshakeKey, dhEphemeral, dhToResponderStatic, dhStaticToEphemeral);

            return new HandshakeResult
            {
                PeerPublicKey = peerStatic,
                // Mirrored: the responder sends on the responder-to-initiator key.
                SendKey = keys.ResponderToInitiator,
                ReceiveKey = keys.InitiatorToResponder,
                SessionCode = SyncProtocol.SessionCodeFrom(finalTranscript),
            };
        }
        finally
        {
            ephemeral.Dispose();
        }
    }

    // ------------------------------------------------------------- internals

    private static void RequireSecretForMode(HandshakeMode mode, byte[]? pairingSecret)
    {
        if (mode == HandshakeMode.Pair && (pairingSecret is null || pairingSecret.Length == 0))
            throw new ArgumentException("Pairing requires the secret from the QR code.", nameof(pairingSecret));
    }

    /// <summary>
    /// Everything public that has been said so far. Both sides build this from the same
    /// inputs in the same order; if any message was altered in flight, the transcripts
    /// diverge and no shared key is ever reached.
    /// </summary>
    private static byte[] BuildTranscript(
        byte[] responderStatic, byte[] initiatorEphemeral, byte[] responderEphemeral) =>
        SyncProtocol.MixHash(
            SyncProtocol.InitialTranscript(),
            responderStatic, initiatorEphemeral, responderEphemeral);

    private static byte[] DeriveHandshakeKey(byte[] transcript, byte[] dh1, byte[] dh2)
    {
        byte[] combined = new byte[dh1.Length + dh2.Length];
        Buffer.BlockCopy(dh1, 0, combined, 0, dh1.Length);
        Buffer.BlockCopy(dh2, 0, combined, dh1.Length, dh2.Length);
        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: combined,
                outputLength: SyncProtocol.KeySize,
                salt: transcript,
                info: Encoding.UTF8.GetBytes("lockwell-sync-v1:handshake"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    /// <summary>
    /// Encrypt with the transcript as associated data, so a message cannot be lifted
    /// out of one handshake and replayed into another.
    /// </summary>
    private static byte[] Seal(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        byte[] nonce = SyncProtocol.NonceFor(0);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[SyncProtocol.TagSize];

        using var aead = new ChaCha20Poly1305(key);
        aead.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        byte[] output = new byte[tag.Length + ciphertext.Length];
        Buffer.BlockCopy(tag, 0, output, 0, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, output, tag.Length, ciphertext.Length);
        return output;
    }

    private static byte[]? Open(byte[] key, byte[] sealedData, byte[] associatedData)
    {
        if (sealedData.Length < SyncProtocol.TagSize) return null;

        byte[] nonce = SyncProtocol.NonceFor(0);
        byte[] tag = sealedData[..SyncProtocol.TagSize];
        byte[] ciphertext = sealedData[SyncProtocol.TagSize..];
        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aead = new ChaCha20Poly1305(key);
            aead.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null; // wrong key, wrong secret, or tampering
        }
    }

    private static byte[] Frame(int version, byte[] payload)
    {
        byte[] framed = new byte[1 + payload.Length];
        framed[0] = (byte)version;
        Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);
        return framed;
    }

    private static (int Version, byte[] Payload) Unframe(byte[] message)
    {
        if (message.Length < 1 + DeviceIdentity.PublicKeySize)
            throw new HandshakeException(HandshakeFailure.Malformed, "Handshake message was too short.");
        return (message[0], message[1..]);
    }

    private static void Wipe(params byte[][] buffers)
    {
        foreach (byte[] buffer in buffers)
            CryptographicOperations.ZeroMemory(buffer);
    }

    // ------------------------------------------------------------- wire I/O

    private static async Task WriteMessageAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        byte[] header = new byte[4];
        header[0] = (byte)(payload.Length >> 24);
        header[1] = (byte)(payload.Length >> 16);
        header[2] = (byte)(payload.Length >> 8);
        header[3] = (byte)payload.Length;

        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async Task<byte[]> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        byte[] header = await ReadExactAsync(stream, 4, token);
        int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];

        // A hostile peer must not be able to make us allocate arbitrarily.
        if (length is <= 0 or > MaxMessage)
            throw new HandshakeException(HandshakeFailure.Malformed,
                $"Handshake message length {length} is out of range.");

        return await ReadExactAsync(stream, length, token);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int got = await stream.ReadAsync(buffer.AsMemory(read, count - read), token);
            if (got == 0)
                throw new HandshakeException(HandshakeFailure.Malformed,
                    "The connection closed part way through the handshake.");
            read += got;
        }
        return buffer;
    }
}
