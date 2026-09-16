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

using System.Text;
using Lockwell.Update;

namespace Lockwell.Tests;

/// <summary>
/// The update path, which is the most dangerous code in the application.
///
/// Everything else in Lockwell protects data that is already on the machine. This is the
/// one feature that takes bytes from the internet and turns them into code running as the
/// user, beside an unlocked vault. So the questions these checks ask are not "does the
/// happy path work" but "what happens when the bytes are hostile": a forged signature, a
/// swapped package, a downgrade, a manifest that is valid but points somewhere it should
/// not.
/// </summary>
internal static class UpdateChecks
{
    public static void Run()
    {
        Signing();
        ManifestParsing();
        VersionOrdering();
        HostileManifests();
        PackageVerification();
        ShippedKeyIsUsable();
    }

    // ------------------------------------------------------------------ signing

    private static void Signing()
    {
        Check.Section("57. A release is only believed if it carries the right signature");

        var (privateKey, publicKey) = ReleaseSigning.CreateKeyPair();
        Check.That("a signing keypair can be generated", privateKey.Length > 0);
        Check.That($"the public key is the raw curve point ({publicKey.Length} bytes)",
            publicKey.Length == ReleaseSigning.PublicKeySize);

        byte[] manifest = Encoding.UTF8.GetBytes("""{"manifest":1,"version":"1.2.0"}""");
        byte[] signature = ReleaseSigning.Sign(manifest, privateKey);

        Check.That($"a signature is fixed size ({signature.Length} bytes)",
            signature.Length == ReleaseSigning.SignatureSize);
        Check.That("the real signature verifies", ReleaseSigning.Verify(manifest, signature, publicKey));

        // The attack this whole mechanism exists for: someone who can publish bytes, but
        // does not hold the key.
        var (_, attackerPublic) = ReleaseSigning.CreateKeyPair();
        Check.That("a signature from a different key is refused",
            !ReleaseSigning.Verify(manifest, signature, attackerPublic));

        var (attackerPrivate, _) = ReleaseSigning.CreateKeyPair();
        byte[] forged = ReleaseSigning.Sign(manifest, attackerPrivate);
        Check.That("an attacker signing the same manifest with their own key is refused",
            !ReleaseSigning.Verify(manifest, forged, publicKey));

        byte[] tampered = (byte[])manifest.Clone();
        tampered[^3] ^= 0x01;
        Check.That("one flipped byte in the manifest invalidates the signature",
            !ReleaseSigning.Verify(tampered, signature, publicKey));

        byte[] bentSignature = (byte[])signature.Clone();
        bentSignature[0] ^= 0x01;
        Check.That("one flipped byte in the signature invalidates it",
            !ReleaseSigning.Verify(manifest, bentSignature, publicKey));

        Check.That("an empty manifest is refused",
            !ReleaseSigning.Verify([], signature, publicKey));
        Check.That("a truncated signature is refused",
            !ReleaseSigning.Verify(manifest, signature[..32], publicKey));
        Check.That("a malformed public key is refused",
            !ReleaseSigning.Verify(manifest, signature, new byte[ReleaseSigning.PublicKeySize]));
        Check.That("a wrong-sized public key is refused",
            !ReleaseSigning.Verify(manifest, signature, [1, 2, 3]));

        // Junk that is the right shape must be a plain "no", never an exception: the
        // caller is deciding whether to replace its own executable.
        var noise = new byte[ReleaseSigning.PublicKeySize];
        Random.Shared.NextBytes(noise);
        bool threw = false;
        try { ReleaseSigning.Verify(manifest, signature, noise); }
        catch { threw = true; }
        Check.That("a random public key returns false rather than throwing", !threw);

        byte[] derivedPublic = ReleaseSigning.PublicKeyFromPrivate(privateKey);
        Check.That("the public key can be re-derived from the private key",
            derivedPublic.SequenceEqual(publicKey));

        Check.Section("58. Package contents are checked against the signed hash");

        byte[] package = Encoding.UTF8.GetBytes("pretend this is a 60 MB zip");
        string digest = ReleaseSigning.HashBytes(package);
        Check.That("a digest is 64 hex characters", digest.Length == 64);
        Check.That("hashing is stable", ReleaseSigning.HashBytes(package) == digest);
        Check.That("the digest matches itself", ReleaseSigning.HashesMatch(digest, digest));
        Check.That("case does not matter when comparing digests",
            ReleaseSigning.HashesMatch(digest, digest.ToUpperInvariant()));

        byte[] swapped = Encoding.UTF8.GetBytes("pretend this is a 60 MB zip of malware");
        Check.That("a swapped package does not match the signed digest",
            !ReleaseSigning.HashesMatch(ReleaseSigning.HashBytes(swapped), digest));
        Check.That("a truncated digest does not match", !ReleaseSigning.HashesMatch(digest[..63], digest));
        Check.That("a null digest does not match", !ReleaseSigning.HashesMatch(null!, digest));
    }

