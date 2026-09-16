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

namespace Lockwell.Mobile.Services;

/// <summary>Small helpers so code-built UI matches the XAML styles.</summary>
public static class Theme
{
    public static Color Color(string key) =>
        Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Color c
            ? c
            : Colors.Gray;
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>Relative time that reads naturally rather than dumping a raw date.</summary>
    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalDays switch
        {
            < 1 => "today",
            < 2 => "yesterday",
            < 30 => $"{(int)span.TotalDays} days ago",
            _ => utc.ToLocalTime().ToString("d MMM yyyy"),
        };
    }
}

/// <summary>
/// What kind of file this is, worked out from its name.
///
/// The picker's own content type cannot be relied on. Depending on which app answered the
/// intent it arrives as the right type, as "application/octet-stream", or as nothing at
/// all, and every one of those makes a GIF or an MP4 land in the vault as an anonymous
/// blob: no preview, no player, just a document icon. The extension is the one thing that
/// is always there, so it decides, and the picker's answer is only used when it is
/// actually specific.
/// </summary>
public static class MediaTypes
{
    public static string For(string fileName, string? reported = null)
    {
        // Trust a specific answer from the picker over guessing from the name.
        if (!string.IsNullOrWhiteSpace(reported) &&
            reported.Contains('/') &&
            !reported.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return reported;
        }

        string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();

        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".heic" or ".heif" => "image/heic",
            ".avif" => "image/avif",
            ".tif" or ".tiff" => "image/tiff",
            ".svg" => "image/svg+xml",

            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".3gp" => "video/3gpp",
            ".ts" => "video/mp2t",

            ".mp3" => "audio/mpeg",
            ".m4a" or ".aac" => "audio/mp4",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/opus",
            ".wma" => "audio/x-ms-wma",

            ".pdf" => "application/pdf",
            ".txt" => "text/plain",

            _ => "application/octet-stream",
        };
    }

    /// <summary>An animated image, which the viewer plays rather than showing one frame of.</summary>
    public static bool IsAnimated(string fileName, string mediaType)
    {
        string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".gif" or ".webp" ||
               mediaType.Equals("image/gif", StringComparison.OrdinalIgnoreCase);
    }
}
