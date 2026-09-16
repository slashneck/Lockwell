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

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Lockwell.Update;

namespace Lockwell.Services;

/// <summary>
/// Finds out whether a newer Lockwell exists, and if the user asks for it, fetches and
/// verifies the package before handing it to the installer to apply.
///
/// This is the only code in the application that makes an outbound network request. Some
/// deliberate decisions about what it does not do:
///
/// - It sends no version number, no machine identifier, no vault information and no
///   telemetry of any kind. The User-Agent is the literal string "Lockwell", because
///   GitHub requires one and there is nothing else it needs to know. Which version is
///   running is decided here, from a file that is public anyway.
/// - It never runs on its own while the vault is unlocked doing something. The caller
///   decides when, and the caller only does it at most once a day.
/// - It does not apply anything. It verifies, then launches the installer, which is the
///   component that already knows how to elevate and replace files in Program Files.
///   Verification happens again there, because that is where files are written.
///
/// Nothing here touches the vault. The whole update path reads and writes only the
/// installed program directory and a temporary folder.
/// </summary>
internal sealed class UpdateService
{
    public const string RepositoryOwner = "slashneck";
    public const string RepositoryName = "Lockwell";

    /// <summary>The release page, for the "download it yourself" route.</summary>
    public const string ReleasesPageUrl =
        $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases";

    /// <summary>Where the source lives. Shown in Settings, opened on request only.</summary>
    public const string SourceCodeUrl =
        $"https://github.com/{RepositoryOwner}/{RepositoryName}";

    /// <summary>Asset names looked for on a release. Anything else attached is ignored.</summary>
    private const string ManifestAssetName = "release.json";
    private const string SignatureAssetName = "release.json.sig";

    /// <summary>
    /// A manifest is a few hundred bytes and a signature is 64. Any "manifest" larger than
    /// this is not one, and refusing early means a hostile server cannot make the app read
    /// an endless stream into memory.
    /// </summary>
    private const int MaxMetadataBytes = 64 * 1024;