    // --------------------------------------------------------------- parsing

    private static void ManifestParsing()
    {
        Check.Section("59. A release manifest round-trips and is validated");

        var manifest = SampleManifest();
        byte[] json = manifest.ToUtf8Json();
        var parsed = ReleaseManifest.Parse(json);

        Check.That("a manifest survives being written and read back", parsed is not null);
        Check.That("the version survives", parsed!.Version == "1.2.0");
        Check.That("the package name survives", parsed.PackageName == "Lockwell-win-x64.zip");
        Check.That("the digest survives", parsed.PackageSha256 == manifest.PackageSha256);
        Check.That("the size survives", parsed.PackageSize == manifest.PackageSize);

        Check.That("garbage is refused rather than throwing",
            ReleaseManifest.Parse(Encoding.UTF8.GetBytes("not json at all")) is null);
        Check.That("an empty document is refused",
            ReleaseManifest.Parse(Encoding.UTF8.GetBytes("{}")) is null);

        var future = SampleManifest();
        future.ManifestVersion = 2;
        Check.That("a manifest format from the future is refused, not guessed at",
            ReleaseManifest.Parse(future.ToUtf8Json()) is null);
    }

    // -------------------------------------------------------------- ordering

    private static void VersionOrdering()
    {
        Check.Section("60. Only a newer release counts as an update");

        var running = new Version(1, 1, 0, 0);

        Check.That("a newer release is an update", At("1.2.0").IsUpgradeFrom(running));
        Check.That("a much newer release is an update", At("2.0.0").IsUpgradeFrom(running));
        Check.That("the same version is not an update", !At("1.1.0").IsUpgradeFrom(running));
        Check.That("an older version is never an update", !At("1.0.0").IsUpgradeFrom(running));
        Check.That("a much older version is never an update", !At("0.9.0").IsUpgradeFrom(running));

        Check.Note("the downgrade case matters: an old manifest is still correctly signed");

        Check.That("a tag written with a leading v parses",
            ReleaseManifest.ParseVersion("v1.2.0") == new Version(1, 2, 0, 0));
        Check.That("a tag written without one parses the same",
            ReleaseManifest.ParseVersion("1.2.0") == new Version(1, 2, 0, 0));
        Check.That("1.1 and 1.1.0 are the same release",
            ReleaseManifest.ParseVersion("1.1") == ReleaseManifest.ParseVersion("1.1.0"));
        Check.That("a pre-release suffix is tolerated",
            ReleaseManifest.ParseVersion("v1.2.0-beta.1") == new Version(1, 2, 0, 0));
        Check.That("build metadata is tolerated",
            ReleaseManifest.ParseVersion("1.2.0+build7") == new Version(1, 2, 0, 0));
        Check.That("nonsense yields nothing rather than a default version",
            ReleaseManifest.ParseVersion("banana") is null);
        Check.That("an empty tag yields nothing", ReleaseManifest.ParseVersion("") is null);
        Check.That("a null tag yields nothing", ReleaseManifest.ParseVersion(null) is null);

        // A release that needs a newer starting point than the running build.
        var gated = At("2.0.0");
        gated.MinimumFromVersion = "1.5.0";
        Check.That("a release is not offered when the running build is below its floor",
            !gated.IsUpgradeFrom(running));
        Check.That("and that case is reported rather than silently finding nothing",
            gated.RequiresManualInstall(running));
        Check.That("a build above the floor is offered normally",
            gated.IsUpgradeFrom(new Version(1, 6, 0, 0)));
        Check.That("and is not told to reinstall by hand",
            !gated.RequiresManualInstall(new Version(1, 6, 0, 0)));
    }

    // ------------------------------------------------------------- hostility

