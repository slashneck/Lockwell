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
using Lockwell.Models;

namespace Lockwell.Services;

/// <summary>
/// Takes a folder and everything under it out of the vault as ordinary files.
///
/// It lives apart from the UI because the answer to one question decides whether someone
/// keeps their data: is it safe to remove this folder from the vault now? The first version
/// answered by counting files, and exported only the first attachment of each item, so an
/// item holding two files lost one and the count still agreed. The answer now comes from
/// <see cref="Result.IsCompleteCopyOf"/>: every attachment of every item in the tree was
/// written and read back identical, and nothing in the tree exists only as vault data.
/// </summary>
public static class FolderExporter
{
    public sealed record PlannedFile(AttachmentRef Attachment, string RelativeDirectory);

    public sealed class Plan
    {
        public List<PlannedFile> Files { get; } = new();

        /// <summary>Sub-folders, including empty ones, so the shape survives the trip.</summary>
        public List<string> Directories { get; } = new();

        /// <summary>
        /// Items holding something a file export cannot carry: typed fields, notes, or no
        /// file at all. Their files are still exported, but the folder must not be offered
        /// for removal, because removing it would destroy that text for good.
        /// </summary>
        public List<string> VaultOnlyItems { get; } = new();

        public long TotalBytes => Files.Sum(f => f.Attachment.SizeBytes);
    }

    public sealed class Result
    {
        public int Exported { get; set; }
        public List<string> Failures { get; } = new();
        public bool Cancelled { get; set; }
        public string Destination { get; set; } = "";

        /// <summary>The one test that allows offering to remove the folder from the vault.</summary>
        public bool IsCompleteCopyOf(Plan plan) =>
            !Cancelled
            && Failures.Count == 0
            && Exported == plan.Files.Count
            && plan.VaultOnlyItems.Count == 0;
    }

    public static Plan BuildPlan(VaultData data, MediaFolder folder)
    {
        var plan = new Plan();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(folder, "");
        return plan;

        void Collect(MediaFolder current, string relative)
        {
            if (!seen.Add(current.Id)) return;   // a corrupt parent loop must not hang this

            foreach (var entry in data.Entries.Where(e => e.MediaFolderId == current.Id))
            {
                foreach (var attachment in entry.Attachments)
                    plan.Files.Add(new PlannedFile(attachment, relative));

                bool hasText = !string.IsNullOrWhiteSpace(entry.Notes)
                               || entry.Fields.Any(f => !string.IsNullOrEmpty(f.Value));
                if (hasText || entry.Attachments.Count == 0)
                    plan.VaultOnlyItems.Add(entry.Title);
            }

            foreach (var sub in data.MediaFolders
                         .Where(f => f.ParentFolderId == current.Id)
                         .OrderBy(f => f.Order))
            {
                string child = Path.Combine(relative, SafeName(sub.Name, "Folder"));
                plan.Directories.Add(child);
                Collect(sub, child);
            }
        }
    }

    /// <summary>
    /// Decrypt and write, one file at a time, then read each back and compare.
    ///
    /// A file that fails is recorded and the run continues: the one unreadable attachment
    /// is exactly the reason to get the other nine hundred out while that is still possible.
    /// </summary>
    public static Result Run(
        Plan plan,
        string folderName,
        string destinationRoot,
        Func<AttachmentRef, byte[]> read,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        var result = new Result();

        string root = UniqueDirectory(destinationRoot, SafeName(folderName, "Folder"));
        Directory.CreateDirectory(root);
        result.Destination = root;

        foreach (string relative in plan.Directories)
            Directory.CreateDirectory(Path.Combine(root, relative));

        int done = 0;
        foreach (var file in plan.Files)
        {
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                return result;
            }

            progress?.Report(new OperationProgress
            {
                Detail = file.Attachment.FileName,
                Current = done,
                Total = plan.Files.Count,
            });

            byte[]? data = null;
            try
            {
                string directory = Path.Combine(root, file.RelativeDirectory);
                Directory.CreateDirectory(directory);
                string path = UniqueFile(directory, SafeName(file.Attachment.FileName, "File"));

                data = read(file.Attachment);
                byte[] expected = SHA256.HashData(data);
                File.WriteAllBytes(path, data);

                // Read back what actually landed. A flaky USB stick can accept a write and
                // return something else, and this copy may be about to become the only one.
                using (var written = File.OpenRead(path))
                {
                    if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(written)))
                        throw new IOException("the file on disk does not match the vault copy");
                }

                result.Exported++;
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{file.Attachment.FileName}: {ex.Message}");
            }
            finally
            {
                if (data is not null) Array.Clear(data, 0, data.Length);
            }

            done++;
        }

        progress?.Report(new OperationProgress
        {
            Detail = "Finishing...",
            Current = plan.Files.Count,
            Total = plan.Files.Count,
        });

        return result;
    }

    /// <summary>
    /// A name that stays where it is put. Folder and file names can arrive from another
    /// device, so "..", a path separator or a reserved Windows name must not be able to walk
    /// out of the chosen folder or fail in a confusing way.
    /// </summary>
    public static string SafeName(string? name, string fallback)
    {
        string safe = name ?? "";
        foreach (char c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');

        safe = safe.Trim().TrimEnd('.', ' ');
        if (safe.Length == 0) return fallback;

        string stem = Path.GetFileNameWithoutExtension(safe).ToUpperInvariant();
        bool reserved = stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]));

        return reserved ? "_" + safe : safe;
    }

    private static string UniqueFile(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; i < 100000; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }

        return Path.Combine(directory, Guid.NewGuid().ToString("N") + ext);
    }

    public static string UniqueDirectory(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        if (!Directory.Exists(path) && !File.Exists(path)) return path;

        for (int i = 2; i < 100000; i++)
        {
            path = Path.Combine(parent, $"{name} ({i})");
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
        }

        return Path.Combine(parent, Guid.NewGuid().ToString("N"));
    }
}
