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
using Lockwell.Models;

namespace Lockwell.Services;

/// <summary>Repairs vault metadata so media entries and folders stay visible after reload.</summary>
public sealed class VaultIntegrityReport
{
    public int MediaCategoryFlagsFixed { get; init; }
    public int EntriesReassignedToMedia { get; init; }
    public int DanglingFolderRefsCleared { get; init; }
    public int OrphanedAttachmentsRecovered { get; init; }

    public bool Changed =>
        MediaCategoryFlagsFixed > 0 ||
        EntriesReassignedToMedia > 0 ||
        DanglingFolderRefsCleared > 0 ||
        OrphanedAttachmentsRecovered > 0;
}

public static class VaultIntegrityService
{
    public static VaultIntegrityReport Repair(VaultManager vault)
    {
        var data = vault.Data;
        int flagsFixed = 0;
        int reassigned = 0;
        int danglingCleared = 0;
        int orphansRecovered = 0;

        Category mediaCategory = ResolveOrCreateMediaCategory(data, ref flagsFixed);
        string mediaCategoryId = mediaCategory.Id;

        var folderIds = new HashSet<string>(data.MediaFolders.Select(f => f.Id));
        var categoryIds = new HashSet<string>(data.Categories.Select(c => c.Id));

        foreach (var entry in data.Entries)
        {
            bool looksLikeMedia = entry.Kind == EntryKind.Media ||
                                  entry.Attachments.Any(IsLikelyMediaAttachment);

            if (looksLikeMedia && entry.CategoryId != mediaCategoryId &&
                (!categoryIds.Contains(entry.CategoryId) || !data.Categories.Any(c => c.Id == entry.CategoryId && c.IsMedia)))
            {
                entry.CategoryId = mediaCategoryId;
                if (entry.Kind != EntryKind.Media)
                    entry.Kind = EntryKind.Media;
                reassigned++;
            }

            if (!string.IsNullOrEmpty(entry.MediaFolderId) && !folderIds.Contains(entry.MediaFolderId))
            {
                entry.MediaFolderId = null;
                danglingCleared++;
            }
        }

        orphansRecovered = RecoverOrphanedAttachments(vault, data, mediaCategoryId);

        return new VaultIntegrityReport
        {
            MediaCategoryFlagsFixed = flagsFixed,
            EntriesReassignedToMedia = reassigned,
            DanglingFolderRefsCleared = danglingCleared,
            OrphanedAttachmentsRecovered = orphansRecovered,
        };
    }

    private static Category ResolveOrCreateMediaCategory(VaultData data, ref int flagsFixed)
    {
        var media = data.Categories.FirstOrDefault(c => c.IsMedia);
        if (media is not null)
            return media;

        media = data.Categories.FirstOrDefault(c =>
            c.Name.Equals("Media vault", StringComparison.OrdinalIgnoreCase));
        if (media is not null)
        {
            if (!media.IsMedia)
            {
                media.IsMedia = true;
                flagsFixed++;
            }
            return media;
        }

        var preset = VaultData.DefaultCategories().First(c => c.IsMedia);
        data.Categories.Add(new Category
        {
            Name = preset.Name,
            Glyph = preset.Glyph,
            Order = data.Categories.Count > 0 ? data.Categories.Max(c => c.Order) + 1 : preset.Order,
            IsMedia = true,
        });
        flagsFixed++;
        return data.Categories.Last();
    }

    private static int RecoverOrphanedAttachments(VaultManager vault, VaultData data, string mediaCategoryId)
    {
        if (!Directory.Exists(vault.AttachmentsDir))
            return 0;

        var referenced = new HashSet<string>(
            data.Entries.SelectMany(e => e.Attachments).Select(a => a.Id),
            StringComparer.OrdinalIgnoreCase);

        int recovered = 0;
        foreach (string path in Directory.EnumerateFiles(vault.AttachmentsDir, "*.lwa"))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            if (referenced.Contains(id))
                continue;

            var att = new AttachmentRef
            {
                Id = id,
                SizeBytes = new FileInfo(path).Length,
            };

            TryFillRecoveredAttachmentMeta(vault, att);

            data.Entries.Add(new VaultEntry
            {
                CategoryId = mediaCategoryId,
                Kind = EntryKind.Media,
                Title = Path.GetFileNameWithoutExtension(att.FileName),
                Attachments = { att },
            });
            recovered++;
        }

        return recovered;
    }

    private static void TryFillRecoveredAttachmentMeta(VaultManager vault, AttachmentRef att)
    {
        try
        {
            byte[] plain = vault.ReadAttachment(att);
            string ext = GuessExtensionFromBytes(plain);
            Array.Clear(plain, 0, plain.Length);
            att.FileName = $"recovered-{att.Id[..Math.Min(8, att.Id.Length)]}{ext}";
            att.MediaType = VaultManagerGuessMediaType(att.FileName);
        }
        catch
        {
            att.FileName = $"recovered-{att.Id[..Math.Min(8, att.Id.Length)]}.bin";
            att.MediaType = "application/octet-stream";
        }
    }

    private static string VaultManagerGuessMediaType(string fileName)
    {
        // Mirror VaultManager private helper without reflection.
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream",
        };
    }

    private static string GuessExtensionFromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return ".png";
        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
            return ".gif";
        if (bytes.Length >= 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
            return ".mp4";
        if (bytes.Length >= 4 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
            return ".wav";
        if (bytes.Length >= 3 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3')
            return ".mp3";
        if (bytes.Length >= 4 && bytes[0] == 'f' && bytes[1] == 'L' && bytes[2] == 'a' && bytes[3] == 'C')
            return ".flac";
        return ".bin";
    }

    private static bool IsLikelyMediaAttachment(AttachmentRef att)
    {
        string name = att.FileName;
        if (string.IsNullOrWhiteSpace(name))
            return att.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                   att.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                   att.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp"
            or ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi"
            or ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".wma";
    }
}
