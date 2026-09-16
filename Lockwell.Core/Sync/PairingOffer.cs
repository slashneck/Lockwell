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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lockwell.Sync;

/// <summary>
/// The contents of the pairing QR code.
///
/// Deliberately carries no vault data at all: only the PC's public key, a single-use
/// secret, and enough to find the PC on the network. Someone who photographs the QR can
/// attempt to pair -- that is the intended model, exactly like a Bluetooth pairing code,
/// because seeing the screen IS the out-of-band authentication. Hence the short expiry
/// and the single-use rule.
///
/// See docs/SYNC-PROTOCOL.md section 4.1.
/// </summary>
public sealed class PairingOffer
{
    /// <summary>Protocol version. Devices refuse to talk across incompatible versions.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = SyncProtocol.Version;

    /// <summary>The offering device's long-term public key.</summary>
    [JsonPropertyName("pk")]
    public string PublicKey { get; set; } = "";

    /// <summary>Single-use pairing secret, mixed into the handshake so it is bound to this QR.</summary>
    [JsonPropertyName("ps")]
    public string PairingSecret { get; set; } = "";

    /// <summary>Display name, so the phone can say what it is pairing with.</summary>
    [JsonPropertyName("n")]
    public string DeviceName { get; set; } = "";

    /// <summary>Unix seconds. Offers are short-lived on purpose.</summary>
    [JsonPropertyName("ep")]
    public long ExpiresAt { get; set; }

    /// <summary>Address hints, to skip discovery on the very first connection.</summary>
    [JsonPropertyName("ad")]
    public string[] Addresses { get; set; } = Array.Empty<string>();

    /// <summary>TCP port the offering device is listening on.</summary>
    [JsonPropertyName("pt")]
    public int Port { get; set; }

    [JsonIgnore]
    public bool HasExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > ExpiresAt;

    /// <summary>How long a freshly created offer stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public static PairingOffer Create(
        DeviceIdentity identity,
        string deviceName,
        int port,
        IEnumerable<string>? addresses = null)
    {
        return new PairingOffer
        {
            Version = SyncProtocol.Version,
            PublicKey = Convert.ToBase64String(identity.PublicKey),
            PairingSecret = Convert.ToBase64String(SecureRandomBytes(32)),
            DeviceName = deviceName,
            ExpiresAt = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds(),
            Addresses = addresses?.ToArray() ?? Array.Empty<string>(),
            Port = port,
        };
    }

    /// <summary>
    /// Text for the QR code. The JSON goes in directly rather than being wrapped in
    /// another layer of Base64: QR handles UTF-8 bytes natively, and the extra layer
    /// added a third to the length for no benefit. Every byte matters here, because a
    /// denser QR is a QR that fails to scan across a desk.
    /// </summary>
    public string ToQrPayload() =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(this, SyncJson.Default.PairingOffer));

    /// <summary>Parse a scanned QR payload. Returns null if it is not a valid offer.</summary>
    public static PairingOffer? FromQrPayload(string payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            byte[] json = Encoding.UTF8.GetBytes(payload);
            var offer = JsonSerializer.Deserialize(json, SyncJson.Default.PairingOffer);
            if (offer is null) return null;
            if (string.IsNullOrEmpty(offer.PublicKey) || string.IsNullOrEmpty(offer.PairingSecret))
                return null;
            return offer;
        }
        catch
        {
            return null;
        }
    }

    // These are derived from the fields above. Without JsonIgnore the serializer emits
    // them too, which silently doubled the QR payload with duplicate key material.
    [JsonIgnore]
    public byte[] PublicKeyBytes => Convert.FromBase64String(PublicKey);

    [JsonIgnore]
    public byte[] PairingSecretBytes => Convert.FromBase64String(PairingSecret);

    /// <summary>The fingerprint the user should see on both screens.</summary>
    [JsonIgnore]
    public string Fingerprint => DeviceIdentity.FingerprintOf(PublicKeyBytes);

    private static byte[] SecureRandomBytes(int count)
    {
        byte[] buffer = new byte[count];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }

}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(PairingOffer))]
public partial class SyncJson : JsonSerializerContext
{
}
