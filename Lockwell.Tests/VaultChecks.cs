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

using System.Text;
using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Tests;

/// <summary>The core promises: a vault is unreadable without the password, tampering
/// is caught, and recovery works. If any of this fails, nothing else matters.</summary>
internal static class VaultChecks
{
    public static void Run()
    {
        RoundTrip();
        WrongPassword();
        Tampering();
        Recovery();
        PasswordChange();
        BackupTimestamp();
        StorageAccounting();
        StolenVaultLeak();
    }

    private static void RoundTrip()
    {
        Check.Section("1. Vault round trip");
        string dir = Check.Scratch("roundtrip");
        try
        {
            var vault = new VaultManager(dir);
            using var pw = Check.Secret("a-long-demo-passphrase-for-tests");

            vault.CreateVault(pw, withRecoveryKey: false);
            Check.That("vault is created and unlocked", vault.IsUnlocked);

            var cat = vault.Data.Categories[0];
            var entry = new VaultEntry { CategoryId = cat.Id, Title = "Canary" };
            entry.Fields.Add(new EntryField { Label = "Password", Value = "canary-value", IsSecret = true });
            vault.Data.Entries.Add(entry);
            vault.Save();

            vault.Lock();
            Check.That("locking clears the unlocked state", !vault.IsUnlocked);

            var reopened = new VaultManager(dir);
            Check.That("re-unlock with the right password", reopened.Unlock(pw));
            Check.That("canary survived the round trip",
                reopened.Data.Entries.Any(e => e.Title == "Canary"));
        }
        finally { Check.Cleanup(dir); }
    }

    private static void WrongPassword()
    {
        Check.Section("2. Wrong password");
        string dir = Check.Scratch("wrongpw");
        try
        {
            var vault = new VaultManager(dir);
            using var right = Check.Secret("the-correct-passphrase-here");
            using var wrong = Check.Secret("the-correct-passphrase-heres"); // one char off
            vault.CreateVault(right, withRecoveryKey: false);
            vault.Lock();

            var attempt = new VaultManager(dir);
            Check.That("a one-character-off password is rejected", !attempt.Unlock(wrong));
            Check.That("vault stays locked after a failed attempt", !attempt.IsUnlocked);
            Check.That("the correct password still works after a failure", attempt.Unlock(right));
        }
        finally { Check.Cleanup(dir); }
    }

    private static void Tampering()
    {
        Check.Section("3. Tamper detection");
        string dir = Check.Scratch("tamper");
        try
        {
            var vault = new VaultManager(dir);
            using var pw = Check.Secret("tamper-test-passphrase-long");
            vault.CreateVault(pw, withRecoveryKey: false);
            vault.Save();
            vault.Lock();

            // Flip one byte inside the encrypted payload.
            string raw = File.ReadAllText(vault.VaultFile);
            int at = raw.IndexOf("\"Data\": \"", StringComparison.Ordinal) + 12;
            char original = raw[at];
            char swapped = original == 'A' ? 'B' : 'A';
            File.WriteAllText(vault.VaultFile, raw[..at] + swapped + raw[(at + 1)..]);

            var attempt = new VaultManager(dir);
            bool opened;
            try { opened = attempt.Unlock(pw); }
            catch { opened = false; } // a mangled base64 blob may throw before GCM sees it

            Check.That("a single flipped byte makes the vault refuse to open", !opened);
        }
        finally { Check.Cleanup(dir); }
    }

    private static void Recovery()
    {
        Check.Section("4. Recovery key");
        string dir = Check.Scratch("recovery");
        try
        {
            var vault = new VaultManager(dir);
            using var pw = Check.Secret("recovery-test-passphrase-x");

            string? recovery = vault.CreateVault(pw, withRecoveryKey: true);
            Check.That("a recovery key is produced", !string.IsNullOrWhiteSpace(recovery));
            vault.Lock();

            var byKey = new VaultManager(dir);
            Check.That("recovery key unlocks the vault", byKey.UnlockWithRecovery(recovery!));

            byKey.Lock();
            var byGarbage = new VaultManager(dir);
            Check.That("a wrong recovery key is rejected",
                !byGarbage.UnlockWithRecovery("ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ"));
        }
        finally { Check.Cleanup(dir); }
    }

    private static void PasswordChange()
    {
        Check.Section("5. Password change keeps the data key");
        string dir = Check.Scratch("changepw");
        try
        {
            var vault = new VaultManager(dir);
            using var oldPw = Check.Secret("original-passphrase-here-1");
            using var newPw = Check.Secret("replacement-passphrase-2");

            string? recovery = vault.CreateVault(oldPw, withRecoveryKey: true);
            var cat = vault.Data.Categories[0];
            vault.Data.Entries.Add(new VaultEntry { CategoryId = cat.Id, Title = "Survivor" });
            vault.Save();

            vault.ChangePassword(newPw);
            vault.Lock();

            var withOld = new VaultManager(dir);
            Check.That("old password no longer works", !withOld.Unlock(oldPw));

            var withNew = new VaultManager(dir);
            Check.That("new password works", withNew.Unlock(newPw));
            Check.That("entries survived the password change",
                withNew.Data.Entries.Any(e => e.Title == "Survivor"));

            withNew.Lock();
            var withRecovery = new VaultManager(dir);
            Check.That("recovery key still works after a password change",
                withRecovery.UnlockWithRecovery(recovery!));
        }
        finally { Check.Cleanup(dir); }
    }