    /// <summary>
    /// A ceiling on the package, so the same trick does not work on the download either.
    /// Well above a real package, which is around 65 MB.
    /// </summary>
    private const long MaxPackageBytes = 400L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
        };

        // Deliberately bare. GitHub rejects requests with no User-Agent, and this is the
        // least it will accept. Appending the running version here would hand over the one
        // piece of information this feature is otherwise careful not to send.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lockwell"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static Version CurrentVersion =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version
        ?? new Version(0, 0, 0, 0);

    /// <summary>What a check concluded.</summary>
    public enum Outcome
    {
        /// <summary>Running the newest release.</summary>
        UpToDate,

        /// <summary>A newer, verified release is available.</summary>
        UpdateAvailable,

        /// <summary>Could not reach GitHub, or it had nothing to say.</summary>
        Unreachable,

        /// <summary>
        /// A release exists but did not verify. This is the interesting one: it means
        /// something is wrong at the other end, and it is reported rather than swallowed.
        /// </summary>
        NotTrusted,

        /// <summary>A newer release exists but this build is too old to jump straight to it.</summary>
        ManualInstallRequired,

        /// <summary>This build has no signing key, so updating is disabled.</summary>
        NotConfigured,
    }

    public sealed record CheckResult(
        Outcome Outcome,
        ReleaseManifest? Manifest,
        string? PackageUrl,
        byte[]? ManifestBytes,
        byte[]? Signature,
        string? Detail)
    {
        public string Describe() => Outcome switch
        {
            Outcome.UpToDate => $"Lockwell {CurrentVersion.ToString(3)} is the latest version.",
            Outcome.UpdateAvailable => $"Lockwell {Manifest!.Version} is available.",
            Outcome.Unreachable => Detail ?? "Could not reach the update server.",
            Outcome.NotTrusted => Detail ?? "The release could not be verified.",
            Outcome.ManualInstallRequired =>
                $"Lockwell {Manifest!.Version} is available, but this build is too old to "
                + "update directly. Download it from the releases page.",
            Outcome.NotConfigured =>
                "This build cannot verify updates, so updating is disabled.",
            _ => "Could not check for updates.",
        };
    }

    /// <summary>
    /// Ask whether a newer release exists.
    ///
    /// Downloads only the manifest and its signature, which together are under a kilobyte.
    /// Nothing large moves until the user has seen what it is and asked for it.
    /// </summary>
    public async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!ReleaseTrust.IsConfigured)
            return new CheckResult(Outcome.NotConfigured, null, null, null, null, null);

        try
        {
            ReleaseAssets? assets = await FindLatestReleaseAssetsAsync(ct).ConfigureAwait(false);
            if (assets is null)
                return new CheckResult(Outcome.Unreachable, null, null, null, null,
                    "No published release was found.");

            byte[]? manifestBytes = await DownloadMetadataAsync(assets.ManifestUrl, ct).ConfigureAwait(false);
            byte[]? signature = await DownloadMetadataAsync(assets.SignatureUrl, ct).ConfigureAwait(false);

            if (manifestBytes is null || signature is null)
                return new CheckResult(Outcome.Unreachable, null, null, null, null,
                    "The release is missing its signed manifest.");

            var verdict = ReleaseTrust.VerifyManifest(manifestBytes, signature);
            if (!verdict.IsTrusted)
                return new CheckResult(Outcome.NotTrusted, verdict.Manifest, null, null, null,
                    verdict.Explain());

            ReleaseManifest manifest = verdict.Manifest!;
            Version running = CurrentVersion;

            if (manifest.RequiresManualInstall(running))
                return new CheckResult(Outcome.ManualInstallRequired, manifest, null, null, null, null);

            if (!manifest.IsUpgradeFrom(running))
                return new CheckResult(Outcome.UpToDate, manifest, null, null, null, null);

            // The manifest names the package; the API listing only tells us where a file of
            // that name happens to live. If the signed name is not attached to the release,
            // that is a mismatch worth refusing rather than guessing around.
            if (!assets.Packages.TryGetValue(manifest.PackageName, out string? packageUrl))
                return new CheckResult(Outcome.NotTrusted, manifest, null, null, null,
                    "The signed release names a package that is not attached to it.");

            return new CheckResult(
                Outcome.UpdateAvailable, manifest, packageUrl, manifestBytes, signature, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return new CheckResult(Outcome.Unreachable, null, null, null, null,
                "Could not reach the update server.");
        }
    }

    /// <summary>
    /// Fetch the package for a checked update and verify it against the signed manifest.
    /// Returns the path to a verified file, or null with the reason in
    /// <paramref name="failure"/>.
    /// </summary>
    public async Task<string?> DownloadVerifiedPackageAsync(
        CheckResult check,
        IProgress<double>? progress,
        Action<string> failure,
        CancellationToken ct = default)
    {
        if (check.Outcome != Outcome.UpdateAvailable || check.Manifest is null
            || check.PackageUrl is null || check.ManifestBytes is null || check.Signature is null)
        {
            failure("There is no verified update to download.");
            return null;
        }

        string stagingDir = Path.Combine(Path.GetTempPath(), "Lockwell", "update");
        Directory.CreateDirectory(stagingDir);

        // The manifest's package name has already been rejected if it contains any path
        // separator or traversal, so combining is safe. Belt and braces: confirm the
        // result still sits inside the staging folder.
        string destination = Path.GetFullPath(Path.Combine(stagingDir, check.Manifest.PackageName));
        if (!destination.StartsWith(Path.GetFullPath(stagingDir) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            failure("The release names a package that cannot be written safely.");
            return null;
        }

        try
        {
            using var response = await Http
                .GetAsync(check.PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                failure("The update package could not be downloaded.");
                return null;
            }

            long? declared = response.Content.Headers.ContentLength;
            if (declared is > MaxPackageBytes)
            {
                failure("The update package is implausibly large and was not downloaded.");
                return null;
            }

            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(destination))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;

                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > MaxPackageBytes)
                    {
                        failure("The update package is implausibly large and was not downloaded.");
                        return null;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    long total = declared ?? check.Manifest.PackageSize;
                    if (total > 0) progress?.Report(Math.Min(100, written / (double)total * 100));
                }
            }

            progress?.Report(100);

            var verdict = ReleaseTrust.VerifyPackage(check.ManifestBytes, check.Signature, destination);
            if (!verdict.IsTrusted)
            {
                TryDelete(destination);
                failure(verdict.Explain());
                return null;
            }

            return destination;
        }
        catch (OperationCanceledException)
        {
            TryDelete(destination);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(destination);
            failure("The update package could not be downloaded.");
            return null;
        }
    }

    /// <summary>
    /// Hand a verified package to the installer and ask it to apply it once this process
    /// has exited.
    ///
    /// The installer is given the manifest and signature alongside the package so it can
    /// repeat the verification itself. It has no reason to take this process's word for
    /// it, and the check belongs next to the file replacement rather than one process away
    /// from it.
    /// </summary>
    public static bool LaunchInstaller(
        string verifiedPackagePath,
        CheckResult check,
        Action<string> failure)
    {
        string? setup = FindInstaller();
        if (setup is null)
        {
            failure(
                "The Lockwell installer could not be found next to the application, so the "
                + "update cannot be applied automatically. Download it from the releases page.");
            return false;
        }

        try
        {
            string metadataDir = Path.GetDirectoryName(verifiedPackagePath)!;
            string manifestPath = Path.Combine(metadataDir, ManifestAssetName);
            string signaturePath = Path.Combine(metadataDir, SignatureAssetName);
            File.WriteAllBytes(manifestPath, check.ManifestBytes!);
            File.WriteAllBytes(signaturePath, check.Signature!);

            var startInfo = new ProcessStartInfo
            {
                FileName = setup,
                UseShellExecute = true,   // required for the UAC prompt
                Verb = "runas",
            };
            startInfo.ArgumentList.Add("/update");
            startInfo.ArgumentList.Add($"--zip={verifiedPackagePath}");
            startInfo.ArgumentList.Add($"--manifest={manifestPath}");
            startInfo.ArgumentList.Add($"--signature={signaturePath}");
            startInfo.ArgumentList.Add($"--pid={Environment.ProcessId}");
            startInfo.ArgumentList.Add($"--version={check.Manifest!.Version}");

            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // A Win32Exception here is usually the user declining the elevation prompt,
            // which is a normal answer and not an error worth alarming them about.
            failure("The update was not started.");
            return false;
        }
    }

    /// <summary>The installer, as copied beside the app at install time.</summary>
    private static string? FindInstaller()
    {
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(dir)) return null;

        foreach (string name in new[] { "LockwellSetup.exe", "Uninstall.exe" })
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    // ------------------------------------------------------------------ github

    private sealed record ReleaseAssets(
        string ManifestUrl,
        string SignatureUrl,
        IReadOnlyDictionary<string, string> Packages);

    private static async Task<ReleaseAssets?> FindLatestReleaseAssetsAsync(CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";

        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? manifestUrl = null;
        string? signatureUrl = null;
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement)) continue;
            if (!asset.TryGetProperty("browser_download_url", out var urlElement)) continue;

            string? name = nameElement.GetString();
            string? assetUrl = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(assetUrl)) continue;

            // Only ever https, and only ever to github. An API response is attacker-
            // controlled in the scenario this whole feature is defending against.
            if (!IsAcceptableDownloadUrl(assetUrl)) continue;

            if (name.Equals(ManifestAssetName, StringComparison.OrdinalIgnoreCase))
                manifestUrl = assetUrl;
            else if (name.Equals(SignatureAssetName, StringComparison.OrdinalIgnoreCase))
                signatureUrl = assetUrl;
            else
                packages[name] = assetUrl;
        }

        return manifestUrl is null || signatureUrl is null
            ? null
            : new ReleaseAssets(manifestUrl, signatureUrl, packages);
    }

    private static bool IsAcceptableDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]?> DownloadMetadataAsync(string url, CancellationToken ct)
    {
        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength is > MaxMetadataBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxMetadataBytes) return null;
        }

        return buffer.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* staging cleanup is best effort */ }
    }
}
