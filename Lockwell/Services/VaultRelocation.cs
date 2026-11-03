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
using System.Security.Cryptography;
using System.Threading;

namespace Lockwell.Services;

/// <summary>
/// Moving a vault to another drive, without ever decrypting it.
///
/// This is how a vault gets onto a USB stick, an external disk, or simply off a drive
/// that is filling up. Everything stays exactly as encrypted as it was: the files are
/// copied byte for byte and the profile is pointed at the new location. No key is
/// derived, nothing is unwrapped, and the master password is not needed or asked for.
///
/// The order of operations is the whole design, and it is deliberately paranoid, because
/// a half-finished move of the only copy of someone's data is unrecoverable:
///
///   1. Copy everything to the new location.
///   2. Verify what landed, against what was read.
///   3. Only then switch the profile over.
///   4. Only then, and only if asked, remove the original.
///
/// Nothing is deleted on the strength of a copy that merely appeared to work. If the
/// verify step finds anything wrong, the original is still exactly where it was and the
/// profile still points at it, so the worst outcome is a wasted copy.
/// </summary>
public static class VaultRelocation
{
    /// <summary>
    /// What makes up a vault on disk. Everything else in the folder belongs to the app,
    /// not to this vault: the default profile's directory is also the application data
    /// root, holding settings.json and the profiles folder, and those must stay where the
    /// app looks for them rather than following a vault onto a removable drive.
    /// </summary>
    public static readonly string[] VaultEntries = { "vault.lwv", "attachments", "sync" };

    public const string VaultFileName = "vault.lwv";

    public sealed record PlannedFile(string RelativePath, long Length);

    public sealed class Plan
    {
        public string Source { get; init; } = "";
        public string Destination { get; init; } = "";
        public List<PlannedFile> Files { get; } = new();
        public long TotalBytes => Files.Sum(f => f.Length);
    }

    /// <summary>
    /// Why this move must not be attempted, or null when it may go ahead.
    ///
    /// Returns a sentence fit to show someone, not an error code, because every one of
    /// these is a situation where the right response is to explain and stop.
    /// </summary>
    public static string? Validate(string sourceDir, string destinationDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(destinationDir))
            return "A source and a destination folder are both needed.";

        string source, destination;
        try
        {
            source = Normalize(sourceDir);
            destination = Normalize(destinationDir);
        }
        catch (Exception ex)
        {
            return "That path could not be read: " + ex.Message;
        }

        if (!File.Exists(Path.Combine(source, VaultFileName)))
            return "There is no vault in the current location to move.";

        if (PathsEqual(source, destination))
            return "The vault is already there.";

        // A destination inside the source would have the copy writing into the directory
        // it is still reading from. A source inside the destination collides on the way
        // back out. Either way the answer is to pick somewhere separate.
        if (Contains(source, destination))
            return "That folder is inside the vault's current location. Pick somewhere outside it.";

        if (Contains(destination, source))
            return "The vault's current location is inside that folder. Pick somewhere else.";

        if (File.Exists(Path.Combine(destination, VaultFileName)))
            return "There is already a vault in that folder. Moving into it would put two vaults in the same place.";

        // Leftovers from another vault would be merged into this one, and cleaning up a
        // stopped move would then delete files this move never created.
        if (VaultEntries.Any(e => Directory.Exists(Path.Combine(destination, e))))
            return "That folder already holds vault files. Pick an empty folder.";

