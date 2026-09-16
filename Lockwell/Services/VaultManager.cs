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
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Lockwell.Crypto;
using Lockwell.Models;

namespace Lockwell.Services;

/// <summary>
/// Owns the one and only in-memory copy of the decrypted vault and the data key.
/// Everything the UI does to secrets goes through here. The UI never sees the
/// master password key or the raw data key.
///
/// On disk:
///   {VaultDir}\vault.lwv            encrypted header + contents
///   {VaultDir}\attachments\{id}.lwa each attachment, individually encrypted
/// </summary>
public sealed class VaultManager
{
    public string VaultDir { get; }
    public string VaultFile => Path.Combine(VaultDir, "vault.lwv");
    public string AttachmentsDir => Path.Combine(VaultDir, "attachments");

    /// <summary>
    /// The data-encryption key, present only while unlocked.
    ///
    /// Held outside the managed heap and pinned against paging, so a copy cannot end
    /// up in pagefile.sys or hiberfil.sys and outlive the process. See LockedKey for
    /// what that does and does not protect against.
    /// </summary>
    private LockedKey? _dek;
    private VaultHeader? _header;
    private VaultData? _data;

    public VaultIntegrityReport? LastIntegrityReport { get; private set; }

    public bool IsUnlocked => _dek is not null && _data is not null;

    public VaultManager(string? vaultDir = null)
    {
        VaultDir = vaultDir ?? AppDataPaths.AppRoot;
    }

    public bool VaultExists => File.Exists(VaultFile);

    /// <summary>
    /// Encrypted footprint on disk: the vault file plus every attachment blob. This is
    /// the real size the vault occupies, not the sum of the original file sizes.
    /// </summary>
    public long CalculateDiskSizeBytes()
    {
        long total = 0;
        try
        {
            if (File.Exists(VaultFile))
                total += new FileInfo(VaultFile).Length;

            if (Directory.Exists(AttachmentsDir))
            {
                foreach (string file in Directory.EnumerateFiles(AttachmentsDir, "*.lwa"))
                    total += new FileInfo(file).Length;
            }
        }
        catch { /* size is informational only */ }
        return total;
    }

    /// <summary>The decrypted vault. Throws if locked, so we never accidentally read secrets while locked.</summary>
    public VaultData Data => _data ?? throw new InvalidOperationException("Vault is locked.");

    // ---------------------------------------------------------------- create

    /// <summary>
    /// Create a brand-new vault. Returns the formatted recovery key if
    /// <paramref name="withRecoveryKey"/> is true, otherwise null.
    /// </summary>
    public string? CreateVault(SecureString masterPassword, bool withRecoveryKey)
    {
        if (VaultExists)
            throw new InvalidOperationException("A vault already exists at this location.");

        Directory.CreateDirectory(VaultDir);
        Directory.CreateDirectory(AttachmentsDir);

        var argonParams = Argon2Parameters.CreateDefault();
        byte[] kekPassword = Argon2KeyDeriver.DeriveKey(masterPassword, argonParams);

        byte[] dek = SecureRandom.GetBytes(Argon2KeyDeriver.KeyLengthBytes);

        string? recoveryFormatted = null;
        string? recoveryWrapped = null;
        if (withRecoveryKey)
        {
            byte[] recoveryKey = RecoveryCode.Generate();
            recoveryFormatted = RecoveryCode.Format(recoveryKey);
            recoveryWrapped = Convert.ToBase64String(VaultCrypto.Encrypt(recoveryKey, dek));
            Array.Clear(recoveryKey, 0, recoveryKey.Length);
        }

        var data = new VaultData { Categories = VaultData.DefaultCategories() };

        _header = new VaultHeader
        {
            Kdf = new KdfSettings
            {
                Algorithm = "argon2id",
                MemoryKib = argonParams.MemoryKib,
                Iterations = argonParams.Iterations,
                Parallelism = argonParams.Parallelism,
                Salt = Convert.ToBase64String(argonParams.Salt),
            },
            PasswordWrappedKey = Convert.ToBase64String(VaultCrypto.Encrypt(kekPassword, dek)),
            RecoveryWrappedKey = recoveryWrapped,
        };

        _dek = new LockedKey(dek);
        _data = data;

        Array.Clear(kekPassword, 0, kekPassword.Length);

        Save();
        return recoveryFormatted;
    }

    // ---------------------------------------------------------------- unlock

    /// <summary>Try to unlock with the master password. Returns false on wrong password.</summary>
    public bool Unlock(SecureString masterPassword)
    {
        var header = ReadHeader();
        byte[] kek = Argon2KeyDeriver.DeriveKey(masterPassword, ParamsFrom(header));
        try
        {
            byte[] dek = VaultCrypto.Decrypt(kek, Convert.FromBase64String(header.PasswordWrappedKey));
            LoadData(header, dek);
            return true;
        }
        catch (CryptographicException)
        {
            return false; // wrong password or tampered file
        }
        finally
        {
            Array.Clear(kek, 0, kek.Length);
        }
    }