    private static void HostileManifests()
    {
        Check.Section("61. A signed manifest still has to be sane");

        Check.Note("a signature proves who wrote it, not that they wrote it correctly");

        var noPackage = SampleManifest();
        noPackage.PackageName = "";
        Check.That("a manifest naming no package is refused", !noPackage.IsWellFormed());

        var badDigest = SampleManifest();
        badDigest.PackageSha256 = "nope";
        Check.That("a manifest with a malformed digest is refused", !badDigest.IsWellFormed());

        var shortDigest = SampleManifest();
        shortDigest.PackageSha256 = new string('a', 63);
        Check.That("a digest of the wrong length is refused", !shortDigest.IsWellFormed());

        var nonHex = SampleManifest();
        nonHex.PackageSha256 = new string('z', 64);
        Check.That("a digest that is not hex is refused", !nonHex.IsWellFormed());

        var noSize = SampleManifest();
        noSize.PackageSize = 0;
        Check.That("a manifest with no package size is refused", !noSize.IsWellFormed());

        var negative = SampleManifest();
        negative.PackageSize = -1;
        Check.That("a negative package size is refused", !negative.IsWellFormed());

        // The package name picks an asset and becomes a file name. It must not be able to
        // become a path.
        foreach (string escape in new[]
                 {
                     "../Lockwell.exe",
                     "..\\..\\Windows\\System32\\evil.dll",
                     "sub/dir/pkg.zip",
                     "sub\\dir\\pkg.zip",
                     "C:/Windows/System32/pkg.zip",
                 })
        {
            var traversal = SampleManifest();
            traversal.PackageName = escape;
            Check.That($"a package name of \"{escape}\" is refused", !traversal.IsWellFormed());
        }

        var badVersion = SampleManifest();
        badVersion.Version = "not-a-version";
        Check.That("a manifest with an unparseable version is refused", !badVersion.IsWellFormed());

        var ok = SampleManifest();
        Check.That("and a well-formed manifest is still accepted", ok.IsWellFormed());
    }


    // ------------------------------------------------------- package verification

