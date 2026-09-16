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
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lockwell.Crypto;
using Lockwell.Models;

namespace Lockwell.Services;

public sealed class VaultMergeResult
{
    public int EntriesAdded { get; set; }
    public int EntriesSkipped { get; set; }
    public int FoldersAdded { get; set; }
    public int FoldersSkipped { get; set; }
    public int CategoriesAdded { get; set; }
    public int AttachmentsCopied { get; set; }
}

/// <summary>
/// Encrypted vault backup export/import. Blobs stay encrypted on disk; import never
/// decrypts to a permanent folder unless re-wrapping attachments for another vault key.
/// </summary>
public static class VaultBackupService
{
    public const string ManifestName = "lockwell-manifest.json";

    public static void Export(VaultManager vault, string zipPath, IProgress<OperationProgress>? progress = null)
    {
        progress?.Report(new OperationProgress
        {
            Title = "Exporting backup",
            Detail = "Saving vault...",
            IsIndeterminate = true,
        });

        // Recorded before the save so the timestamp travels inside the backup itself.
        if (vault.IsUnlocked)
            vault.Data.LastExportUtc = DateTime.UtcNow;

        vault.Save();

        if (!File.Exists(vault.VaultFile))
            throw new InvalidOperationException("Vault file is missing.");

        string? dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(zipPath))
            File.Delete(zipPath);

        var attachmentFiles = Directory.Exists(vault.AttachmentsDir)
            ? Directory.EnumerateFiles(vault.AttachmentsDir, "*.lwa").ToList()
            : new List<string>();

        int totalSteps = 2 + attachmentFiles.Count;
        int step = 0;

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        progress?.Report(new OperationProgress
        {
            Title = "Exporting backup",
            Detail = "Adding vault file...",
            Current = ++step,
            Total = totalSteps,
        });
        zip.CreateEntryFromFile(vault.VaultFile, "vault.lwv", CompressionLevel.Optimal);

        foreach (string file in attachmentFiles)
        {
            string name = Path.GetFileName(file);
            progress?.Report(new OperationProgress
            {
                Title = "Exporting backup",
                Detail = name,
                Current = ++step,
                Total = totalSteps,
            });
            zip.CreateEntryFromFile(file, "attachments/" + name, CompressionLevel.Optimal);
        }

        var manifest = new BackupManifest
        {
            Format = "lockwell-backup-v1",
            ExportedUtc = DateTime.UtcNow,
            AttachmentCount = attachmentFiles.Count,
        };

        progress?.Report(new OperationProgress
        {
            Title = "Exporting backup",
            Detail = "Writing manifest...",
            Current = ++step,
            Total = totalSteps,
        });

