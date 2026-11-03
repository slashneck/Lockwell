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

using System.Threading;
using Lockwell.Services;

namespace Lockwell.Tests;

/// <summary>
/// Moving a vault to another drive.
///
/// This is the most dangerous operation in the app that does not involve a password: it
/// copies the only copy of someone's data somewhere else and then offers to delete the
/// original. The checks here are mostly about the refusals, because every one of them is
/// a case where going ahead would have destroyed or orphaned real data.
///
/// The move must also never need the master password. A vault that had to be unlocked to
/// be moved would mean decrypting everything to put it on a USB stick, which is the
/// opposite of the point. So the round-trip check here moves a vault while it is locked
/// and then opens it at the far end.
/// </summary>
internal static class RelocationChecks
{
    public static void Run()
    {
        Refusals();
        RoundTrip();
        VerifyCatchesDamage();
        ProfileDeletion();
    }

    // ------------------------------------------------------- deleting a profile

    private static void ProfileDeletion()
    {
        Check.Section("69. Deleting a profile removes that vault and nothing else");

        string scratch = Check.Scratch("profile-delete");
        try
        {
            // The first profile's vault lives in the data root, with everything else inside it.
            File.WriteAllText(Path.Combine(scratch, "vault.lwv"), "{}");
            File.WriteAllText(Path.Combine(scratch, "settings.json"), "{}");
            Directory.CreateDirectory(Path.Combine(scratch, "attachments"));
            string other = Path.Combine(scratch, "profiles", "other");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "vault.lwv"), "{}");

            ProfileManager.DeleteVaultFiles(scratch);

            Check.That("the vault's own file is gone", !File.Exists(Path.Combine(scratch, "vault.lwv")));
            Check.That("its attachments are gone", !Directory.Exists(Path.Combine(scratch, "attachments")));
            Check.That("settings beside it survive", File.Exists(Path.Combine(scratch, "settings.json")));
            Check.That("another profile's vault stored underneath survives",
                File.Exists(Path.Combine(other, "vault.lwv")));

            string lone = Path.Combine(scratch, "lone");
            Directory.CreateDirectory(lone);
            File.WriteAllText(Path.Combine(lone, "vault.lwv"), "{}");
            ProfileManager.DeleteVaultFiles(lone);
            Check.That("a profile's own folder is tidied away once empty", !Directory.Exists(lone));

