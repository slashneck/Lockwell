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

namespace Lockwell.Update;

/// <summary>
/// Proves that a release manifest was written by whoever holds Lockwell's signing key,
/// and by nobody else.
///
/// The reason this exists rather than "download over HTTPS and trust it": HTTPS proves you
/// reached GitHub. It says nothing about whether what GitHub is serving is what the author
/// published. An account compromise, a stolen token, or anyone who can publish a release
/// would otherwise get to replace a vault application on every machine that has it
/// installed -- next to an unlocked vault, with the user's own privileges. That is the
/// single worst thing an updater can do, so the update path does not trust the transport,
/// the API, the file name, or the server. It trusts one public key, compiled in.
///
/// What this does NOT protect against, stated plainly:
///
/// - Losing the private key. If it leaks, an attacker who can also serve bytes can sign a
///   malicious release, and the only remedy is shipping a build with a new key by some
///   other route. Keep it offline.
/// - A compromised machine. Nothing here helps if malware is already running as the user;
///   it can patch the installed binary directly and never touch the update path.
/// - The first install. A signature check secures updates to an app you already have. It
///   cannot vouch for the installer you downloaded the first time; that is what code
///   signing and the release page checksums are for.
///
/// P-256 with SHA-256, matching <c>DeviceIdentity</c>'s curve. Ed25519 would be the more
/// fashionable choice and is not in .NET 8's base library, and this project does not add a
/// dependency to a security path for fashion. P-256 is in the runtime, is FIPS-blessed,
/// and is the same primitive the pairing code already relies on.
/// </summary>
public static class ReleaseSigning
{
    /// <summary>Raw public key: the P-256 point as 32-byte X followed by 32-byte Y.</summary>
    public const int PublicKeySize = 64;

    /// <summary>Raw signature: r and s, each left-padded to 32 bytes.</summary>
    public const int SignatureSize = 64;

    /// <summary>
    /// Is <paramref name="signature"/> a valid signature over <paramref name="manifestBytes"/>
    /// for <paramref name="publicKey"/>?
    ///
    /// The manifest's exact bytes are signed, not a re-serialisation of a parsed object.
    /// Re-serialising would mean the thing verified and the thing parsed could differ by
    /// whitespace, key order, or number formatting, and every signature scheme that has
    /// been broken by canonicalisation was broken exactly there.
    ///
    /// Returns false rather than throwing for any malformed input. A caller deciding
    /// whether to replace its own executable should get one boolean and no exceptions to
    /// accidentally swallow.
    /// </summary>
    public static bool Verify(byte[] manifestBytes, byte[] signature, byte[] publicKey)
    {
        if (manifestBytes is not { Length: > 0 }) return false;
        if (signature is not { Length: SignatureSize }) return false;
        if (publicKey is not { Length: PublicKeySize }) return false;

        try
        {
            using ECDsa key = ImportPublicKey(publicKey);
            return key.VerifyData(
                manifestBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (
            ex is CryptographicException or NotSupportedException or ArgumentException)
        {
            // A public key that is not a point on the curve ends up here, and it does not
            // arrive as the exception you would expect. On Windows, CNG reports it as
            // PlatformNotSupportedException("the specified curve or its parameters are not
            // valid for this platform") wrapping a CryptographicException, which reads like
            // the machine lacks P-256 support rather than like bad input. Catching only
            // CryptographicException let it escape, and an escaping exception here would
            // have reached a caller that was deciding whether to overwrite its own
            // executable. Checks in section 57 hold this shut.
            return false;
        }
    }

    /// <summary>
    /// Sign a manifest. Used only by the release tooling, never by the app: the shipped
    /// application has no private key and no code path that wants one.
    /// </summary>
    public static byte[] Sign(byte[] manifestBytes, byte[] pkcs8PrivateKey)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(pkcs8PrivateKey);

        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);

        return key.SignData(
            manifestBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Generate a fresh signing keypair. Release tooling only.</summary>
    public static (byte[] Pkcs8PrivateKey, byte[] PublicKey) CreateKeyPair()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKey(), ExportPublicKey(key));
    }

    /// <summary>The public half of a stored private key, for regenerating the constant.</summary>
    public static byte[] PublicKeyFromPrivate(byte[] pkcs8PrivateKey)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        return ExportPublicKey(key);
    }

    private static byte[] ExportPublicKey(ECDsa key)
    {
        ECParameters p = key.ExportParameters(includePrivateParameters: false);
        byte[] raw = new byte[PublicKeySize];
        CopyFixed(p.Q.X!, raw, 0);
        CopyFixed(p.Q.Y!, raw, 32);
        return raw;
    }

    private static ECDsa ImportPublicKey(byte[] raw)
    {
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = raw[..32],
                Y = raw[32..],
            },
        };

        // Throws CryptographicException if the point is not on the curve, which Verify
        // turns into a plain "no".
        parameters.Validate();
        return ECDsa.Create(parameters);
    }

    /// <summary>Left-pad to 32 bytes: exported coordinates can be shorter with leading zeros.</summary>
    private static void CopyFixed(byte[] source, byte[] destination, int offset)
    {
        int pad = 32 - source.Length;
        Buffer.BlockCopy(source, 0, destination, offset + pad, source.Length);
    }

    /// <summary>
    /// Lowercase hex SHA-256 of a file, streamed rather than loaded.
    ///
    /// The package is measured in tens of megabytes and this runs while the vault may be
    /// unlocked; there is no reason to pull it all into memory to hash it.
    /// </summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Lowercase hex SHA-256 of a byte array.</summary>
    public static string HashBytes(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Constant-time comparison of two hex digests.
    ///
    /// Timing is not a realistic attack on a file hash, but the alternative is a plain
    /// string compare on a security decision, and getting into the habit of writing those
    /// is how one eventually lands somewhere it does matter.
    /// </summary>
    public static bool HashesMatch(string a, string b)
    {
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;

        int diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= char.ToLowerInvariant(a[i]) ^ char.ToLowerInvariant(b[i]);

        return diff == 0;
    }
}
