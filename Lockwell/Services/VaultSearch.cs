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

using Lockwell.Models;

namespace Lockwell.Services;

/// <summary>
/// Finds anything in the unlocked vault from one box.
///
/// Three limits are deliberate. It never searches a secret field or a note, because a search
/// box that responds to the first letters of a password turns "does it match?" into a way of
/// guessing one over someone's shoulder. It runs over what is already decrypted in memory and
/// writes no index, so there is nothing new on disk. And it respects the NSFW setting all the
/// way down: hiding a folder has to hide what is inside it too.
/// </summary>
public static class VaultSearch
{
    public enum HitKind { Item, File, Folder, Section }

    public sealed record Hit(
        HitKind Kind,
        string Title,
        string Subtitle,
        int Score,
        VaultEntry? Entry = null,
        MediaFolder? Folder = null,
        Category? Category = null);

    public static IReadOnlyList<Hit> Find(
        VaultData data, string query, bool hideNsfw, int limit = 40)
    {
        query = query.Trim();
        if (query.Length == 0) return Array.Empty<Hit>();

        var hits = new List<Hit>();
        var mediaCategories = data.Categories.Where(c => c.IsMedia).Select(c => c.Id).ToHashSet();
        var hiddenFolders = hideNsfw ? HiddenFolderIds(data) : new HashSet<string>();

        foreach (var category in data.Categories)
        {
            int score = Score(category.Name, query);
            if (score > 0)
                hits.Add(new Hit(HitKind.Section, category.Name, "Section", score - 5, Category: category));
        }

        foreach (var folder in data.MediaFolders)
        {
            if (hiddenFolders.Contains(folder.Id)) continue;

            int score = Score(folder.Name, query);
            if (score > 0)
                hits.Add(new Hit(HitKind.Folder, folder.Name, "Folder", score, Folder: folder));
        }

        foreach (var entry in data.Entries)
        {
            if (entry.MediaFolderId is { } folderId && hiddenFolders.Contains(folderId)) continue;

            bool isFile = mediaCategories.Contains(entry.CategoryId);
            int score = Score(entry.Title, query);

            foreach (var field in entry.Fields)
            {
                if (field.IsSecret) continue;
                score = Math.Max(score, Score(field.Value, query) - 10);
            }

            foreach (var attachment in entry.Attachments)
                score = Math.Max(score, Score(attachment.FileName, query) - 10);

            if (score <= 0) continue;

            hits.Add(new Hit(
                isFile ? HitKind.File : HitKind.Item,
                entry.Title,
                isFile ? "File" : Subtitle(entry, data),
                score,
                Entry: entry));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>The field to copy for an item: a password if it has one, else its first secret.</summary>
    public static EntryField? PrimarySecret(VaultEntry entry) =>
        entry.Fields.FirstOrDefault(f => f.IsSecret && !string.IsNullOrEmpty(f.Value)
                                         && f.Label.Contains("password", StringComparison.OrdinalIgnoreCase))
        ?? entry.Fields.FirstOrDefault(f => f.IsSecret && !string.IsNullOrEmpty(f.Value));

    /// <summary>Every folder hidden by the NSFW setting: marked ones and everything beneath them.</summary>
    public static HashSet<string> HiddenFolderIds(VaultData data)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        void Hide(string id)
        {
            if (!hidden.Add(id)) return;
            foreach (var child in data.MediaFolders.Where(f => f.ParentFolderId == id))
                Hide(child.Id);
        }

        foreach (var folder in data.MediaFolders.Where(f => f.IsNsfw))
            Hide(folder.Id);

        return hidden;
    }

    private static int Score(string? text, string query)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 80;

        int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return 0;

        bool wordStart = !char.IsLetterOrDigit(text[at - 1]);
        return wordStart ? 60 : 40;
    }

    private static string Subtitle(VaultEntry entry, VaultData data)
    {
        var visible = entry.Fields.FirstOrDefault(f => !f.IsSecret && !string.IsNullOrWhiteSpace(f.Value));
        if (visible is not null) return visible.Value;
        return data.Categories.FirstOrDefault(c => c.Id == entry.CategoryId)?.Name ?? "";
    }
}
