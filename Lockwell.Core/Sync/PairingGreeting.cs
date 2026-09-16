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

namespace Lockwell.Sync;

/// <summary>
/// The first thing the listening device says when someone connects: its protocol
/// version, its long-term public key, and its name.
///
/// Why this exists: the initiator has to know the responder's public key before the
/// handshake, because it both authenticates the responder and salts the pairing code.
/// Typing a 64-byte key is obviously out, so the responder simply states it.
///
/// Sending it in the clear is safe, and worth being precise about why. An impostor can
/// of course send their own key instead. But the pairing code is stretched with that
/// key as salt, so an impostor's key produces a different secret, and without the code
/// they cannot reach the one the phone derived. The key gets the handshake started;
/// the code is what makes it mean anything. The fingerprint both users compare at the
/// end closes the loop by proving which key was actually used.
///
/// The greeting reveals only that a Lockwell device is listening, which is already
/// implied by something answering on the port.
/// </summary>
public static class PairingGreeting
{
    private const int MaxNameBytes = 64;

    public static async Task SendAsync(
        Stream stream, byte[] publicKey, string deviceName, CancellationToken token = default)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(deviceName);
        if (name.Length > MaxNameBytes) name = name[..MaxNameBytes];

        byte[] message = new byte[1 + DeviceIdentity.PublicKeySize + 1 + name.Length];
        message[0] = (byte)SyncProtocol.Version;
        Buffer.BlockCopy(publicKey, 0, message, 1, DeviceIdentity.PublicKeySize);
        message[1 + DeviceIdentity.PublicKeySize] = (byte)name.Length;
        Buffer.BlockCopy(name, 0, message, 2 + DeviceIdentity.PublicKeySize, name.Length);

        await stream.WriteAsync(message, token);
        await stream.FlushAsync(token);
    }

    public static async Task<(byte[] PublicKey, string DeviceName)> ReceiveAsync(
        Stream stream, CancellationToken token = default)
    {
        byte[] head = await ReadExactAsync(stream, 1 + DeviceIdentity.PublicKeySize + 1, token);

        int version = head[0];
        if (version != SyncProtocol.Version)
            throw new HandshakeException(HandshakeFailure.VersionMismatch,
                $"That device speaks protocol version {version}, this one speaks {SyncProtocol.Version}.");

        byte[] publicKey = head[1..(1 + DeviceIdentity.PublicKeySize)];
        int nameLength = head[1 + DeviceIdentity.PublicKeySize];

        // A hostile peer must not be able to make us allocate arbitrarily.
        if (nameLength > MaxNameBytes)
            throw new HandshakeException(HandshakeFailure.Malformed, "Device name was too long.");

        string name = nameLength == 0
            ? "PC"
            : System.Text.Encoding.UTF8.GetString(await ReadExactAsync(stream, nameLength, token));

        return (publicKey, name);
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
                    "The connection closed before the device introduced itself.");
            read += got;
        }
        return buffer;
    }
}
