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

using System.Globalization;
using System.Security.Cryptography;
using Lockwell.Update;

namespace Lockwell.ReleaseKit;

/// <summary>
/// The publisher side of the update trust chain.
///
/// Lockwell decides whether an update is genuine by checking a detached signature over a
/// release manifest against a public key compiled into the build. This tool is what
/// produces the other half: it generates the keypair, and it signs a manifest for a
/// package that has actually been built. It links <c>Lockwell.Core</c> and calls
/// <see cref="ReleaseSigning"/> directly, so signing and verification can never drift
/// apart the way they would if the signature format were reimplemented in a script.
///
/// The private key never enters the repository, never enters the app, and is never
/// printed. It is written once, to a path chosen by whoever runs <c>keygen</c>.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "keygen" => KeyGen(args),
                "sign" => Sign(args),
                "verify" => VerifyCommand(args),
                "pubkey" => PubKey(args),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            ReleaseKit - Lockwell release signing

              keygen  --private <path>
                      Create a signing keypair. Writes the private key to <path> and
                      prints the public key hex to paste into ReleaseTrust.

              pubkey  --private <path>
                      Print the public key hex for an existing private key.

              sign    --private <path> --package <zip> --version <x.y.z>
                      [--tag v<x.y.z>] [--notes <text>] [--notes-file <path>]
                      [--min-from <x.y.z>] --out <dir>
                      Measure the package, write release.json and release.json.sig,
                      then verify what was written before reporting success.

              verify  --manifest <path> --signature <path> --package <zip>
                      --public <hex>
                      Check a signed release the way the app and installer will.
            """);
        return 2;
    }

    // ---------------------------------------------------------------- keygen

    private static int KeyGen(string[] args)
    {
        string privatePath = Required(args, "--private");

        // Refusing to overwrite is the point. A signing key that is silently replaced
        // orphans every release signed with the previous one, and there is no way to
        // notice from the output that it happened.
        if (File.Exists(privatePath))
        {
            Console.Error.WriteLine($"error: {privatePath} already exists. Refusing to overwrite a signing key.");
            return 1;
        }

        (byte[] pkcs8, byte[] publicKey) = ReleaseSigning.CreateKeyPair();

        string? dir = Path.GetDirectoryName(Path.GetFullPath(privatePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(privatePath, pkcs8);
        CryptographicOperations.ZeroMemory(pkcs8);

        Console.WriteLine("Private key written to: " + Path.GetFullPath(privatePath));
        Console.WriteLine();
        Console.WriteLine("Public key hex (this is the value ReleaseTrust.SigningPublicKeyHex needs):");
        Console.WriteLine(Convert.ToHexString(publicKey).ToLowerInvariant());
        Console.WriteLine();
        Console.WriteLine("Back the private key up offline. Losing it means no future release can");
        Console.WriteLine("be signed with this key; leaking it means someone else can sign one.");
        return 0;
    }

    private static int PubKey(string[] args)
    {
        byte[] pkcs8 = File.ReadAllBytes(Required(args, "--private"));
        try
        {
            Console.WriteLine(Convert.ToHexString(ReleaseSigning.PublicKeyFromPrivate(pkcs8)).ToLowerInvariant());
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    // ------------------------------------------------------------------ sign

    private static int Sign(string[] args)
    {
        string privatePath = Required(args, "--private");
        string packagePath = Required(args, "--package");
        string version = Required(args, "--version");
        string outDir = Required(args, "--out");
        string tag = Optional(args, "--tag") ?? "v" + version;
        string minFrom = Optional(args, "--min-from") ?? "";

        string notes = Optional(args, "--notes") ?? "";
        string? notesFile = Optional(args, "--notes-file");
        if (notesFile is not null) notes = File.ReadAllText(notesFile).Trim();

        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Package not found: " + packagePath);

        var package = new FileInfo(packagePath);

        var manifest = new ReleaseManifest
        {
            ManifestVersion = 1,
            Version = version,
            Tag = tag,
            ReleasedUtc = DateTimeOffset.UtcNow,
            PackageName = package.Name,
            PackageSize = package.Length,
            PackageSha256 = ReleaseSigning.HashFile(packagePath),
            Notes = notes,
            MinimumFromVersion = minFrom,
        };

        if (!manifest.IsWellFormed())
            throw new InvalidOperationException("The manifest these inputs produce is not well formed. Check --version and the package name.");

        // Sign the exact bytes that get written. Signing a manifest object and then
        // serialising it again invites a canonicalisation bug where the signature covers
        // something subtly different from the file the app downloads.
        byte[] manifestBytes = manifest.ToUtf8Json();

        byte[] pkcs8 = File.ReadAllBytes(privatePath);
        byte[] signature;
        byte[] publicKey;
        try
        {
            signature = ReleaseSigning.Sign(manifestBytes, pkcs8);
            publicKey = ReleaseSigning.PublicKeyFromPrivate(pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        Directory.CreateDirectory(outDir);
        string manifestOut = Path.Combine(outDir, "release.json");
        string signatureOut = Path.Combine(outDir, "release.json.sig");
        File.WriteAllBytes(manifestOut, manifestBytes);
        File.WriteAllBytes(signatureOut, signature);

        // Verify the files on disk, not the values still in memory. This is the step that
        // catches a truncated write or a stray editor before a broken release is published.
        var check = ReleaseTrust.VerifyPackage(
            File.ReadAllBytes(manifestOut),
            File.ReadAllBytes(signatureOut),
            packagePath,
            publicKey);

        if (!check.IsTrusted)
        {
            Console.Error.WriteLine("error: the signed release did not verify: " + check.Verdict);
            return 1;
        }

        Console.WriteLine("Signed release " + manifest.Tag);
        Console.WriteLine("  package   " + manifest.PackageName);
        Console.WriteLine("  size      " + manifest.PackageSize.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
        Console.WriteLine("  sha256    " + manifest.PackageSha256);
        Console.WriteLine("  manifest  " + manifestOut);
        Console.WriteLine("  signature " + signatureOut);
        Console.WriteLine("  public    " + Convert.ToHexString(publicKey).ToLowerInvariant());
        Console.WriteLine();
        Console.WriteLine("Verified against the signed files on disk.");
        Console.WriteLine("Upload release.json and release.json.sig alongside the package.");
        return 0;
    }

    // ---------------------------------------------------------------- verify

    private static int VerifyCommand(string[] args)
    {
        byte[] manifestBytes = File.ReadAllBytes(Required(args, "--manifest"));
        byte[] signature = File.ReadAllBytes(Required(args, "--signature"));
        string packagePath = Required(args, "--package");
        byte[] publicKey = Convert.FromHexString(Required(args, "--public"));

        var result = ReleaseTrust.VerifyPackage(manifestBytes, signature, packagePath, publicKey);
        Console.WriteLine(result.Verdict + ": " + result.Explain());
        return result.IsTrusted ? 0 : 1;
    }

    // ----------------------------------------------------------------- args

    private static string Required(string[] args, string name) =>
        Optional(args, name) ?? throw new ArgumentException("Missing " + name);

    private static string? Optional(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