        return null;
    }

    /// <summary>
    /// The folder a move should land in, created fresh inside whatever was picked. Someone
    /// choosing the root of a USB stick should get a tidy folder there, not vault files
    /// loose beside their photos.
    /// </summary>
    public static string ResolveDestination(string pickedFolder) =>
        FolderExporter.UniqueDirectory(pickedFolder, DestinationFolderName);

    public const string DestinationFolderName = "Lockwell vault";

    /// <summary>List every file that has to travel, with the size it should arrive at.</summary>
    public static Plan Prepare(string sourceDir, string destinationDir)
    {
        var plan = new Plan
        {
            Source = Normalize(sourceDir),
            Destination = Normalize(destinationDir),
        };

        foreach (string name in VaultEntries)
        {
            string path = Path.Combine(plan.Source, name);

            if (File.Exists(path))
            {
                plan.Files.Add(new PlannedFile(name, new FileInfo(path).Length));
                continue;
            }

            if (!Directory.Exists(path)) continue;

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(plan.Source, file);
                plan.Files.Add(new PlannedFile(relative, new FileInfo(file).Length));
            }
        }

        return plan;
    }

    /// <summary>
    /// Is there room? Asked before anything is written, because running out of space
    /// halfway is how a move turns into a mess that has to be understood before it can be
    /// undone.
    /// </summary>
    public static bool HasRoomFor(Plan plan, out long availableBytes)
    {
        availableBytes = 0;
        try
        {
            string? root = Path.GetPathRoot(plan.Destination);
            if (string.IsNullOrEmpty(root)) return true;

            availableBytes = new DriveInfo(root).AvailableFreeSpace;

            // A small margin, so the destination is not left with literally nothing.
            return availableBytes > plan.TotalBytes + (16L * 1024 * 1024);
        }
        catch (Exception)
        {
            // Network paths and some removable drives refuse to report free space.
            // Not knowing is not a reason to refuse; the copy itself will fail loudly.
            return true;
        }
    }

    public static void Copy(
        Plan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(plan.Destination);

        long copiedBytes = 0;
        int copiedFiles = 0;

        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();

            string from = Path.Combine(plan.Source, file.RelativePath);
            string to = Path.Combine(plan.Destination, file.RelativePath);

            string? parent = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            File.Copy(from, to, overwrite: true);

            copiedBytes += file.Length;
            copiedFiles++;

            progress?.Report(new OperationProgress
            {
                Detail = $"{file.RelativePath}  ({Megabytes(copiedBytes)} of {Megabytes(plan.TotalBytes)})",
                Current = copiedFiles,
                Total = plan.Files.Count,
            });
        }

        // Empty directories carry no data but they do carry intent, and a vault whose
        // attachments folder vanished because it happened to be empty would look broken.
        foreach (string name in VaultEntries)
        {
            string path = Path.Combine(plan.Source, name);
            if (Directory.Exists(path))
                Directory.CreateDirectory(Path.Combine(plan.Destination, name));
        }
    }

    /// <summary>
    /// Check what arrived, by hash, for every file. Returns a description of everything
    /// wrong, empty when the copy is sound.
    ///
    /// Every file, because this copy may be about to become the only one, and cheap USB
    /// sticks can return different bytes from the ones they accepted without any error.
    /// It reads everything twice and takes as long as it takes.
    /// </summary>
    public static List<string> Verify(
        Plan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken token = default)
    {
        var problems = new List<string>();
        int checkedFiles = 0;

        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();

            string from = Path.Combine(plan.Source, file.RelativePath);
            string to = Path.Combine(plan.Destination, file.RelativePath);
            string label = file.RelativePath.Equals(VaultFileName, StringComparison.OrdinalIgnoreCase)
                ? "the vault file"
                : file.RelativePath;

            progress?.Report(new OperationProgress
            {
                Title = "Checking the copy",
                Detail = file.RelativePath,
                Current = checkedFiles,
                Total = plan.Files.Count,
            });
            checkedFiles++;

            if (!File.Exists(to))
            {
                problems.Add($"{label} did not arrive");
                continue;
            }

            long landed = new FileInfo(to).Length;
            if (landed != file.Length)
            {
                problems.Add($"{label} is {landed} bytes, expected {file.Length}");
                continue;
            }

            if (!HashesMatch(from, to))
                problems.Add($"{label} does not match the original");
        }

        return problems;
    }

    /// <summary>
    /// Remove a copy this move created and has given up on. Only for a destination that
    /// <see cref="Validate"/> accepted, which guarantees none of these were there before.
    /// </summary>
    public static void DeleteAbandonedCopy(Plan plan)
    {
        foreach (string name in VaultEntries)
        {
            string path = Path.Combine(plan.Destination, name);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception)
            {
                // Untidy, not dangerous. The original has not been touched.
            }
        }

        try
        {
            if (Directory.Exists(plan.Destination) && !Directory.EnumerateFileSystemEntries(plan.Destination).Any())
                Directory.Delete(plan.Destination);
        }
        catch (Exception)
        {
            // as above
        }
    }

    /// <summary>
    /// Remove the vault from its old home. Only ever called after a verified copy and an
    /// explicit yes, and it removes only the vault's own entries: the folder it sat in
    /// may be the application data root, which still holds settings and other profiles.
    /// </summary>
    public static List<string> DeleteSource(Plan plan)
    {
        var failures = new List<string>();

        foreach (string name in VaultEntries)
        {
            string path = Path.Combine(plan.Source, name);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
            }
        }

        return failures;
    }

    // ------------------------------------------------------------------ helpers

    private static bool HashesMatch(string a, string b)
    {
        using var streamA = File.OpenRead(a);
        using var streamB = File.OpenRead(b);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(streamA), SHA256.HashData(streamB));
    }

    public static string Megabytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
            : $"{bytes / 1024.0 / 1024:0.0} MB";

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Is <paramref name="inner"/> the same as, or underneath, <paramref name="outer"/>?</summary>
    public static bool Contains(string outer, string inner)
    {
        string o = Normalize(outer);
        string i = Normalize(inner);

        return PathsEqual(o, i)
            || i.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
