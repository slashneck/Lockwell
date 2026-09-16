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

namespace Lockwell.Sync;

/// <summary>
/// The encrypted tunnel that exists once a handshake has succeeded.
///
/// Every frame is ChaCha20-Poly1305 with a counter nonce that never resets inside a
/// session, so a nonce is never reused under a key. The counter is also authenticated,
/// which means a frame cannot be dropped, duplicated or reordered without the receiver
/// noticing -- a truncated transfer fails loudly rather than producing a corrupt file.
///
/// See docs/SYNC-PROTOCOL.md section 6.
/// </summary>
public sealed class SyncSession : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;

    private ulong _sendCounter;
    private ulong _receiveCounter;
    private bool _disposed;

    /// <summary>The peer's proven identity, carried over from the handshake.</summary>
    public byte[] PeerPublicKey { get; }

    public string PeerFingerprint => DeviceIdentity.FingerprintOf(PeerPublicKey);

    public SyncSession(Stream stream, HandshakeResult handshake)
    {
        _stream = stream;
        _sendKey = handshake.SendKey;
        _receiveKey = handshake.ReceiveKey;
        PeerPublicKey = handshake.PeerPublicKey;
    }

    /// <summary>
    /// Send one frame. Payloads larger than the frame limit must be chunked first.
    ///
    /// Caller beware: this writes straight to the socket, so queueing several large
    /// frames without the far end reading will fill the send buffer and block. A file
    /// transfer must read and write concurrently rather than sending everything first.
    /// </summary>
    public async Task SendAsync(byte[] payload, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (payload.Length > SyncProtocol.MaxFramePayload)
            throw new ArgumentException(
                $"Frame payload of {payload.Length} exceeds the {SyncProtocol.MaxFramePayload} byte limit. Chunk it.",
                nameof(payload));

        ulong counter = _sendCounter++;
        byte[] nonce = SyncProtocol.NonceFor(counter);
        byte[] ciphertext = new byte[payload.Length];
        byte[] tag = new byte[SyncProtocol.TagSize];

        // The counter is associated data, so its position in the stream is authenticated
        // even though it is not secret.
        byte[] associated = SyncProtocol.NonceFor(counter);

        using (var aead = new ChaCha20Poly1305(_sendKey))
            aead.Encrypt(nonce, payload, ciphertext, tag, associated);

        byte[] frame = new byte[4 + SyncProtocol.TagSize + ciphertext.Length];
        WriteLength(frame, SyncProtocol.TagSize + ciphertext.Length);
        Buffer.BlockCopy(tag, 0, frame, 4, SyncProtocol.TagSize);
        Buffer.BlockCopy(ciphertext, 0, frame, 4 + SyncProtocol.TagSize, ciphertext.Length);

        await _stream.WriteAsync(frame, token);
        await _stream.FlushAsync(token);
    }

    /// <summary>
    /// Receive one frame. Throws if it fails to authenticate, which covers a wrong key,
    /// tampering, and any attempt to replay or reorder frames.
    /// </summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] header = await ReadExactAsync(4, token);
        int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];

        if (length < SyncProtocol.TagSize ||
            length > SyncProtocol.TagSize + SyncProtocol.MaxFramePayload)
        {
            throw new InvalidDataException($"Frame length {length} is out of range.");
        }

        byte[] body = await ReadExactAsync(length, token);

        ulong counter = _receiveCounter++;
        byte[] nonce = SyncProtocol.NonceFor(counter);
        byte[] associated = SyncProtocol.NonceFor(counter);

        byte[] tag = body[..SyncProtocol.TagSize];
        byte[] ciphertext = body[SyncProtocol.TagSize..];
        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aead = new ChaCha20Poly1305(_receiveKey);
            aead.Decrypt(nonce, ciphertext, tag, plaintext, associated);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException(
                "A frame failed to authenticate. The connection has been tampered with, " +
                "frames arrived out of order, or the peer is using different keys.");
        }

        return plaintext;
    }

    /// <summary>Split a large payload into frames the tunnel can carry.</summary>
    public static IEnumerable<ReadOnlyMemory<byte>> Chunk(ReadOnlyMemory<byte> data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int size = Math.Min(SyncProtocol.MaxFramePayload, data.Length - offset);
            yield return data.Slice(offset, size);
            offset += size;
        }
    }

    private static void WriteLength(byte[] destination, int length)
    {
        destination[0] = (byte)(length >> 24);
        destination[1] = (byte)(length >> 16);
        destination[2] = (byte)(length >> 8);
        destination[3] = (byte)length;
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken token)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int got = await _stream.ReadAsync(buffer.AsMemory(read, count - read), token);
            if (got == 0) throw new EndOfStreamException("The peer closed the connection.");
            read += got;
        }
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Session keys have no reason to outlive the session.
        CryptographicOperations.ZeroMemory(_sendKey);
        CryptographicOperations.ZeroMemory(_receiveKey);
    }
}
