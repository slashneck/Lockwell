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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lockwell.Update;

/// <summary>
/// What a published release claims about itself: a version, and the exact bytes of the
/// package that carries it.
///
/// This is the only thing an update decision is ever made from. The GitHub API response
/// is not trusted for anything except "here are some bytes to look at" -- not for the
/// version number, not for the download size, not for which asset is the real one. All of
/// that comes from this manifest, and this manifest is only believed once its signature
/// has been checked against a key that was compiled into the app. See
/// <see cref="ReleaseSigning"/> for why that distinction is the whole point.
/// </summary>
public sealed class ReleaseManifest
{
    /// <summary>
    /// Manifest format version, so a future change can be recognised rather than
    /// misread. An app that does not understand a format refuses the update instead of
    /// guessing at it.
    /// </summary>
    [JsonPropertyName("manifest")]
    public int ManifestVersion { get; set; } = 1;

    /// <summary>The release version, without any leading "v".</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>The git tag this release was published under, for display and for links.</summary>
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    /// <summary>When it was published, UTC. Display only; never used to decide anything.</summary>
    [JsonPropertyName("released")]
    public DateTimeOffset ReleasedUtc { get; set; }

    /// <summary>File name of the package asset attached to the same release.</summary>
    [JsonPropertyName("package")]
    public string PackageName { get; set; } = "";

    /// <summary>Exact size of that package in bytes.</summary>
    [JsonPropertyName("size")]
    public long PackageSize { get; set; }

    /// <summary>Lowercase hex SHA-256 of the package.</summary>
    [JsonPropertyName("sha256")]
    public string PackageSha256 { get; set; } = "";

    /// <summary>A short, human summary shown before anything is installed.</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    /// <summary>
    /// The minimum version that may update straight to this one. Zero means any.
    ///
    /// This exists so a future release that changes the vault format on disk can refuse
    /// to be installed over a build too old to have the migration code, rather than
    /// landing on it and failing at the worst possible moment.
    /// </summary>
    [JsonPropertyName("minFrom")]
    public string MinimumFromVersion { get; set; } = "";

    public static ReleaseManifest? Parse(byte[] utf8Json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize(utf8Json, UpdateJson.Default.ReleaseManifest);
            return manifest is null || !manifest.IsWellFormed() ? null : manifest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public byte[] ToUtf8Json() =>
        JsonSerializer.SerializeToUtf8Bytes(this, UpdateJson.Default.ReleaseManifest);

    /// <summary>
    /// Everything that must be true before this is worth acting on at all.
    ///
    /// A signature proves who wrote the manifest, not that they wrote it correctly, so a
    /// malformed manifest from the right key is still refused rather than half-applied.
    /// </summary>
    public bool IsWellFormed()
    {
        if (ManifestVersion != 1) return false;
        if (ParseVersion(Version) is null) return false;
        if (string.IsNullOrWhiteSpace(PackageName)) return false;
        if (PackageSize <= 0) return false;
        if (!IsHexSha256(PackageSha256)) return false;

        // A package name is used to pick an asset and must never be able to escape into a
        // path. Rejecting separators here means the download step cannot be talked into
        // writing somewhere else.
        if (PackageName.Contains('/') || PackageName.Contains('\\')) return false;
        if (PackageName.Contains("..", StringComparison.Ordinal)) return false;
        if (PackageName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        return true;
    }

    private static bool IsHexSha256(string value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (char c in value)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
        }
        return true;
    }

    /// <summary>
    /// Read a version out of a tag, tolerating the shapes tags actually come in:
    /// "v1.2.0", "1.2.0", "v1.2.0-beta.1".
    ///
    /// A pre-release suffix is dropped rather than ordered, because ordering it properly
    /// is a surprising amount of rule for no benefit here: releases are compared to the
    /// running build, and the running build never has one.
    /// </summary>
    public static Version? ParseVersion(string? tagOrVersion)
    {
        if (string.IsNullOrWhiteSpace(tagOrVersion)) return null;

        string s = tagOrVersion.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        int dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0) s = s[..dash];

        int plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0) s = s[..plus];

        if (!System.Version.TryParse(s, out var parsed)) return null;

        // Version compares unset components as -1, so 1.1 and 1.1.0 would differ. Normalise
        // so a tag written either way means the same release.
        return new Version(
            parsed.Major,
            parsed.Minor,
            parsed.Build < 0 ? 0 : parsed.Build,
            parsed.Revision < 0 ? 0 : parsed.Revision);
    }

    /// <summary>
    /// Should <paramref name="running"/> be offered this release?
    ///
    /// Only ever forward. Serving an old-but-correctly-signed manifest is the cheapest
    /// attack available to anyone who can choose which bytes we see, and the answer to it
    /// is simply never to treat "older" as an update.
    /// </summary>
    public bool IsUpgradeFrom(Version running)
    {
        var candidate = ParseVersion(Version);
        if (candidate is null) return false;
        if (candidate <= running) return false;

        var floor = ParseVersion(MinimumFromVersion);
        if (floor is not null && running < floor) return false;

        return true;
    }

    /// <summary>
    /// True when this release exists but cannot be installed directly, because the
    /// running build is older than the release's stated floor. The user is told to
    /// reinstall rather than left with a check that silently finds nothing.
    /// </summary>
    public bool RequiresManualInstall(Version running)
    {
        var candidate = ParseVersion(Version);
        if (candidate is null || candidate <= running) return false;

        var floor = ParseVersion(MinimumFromVersion);
        return floor is not null && running < floor;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReleaseManifest))]
public partial class UpdateJson : JsonSerializerContext
{
}