            var unplugged = new Models.VaultProfile
            {
                VaultDir = Path.Combine(scratch, "gone", "Lockwell vault"),
            };
            Check.That("a vault outside app storage that is missing counts as disconnected",
                ProfileManager.IsVaultDisconnected(unplugged));
            Check.That("a new profile inside app storage does not",
                !ProfileManager.IsVaultDisconnected(new Models.VaultProfile
                {
                    VaultDir = Path.Combine(ProfileManager.ProfilesRoot, "new"),
                }));
        }
        finally
        {
            Check.Cleanup(scratch);
        }
    }

    // ---------------------------------------------------------------- refusals

    private static void Refusals()
    {
        Check.Section("66. A vault move refuses anything that could lose data");

        string scratch = Check.Scratch("relocate-refuse");
        try
        {
            string source = Path.Combine(scratch, "source");
            string elsewhere = Path.Combine(scratch, "elsewhere");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(elsewhere);

            Check.That("a folder with no vault in it cannot be moved",
                VaultRelocation.Validate(source, elsewhere) is not null);

            File.WriteAllText(Path.Combine(source, VaultRelocation.VaultFileName), "{}");

            Check.That("a real vault can be moved somewhere separate",
                VaultRelocation.Validate(source, elsewhere) is null);

            Check.That("moving a vault onto itself is refused",
                VaultRelocation.Validate(source, source) is not null);

            // The copy would be writing into the directory it is reading from.
            string inside = Path.Combine(source, "nested");
            Directory.CreateDirectory(inside);
            Check.That("a destination inside the vault is refused",
                VaultRelocation.Validate(source, inside) is not null);

            Check.That("a destination that contains the vault is refused",
                VaultRelocation.Validate(source, scratch) is not null);

            // Landing on top of a different vault would leave two vaults in one folder,
            // and the second one would be unreachable.
            File.WriteAllText(Path.Combine(elsewhere, VaultRelocation.VaultFileName), "{}");
            Check.That("moving onto an existing vault is refused",
                VaultRelocation.Validate(source, elsewhere) is not null);

            string leftovers = Path.Combine(scratch, "leftovers");
            Directory.CreateDirectory(Path.Combine(leftovers, "attachments"));
            Check.That("a folder holding another vault's leftovers is refused",
                VaultRelocation.Validate(source, leftovers) is not null);

            string usb = Path.Combine(scratch, "usb");
            Directory.CreateDirectory(usb);
            string landed = VaultRelocation.ResolveDestination(usb);
            Check.That("a move lands in its own new folder, not loose in the one picked",
                !Directory.Exists(landed) && Path.GetDirectoryName(landed) == usb);

            Check.That("every refusal explains itself in a sentence",
                VaultRelocation.Validate(source, source)?.EndsWith('.') == true);

            Check.That("a path inside another is recognised",
                VaultRelocation.Contains(scratch, source));
            Check.That("a sibling path is not",
                !VaultRelocation.Contains(source, elsewhere));
        }
        finally
        {
            Check.Cleanup(scratch);
        }
    }

    // -------------------------------------------------------------- round trip

    private static void RoundTrip()
    {
        Check.Section("67. A moved vault still opens, without ever being decrypted");

        string scratch = Check.Scratch("relocate-move");
        try
        {
            string source = Path.Combine(scratch, "before");
            string destination = Path.Combine(scratch, "after");
            Directory.CreateDirectory(source);

            // A real vault with a real attachment in it.
            var vault = new VaultManager(source);
            using (var password = Check.Secret("a long enough passphrase"))
                vault.CreateVault(password, withRecoveryKey: false);

            string payloadPath = Path.Combine(scratch, "payload.bin");
            byte[] payload = new byte[64 * 1024];
            Random.Shared.NextBytes(payload);
            File.WriteAllBytes(payloadPath, payload);

            var attachment = vault.AddAttachment(payloadPath);
            var entry = new Models.VaultEntry { Title = "Moved item" };
            entry.Attachments.Add(attachment);
            vault.Data.Entries.Add(entry);
            vault.Save();

            // Locked for the whole move, exactly as the UI does it.
            vault.Lock();

            var plan = VaultRelocation.Prepare(source, destination);
            Check.That($"the plan found the vault's files ({plan.Files.Count})",
                plan.Files.Count >= 2);
            Check.That("the plan knows how big the move is", plan.TotalBytes > 64 * 1024);

            VaultRelocation.Copy(plan, null, CancellationToken.None);

            Check.That("the copy verifies", VaultRelocation.Verify(plan).Count == 0);
            Check.That("the original is still there",
                File.Exists(Path.Combine(source, VaultRelocation.VaultFileName)));

            // The point of the whole feature: open it at the new location.
            var moved = new VaultManager(destination);
            bool opened;
            using (var password = Check.Secret("a long enough passphrase"))
                opened = moved.Unlock(password);

            Check.That("the moved vault unlocks with the same password", opened);
            Check.That("its entry survived the move", moved.Data.Entries.Count == 1);

            var movedAttachment = moved.Data.Entries[0].Attachments.FirstOrDefault();
            Check.That("its attachment came too", movedAttachment is not null);

            if (movedAttachment is not null)
            {
                byte[] readBack = moved.ReadAttachment(movedAttachment);
                Check.That("and decrypts to exactly the original bytes",
                    readBack.AsSpan().SequenceEqual(payload));
            }

            moved.Lock();

            // Only now may the original go, and only the vault's own entries.
            File.WriteAllText(Path.Combine(source, "settings.json"), "{}");
            var failures = VaultRelocation.DeleteSource(plan);
            Check.That("removing the original reports no failures", failures.Count == 0);
            Check.That("the original vault file is gone",
                !File.Exists(Path.Combine(source, VaultRelocation.VaultFileName)));
            Check.That("but settings.json beside it was left alone",
                File.Exists(Path.Combine(source, "settings.json")));
        }
        finally
        {
            Check.Cleanup(scratch);
        }
    }

    // ------------------------------------------------------------ damaged copy

    private static void VerifyCatchesDamage()
    {
        Check.Section("68. A damaged copy is caught before anything is switched over");

        string scratch = Check.Scratch("relocate-damage");
        try
        {
            string source = Path.Combine(scratch, "before");
            string destination = Path.Combine(scratch, "after");
            Directory.CreateDirectory(source);

            var vault = new VaultManager(source);
            using (var password = Check.Secret("a long enough passphrase"))
                vault.CreateVault(password, withRecoveryKey: false);
            vault.Lock();

            var plan = VaultRelocation.Prepare(source, destination);
            VaultRelocation.Copy(plan, null, CancellationToken.None);
            Check.That("a clean copy verifies", VaultRelocation.Verify(plan).Count == 0);

            // A file that arrived the wrong size.
            string copiedVault = Path.Combine(destination, VaultRelocation.VaultFileName);
            File.AppendAllText(copiedVault, "extra");
            Check.That("a file of the wrong size is caught",
                VaultRelocation.Verify(plan).Count > 0);

            // The same size, different contents. This is the one a size check alone
            // would wave through, which is why the vault file is compared by hash.
            byte[] original = File.ReadAllBytes(Path.Combine(source, VaultRelocation.VaultFileName));
            byte[] tampered = (byte[])original.Clone();
            tampered[^1] ^= 0xFF;
            File.WriteAllBytes(copiedVault, tampered);

            var problems = VaultRelocation.Verify(plan);
            Check.That("a same-size vault file with different contents is still caught",
                problems.Count > 0);
            Check.That("and the problem names the vault file",
                problems.Any(p => p.Contains("vault file", StringComparison.OrdinalIgnoreCase)));

            File.WriteAllBytes(copiedVault, original);

            // The same check for an attachment. A size comparison alone would wave this
            // through, and a cheap USB stick can do exactly this without any error.
            string attachments = Path.Combine(source, "attachments");
            Directory.CreateDirectory(attachments);
            File.WriteAllBytes(Path.Combine(attachments, "a.lwa"), new byte[] { 1, 2, 3, 4 });
            var withFile = VaultRelocation.Prepare(source, destination);
            VaultRelocation.Copy(withFile, null, CancellationToken.None);
            Check.That("a clean attachment copy verifies", VaultRelocation.Verify(withFile).Count == 0);
            File.WriteAllBytes(Path.Combine(destination, "attachments", "a.lwa"), new byte[] { 1, 2, 3, 5 });
            Check.That("a same-size attachment with different contents is caught",
                VaultRelocation.Verify(withFile).Count > 0);

            VaultRelocation.DeleteAbandonedCopy(withFile);
            Check.That("giving up removes the copy", !Directory.Exists(destination));
            Check.That("and leaves the original alone",
                File.Exists(Path.Combine(source, VaultRelocation.VaultFileName))
                && File.Exists(Path.Combine(attachments, "a.lwa")));

            VaultRelocation.Copy(plan, null, CancellationToken.None);

            // A missing file.
            File.Delete(copiedVault);
            Check.That("a file that never arrived is caught",
                VaultRelocation.Verify(plan).Count > 0);
        }
        finally
        {
            Check.Cleanup(scratch);
        }
    }
}