    private static void BackupTimestamp()
    {
        Check.Section("6. Backup timestamp lives inside the encrypted vault");
        string dir = Check.Scratch("backup");
        try
        {
            var vault = new VaultManager(dir);
            using var pw = Check.Secret("backup-timestamp-passphrase");
            vault.CreateVault(pw, withRecoveryKey: false);

            Check.That("a fresh vault reports no previous export", vault.Data.LastExportUtc is null);

            string zip = Path.Combine(dir, "backup.zip");
            VaultBackupService.Export(vault, zip);

            Check.That("export stamps LastExportUtc", vault.Data.LastExportUtc is not null);
            Check.That("backup zip was produced", File.Exists(zip));

            DateTime stamped = vault.Data.LastExportUtc!.Value;
            vault.Lock();

            var reopened = new VaultManager(dir);
            Check.That("re-unlock succeeds", reopened.Unlock(pw));
            Check.That("timestamp survived the encrypted round trip",
                reopened.Data.LastExportUtc == stamped);

            string onDisk = File.ReadAllText(reopened.VaultFile);
            Check.That("timestamp is NOT readable in the vault file",
                !onDisk.Contains("LastExportUtc", StringComparison.Ordinal));
        }
        finally { Check.Cleanup(dir); }
    }

    private static void StorageAccounting()
    {
        Check.Section("7. Storage accounting");
        string dir = Check.Scratch("storage");
        try
        {
            var vault = new VaultManager(dir);
            using var pw = Check.Secret("storage-accounting-passphrase");
            vault.CreateVault(pw, withRecoveryKey: false);

            long before = vault.CalculateDiskSizeBytes();
            Check.That($"an empty vault still occupies space ({before} B)", before > 0);

            int[] sizes = { 400_000, 90_000, 12_000 };
            long plaintextTotal = 0;
            foreach (int size in sizes)
            {
                var att = vault.AddAttachmentFromBytes(new byte[size], $"f{size}.bin", "application/octet-stream");
                plaintextTotal += att.SizeBytes;
                Check.That($"attachment records its true size ({size:N0} B)", att.SizeBytes == size);
            }

            long growth = vault.CalculateDiskSizeBytes() - before;
            Check.That($"disk grew by the attachment bytes ({growth:N0} B)",
                growth >= plaintextTotal && growth <= plaintextTotal + sizes.Length * 64);
            Check.That("encryption overhead stays negligible",
                growth - plaintextTotal <= sizes.Length * 64);
        }
        finally { Check.Cleanup(dir); }
    }

    /// <summary>
    /// Proves what someone holding the vault file can and cannot recover. This is the
    /// check that answers "is my data actually safe if the PC is stolen".
    /// </summary>
    private static void StolenVaultLeak()
    {
        Check.Section("8. What leaks from a stolen vault file");
        string dir = Check.Scratch("leak");
        try
        {
            var vault = new VaultManager(dir);
            using var pw = Check.Secret("a-very-long-master-passphrase");
            vault.CreateVault(pw, withRecoveryKey: false);

            var cat = vault.Data.Categories[0];
            var entry = new VaultEntry { CategoryId = cat.Id, Title = "MyBankLogin", Notes = "SUPERSECRETNOTE" };
            entry.Fields.Add(new EntryField { Label = "Password", Value = "hunter2-TOPSECRET", IsSecret = true });
            vault.Data.Entries.Add(entry);

            var att = vault.AddAttachmentFromBytes(
                Encoding.UTF8.GetBytes("PNG-PIXEL-DATA-RECOGNISABLE"), "HolidayPhoto.png", "image/png");
            entry.Attachments.Add(att);
            vault.Save();
            vault.Lock();

            string raw = File.ReadAllText(vault.VaultFile);
            foreach (string secret in new[]
                     { "MyBankLogin", "SUPERSECRETNOTE", "hunter2-TOPSECRET", "HolidayPhoto" })
            {
                Check.That($"hidden from a stolen vault file: {secret}",
                    !raw.Contains(secret, StringComparison.OrdinalIgnoreCase));
            }

            string blobDir = Path.Combine(dir, "attachments");
            string blob = File.ReadAllText(Directory.GetFiles(blobDir, "*.lwa")[0]);
            Check.That("attachment body is not readable in the .lwa blob",
                !blob.Contains("PNG-PIXEL-DATA", StringComparison.Ordinal));
            Check.That("attachment filename is not readable in the .lwa blob",
                !blob.Contains("HolidayPhoto", StringComparison.Ordinal));

            Check.Note("Visible without the password (unavoidable): file sizes, attachment");
            Check.Note("count, timestamps, and the KDF salt/params. Contents are not.");
        }
        finally { Check.Cleanup(dir); }
    }
}
