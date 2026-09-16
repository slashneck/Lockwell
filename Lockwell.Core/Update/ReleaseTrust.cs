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

namespace Lockwell.Update;

/// <summary>
/// The single place that decides whether a downloaded release is genuine, used by both
/// the app and the installer.
///
/// It lives here rather than in either of them because the check has to happen where the
/// files are actually replaced. The app verifying a package and then handing it to an
/// installer that takes anything it is given would put the security property in the wrong
/// process: the guarantee is not "the app checked once", it is "nothing is written into
/// the install directory that did not verify". Both ends run this.
/// </summary>
public static class ReleaseTrust
{
    /// <summary>
    /// The public half of Lockwell's release signing key, as the raw P-256 point in hex.
    ///
    /// Public by design. Knowing this key lets anyone check that a release came from the
    /// holder of the private half; it does not help anyone produce one. The private half
    /// is never in this repository, never in a build, and never on a machine that
    /// publishes anything.
    ///
    /// An empty value is not a mistake that quietly downgrades to unverified updates; it
    /// disables updating entirely, because an updater that cannot tell a genuine release
    /// from a forged one is worse than no updater at all.
    /// </summary>
    public const string SigningPublicKeyHex =
        "2ef7e7c8ab5e818554e59bf5399f7aaaf1f08c9128c0fddcc77d0fcf9b173779"
        + "575e33a7b45d200a9fab671216be7ff5406ebfeac90755cb8fb3d8f00cea3bee";

    /// <summary>Is this build configured to verify and therefore to accept updates?</summary>
    public static bool IsConfigured => TryGetPublicKey(out _);

    public static bool TryGetPublicKey(out byte[] publicKey)
    {
        publicKey = [];
        if (string.IsNullOrWhiteSpace(SigningPublicKeyHex)) return false;
        if (SigningPublicKeyHex.Length != ReleaseSigning.PublicKeySize * 2) return false;

        try
        {
            publicKey = Convert.FromHexString(SigningPublicKeyHex);
            return publicKey.Length == ReleaseSigning.PublicKeySize;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Why a release was refused. Ordered roughly by how alarming it is.</summary>
    public enum Verdict
    {
        /// <summary>Signature and contents both check out.</summary>
        Trusted,

        /// <summary>This build has no signing key, so it cannot verify anything.</summary>
        NoSigningKey,

        /// <summary>The manifest is not valid JSON, or not a format this build knows.</summary>
        MalformedManifest,

        /// <summary>
        /// The signature does not match. Either the manifest was altered in transit, or it
        /// was published by someone who does not hold the signing key.
        /// </summary>
        BadSignature,

        /// <summary>The package is not the size the signed manifest says it is.</summary>
        WrongSize,

        /// <summary>
        /// The package hashes to something other than what the manifest promised. The
        /// manifest is genuine; the file attached to it is not the file that was signed.
        /// </summary>
        WrongContents,

        /// <summary>The package file is not where it was said to be.</summary>
        MissingPackage,
    }

    public sealed record Result(Verdict Verdict, ReleaseManifest? Manifest)
    {
        public bool IsTrusted => Verdict == Verdict.Trusted && Manifest is not null;

        /// <summary>A sentence fit to show a user who is about to install something.</summary>
        public string Explain() => Verdict switch
        {
            Verdict.Trusted => "This release is signed by Lockwell and its contents match.",
            Verdict.NoSigningKey =>
                "This build has no update signing key, so it cannot check whether a release "
                + "is genuine. Updating is disabled. Download from the releases page instead.",
            Verdict.MalformedManifest =>
                "The release information could not be read. Nothing was installed.",
            Verdict.BadSignature =>
                "This release is not signed by Lockwell. It was either altered on the way "
                + "here or published by someone else. Nothing was installed.",
            Verdict.WrongSize =>
                "The downloaded package is not the size the signed release says it should "
                + "be. Nothing was installed.",
            Verdict.WrongContents =>
                "The downloaded package does not match the signed release. It may have been "
                + "corrupted or replaced. Nothing was installed.",
            Verdict.MissingPackage => "The downloaded package could not be found.",
            _ => "The release could not be verified. Nothing was installed.",
        };
    }

    /// <summary>
    /// Check a manifest's signature only. Used for the "is there an update?" question,
    /// before anything large is downloaded.
    /// </summary>
    public static Result VerifyManifest(byte[] manifestBytes, byte[] signature) =>
        TryGetPublicKey(out byte[] publicKey)
            ? VerifyManifest(manifestBytes, signature, publicKey)
            : new Result(Verdict.NoSigningKey, null);

    /// <summary>
    /// The same check against an explicitly supplied key.
    ///
    /// This overload exists so the verification rules can be tested against a throwaway
    /// keypair. The compiled-in key is a build-time constant, and a security check that
    /// can only be exercised by shipping a real release is a security check nobody runs.
    /// </summary>
    public static Result VerifyManifest(byte[] manifestBytes, byte[] signature, byte[] publicKey)
    {
        if (publicKey is not { Length: ReleaseSigning.PublicKeySize })
            return new Result(Verdict.NoSigningKey, null);

        // Signature first, contents second. Parsing untrusted JSON is a smaller attack
        // surface than most things, but it is still work done on an attacker's bytes, and
        // there is no reason to do any of it before knowing who wrote them.
        if (!ReleaseSigning.Verify(manifestBytes, signature, publicKey))
            return new Result(Verdict.BadSignature, null);

        var manifest = ReleaseManifest.Parse(manifestBytes);
        return manifest is null
            ? new Result(Verdict.MalformedManifest, null)
            : new Result(Verdict.Trusted, manifest);
    }

    /// <summary>
    /// Check a manifest's signature and then that the downloaded package is the exact file
    /// that manifest describes. This is what must pass before anything is written into an
    /// install directory.
    /// </summary>
    public static Result VerifyPackage(byte[] manifestBytes, byte[] signature, string packagePath) =>
        TryGetPublicKey(out byte[] publicKey)
            ? VerifyPackage(manifestBytes, signature, packagePath, publicKey)
            : new Result(Verdict.NoSigningKey, null);

    /// <summary>The same check against an explicitly supplied key. See the overload above.</summary>
    public static Result VerifyPackage(
        byte[] manifestBytes, byte[] signature, string packagePath, byte[] publicKey)
    {
        var result = VerifyManifest(manifestBytes, signature, publicKey);
        if (!result.IsTrusted) return result;

        ReleaseManifest manifest = result.Manifest!;

        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            return new Result(Verdict.MissingPackage, manifest);

        // Size before hash: it is free, and it means a wildly wrong file is rejected
        // without reading tens of megabytes through SHA-256 first.
        var info = new FileInfo(packagePath);
        if (info.Length != manifest.PackageSize)
            return new Result(Verdict.WrongSize, manifest);

        string actual = ReleaseSigning.HashFile(packagePath);
        return ReleaseSigning.HashesMatch(actual, manifest.PackageSha256)
            ? new Result(Verdict.Trusted, manifest)
            : new Result(Verdict.WrongContents, manifest);
    }
}
