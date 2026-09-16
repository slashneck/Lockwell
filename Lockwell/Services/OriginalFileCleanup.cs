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
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lockwell.Services;

/// <summary>What else on this machine still holds a copy of a file just imported.</summary>
public sealed record CopyWarning(string Path, string Where, string Detail);

/// <summary>
/// Removing the original after a file has been taken into the vault.
///
/// Two honest things shape this whole file.
///
/// The first is that **overwriting does not reliably erase anything on modern storage**.
/// It worked on spinning disks: same sector, new bytes, old data gone. Solid-state drives
/// do not work that way. Their controllers use wear levelling, deliberately writing each
/// new version of a block to a different physical cell so the flash wears evenly. So
/// overwriting a file thirty times writes thirty times to thirty different places, and the
/// original cell still holds the original data, unreachable from software and invisible to
/// the operating system. Tools that promise a "secure wipe" on an SSD are promising
/// something the hardware does not let them deliver, and NIST's own guidance moved away
/// from overwrite-based sanitisation of flash for exactly this reason.
///
/// So the overwrite here is described as what it is: it defeats undelete tools and file
/// recovery software, which is a real and worthwhile threat to defeat, and it does not
/// guarantee the bytes are gone from the physical medium. What actually protects data at
/// rest on an SSD is full-disk encryption, which is a property of the machine rather than
/// of this app.
///
/// The second is that the copy most likely to be found later is usually not the deleted
/// file at all. It is the one in the Recycle Bin, or in a folder that syncs to OneDrive or
/// Dropbox, where deleting locally changes nothing on the server. Telling the user about
/// those is worth more than any number of overwrite passes, so this looks for them and
/// says so.
/// </summary>
public static class OriginalFileCleanup
{
    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    /// <summary>
    /// Overwrite a file with random bytes, then delete it.
    ///
    /// Read the class summary before trusting this: on an SSD it makes recovery by
    /// ordinary tools very unlikely, and it does not guarantee erasure.
    /// </summary>
    public static bool Shred(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        try
        {
            long length = new FileInfo(path).Length;

            if (length > 0)
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Write, FileShare.None);

                byte[] buffer = new byte[(int)Math.Min(1 << 20, length)];
                using var rng = RandomNumberGenerator.Create();

                // Two passes: one random, one zeroes. More passes are cargo cult on any
                // storage made this century, and on flash even the first one is a
                // best-effort measure rather than a guarantee.
                for (int pass = 0; pass < 2; pass++)
                {
                    stream.Position = 0;
                    long remaining = length;

                    while (remaining > 0)
                    {
                        int chunk = (int)Math.Min(buffer.Length, remaining);

                        if (pass == 0) rng.GetBytes(buffer.AsSpan(0, chunk));
                        else Array.Clear(buffer, 0, chunk);

                        stream.Write(buffer, 0, chunk);
                        remaining -= chunk;
                    }

                    stream.Flush(flushToDisk: true);
                }
            }

            // Renaming before deleting removes the original name from the directory, so a
            // recovered entry does not even reveal what the file was called.
            string scrambled = Path.Combine(
                Path.GetDirectoryName(path) ?? "",
                Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant());

            try
            {
                File.Move(path, scrambled);
                File.Delete(scrambled);
            }
            catch
            {
                File.Delete(path);
            }

            return true;
        }
        catch
        {
            // Still held open by something. Have Windows remove it at reboot rather than
            // leaving it, and report that it is not gone yet.
            try { MoveFileEx(path, null, MoveFileDelayUntilReboot); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// Look for other places this file is likely to still exist. Cheap, path-based checks
    /// only: no scanning, nothing opened, nothing sent anywhere.
    /// </summary>
    public static IReadOnlyList<CopyWarning> FindOtherCopies(IEnumerable<string> paths)
    {
        var warnings = new List<CopyWarning>();

        foreach (string path in paths)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch { continue; }

            foreach ((string marker, string where, string detail) in SyncedLocations())
            {
                if (marker.Length == 0) continue;
                if (!full.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;

                warnings.Add(new CopyWarning(full, where, detail));
                break;
            }
        }

        return warnings;
    }

    /// <summary>
    /// Folders whose contents are mirrored somewhere Lockwell cannot reach. Found from the
    /// environment rather than guessed, so a machine without them reports nothing.
    /// </summary>
    private static IEnumerable<(string Path, string Where, string Detail)> SyncedLocations()
    {
        (string Variable, string Where, string Detail)[] candidates =
        {
            ("OneDrive", "OneDrive",
                "Deleting the local file does not remove the copy in your OneDrive, and OneDrive keeps its own recycle bin."),
            ("OneDriveConsumer", "OneDrive",
                "Deleting the local file does not remove the copy in your OneDrive, and OneDrive keeps its own recycle bin."),
            ("OneDriveCommercial", "OneDrive for Business",
                "Deleting the local file does not remove the copy stored for your organisation."),
        };

        foreach ((string variable, string where, string detail) in candidates)
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root)) yield return (root, where, detail);
        }

        // Dropbox and Google Drive do not advertise themselves in the environment, so
        // their conventional locations under the profile are checked instead.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length == 0) yield break;

        (string Folder, string Where, string Detail)[] byConvention =
        {
            ("Dropbox", "Dropbox",
                "Deleting the local file does not remove the copy in Dropbox, which also keeps deleted files for a time."),
            ("Google Drive", "Google Drive",
                "Deleting the local file does not remove the copy in Google Drive."),
            ("My Drive", "Google Drive",
                "Deleting the local file does not remove the copy in Google Drive."),
            ("iCloudDrive", "iCloud Drive",
                "Deleting the local file does not remove the copy in iCloud."),
        };

        foreach ((string folder, string where, string detail) in byConvention)
        {
            string candidate = Path.Combine(profile, folder);
            if (Directory.Exists(candidate)) yield return (candidate, where, detail);
        }
    }
}