        ZipArchiveEntry manifestEntry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
        using (Stream stream = manifestEntry.Open())
            JsonSerializer.Serialize(stream, manifest, BackupManifestJsonContext.Default.BackupManifest);
    }

    public static VaultMergeResult ImportMerge(
        VaultManager target,
        string zipPath,
        SecureString backupPassword,
        IProgress<OperationProgress>? progress = null)
    {
        if (!target.IsUnlocked)
            throw new InvalidOperationException("Unlock the vault before importing.");

        progress?.Report(new OperationProgress
        {
            Title = "Importing backup",
            Detail = "Extracting ZIP...",
            IsIndeterminate = true,
        });

        string tempDir = Path.Combine(Path.GetTempPath(), "lockwell-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir);

            string vaultFile = Path.Combine(tempDir, "vault.lwv");
            if (!File.Exists(vaultFile))
                throw new InvalidDataException("This ZIP is not a Lockwell backup (vault.lwv missing).");

            progress?.Report(new OperationProgress
            {
                Title = "Importing backup",
                Detail = "Unlocking backup...",
                IsIndeterminate = true,
            });

            string attachmentsDir = Path.Combine(tempDir, "attachments");
            if (!VaultReader.TryUnlock(vaultFile, backupPassword, out byte[]? backupDek, out VaultData? backupData))
                throw new CryptographicException("Wrong password or backup file is damaged.");

            try
            {
                progress?.Report(new OperationProgress
                {
                    Title = "Importing backup",
                    Detail = "Merging into your vault...",
                    IsIndeterminate = true,
                });
                return target.MergeFrom(backupData!, attachmentsDir, backupDek!, progress);
            }
            finally
            {
                if (backupDek is not null) Array.Clear(backupDek, 0, backupDek.Length);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* temp cleanup best effort */ }
        }
    }

    /// <summary>
    /// Restore a backup ZIP into an empty profile folder. Verifies the password before writing files.
    /// </summary>
    public static void RestoreToVaultDirectory(
        string zipPath,
        string vaultDir,
        SecureString backupPassword,
        IProgress<OperationProgress>? progress = null)
    {
        if (File.Exists(Path.Combine(vaultDir, "vault.lwv")))
            throw new InvalidOperationException("This profile already has a vault.");

        progress?.Report(new OperationProgress
        {
            Title = "Importing backup",
            Detail = "Extracting ZIP...",
            IsIndeterminate = true,
        });

        string tempDir = Path.Combine(Path.GetTempPath(), "lockwell-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir);

            string vaultFile = Path.Combine(tempDir, "vault.lwv");
            if (!File.Exists(vaultFile))
                throw new InvalidDataException("This ZIP is not a Lockwell backup (vault.lwv missing).");

            progress?.Report(new OperationProgress
            {
                Title = "Importing backup",
                Detail = "Checking backup password...",
                IsIndeterminate = true,
            });

            if (!VaultReader.TryUnlock(vaultFile, backupPassword, out byte[]? dek, out VaultData? _))
            {
                throw new CryptographicException("Wrong password or backup file is damaged.");
            }

            if (dek is not null)
                Array.Clear(dek, 0, dek.Length);

            Directory.CreateDirectory(vaultDir);
            string attachmentsDir = Path.Combine(vaultDir, "attachments");
            Directory.CreateDirectory(attachmentsDir);

            progress?.Report(new OperationProgress
            {
                Title = "Importing backup",
                Detail = "Restoring vault...",
                IsIndeterminate = true,
            });

            File.Copy(vaultFile, Path.Combine(vaultDir, "vault.lwv"), overwrite: false);

            string sourceAttachments = Path.Combine(tempDir, "attachments");
            if (Directory.Exists(sourceAttachments))
            {
                foreach (string file in Directory.EnumerateFiles(sourceAttachments, "*.lwa"))
                {
                    string dest = Path.Combine(attachmentsDir, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: false);
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* temp cleanup best effort */ }
        }
    }
}

public sealed class BackupManifest
{
    public string Format { get; set; } = "";
    public DateTime ExportedUtc { get; set; }
    public int AttachmentCount { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(BackupManifest))]
internal partial class BackupManifestJsonContext : JsonSerializerContext
{
}

/// <summary>Read an on-disk vault file without mounting it in VaultManager.</summary>
internal static class VaultReader
{
    public static bool TryUnlock(string vaultFilePath, SecureString password, out byte[]? dek, out VaultData? data)
    {
        dek = null;
        data = null;

        try
        {
            byte[] bytes = File.ReadAllBytes(vaultFilePath);
            var header = JsonSerializer.Deserialize(bytes, VaultHeaderJsonContext.Default.VaultHeader)
                         ?? throw new InvalidDataException("Vault header is unreadable.");
            if (header.Magic != "LOCKWELL")
                throw new InvalidDataException("This file is not a Lockwell vault.");

            var p = new Argon2Parameters
            {
                Salt = Convert.FromBase64String(header.Kdf.Salt),
                MemoryKib = header.Kdf.MemoryKib,
                Iterations = header.Kdf.Iterations,
                Parallelism = header.Kdf.Parallelism,
            };

            byte[] kek = Argon2KeyDeriver.DeriveKey(password, p);
            try
            {
                byte[] key = VaultCrypto.Decrypt(kek, Convert.FromBase64String(header.PasswordWrappedKey));
                byte[] json = VaultCrypto.Decrypt(key, Convert.FromBase64String(header.Data));
                var vaultData = JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultData)
                                  ?? new VaultData { Categories = VaultData.DefaultCategories() };
                VaultData.ApplyMigrations(vaultData);
                Array.Clear(json, 0, json.Length);
                dek = key;
                data = vaultData;
                return true;
            }
            finally
            {
                Array.Clear(kek, 0, kek.Length);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