    private static void PackageVerification()
    {
        Check.Section("62. Nothing is installed that did not verify");

        Check.Note("this is the check that runs where files are actually replaced");

        var (privateKey, publicKey) = ReleaseSigning.CreateKeyPair();
        string scratch = Check.Scratch("update");

        try
        {
            byte[] package = new byte[4096];
            Random.Shared.NextBytes(package);
            string packagePath = Path.Combine(scratch, "Lockwell-win-x64.zip");
            File.WriteAllBytes(packagePath, package);

            var manifest = new ReleaseManifest
            {
                ManifestVersion = 1,
                Version = "1.3.0",
                Tag = "v1.3.0",
                ReleasedUtc = DateTimeOffset.UtcNow,
                PackageName = "Lockwell-win-x64.zip",
                PackageSize = package.Length,
                PackageSha256 = ReleaseSigning.HashFile(packagePath),
                Notes = "Example.",
            };

            byte[] manifestBytes = manifest.ToUtf8Json();
            byte[] signature = ReleaseSigning.Sign(manifestBytes, privateKey);

            var good = ReleaseTrust.VerifyPackage(manifestBytes, signature, packagePath, publicKey);
            Check.That("a genuine release with matching contents is trusted", good.IsTrusted);
            Check.That("and the manifest comes back with it", good.Manifest?.Version == "1.3.0");

            // The attack the second check exists for: the manifest is genuinely signed,
            // but the file sitting next to it is not the file that was signed.
            File.WriteAllBytes(packagePath, [.. package, 0x00]);
            var swapped = ReleaseTrust.VerifyPackage(manifestBytes, signature, packagePath, publicKey);
            Check.That("a swapped package under a genuine manifest is refused", !swapped.IsTrusted);
            Check.That("and it is reported as the wrong size, not a bad signature",
                swapped.Verdict == ReleaseTrust.Verdict.WrongSize);

            // Same length, different bytes: size alone would let this through.
            byte[] evil = new byte[package.Length];
            Random.Shared.NextBytes(evil);
            File.WriteAllBytes(packagePath, evil);
            var replaced = ReleaseTrust.VerifyPackage(manifestBytes, signature, packagePath, publicKey);
            Check.That("a same-size package with different contents is refused", !replaced.IsTrusted);
            Check.That("and it is reported as wrong contents",
                replaced.Verdict == ReleaseTrust.Verdict.WrongContents);

            // Put the real package back and attack the manifest instead.
            File.WriteAllBytes(packagePath, package);

            var (attackerPrivate, _) = ReleaseSigning.CreateKeyPair();
            byte[] forgedSignature = ReleaseSigning.Sign(manifestBytes, attackerPrivate);
            var forged = ReleaseTrust.VerifyPackage(manifestBytes, forgedSignature, packagePath, publicKey);
            Check.That("a manifest signed by the wrong key is refused", !forged.IsTrusted);
            Check.That("and it is reported as a bad signature",
                forged.Verdict == ReleaseTrust.Verdict.BadSignature);

            // An attacker who rewrites the digest to match their own package still has to
            // sign it, and cannot.
            var rewritten = new ReleaseManifest
            {
                ManifestVersion = 1,
                Version = "9.9.9",
                Tag = "v9.9.9",
                PackageName = "Lockwell-win-x64.zip",
                PackageSize = evil.Length,
                PackageSha256 = ReleaseSigning.HashBytes(evil),
                Notes = "Totally legitimate.",
            };
            var unsigned = ReleaseTrust.VerifyPackage(
                rewritten.ToUtf8Json(), signature, packagePath, publicKey);
            Check.That("a rewritten manifest cannot reuse the original signature", !unsigned.IsTrusted);
            Check.That("and is reported as a bad signature",
                unsigned.Verdict == ReleaseTrust.Verdict.BadSignature);

            var missing = ReleaseTrust.VerifyPackage(
                manifestBytes, signature, Path.Combine(scratch, "not-here.zip"), publicKey);
            Check.That("a missing package is refused", !missing.IsTrusted);
            Check.That("and is reported as missing",
                missing.Verdict == ReleaseTrust.Verdict.MissingPackage);

            // A build with no key configured must refuse, not fall back to trusting.
            var noKey = ReleaseTrust.VerifyPackage(manifestBytes, signature, packagePath, []);
            Check.That("a build with no signing key refuses rather than trusting blindly",
                !noKey.IsTrusted);
            Check.That("and says so plainly",
                noKey.Verdict == ReleaseTrust.Verdict.NoSigningKey);

            foreach (var verdict in Enum.GetValues<ReleaseTrust.Verdict>())
            {
                var explained = new ReleaseTrust.Result(verdict, null).Explain();
                Check.That($"the \"{verdict}\" outcome has something to show a user",
                    !string.IsNullOrWhiteSpace(explained) && explained.Length > 20);
            }
        }
        finally
        {
            Check.Cleanup(scratch);
        }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The key that actually ships.
    ///
    /// Every other check in this file proves the verification rules are right using a
    /// throwaway keypair. None of them notice if the build was shipped with the key
    /// constant still empty or half-pasted, which would leave updating silently disabled
    /// in a release that advertises it. This is the check that looks at the real value.
    /// </summary>
    private static void ShippedKeyIsUsable()
    {
        Check.Section("65. The build ships a usable release signing key");

        Check.That("the key constant is not empty",
            !string.IsNullOrWhiteSpace(ReleaseTrust.SigningPublicKeyHex));

        Check.That($"it is the right length for a P-256 point ({ReleaseSigning.PublicKeySize * 2} hex chars)",
            ReleaseTrust.SigningPublicKeyHex.Length == ReleaseSigning.PublicKeySize * 2);

        Check.That("it parses as hex and this build will therefore verify updates",
            ReleaseTrust.TryGetPublicKey(out _) && ReleaseTrust.IsConfigured);

        // A public key is meant to be public, but the private half never is. If the
        // constant ever held a PKCS#8 blob by mistake it would be exactly this long and
        // look exactly this plausible, so check the shape rather than trusting it.
        Check.That("it is a raw public point, not a wrapped private key",
            ReleaseTrust.TryGetPublicKey(out byte[] shipped)
            && shipped.Length == ReleaseSigning.PublicKeySize);

        // Signing with a fresh key and checking it against the shipped one must fail.
        // If this ever passed, the shipped key would be one an attacker could derive.
        var (otherPrivate, _) = ReleaseSigning.CreateKeyPair();
        byte[] message = SampleManifest().ToUtf8Json();
        byte[] foreignSignature = ReleaseSigning.Sign(message, otherPrivate);

        Check.That("a release signed by any other key is refused by the shipped key",
            ReleaseTrust.VerifyManifest(message, foreignSignature).Verdict == ReleaseTrust.Verdict.BadSignature);
    }

    private static ReleaseManifest At(string version)
    {
        var m = SampleManifest();
        m.Version = version;
        m.Tag = "v" + version;
        return m;
    }

    private static ReleaseManifest SampleManifest() => new()
    {
        ManifestVersion = 1,
        Version = "1.2.0",
        Tag = "v1.2.0",
        ReleasedUtc = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
        PackageName = "Lockwell-win-x64.zip",
        PackageSize = 64_313_005,
        PackageSha256 = new string('a', 64),
        Notes = "Example release.",
    };
}
