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
/// A device's long-term identity: the keypair that proves "this is the same phone you
/// paired with" on every later connection.
///
/// The private half never leaves the device it was generated on. On Windows it lives
/// inside the encrypted vault; on Android it is held by the platform Keystore. Only the
/// public half is ever transmitted, and only during pairing.
///
/// See docs/SYNC-PROTOCOL.md section 3.
/// </summary>
public sealed class DeviceIdentity
{
    /// <summary>Curve used for key agreement. See <see cref="KeyAgreement"/> for why.</summary>
    public const string CurveName = "nistP256";

    private readonly ECDiffieHellman _key;

    private DeviceIdentity(ECDiffieHellman key) => _key = key;

    /// <summary>Generate a brand new identity. Called once, on first run.</summary>
    public static DeviceIdentity Create() =>
        new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Restore an identity from its stored private key.</summary>
    public static DeviceIdentity Import(byte[] privateKey)
    {
        var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(privateKey, out _);
        return new DeviceIdentity(key);
    }

    /// <summary>Size of a raw public key: the P-256 point as X followed by Y.</summary>
    public const int PublicKeySize = 64;

    /// <summary>
    /// The private key, for storing somewhere that is already protected. The caller is
    /// responsible for never letting this touch unencrypted storage.
    /// </summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    /// <summary>
    /// The public half, as the raw curve point (32-byte X then 32-byte Y).
    ///
    /// Deliberately not the DER/SPKI encoding: that wraps the same 64 bytes in about
    /// 27 bytes of ASN.1 and OID scaffolding, which pushed the pairing QR past the
    /// size where a phone camera scans it reliably. The curve is fixed and known to
    /// both sides, so none of that scaffolding carries information.
    /// </summary>
    public byte[] PublicKey
    {
        get
        {
            ECParameters p = _key.ExportParameters(includePrivateParameters: false);
            byte[] raw = new byte[PublicKeySize];
            CopyFixed(p.Q.X!, raw, 0);
            CopyFixed(p.Q.Y!, raw, 32);
            return raw;
        }
    }

    /// <summary>Left-pad to 32 bytes: exported coordinates can be shorter with leading zeros.</summary>
    private static void CopyFixed(byte[] source, byte[] destination, int offset)
    {
        int pad = 32 - source.Length;
        Buffer.BlockCopy(source, 0, destination, offset + pad, source.Length);
    }

    private static ECDiffieHellman FromRawPublicKey(byte[] raw)
    {
        if (raw.Length != PublicKeySize)
            throw new ArgumentException($"Public key must be {PublicKeySize} bytes.", nameof(raw));

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = raw[..32], Y = raw[32..] },
        });
    }

    /// <summary>
    /// Short human-checkable identifier, shown on both screens during pairing so the
    /// user can confirm they paired with the device they think they did.
    /// Format: "A4F2 91C7 3B08 5E1D".
    /// </summary>
    public string Fingerprint => FingerprintOf(PublicKey);

    public static string FingerprintOf(byte[] publicKey)
    {
        byte[] hash = SHA256.HashData(publicKey);
        return string.Join(' ', Enumerable.Range(0, 4)
            .Select(i => Convert.ToHexString(hash, i * 2, 2)));
    }

    /// <summary>
    /// Raw shared secret with a peer. Never used as a key directly -- it is always run
    /// through HKDF with the handshake transcript first, so two sessions between the
    /// same pair of devices never produce the same keys.
    /// </summary>
    public byte[] Agree(byte[] peerPublicKey)
    {
        using var peer = FromRawPublicKey(peerPublicKey);
        return _key.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public void Dispose() => _key.Dispose();
}
