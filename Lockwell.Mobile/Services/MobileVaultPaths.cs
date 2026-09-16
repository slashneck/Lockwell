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

/// <summary>
/// Where the phone vault lives on disk.
///
/// Everything is kept in the app's private internal storage. That is deliberate and it
/// is the whole reason uninstalling Lockwell takes its data with it:
///
///   - Internal app storage (this) is owned by the package. Android deletes the entire
///     directory when the app is uninstalled. Nothing is left behind.
///   - Shared/external storage (/sdcard, Documents, MediaStore) SURVIVES uninstall.
///     Files written there become orphaned rubbish the user has to hunt down by hand.
///
/// So Lockwell never writes outside this directory. The only exception is a file the
/// user explicitly exports and chooses the destination for, which is their file at that
/// point, not ours.
///
/// It also happens to be the more private choice: internal app storage is sandboxed
/// from other apps, whereas shared storage is readable by anything with the permission.
/// </summary>
public static class MobileVaultPaths
{
    /// <summary>
    /// The app's private data directory. On Android this maps to
    /// /data/data/com.lockwell.vault/files, which is removed on uninstall.
    /// </summary>
    public static string Root => FileSystem.AppDataDirectory;

    public static string VaultFile => Path.Combine(Root, "vault.lwv");

    public static string AttachmentsDir => Path.Combine(Root, "attachments");

    /// <summary>Trusted device records and this device's own identity key.</summary>
    public static string SyncDir => Path.Combine(Root, "sync");

    /// <summary>
    /// Scratch space for decrypting a file to hand it to a system viewer. Uses the
    /// cache directory, which Android may reclaim under storage pressure and which is
    /// also removed on uninstall. Purged on lock and on launch regardless.
    /// </summary>
    public static string TempDir => Path.Combine(FileSystem.CacheDirectory, "lockwell-temp");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AttachmentsDir);
        Directory.CreateDirectory(SyncDir);
    }

    /// <summary>
    /// Remove every decrypted scratch file. Called on lock, on exit, and on launch, so a
    /// crash cannot leave plaintext media sitting in the cache.
    ///
    /// This clears the whole cache directory, not just Lockwell's own scratch folder, and
    /// that width is the point. An earlier version emptied only <see cref="TempDir"/> and
    /// missed the real leak: handing a file to the share sheet makes the platform copy it
    /// somewhere of its own choosing under the cache, and picking a photo does the same in
    /// reverse. Those copies are decrypted vault contents that no amount of locking was
    /// removing, and they survived relaunch. Everything under the cache directory is
    /// disposable by definition -- Android may delete it at any moment for space -- so
    /// there is nothing here worth keeping and nothing to lose by being thorough.
    /// </summary>
    public static void PurgeTemp()
    {
        Purge(TempDir);
        Purge(FileSystem.CacheDirectory);
    }

    private static void Purge(string directory)
    {
        try
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { /* held open, or already gone */ }
            }

            // Empty folders left by the platform's copies are noise; take them too, but
            // never the cache root itself, which Android expects to exist.
            foreach (string sub in Directory.EnumerateDirectories(directory))
            {
                try { Directory.Delete(sub, recursive: true); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Whether a path points inside storage that belongs to this app rather than at
    /// something the user would recognise as their own file.
    ///
    /// Android exposes the same private directory under two names -- /data/data/&lt;pkg&gt;
    /// and /data/user/0/&lt;pkg&gt; -- so comparing one against the other as strings quietly
    /// fails. Getting that wrong matters: it is what decides whether Lockwell may claim it
    /// deleted the phone's copy of a photo, and claiming it about a scratch file the
    /// platform made would be a lie about the one thing this app is for.
    /// </summary>
    public static bool IsOurs(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return true; }   // unreadable: assume ours, and claim nothing

        foreach (string root in new[] { FileSystem.CacheDirectory, FileSystem.AppDataDirectory })
        {
            if (string.IsNullOrEmpty(root)) continue;

            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;

            // Match on the package segment as well, which both spellings of the private
            // directory share and nothing outside the sandbox has.
            string marker = PackageSegment(root);
            if (marker.Length > 0 && full.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>The "/com.example.app/" slice of a private path, or empty if not found.</summary>
    private static string PackageSegment(string root)
    {
        foreach (string prefix in new[] { "/data/user/0/", "/data/data/" })
        {
            int at = root.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            int start = at + prefix.Length;
            int end = root.IndexOf('/', start);
            if (end <= start) continue;

            return "/" + root[start..end] + "/";
        }
        return "";
    }

    /// <summary>
    /// Everything the app has written, for the "what is Lockwell using?" screen and to
    /// make it verifiable that nothing lives outside the sandbox.
    /// </summary>
    public static long TotalBytesUsed()
    {
        long total = 0;
        foreach (string dir in new[] { Root, TempDir })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; } catch { /* skip */ }
                }
            }
            catch { /* skip */ }
        }
        return total;
    }
}
