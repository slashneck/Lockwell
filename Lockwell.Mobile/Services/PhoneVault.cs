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

using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lockwell.Crypto;
using Lockwell.Media;

namespace Lockwell.Mobile.Services;

/// <summary>
/// One vault on the phone.
///
/// This is a full vault in its own right, not a mirror of anything: you can create it,
/// put photos, files and notes into it, and use it with no PC involved. Linking to a
/// desktop is a feature layered on top, not a prerequisite.
///
/// Same envelope design as the desktop app, running the identical Argon2id and
/// AES-256-GCM from Lockwell.Core:
///
///     password --Argon2id(salt)--> KEK
///     DEK = 32 random bytes, generated here, never leaves this phone
///     DEK --AES-256-GCM(KEK)--> wrapped key in the header
///     contents --AES-256-GCM(DEK)--> header.Data
///     each attachment --AES-256-GCM(DEK)--> its own .lwa file
///
/// Each vault has its own DEK, so unlocking one says nothing about any other.
/// </summary>
public sealed class PhoneVault
{
    private readonly string _dir;
    private byte[]? _dek;
    private PhoneVaultHeader? _header;
    private PhoneVaultData? _data;

    public PhoneVault(string vaultDirectory)
    {
        _dir = vaultDirectory;
        Directory.CreateDirectory(_dir);
    }

    public string VaultFile => Path.Combine(_dir, "vault.lwv");
    public string AttachmentsDir => Path.Combine(_dir, "attachments");
    public string BiometricKeyFile => Path.Combine(_dir, "biometric.key");

    public bool Exists => File.Exists(VaultFile);
    public bool IsUnlocked => _dek is not null && _data is not null;

    public PhoneVaultData Data =>
        _data ?? throw new InvalidOperationException("The vault is locked.");

    // ---------------------------------------------------------------- create

