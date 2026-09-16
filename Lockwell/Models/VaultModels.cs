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

using System.Text.Json.Serialization;

namespace Lockwell.Models;

/// <summary>
/// The entire decrypted contents of a vault. This object only ever exists in
/// memory while the vault is unlocked. On disk it is a single AES-256-GCM blob.
/// </summary>
public sealed class VaultData
{
    public int SchemaVersion { get; set; } = 6;
    public List<Category> Categories { get; set; } = new();
    public List<MediaFolder> MediaFolders { get; set; } = new();
    public List<VaultEntry> Entries { get; set; } = new();

    /// <summary>
    /// When a backup was last exported. Kept inside the encrypted vault rather than in
    /// settings.json, because when and how often you back up is itself information
    /// about you. Null means never exported from this vault.
    /// </summary>
    public DateTime? LastExportUtc { get; set; }

    /// <summary>The preset categories every new vault starts with.</summary>
    public static List<Category> DefaultCategories() => new()
    {
        new Category { Name = "Email accounts",   Glyph = "\uE715", Order = 0 },
        new Category { Name = "Game Platforms",   Glyph = "\uE770", Order = 1 },
        new Category { Name = "Gaming",           Glyph = "\uE7FC", Order = 2 },
        new Category { Name = "Social",           Glyph = "\uE8F2", Order = 3 },
        new Category { Name = "Crypto",           Glyph = "\uE8D7", Order = 4 },
        new Category { Name = "Secure notes",     Glyph = "\uE70B", Order = 5 },
        new Category { Name = "Media vault",      Glyph = "\uEB9F", Order = 6, IsMedia = true },
    };

    /// <summary>Upgrade older vaults when preset sections change.</summary>
    public static bool ApplyMigrations(VaultData data)
    {
        bool changed = false;

        foreach (var cat in data.Categories.Where(c => c.Name == "Social & Discord"))
        {
            cat.Name = "Social";
            changed = true;
        }

        if (!data.Categories.Any(c => c.Name == "Game Platforms"))
        {
            var gaming = data.Categories.FirstOrDefault(c => c.Name == "Gaming");
            int insertOrder = gaming?.Order ?? 1;
            foreach (var cat in data.Categories.Where(c => c.Order >= insertOrder))
                cat.Order++;

            data.Categories.Add(new Category
            {
                Name = "Game Platforms",
                Glyph = "\uE770",
                Order = insertOrder,
            });
            changed = true;
        }

        if (data.SchemaVersion < 4)
        {
            data.SchemaVersion = 4;
            changed = true;
        }

        if (data.SchemaVersion < 6)
        {
            // "Crypto & wallets" became simply "Crypto". The section is for the two things
            // actually worth locking away -- a seed phrase and a private key -- and the
            // old name suggested a directory of exchange logins, which is what the Login
            // sections are already for.
            foreach (var cat in data.Categories.Where(c =>
                         c.Name.Equals("Crypto & wallets", StringComparison.OrdinalIgnoreCase)))
            {
                cat.Name = "Crypto";
                cat.Glyph = "\uE8D7";
                changed = true;
            }

            // Two preset sections were dropped for being duplicates of what a Login
            // section already does. Nothing is deleted: a section that still holds
            // something stays, because removing a preset must never remove a secret.
            // Only the empty ones go, and anyone who wants them back can add a section.
            foreach (string name in new[] { "Banking & finance", "Work & tools" })
            {
                var cat = data.Categories.FirstOrDefault(c =>
                    c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (cat is null) continue;

                bool holdsSomething = data.Entries.Any(e => e.CategoryId == cat.Id);
                if (holdsSomething) continue;

                data.Categories.Remove(cat);
                changed = true;
            }

            data.SchemaVersion = 6;
            changed = true;
        }

        if (data.SchemaVersion < 5)
        {
            foreach (var cat in data.Categories.Where(c =>
                         c.Name.Equals("Media vault", StringComparison.OrdinalIgnoreCase) && !c.IsMedia))
            {
                cat.IsMedia = true;
                changed = true;
            }

            data.SchemaVersion = 5;
            changed = true;
        }

        return changed;
    }
}

public sealed class Category
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Segoe MDL2 Assets glyph code shown in the sidebar.</summary>
    public string Glyph { get; set; } = "\uE8F4";
    public int Order { get; set; }

    /// <summary>Media categories show a thumbnail grid instead of a login list.</summary>
    public bool IsMedia { get; set; }

    /// <summary>
    /// True for sections the user made themselves, as opposed to the ones Lockwell ships
    /// with. They are listed separately and can be folded away, so a vault with a dozen
    /// custom sections does not bury the handful that are always there.
    /// </summary>
    public bool IsCustom { get; set; }
}

/// <summary>Album-style folder inside a media category (e.g. "Vacation pictures").</summary>
public sealed class MediaFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CategoryId { get; set; } = "";
    public string? ParentFolderId { get; set; }
    public string Name { get; set; } = "";
    public MediaFolderKind Kind { get; set; } = MediaFolderKind.Mixed;
    public bool IsNsfw { get; set; }
    public bool IsFavorite { get; set; }
    public int Order { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum MediaFolderKind
{
    Mixed = 0,
    Photos = 1,
    Video = 2,
    Audio = 3,
}

public enum EntryKind
{
    Login,
    SecureNote,
    CryptoWallet,
    Card,
    Media
}

public sealed class VaultEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CategoryId { get; set; } = "";

    /// <summary>When set, this media entry lives inside a <see cref="MediaFolder"/>.</summary>
    public string? MediaFolderId { get; set; }

    /// <summary>Display order inside a media folder (lower = first).</summary>
    public int MediaSortOrder { get; set; }

    public EntryKind Kind { get; set; } = EntryKind.Login;
    public string Title { get; set; } = "";
    public bool Favorite { get; set; }

    /// <summary>Ordered, flexible field list so any kind of secret can be modeled.</summary>
    public List<EntryField> Fields { get; set; } = new();

    public string Notes { get; set; } = "";
    public List<AttachmentRef> Attachments { get; set; } = new();

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Devices this item has actually been handed to, and when.
    ///
    /// Recorded when a transfer completes rather than when one is queued, so the mark
    /// means "this device has it" and not "I meant to send this". Keyed by the trusted
    /// device's id, so unlinking and re-linking a phone does not leave it looking like it
    /// still holds things it no longer does.
    /// </summary>
    public Dictionary<string, DateTime> SyncedToDevices { get; set; } = new();

    /// <summary>Whether any linked device has been given this item.</summary>
    public bool IsSyncedAnywhere => SyncedToDevices.Count > 0;
}

public sealed class EntryField
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>Secret fields are masked in the UI and excluded from search previews.</summary>
    public bool IsSecret { get; set; }
}

/// <summary>
/// Pointer to an encrypted attachment file. The bytes live in their own
/// <c>attachments/{Id}.lwa</c> blob and are only decrypted when the user opens
/// them (lazy load), so large media never sits in memory unless requested.
/// </summary>
public sealed class AttachmentRef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public string MediaType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Source-generated JSON context. Avoids reflection-based serialization
/// (smaller, faster, trim-friendly) and keeps serialization explicit.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(VaultData))]
public partial class VaultJsonContext : JsonSerializerContext
{
}