    /// <summary>Try to unlock with the recovery key. Returns false if it does not match or recovery is disabled.</summary>
    public bool UnlockWithRecovery(string recoveryKeyText)
    {
        var header = ReadHeader();
        if (header.RecoveryWrappedKey is null) return false;

        byte[]? recoveryKey = RecoveryCode.TryParse(recoveryKeyText);
        if (recoveryKey is null) return false;

        try
        {
            byte[] dek = VaultCrypto.Decrypt(recoveryKey, Convert.FromBase64String(header.RecoveryWrappedKey));
            LoadData(header, dek);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            Array.Clear(recoveryKey, 0, recoveryKey.Length);
        }
    }

    private void LoadData(VaultHeader header, byte[] dek)
    {
        byte[] json = VaultCrypto.Decrypt(dek, Convert.FromBase64String(header.Data));
        var data = JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultData)
                   ?? new VaultData { Categories = VaultData.DefaultCategories() };
        bool migrated = VaultData.ApplyMigrations(data);
        Array.Clear(json, 0, json.Length);

        _header = header;
        _dek = new LockedKey(dek);
        _data = data;

        var integrity = VaultIntegrityService.Repair(this);
        LastIntegrityReport = integrity;
        if (migrated || integrity.Changed)
            Save();
    }

    // ---------------------------------------------------------------- lock

    /// <summary>Wipe all secrets from memory. After this the UI can show nothing sensitive.</summary>
    public void Lock()
    {
        _dek?.Dispose();
        _dek = null;
        _data = null;
        LastIntegrityReport = null;
        // header may stay; it contains no secrets
    }

    // ---------------------------------------------------------------- save

    /// <summary>Re-encrypt the in-memory vault and write it atomically to disk.</summary>
    public void Save()
    {
        if (_dek is null || _data is null || _header is null)
            throw new InvalidOperationException("Cannot save a locked vault.");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(_data, VaultJsonContext.Default.VaultData);
        _header.Data = _dek.Use(key => Convert.ToBase64String(VaultCrypto.Encrypt(key, json)));
        Array.Clear(json, 0, json.Length);

        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(_header, VaultHeaderJsonContext.Default.VaultHeader);

        Directory.CreateDirectory(VaultDir);
        string tmp = VaultFile + ".tmp";
        File.WriteAllBytes(tmp, headerBytes);
        if (File.Exists(VaultFile)) File.Replace(tmp, VaultFile, null);
        else File.Move(tmp, VaultFile);
    }

    // ------------------------------------------------------- password change

    /// <summary>Change the master password by re-wrapping the same data key. Contents are untouched.</summary>
    public void ChangePassword(SecureString newPassword)
    {
        if (_dek is null || _header is null)
            throw new InvalidOperationException("Unlock the vault first.");

        var argonParams = Argon2Parameters.CreateDefault();
        byte[] kek = Argon2KeyDeriver.DeriveKey(newPassword, argonParams);
        _header.Kdf = new KdfSettings
        {
            Algorithm = "argon2id",
            MemoryKib = argonParams.MemoryKib,
            Iterations = argonParams.Iterations,
            Parallelism = argonParams.Parallelism,
            Salt = Convert.ToBase64String(argonParams.Salt),
        };
        _header.PasswordWrappedKey = _dek.Use(key => Convert.ToBase64String(VaultCrypto.Encrypt(kek, key)));
        Array.Clear(kek, 0, kek.Length);
        Save();
    }

    // ---------------------------------------------------------- attachments

    /// <summary>Encrypt a file from disk into the vault and return its reference.</summary>
    public AttachmentRef AddAttachment(string sourcePath)
    {
        if (_dek is null) throw new InvalidOperationException("Vault is locked.");

        byte[] plain = File.ReadAllBytes(sourcePath);
        var att = new AttachmentRef
        {
            FileName = Path.GetFileName(sourcePath),
            MediaType = GuessMediaType(sourcePath),
            SizeBytes = plain.Length,
        };

        Directory.CreateDirectory(AttachmentsDir);
        byte[] blob = _dek.Use(key => VaultCrypto.Encrypt(key, plain));
        File.WriteAllBytes(Path.Combine(AttachmentsDir, att.Id + ".lwa"), blob);
        Array.Clear(plain, 0, plain.Length);
        return att;
    }

    /// <summary>
    /// Store bytes that are already in memory as a new attachment. Used by compression,
    /// which produces its result in RAM -- going via a temp file would put decrypted
    /// data on disk for no reason.
    /// </summary>
    public AttachmentRef AddAttachmentFromBytes(byte[] plain, string fileName, string mediaType)
    {
        if (_dek is null) throw new InvalidOperationException("Vault is locked.");

        var att = new AttachmentRef
        {
            FileName = fileName,
            MediaType = mediaType,
            SizeBytes = plain.Length,
        };

        Directory.CreateDirectory(AttachmentsDir);
        byte[] blob = _dek.Use(key => VaultCrypto.Encrypt(key, plain));
        File.WriteAllBytes(Path.Combine(AttachmentsDir, att.Id + ".lwa"), blob);
        Array.Clear(blob, 0, blob.Length);
        return att;
    }

    /// <summary>Decrypt and return an attachment's bytes. Called lazily, only when opened.</summary>
    public byte[] ReadAttachment(AttachmentRef att)
    {
        if (_dek is null) throw new InvalidOperationException("Vault is locked.");
        byte[] blob = File.ReadAllBytes(Path.Combine(AttachmentsDir, att.Id + ".lwa"));
        return _dek.Use(key => VaultCrypto.Decrypt(key, blob));
    }

    public void DeleteAttachment(AttachmentRef att)
    {
        string path = Path.Combine(AttachmentsDir, att.Id + ".lwa");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Merge items from a backup vault into this one. Skips IDs that already exist.
    /// Re-encrypts attachments when the backup used a different data key.
    /// </summary>
    public VaultMergeResult MergeFrom(
        VaultData incoming,
        string incomingAttachmentsDir,
        byte[] incomingDek,
        IProgress<OperationProgress>? progress = null)
    {
        if (_dek is null || _data is null)
            throw new InvalidOperationException("Vault is locked.");

        bool sameKey = incomingDek.Length == _dek.Length &&
                       _dek.Use(key => CryptographicOperations.FixedTimeEquals(incomingDek, key));

        var result = new VaultMergeResult();
        Directory.CreateDirectory(AttachmentsDir);

        var categoryIds = new HashSet<string>(_data.Categories.Select(c => c.Id));
        foreach (var category in incoming.Categories)
        {
            if (!categoryIds.Add(category.Id)) continue;
            _data.Categories.Add(category);
            result.CategoriesAdded++;
        }

        var folderIds = new HashSet<string>(_data.MediaFolders.Select(f => f.Id));
        foreach (var folder in incoming.MediaFolders
                     .OrderBy(f => string.IsNullOrEmpty(f.ParentFolderId) ? 0 : 1)
                     .ThenBy(f => f.Order))
        {
            if (!folderIds.Add(folder.Id))
            {
                result.FoldersSkipped++;
                continue;
            }
            _data.MediaFolders.Add(folder);
            result.FoldersAdded++;
        }

        var entryIds = new HashSet<string>(_data.Entries.Select(e => e.Id));
        int attachmentTotal = incoming.Entries.Sum(e => e.Attachments.Count);
        int attachmentStep = 0;

        foreach (var entry in incoming.Entries)
        {
            if (!entryIds.Add(entry.Id))
            {
                result.EntriesSkipped++;
                continue;
            }

            foreach (var att in entry.Attachments)
            {
                string src = Path.Combine(incomingAttachmentsDir, att.Id + ".lwa");
                string dest = Path.Combine(AttachmentsDir, att.Id + ".lwa");
                if (!File.Exists(src)) continue;

                progress?.Report(new OperationProgress
                {
                    Title = "Importing backup",
                    Detail = att.FileName,
                    Current = attachmentStep + 1,
                    Total = Math.Max(1, attachmentTotal),
                });

                if (File.Exists(dest))
                {
                    result.AttachmentsCopied++;
                    attachmentStep++;
                    continue;
                }

                byte[] blob = File.ReadAllBytes(src);
                if (!sameKey)
                {
                    byte[] plain = VaultCrypto.Decrypt(incomingDek, blob);
                    blob = _dek.Use(key => VaultCrypto.Encrypt(key, plain));
                    Array.Clear(plain, 0, plain.Length);
                }
                File.WriteAllBytes(dest, blob);
                Array.Clear(blob, 0, blob.Length);
                result.AttachmentsCopied++;
                attachmentStep++;
            }

            _data.Entries.Add(entry);
            result.EntriesAdded++;
        }

        Save();
        return result;
    }

    // ---------------------------------------------------------------- helpers

    private VaultHeader ReadHeader()
    {
        if (_header is not null && _data is null)
        {
            // header cached from a previous read but vault still locked
        }
        byte[] bytes = File.ReadAllBytes(VaultFile);
        var header = JsonSerializer.Deserialize(bytes, VaultHeaderJsonContext.Default.VaultHeader)
                     ?? throw new InvalidDataException("Vault header is unreadable.");
        if (header.Magic != "LOCKWELL")
            throw new InvalidDataException("This file is not a Lockwell vault.");
        return header;
    }

    private static Argon2Parameters ParamsFrom(VaultHeader header) => new()
    {
        Salt = Convert.FromBase64String(header.Kdf.Salt),
        MemoryKib = header.Kdf.MemoryKib,
        Iterations = header.Kdf.Iterations,
        Parallelism = header.Kdf.Parallelism,
    };

    public bool RecoveryEnabled
    {
        get
        {
            try { return ReadHeader().RecoveryWrappedKey is not null; }
            catch { return false; }
        }
    }

    private static string GuessMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
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
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };
}