    public void Create(SecureString password)
    {
        if (Exists) throw new InvalidOperationException("This vault already exists.");

        Directory.CreateDirectory(AttachmentsDir);

        var kdf = Argon2Parameters.CreateDefault();
        byte[] kek = Argon2KeyDeriver.DeriveKey(password, kdf);
        byte[] dek = SecureRandom.GetBytes(Argon2KeyDeriver.KeyLengthBytes);

        try
        {
            _header = new PhoneVaultHeader
            {
                MemoryKib = kdf.MemoryKib,
                Iterations = kdf.Iterations,
                Parallelism = kdf.Parallelism,
                Salt = Convert.ToBase64String(kdf.Salt),
                PasswordWrappedKey = Convert.ToBase64String(VaultCrypto.Encrypt(kek, dek)),
            };
            _dek = dek;
            _data = new PhoneVaultData();
            Save();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    // ---------------------------------------------------------------- unlock

    public bool Unlock(SecureString password)
    {
        var header = ReadHeader();
        var kdf = new Argon2Parameters
        {
            Salt = Convert.FromBase64String(header.Salt),
            MemoryKib = header.MemoryKib,
            Iterations = header.Iterations,
            Parallelism = header.Parallelism,
        };

        byte[] kek = Argon2KeyDeriver.DeriveKey(password, kdf);
        try
        {
            byte[] dek = VaultCrypto.Decrypt(kek, Convert.FromBase64String(header.PasswordWrappedKey));
            LoadData(header, dek);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Open using a data key recovered from the biometric Keystore, skipping Argon2id.
    /// Not a weaker path: the key still had to be released by hardware that demanded a
    /// fingerprint.
    /// </summary>
    public bool UnlockWithDataKey(byte[] dataKey)
    {
        try
        {
            LoadData(ReadHeader(), dataKey);
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private void LoadData(PhoneVaultHeader header, byte[] dek)
    {
        byte[] json = VaultCrypto.Decrypt(dek, Convert.FromBase64String(header.Data));
        _data = JsonSerializer.Deserialize(json, PhoneVaultJson.Default.PhoneVaultData)
                ?? new PhoneVaultData();
        CryptographicOperations.ZeroMemory(json);

        _header = header;
        _dek = dek;

        // Both run before anything is shown, so an expired or stale item is never
        // visible in a vault that should already have removed it.
        PurgeExpiredItems();
        ApplyStaleWipe();
    }

    public void Lock()
    {
        if (_dek is not null) CryptographicOperations.ZeroMemory(_dek);
        _dek = null;
        _data = null;
        MobileVaultPaths.PurgeTemp();
    }

    // ----------------------------------------------------------- password

    public void ChangePassword(SecureString newPassword)
    {
        if (_dek is null || _header is null)
            throw new InvalidOperationException("Unlock the vault first.");

        var kdf = Argon2Parameters.CreateDefault();
        byte[] kek = Argon2KeyDeriver.DeriveKey(newPassword, kdf);
        try
        {
            // Only the wrapped key is rewritten; the contents keep the same data key.
            _header.MemoryKib = kdf.MemoryKib;
            _header.Iterations = kdf.Iterations;
            _header.Parallelism = kdf.Parallelism;
            _header.Salt = Convert.ToBase64String(kdf.Salt);
            _header.PasswordWrappedKey = Convert.ToBase64String(VaultCrypto.Encrypt(kek, _dek));
            Save();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    internal byte[] ExportDataKeyForBiometricWrap() =>
        _dek ?? throw new InvalidOperationException("The vault is locked.");

    // -------------------------------------------------------------- items

    /// <summary>Encrypt bytes into this vault and record them as an item.</summary>
    public PhoneItem AddItem(byte[] contents, string fileName, string mediaType, string title,
        string? folderId = null)
    {
        if (_dek is null || _data is null)
            throw new InvalidOperationException("The vault is locked.");

        Directory.CreateDirectory(AttachmentsDir);

        var item = new PhoneItem
        {
            Title = string.IsNullOrWhiteSpace(title) ? fileName : title,
            FileName = fileName,
            MediaType = mediaType,
            SizeBytes = contents.LongLength,
            Origin = PhoneItemOrigin.CreatedHere,
            // A folder that has since been deleted must not strand the item out of sight.
            FolderId = FolderExists(folderId) ? folderId : null,
        };

        byte[] blob = VaultCrypto.Encrypt(_dek, contents);
        File.WriteAllBytes(Path.Combine(AttachmentsDir, item.Id + ".lwa"), blob);

        _data.Items.Add(item);
        Save();
        return item;
    }

    public byte[] ReadItem(PhoneItem item)
    {
        if (_dek is null) throw new InvalidOperationException("The vault is locked.");
        byte[] blob = File.ReadAllBytes(Path.Combine(AttachmentsDir, item.Id + ".lwa"));
        return VaultCrypto.Decrypt(_dek, blob);
    }

    public void DeleteItem(PhoneItem item)
    {
        if (_data is null) throw new InvalidOperationException("The vault is locked.");

        try
        {
            string path = Path.Combine(AttachmentsDir, item.Id + ".lwa");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* the record goes either way, so it stops being reachable */ }

        _data.Items.Remove(item);
        Save();
    }

    public void RenameItem(PhoneItem item, string title)
    {
        if (_data is null) return;
        item.Title = title;
        Save();
    }

    // ------------------------------------------------------------------ folders

    /// <summary>Folders in this vault. Read-only: use the methods below to change them.</summary>
    public IReadOnlyList<PhoneFolder> Folders =>
        _data?.Folders ?? (IReadOnlyList<PhoneFolder>)Array.Empty<PhoneFolder>();

    public bool FolderExists(string? folderId) =>
        folderId is not null && _data is not null && _data.Folders.Any(f => f.Id == folderId);

    /// <summary>
    /// Make a folder. The name is made unique among its siblings rather than rejected,
    /// so two folders never look identical in the list.
    /// </summary>
    public PhoneFolder CreateFolder(string name, string? parentId = null)
    {
        if (_data is null) throw new InvalidOperationException("The vault is locked.");

        if (!FolderExists(parentId)) parentId = null;

        var folder = new PhoneFolder
        {
            Name = FolderTree.UniqueName(_data.Folders, parentId, name),
            ParentFolderId = parentId,
            Order = _data.Folders.Count,
        };

        _data.Folders.Add(folder);
        Save();
        return folder;
    }

    public void RenameFolder(PhoneFolder folder, string name)
    {
        if (_data is null) return;
        folder.Name = FolderTree.UniqueName(_data.Folders, folder.ParentFolderId, name, folder.Id);
        Save();
    }

    /// <summary>
    /// Move a folder somewhere else. Returns false, changing nothing, if that would put
    /// the folder inside itself.
    /// </summary>
    public bool MoveFolder(PhoneFolder folder, string? newParentId)
    {
        if (_data is null) return false;
        if (!FolderTree.CanMove(_data.Folders, folder.Id, newParentId)) return false;

        folder.ParentFolderId = newParentId;
        Save();
        return true;
    }

    /// <summary>
    /// Remove a folder, lifting everything inside it up to where the folder was.
    ///
    /// Deliberately not a recursive delete. Removing a container is a tidying action, and
    /// tidying should never be the thing that destroys the only copy of a photo. Anyone
    /// who really wants the contents gone can delete them, having seen them.
    /// </summary>
    public void DeleteFolder(PhoneFolder folder)
    {
        if (_data is null) return;

        FolderTree.LiftContentsOf(_data.Folders, _data.Items, folder.Id, folder.ParentFolderId);
        _data.Folders.Remove(folder);
        Save();
    }

    /// <summary>
    /// Delete a folder and everything in it, however deep. Only ever reached through an
    /// explicit, separately worded choice, never as the default for removing a folder.
    /// </summary>
    public int DeleteFolderAndContents(PhoneFolder folder)
    {
        if (_data is null) return 0;

        var doomed = FolderTree.DescendantsOf(_data.Folders, folder.Id).ToList();
        doomed.Add(folder);

        var ids = doomed.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var items = _data.Items.Where(i => i.FolderId is not null && ids.Contains(i.FolderId)).ToList();

        foreach (PhoneItem item in items)
        {
            try
            {
                string path = Path.Combine(AttachmentsDir, item.Id + ".lwa");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* the record goes either way, so it stops being reachable */ }
            _data.Items.Remove(item);
        }

        foreach (PhoneFolder f in doomed) _data.Folders.Remove(f);

        Save();
        return items.Count;
    }

    /// <summary>Put an item in a folder, or at the top level when null.</summary>
    public void MoveItem(PhoneItem item, string? folderId)
    {
        if (_data is null) return;
        item.FolderId = FolderExists(folderId) ? folderId : null;
        Save();
    }

    /// <summary>What to show inside a folder, in the order this vault is set to use.</summary>
    public (IReadOnlyList<PhoneFolder> Folders, IReadOnlyList<PhoneItem> Items) Contents(string? folderId)
    {
        if (_data is null)
            return (Array.Empty<PhoneFolder>(), Array.Empty<PhoneItem>());

        var folders = ItemOrdering.SortFolders(
            FolderTree.ChildrenOf(_data.Folders, folderId), _data.SortBy, _data.SortDescending);

        var items = ItemOrdering.SortItems(
            _data.Items.Where(i => i.FolderId == folderId), _data.SortBy, _data.SortDescending);

        return (folders, items);
    }

    /// <summary>How many items sit inside a folder and everything under it.</summary>
    public int CountInside(PhoneFolder folder)
    {
        if (_data is null) return 0;

        var ids = FolderTree.DescendantsOf(_data.Folders, folder.Id)
            .Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        ids.Add(folder.Id);

        return _data.Items.Count(i => i.FolderId is not null && ids.Contains(i.FolderId));
    }

    /// <summary>
    /// Put one item immediately before another in the current folder, and remember that
    /// arrangement. Switches the vault to a hand-arranged order, because a manual position
    /// has no meaning under any other sort and dragging would otherwise appear to do
    /// nothing. Returns true if anything actually moved.
    /// </summary>
    public bool ArrangeItem(PhoneItem moved, PhoneItem? target, string? folderId)
    {
        if (_data is null) return false;

        // Arrange within the folder being looked at, in whatever order is on screen, so
        // "before that one" means what the user just saw.
        var visible = ItemOrdering
            .SortItems(_data.Items.Where(i => i.FolderId == folderId), _data.SortBy, _data.SortDescending)
            .ToList();

        if (!FolderTree.Arrange(visible, moved, target)) return false;

        _data.SortBy = ItemSort.Custom;
        _data.SortDescending = false;
        Save();
        return true;
    }

    public void SetSort(ItemSort by, bool descending)
    {
        if (_data is null) return;
        _data.SortBy = by;
        _data.SortDescending = descending;
        Save();
    }

    // ------------------------------------------------------------------- expiry

    /// <summary>
    /// Delete anything whose expiry has passed. Enforced by this phone using its own
    /// clock, so it works with no network and no contact with the sending PC.
    /// </summary>
    public int PurgeExpiredItems()
    {
        if (_data is null) return 0;

        var expired = _data.Items
            .Where(i => i.ExpiresUtc is not null && i.ExpiresUtc <= DateTime.UtcNow)
            .ToList();

        foreach (var item in expired)
        {
            try
            {
                string path = Path.Combine(AttachmentsDir, item.Id + ".lwa");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
            _data.Items.Remove(item);
        }

        if (expired.Count > 0) Save();
        return expired.Count;
    }

    /// <summary>
    /// The dead-man's switch. If this phone has not synced within the configured window,
    /// remove everything a PC sent it.
    ///
    /// Deliberately only touches <see cref="PhoneItemOrigin.ReceivedFromDevice"/>: those
    /// still exist on the PC that sent them, so deleting them costs nothing. Items
    /// created on this phone are never removed by any automatic rule -- destroying the
    /// only copy of something is not a security measure.
    ///
    /// Returns how many items were removed.
    /// </summary>
    public int ApplyStaleWipe()
    {
        if (_data is null || !_data.WipeReceivedIfStale) return 0;

        // Never synced yet means there is nothing that arrived from a PC to remove.
        if (_data.LastSyncUtc is null) return 0;

        double idleDays = (DateTime.UtcNow - _data.LastSyncUtc.Value).TotalDays;
        if (idleDays < Math.Max(1, _data.StaleAfterDays)) return 0;

        var received = _data.Items
            .Where(i => i.Origin == PhoneItemOrigin.ReceivedFromDevice)
            .ToList();

        foreach (var item in received)
        {
            try
            {
                string path = Path.Combine(AttachmentsDir, item.Id + ".lwa");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
            _data.Items.Remove(item);
        }

        if (received.Count > 0) Save();
        return received.Count;
    }

    /// <summary>Record a successful sync, which resets the stale-wipe clock.</summary>
    public void MarkSynced()
    {
        if (_data is null) return;
        _data.LastSyncUtc = DateTime.UtcNow;
        Save();
    }

    // --------------------------------------------------------------- save

    public void Save()
    {
        if (_dek is null || _data is null || _header is null)
            throw new InvalidOperationException("Cannot save a locked vault.");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(_data, PhoneVaultJson.Default.PhoneVaultData);
        _header.Data = Convert.ToBase64String(VaultCrypto.Encrypt(_dek, json));
        CryptographicOperations.ZeroMemory(json);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_header, PhoneVaultJson.Default.PhoneVaultHeader);

        // Temp file then move, so an interrupted write cannot truncate the vault.
        string temp = VaultFile + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, VaultFile, overwrite: true);
    }

    private PhoneVaultHeader ReadHeader()
    {
        byte[] bytes = File.ReadAllBytes(VaultFile);
        var header = JsonSerializer.Deserialize(bytes, PhoneVaultJson.Default.PhoneVaultHeader)
                     ?? throw new InvalidDataException("The vault header is unreadable.");
        if (header.Magic != "LOCKWELL-PHONE")
            throw new InvalidDataException("This file is not a Lockwell vault.");
        return header;
    }

    public static SecureString ToSecure(string text)
    {
        var secure = new SecureString();
        foreach (char c in text) secure.AppendChar(c);
        secure.MakeReadOnly();
        return secure;
    }
}

public sealed class PhoneVaultHeader
{
    public string Magic { get; set; } = "LOCKWELL-PHONE";
    public int Version { get; set; } = 1;

    public int MemoryKib { get; set; }
    public int Iterations { get; set; }
    public int Parallelism { get; set; }
    public string Salt { get; set; } = "";

    public string PasswordWrappedKey { get; set; } = "";
    public string Data { get; set; } = "";
}

public sealed class PhoneVaultData
{
    public int SchemaVersion { get; set; } = 1;
    public List<PhoneItem> Items { get; set; } = new();

    /// <summary>
    /// Folders for organising what is here. A vault written before folders existed has
    /// no such list, which deserialises to an empty one, leaving every item at the top
    /// level exactly where it was.
    /// </summary>
    public List<PhoneFolder> Folders { get; set; } = new();

    /// <summary>How the library is ordered. Remembered per vault, not per session.</summary>
    public ItemSort SortBy { get; set; } = ItemSort.Added;

    /// <summary>Newest first by default, which is what the top of a photo library means.</summary>
    public bool SortDescending { get; set; } = true;

    /// <summary>
    /// Offer to remove the phone's own copy after something is added to the vault.
    /// Off by default, and never more than an offer: the deletion is confirmed at the
    /// time, and only ever after the encrypted copy is safely written and read back.
    /// </summary>
    public bool OfferDeleteOriginal { get; set; }

    /// <summary>
    /// Wipe items received from a PC if this phone has not synced for a while.
    ///
    /// Off by default and self-enforced: the phone checks its own clock on unlock, so
    /// it works with no network and no cooperation from wherever the phone now is. It
    /// only removes items whose original still sits on the PC that sent them. Anything
    /// created here is never touched, because this phone may hold the only copy.
    /// </summary>
    public bool WipeReceivedIfStale { get; set; }

    /// <summary>Days without a sync before that wipe fires.</summary>
    public int StaleAfterDays { get; set; } = 30;

    /// <summary>Last successful sync with any linked PC.</summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Seconds the app may sit in the background before this vault locks itself.
    /// Zero locks immediately; a negative value never locks on leaving the app.
    ///
    /// Defaults to a minute rather than to zero because unlocking costs an Argon2id
    /// derivation, and making every glance at a notification cost several seconds would
    /// push people towards turning the protection off entirely.
    /// </summary>
    public int LockAfterSecondsAway { get; set; } = 60;
}

/// <summary>
/// A folder in the phone's library. Mirrors the desktop app's MediaFolder, minus the
/// parts that only make sense there, and implements the shared IFolderNode so the tree
/// rules in Lockwell.Core apply to it unchanged.
/// </summary>
public sealed class PhoneFolder : IFolderNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentFolderId { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum PhoneItemOrigin
{
    /// <summary>Added on this phone. This may be the only copy, so never auto-wiped.</summary>
    CreatedHere = 0,

    /// <summary>Sent from a linked PC, which still holds the original.</summary>
    ReceivedFromDevice = 1,
}

public sealed class PhoneItem : ISortableItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Which folder holds this, or null for the top level. Absent from vaults written
    /// before folders existed, which deserialises to null and so reads as top level.
    /// </summary>
    public string? FolderId { get; set; }
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MediaType { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    public PhoneItemOrigin Origin { get; set; } = PhoneItemOrigin.CreatedHere;

    /// <summary>Which device sent it, so a wipe can target only received items.</summary>
    public string FromDeviceFingerprint { get; set; } = "";

    /// <summary>Offer this item to a linked PC on the next sync.</summary>
    public bool OfferToPc { get; set; }

    /// <summary>Position when the library is arranged by hand. See ItemSort.Custom.</summary>
    public int Order { get; set; }

    /// <summary>
    /// When this item was last handed to a PC, or null if it never has been.
    ///
    /// Recorded when a transfer completes rather than when one is queued, so the mark says
    /// "the PC has this" and not "I meant to send this". Without it there was no way to
    /// tell, looking at the library, which things had actually made it across.
    /// </summary>
    public DateTime? SentToPcUtc { get; set; }

    /// <summary>Hash of the plaintext, so a re-sync can skip what is already here.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>Optional expiry, enforced locally by this phone.</summary>
    public DateTime? ExpiresUtc { get; set; }

    public bool IsImage => MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public bool IsVideo => MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    public bool IsAudio => MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(PhoneVaultHeader))]
[JsonSerializable(typeof(PhoneVaultData))]
[JsonSerializable(typeof(PhoneFolder))]
public partial class PhoneVaultJson : JsonSerializerContext
{
}
