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

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lockwell.Models;

namespace Lockwell.Services;

/// <summary>
/// Non-secret preferences. These deliberately contain nothing sensitive, so they
/// are stored as plain JSON next to the vault. No vault contents, paths to
/// secrets, or telemetry ever go here.
/// </summary>
/// <summary>Ordering options for the media gallery.</summary>
public enum MediaSortMode
{
    /// <summary>Manual order, the arrangement you set by dragging.</summary>
    Custom = 0,
    Name = 1,
    Date = 2,
    Size = 3,
}

public sealed class AppSettings
{
    public bool ExcludeFromCapture { get; set; } = true;
    public int AutoLockMinutes { get; set; } = 5;
    public int ClipboardClearSeconds { get; set; } = 30;
    public bool RunPreflight { get; set; } = true;

    /// <summary>Lock the vault when the window is minimized.</summary>
    public bool LockOnMinimize { get; set; } = true;

    /// <summary>Blur image thumbnails in the media gallery until opened.</summary>
    public bool BlurMediaThumbnails { get; set; } = true;

    /// <summary>Hide NSFW-labeled media folders from the media gallery.</summary>
    public bool HideNsfwMediaFolders { get; set; } = true;

    /// <summary>How the media gallery orders folders and files.</summary>
    public MediaSortMode MediaSortMode { get; set; } = MediaSortMode.Custom;

    /// <summary>Reverse the active sort (Z-A, oldest first, smallest first).</summary>
    public bool MediaSortDescending { get; set; }

    /// <summary>Default quality preset offered in the compress dialog.</summary>
    public CompressionQuality CompressionQuality { get; set; } = CompressionQuality.Balanced;

    /// <summary>Optional cap on the longest image edge when compressing. 0 = keep resolution.</summary>
    public int CompressionMaxImageDimension { get; set; }

    /// <summary>
    /// Suppresses the one-time explanation of what compression does. The per-file
    /// confirmation itself is never suppressed -- see the compress dialog.
    /// </summary>
    public bool CompressionWarningAcknowledged { get; set; }

    /// <summary>
    /// Frames per second for animated images, or 0 to hold on one frame.
    ///
    /// Twenty rather than whatever the file asks for. A great many GIFs in the wild carry
    /// nonsense timings -- 0ms, 10ms, values no renderer has ever honoured -- and playing
    /// them literally makes an animation run several times too fast or crawl. A steady rate
    /// looks right far more often than the file's own answer does, and anyone who disagrees
    /// about a particular one can change it while watching.
    ///
    /// Remembered, because the setting belongs to how someone likes to watch things and not
    /// to one picture. Having it snap back on the next image was the specific complaint.
    /// </summary>
    public double AnimationFramesPerSecond { get; set; } = 20;

    /// <summary>Default to keeping the original and adding the smaller copy alongside it.</summary>
    public bool CompressionKeepOriginal { get; set; } = true;

    /// <summary>
    /// After importing a file into the vault, offer to remove the copy it came from.
    ///
    /// Off by default and always an offer, never automatic. A vault that protects a photo
    /// while the same photo sits in a folder is protecting less than it appears to, so the
    /// option is worth having; doing it without asking would risk destroying the only copy
    /// of something, which this project does not do.
    /// </summary>
    public bool OfferShredAfterImport { get; set; }

    /// <summary>
    /// Ask GitHub, once a day at most, whether a newer release exists.
    ///
    /// On by default, and the one thing in Lockwell that touches the internet on its own.
    /// It is worth being straight about the trade: a vault that never phones home also
    /// never tells you it has a security fix waiting. The check sends no identifier, no
    /// vault information and no version number -- it fetches a public file and compares
    /// locally -- but it is still a request, so GitHub sees the IP address asking and
    /// roughly when. Anyone who would rather not make that request turns this off and
    /// updates from the releases page instead. See docs/THREAT-MODEL.md.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last check ran, so it does not repeat on every launch.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// A version the user chose to pass over. Only suppresses the prompt for that exact
    /// version; anything newer asks again.
    /// </summary>
    public string? SkippedUpdateVersion { get; set; }

    public List<VaultProfile> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }

    [JsonIgnore]
    public static string FilePath => AppDataPaths.SettingsFilePath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                byte[] bytes = File.ReadAllBytes(FilePath);
                return JsonSerializer.Deserialize(bytes, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllBytes(FilePath, bytes);
        }
        catch { /* preferences are best-effort */ }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(VaultProfile))]
[JsonSerializable(typeof(List<VaultProfile>))]
public partial class AppSettingsJsonContext : JsonSerializerContext
{
}
