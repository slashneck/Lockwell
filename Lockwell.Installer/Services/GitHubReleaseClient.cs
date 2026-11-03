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
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Lockwell.Update;

namespace Lockwell.Installer.Services;

internal static class GitHubReleaseClient
{
    public const string DefaultOwner = "slashneck";
    public const string DefaultRepo = "Lockwell";
    public const string ReleaseZipAssetFileName = "Lockwell-win-x64.zip";
    public const string ManifestAssetFileName = "release.json";
    public const string SignatureAssetFileName = "release.json.sig";

    /// <summary>A manifest and signature are under a kilobyte. Refuse anything silly.</summary>
    private const int MaxMetadataBytes = 64 * 1024;

    /// <summary>Same ceiling the app applies to a package.</summary>
    private const long MaxPackageBytes = 400L * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();

    public sealed record ReleaseCheckResult(Version LatestVersion, string TagName, string DownloadUrl);

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var version = ReadAppVersion()?.ToString() ?? "1.0.0";
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LockwellSetup", version));
        return c;
    }

    public static Version? ReadAppVersion()
    {
        try
        {
            return (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<ReleaseCheckResult?> CheckLatestReleaseAsync(CancellationToken ct = default)
    {
        var parsed = await ResolveLatestReleaseAsync(ct).ConfigureAwait(false);
        if (parsed is null)
        {
            return null;
        }

        return new ReleaseCheckResult(parsed.Version, parsed.TagName, parsed.DownloadUrl);
    }

    /// <summary>
    /// GitHub <c>/releases/latest</c> excludes pre-releases. Fall back to the newest published
    /// non-draft release (including pre-releases) so beta tags still work.
    /// </summary>
    private static async Task<ParsedRelease?> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        var latestUrl = $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepo}/releases/latest";
        var parsed = await TryParseReleaseFromGetAsync(latestUrl, ct).ConfigureAwait(false);
        if (parsed is not null)
        {
            return parsed;
        }

        var listUrl = $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepo}/releases";
        using var resp = await Http.GetAsync(listUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            parsed = ParseReleaseElement(rel);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static async Task<ParsedRelease?> TryParseReleaseFromGetAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseReleaseElement(doc.RootElement);
    }

    private sealed record ParsedRelease(
        Version Version,
        string TagName,
        string DownloadUrl,
        string? ManifestUrl,
        string? SignatureUrl,
        IReadOnlyDictionary<string, string> Assets);

    private static ParsedRelease? ParseReleaseElement(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tagEl))
        {
            return null;
        }

        var tag = tagEl.GetString() ?? string.Empty;
        var latest = ParseVersionFromTag(tag);
        var downloadUrl = PickZipAssetUrl(root);
        if (latest is null || string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        var assets = ReadAssets(root);
        assets.TryGetValue(ManifestAssetFileName, out var manifestUrl);
        assets.TryGetValue(SignatureAssetFileName, out var signatureUrl);
        return new ParsedRelease(latest, tag.Trim(), downloadUrl, manifestUrl, signatureUrl, assets);
    }

    /// <summary>
    /// Fetch the latest release and prove it is genuine, or throw saying why not.
    ///
    /// Setup used to download the zip the API pointed at and unpack it, with HTTPS as the
    /// only check, while its own screens said it verified a signature. This is the one
    /// route to a package now: the manifest and signature come first and are checked
    /// against the key compiled in here, the package is chosen by the name the signed
    /// manifest gives, and it must match that manifest's size and hash. A release with no
    /// signed manifest is refused rather than installed on trust.
    /// </summary>
    public static async Task<VerifiedPackage> DownloadVerifiedReleaseAsync(
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var release = await ResolveLatestReleaseAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Could not reach the update server or no release package was found.");

        if (release.ManifestUrl is null || release.SignatureUrl is null)
        {
            throw new InvalidOperationException(
                "This release has no signed manifest, so it cannot be verified. Nothing was installed.");
        }

        var manifestBytes = await DownloadMetadataAsync(release.ManifestUrl, ct).ConfigureAwait(false);
        var signature = await DownloadMetadataAsync(release.SignatureUrl, ct).ConfigureAwait(false);

        var verdict = ReleaseTrust.VerifyManifest(manifestBytes, signature);
        if (!verdict.IsTrusted)
        {
            throw new InvalidOperationException(verdict.Explain());
        }

        // The API listing only says where a file of that name lives. Which file is the
        // real package is the signed manifest's decision.
        if (!release.Assets.TryGetValue(verdict.Manifest!.PackageName, out var packageUrl))
        {
            throw new InvalidOperationException(
                "The signed release names a package that is not attached to it. Nothing was installed.");
        }

        var zipPath = await DownloadReleaseZipAsync(packageUrl, progress, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Download failed.");

        try
        {
            return VerifiedPackage.Verify(manifestBytes, signature, zipPath);
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }
    }

    private static async Task<byte[]> DownloadMetadataAsync(string url, CancellationToken ct)
    {
        RequireTrustedHost(url);

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "The signed manifest could not be downloaded. Nothing was installed.");
        }

        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxMetadataBytes)
            {
                throw new InvalidOperationException(
                    "The signed manifest is implausibly large. Nothing was installed.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<string?> DownloadReleaseZipAsync(
        string downloadUrl,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        RequireTrustedHost(downloadUrl);

        var dest = Path.Combine(Path.GetTempPath(), "Lockwell", $"setup-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        using var resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var total = resp.Content.Headers.ContentLength;
        if (total > MaxPackageBytes)
        {
            throw new InvalidOperationException("The release package is implausibly large. Nothing was installed.");
        }

        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var output = File.Create(dest))
        {
            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                read += count;
                if (read > MaxPackageBytes)
                {
                    output.Close();
                    TryDelete(dest);
                    throw new InvalidOperationException("The release package is implausibly large. Nothing was installed.");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                if (total is > 0)
                {
                    progress?.Report(read / (double)total.Value * 100.0);
                }
            }
        }

        progress?.Report(100);
        return dest;
    }

    /// <summary>Same allow-list as the app: HTTPS, and only GitHub's own hosts.</summary>
    private static void RequireTrustedHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The release points somewhere unexpected. Nothing was installed.");
        }
    }

    /// <summary>
    /// Refuse to replace an installed copy with an older one. Equal is allowed, so
    /// reinstalling over a damaged install keeps working.
    /// </summary>
    public static void RefuseDowngrade(string installDirectory, VerifiedPackage package)
    {
        var installed = ReadInstalledVersion(installDirectory);
        var offered = ReleaseManifest.ParseVersion(package.Manifest.Version);
        if (installed is not null && offered is not null && offered < installed)
        {
            throw new InvalidOperationException(
                $"The installed version ({installed.ToString(3)}) is newer than this release "
                + $"({offered.ToString(3)}). Nothing was changed.");
        }
    }

    /// <summary>
    /// The version actually on disk, from the executable rather than the registry: the
    /// registry records what an installer believed it wrote.
    /// </summary>
    public static Version? ReadInstalledVersion(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        try
        {
            var exe = Path.Combine(installDirectory, InstallPaths.ExecutableFileName);
            if (!File.Exists(exe))
            {
                return null;
            }

            return ReleaseManifest.ParseVersion(FileVersionInfo.GetVersionInfo(exe).FileVersion);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Takes a <see cref="VerifiedPackage"/> rather than a path, on purpose. That type can
    /// only be made by checking a signature, so no install route can skip the check, now
    /// or later, without failing to compile.
    /// </summary>
    public static bool InstallReleaseZipToDirectory(VerifiedPackage package, string targetDirectory)
    {
        var zipPath = package.ZipPath;
        if (!File.Exists(zipPath))
        {
            return false;
        }

        var extractRoot = Path.Combine(Path.GetTempPath(), "Lockwell", "extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot);

            var sourceDir = FindFolderContainingExe(extractRoot, InstallPaths.ExecutableFileName);
            if (string.IsNullOrEmpty(sourceDir))
            {
                return false;
            }

            Directory.CreateDirectory(targetDirectory);
            return MirrorDirectory(sourceDir, targetDirectory);
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractRoot))
                {
                    Directory.Delete(extractRoot, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // a stale temp file is untidy, not dangerous
        }
    }

    private static bool MirrorDirectory(string sourceDir, string targetDirectory)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "robocopy",
            Arguments = $"\"{sourceDir}\" \"{targetDirectory}\" /E /IS /IT /NFL /NDL /NJH /NJS",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (proc is null)
        {
            return false;
        }

        proc.WaitForExit();
        return proc.ExitCode is >= 0 and < 8;
    }

    private static string? FindFolderContainingExe(string extractRoot, string exeName)
    {
        try
        {
            var hits = Directory.GetFiles(extractRoot, exeName, SearchOption.AllDirectories);
            return hits.Length == 0 ? null : Path.GetDirectoryName(hits[0]);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadAssets(JsonElement root)
    {
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("assets", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return assets;
        }

        foreach (var a in list.EnumerateArray())
        {
            if (a.TryGetProperty("name", out var nameEl)
                && a.TryGetProperty("browser_download_url", out var urlEl)
                && nameEl.GetString() is { Length: > 0 } name
                && urlEl.GetString() is { Length: > 0 } url)
            {
                assets[name] = url;
            }
        }

        return assets;
    }

    private static string? PickZipAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? firstZip = null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var nameEl) || !a.TryGetProperty("browser_download_url", out var urlEl))
            {
                continue;
            }

            var name = nameEl.GetString() ?? string.Empty;
            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            firstZip ??= url;
            if (string.Equals(name, ReleaseZipAssetFileName, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return firstZip;
    }

    private static Version? ParseVersionFromTag(string tag)
    {
        var s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            s = s[..dash];
        }

        return Version.TryParse(s, out var v) ? v : null;
    }
}

/// <summary>
/// Proof that a package on disk matched a manifest signed by the release key at the moment
/// it was checked. The constructor is private: the only way to hold one is to pass
/// <see cref="ReleaseTrust.VerifyPackage(byte[], byte[], string)"/>.
/// </summary>
internal sealed class VerifiedPackage
{
    private VerifiedPackage(string zipPath, ReleaseManifest manifest)
    {
        ZipPath = zipPath;
        Manifest = manifest;
    }

    public string ZipPath { get; }

    public ReleaseManifest Manifest { get; }

    public static VerifiedPackage Verify(byte[] manifestBytes, byte[] signature, string zipPath)
    {
        var verdict = ReleaseTrust.VerifyPackage(manifestBytes, signature, zipPath);
        if (!verdict.IsTrusted)
        {
            throw new InvalidOperationException(verdict.Explain());
        }

        return new VerifiedPackage(zipPath, verdict.Manifest!);
    }
}
