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
using System.Threading;
using System.Windows;
using Lockwell.Models;
using Lockwell.Services;
using Microsoft.Win32;

namespace Lockwell.Views;

/// <summary>
/// Getting a whole folder back out of the vault, as ordinary files.
///
/// Folders could already be deleted, and a single file could be saved out from the
/// viewer, but there was no way to take a folder and its sub-folders out in one go. That
/// made the vault a place things could go into more easily than they could come out of,
/// which is not a property a vault should have. Data you cannot get back out is data you
/// have lost in a slower way.
///
/// What comes out is **decrypted**. That is the only form another program can use, and it
/// is worth being blunt about rather than burying: the moment these files land, they are
/// ordinary files with no protection at all, and on an SSD deleting them afterwards does
/// not reliably destroy them (see docs/THREAT-MODEL.md on why overwriting is weaker than
/// it sounds). So every path through here says so, and the offer to delete the originals
/// is made only after every single file has been verified on disk.
///
/// There is deliberately no "export still encrypted" option. Every vault has its own
/// random data key, and the password only unwraps that key, so an encrypted blob from one
/// vault is meaningless to another vault even when both use the same password. An export
/// that produced unreadable files would be a trap, not a feature.
/// </summary>
public partial class VaultShellView
{
    /// <summary>One file to write out, with the path it should land at.</summary>
    private sealed record FolderExportItem(AttachmentRef Attachment, string RelativePath);

    private sealed class FolderExportPlan
    {
        public List<FolderExportItem> Items { get; } = new();

        /// <summary>Sub-folders, including empty ones, so the shape survives the trip.</summary>
        public List<string> Directories { get; } = new();
    }

    private sealed class FolderExportResult
    {
        public int Exported { get; set; }
        public List<string> Failures { get; } = new();
        public bool Cancelled { get; set; }
        public string Destination { get; set; } = "";
    }

    // ---------------------------------------------------------------- entry points

    /// <summary>Export button on a folder tile in the gallery.</summary>
    private async void ExportMediaFolder(MediaFolder folder) =>
        await ExportMediaFolderAsync(folder).ConfigureAwait(true);

    /// <summary>Export button in the header, while inside a folder.</summary>
    private async void MediaFolderExportButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = CurrentMediaFolder();
        if (folder is not null) await ExportMediaFolderAsync(folder).ConfigureAwait(true);
    }

    /// <summary>Delete button in the header, while inside a folder.</summary>
    private void MediaFolderDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = CurrentMediaFolder();
        if (folder is not null) DeleteMediaFolder(folder);
    }

    private MediaFolder? CurrentMediaFolder() =>
        _currentMediaFolderId is null
            ? null
            : _vault.Data.MediaFolders.FirstOrDefault(f => f.Id == _currentMediaFolderId);

    // ---------------------------------------------------------------- the flow

    private async Task ExportMediaFolderAsync(MediaFolder folder)
    {
        if (_busyOperation) return;

        // Build the plan on the UI thread. The background half then only reads and
        // decrypts attachments, and never touches the vault's own collections while the
        // UI could still be changing them.
        var plan = BuildFolderExportPlan(folder);

        if (plan.Items.Count == 0 && plan.Directories.Count == 0)
        {
            MessageBox.Show(
                $"\"{folder.Name}\" has nothing in it to export.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string countText = plan.Items.Count == 1 ? "1 file" : $"{plan.Items.Count} files";
        var confirm = MessageBox.Show(
            $"Export \"{folder.Name}\" ({countText}) out of the vault?\n\n"
            + "The files are written decrypted, so another program can read them. Once "
            + "they are on disk they have no protection at all, and anyone with access to "
            + "that drive can open them.\n\n"
            + "Choose somewhere you trust.",
            "Lockwell", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var dlg = new OpenFolderDialog { Title = $"Where should \"{folder.Name}\" go?" };
        if (dlg.ShowDialog() != true) return;

        string destinationRoot = dlg.FolderName;

        // Writing the vault's own storage back into itself would mean exporting into a
        // directory that the export is also reading from.
        if (IsInsideVaultStorage(destinationRoot))
        {
            MessageBox.Show(
                "That location is inside Lockwell's own storage. Pick somewhere else.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var token = ShowCancellableProgress($"Exporting {folder.Name}", "Preparing...");
        var result = new FolderExportResult();

        try
        {
            var uiProgress = new Progress<OperationProgress>(ApplyOperationProgress);
            await Task.Run(
                () => RunFolderExport(folder, plan, destinationRoot, result, uiProgress, token),
                token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        catch (Exception ex)
        {
            HideProgress();
            MessageBox.Show("Could not export the folder: " + ex.Message, "Lockwell",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await FinishProgressAsync(
            result.Cancelled ? "Stopped." : $"Exported {result.Exported} of {plan.Items.Count}.")
            .ConfigureAwait(true);

        ReportExportOutcome(folder, plan, result);
    }

    /// <summary>
    /// Walk the folder tree and record what has to be written where.
    ///
    /// Paths are relative and built here, so the background half never has to work out
    /// where something belongs. Empty sub-folders are included on purpose: a folder
    /// someone made and has not filled yet is still part of how they arranged things.
    /// </summary>
    private FolderExportPlan BuildFolderExportPlan(MediaFolder folder)
    {
        var plan = new FolderExportPlan();
        Collect(folder, "");
        return plan;

        void Collect(MediaFolder current, string prefix)
        {
            string here = prefix.Length == 0
                ? ""
                : prefix;

            foreach (var entry in _vault.Data.Entries.Where(e => e.MediaFolderId == current.Id))
            {
                var att = entry.Attachments.FirstOrDefault();
                if (att is null) continue;
                plan.Items.Add(new FolderExportItem(att, here));
            }

            foreach (var sub in _vault.Data.MediaFolders
                         .Where(f => f.ParentFolderId == current.Id)
                         .OrderBy(f => f.Order))
            {
                string childPath = Path.Combine(here, SanitizePathName(sub.Name));
                plan.Directories.Add(childPath);
                Collect(sub, childPath);
            }
        }
    }

    /// <summary>
    /// The background half. Decrypts and writes, one file at a time.
    ///
    /// A file that fails is recorded and the run continues. Stopping the whole export on
    /// the first unreadable attachment would be the worst possible behaviour here: the
    /// one file that cannot be read is exactly the reason to get the other nine hundred
    /// out while that is still possible.
    /// </summary>
    private void RunFolderExport(
        MediaFolder folder,
        FolderExportPlan plan,
        string destinationRoot,
        FolderExportResult result,
        IProgress<OperationProgress> progress,
        CancellationToken token)
    {
        string root = UniqueDirectoryPath(destinationRoot, SanitizePathName(folder.Name));
        Directory.CreateDirectory(root);
        result.Destination = root;

        foreach (string relative in plan.Directories)
            Directory.CreateDirectory(Path.Combine(root, relative));

        int done = 0;
        foreach (var item in plan.Items)
        {
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                return;
            }

            progress.Report(new OperationProgress
            {
                Detail = item.Attachment.FileName,
                Current = done,
                Total = plan.Items.Count,
            });

            byte[]? data = null;
            try
            {
                string directory = Path.Combine(root, item.RelativePath);
                Directory.CreateDirectory(directory);
                string path = UniqueFilePath(directory, item.Attachment.FileName);

                lock (_vaultOpLock)
                {
                    data = _vault.ReadAttachment(item.Attachment);
                }

                File.WriteAllBytes(path, data);
                result.Exported++;
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{item.Attachment.FileName}: {ex.Message}");
            }
            finally
            {
                // The decrypted bytes exist in memory for as long as it takes to write
                // them and not a moment longer.
                if (data is not null) Array.Clear(data, 0, data.Length);
            }

            done++;
        }

        progress.Report(new OperationProgress
        {
            Detail = "Finishing...",
            Current = plan.Items.Count,
            Total = plan.Items.Count,
        });
    }

    // ---------------------------------------------------------------- the report

    private void ReportExportOutcome(
        MediaFolder folder, FolderExportPlan plan, FolderExportResult result)
    {
        if (result.Cancelled)
        {
            MessageBox.Show(
                $"Stopped after {result.Exported} of {plan.Items.Count} files.\n\n"
                + $"What was written is in:\n{result.Destination}\n\n"
                + "Nothing was removed from the vault.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (result.Failures.Count > 0)
        {
            string shown = string.Join("\n", result.Failures.Take(5));
            if (result.Failures.Count > 5)
                shown += $"\n...and {result.Failures.Count - 5} more.";

            MessageBox.Show(
                $"Exported {result.Exported} of {plan.Items.Count} files to:\n{result.Destination}\n\n"
                + $"{result.Failures.Count} could not be written:\n{shown}\n\n"
                + "Nothing was removed from the vault.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OfferToRemoveExportedFolder(folder, plan, result);
    }

    /// <summary>
    /// The second half of moving a folder somewhere else.
    ///
    /// Only offered when every planned file was written and none failed, and the files
    /// are counted again on disk first rather than trusting the counter that just ran.
    /// Deleting a folder out of the vault on the strength of an export that half worked
    /// would destroy the only remaining copy, and no amount of convenience is worth that.
    /// It defaults to No.
    /// </summary>
    private void OfferToRemoveExportedFolder(
        MediaFolder folder, FolderExportPlan plan, FolderExportResult result)
    {
        int onDisk = CountFilesOnDisk(result.Destination);
        bool verified = result.Exported == plan.Items.Count && onDisk >= plan.Items.Count;

        string summary = plan.Items.Count == 1
            ? "Exported 1 file to:"
            : $"Exported all {plan.Items.Count} files to:";

        if (!verified)
        {
            MessageBox.Show(
                $"{summary}\n{result.Destination}\n\n"
                + $"Only {onDisk} file(s) could be found there afterwards, so the vault "
                + "copy has been left alone. Check the destination before removing "
                + "anything.\n\n"
                + "These files are decrypted and unprotected now.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = MessageBox.Show(
            $"{summary}\n{result.Destination}\n\n"
            + "They are decrypted and unprotected there now.\n\n"
            + $"Remove \"{folder.Name}\" and everything in it from the vault?\n\n"
            + "This cannot be undone, and the exported copy becomes your only copy. "
            + "Choose No to keep both.",
            "Lockwell", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        DeleteMediaFolderTree(folder);
        if (_currentMediaFolderId is not null &&
            !_vault.Data.MediaFolders.Any(f => f.Id == _currentMediaFolderId))
            _currentMediaFolderId = folder.ParentFolderId;

        _vault.Save();
        UpdateStats();
        RefreshCategories();
        RefreshMediaGallery();

        MessageBox.Show(
            $"\"{folder.Name}\" was removed from the vault. The exported copy in "
            + $"{result.Destination} is now the only one.",
            "Lockwell", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static int CountFilesOnDisk(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Is this destination inside the vault's own storage? Exporting into it would have
    /// the export writing plain files into the directory holding the encrypted ones.
    /// </summary>
    private bool IsInsideVaultStorage(string destination)
    {
        try
        {
            string target = Path.GetFullPath(destination)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string vaultDir = Path.GetFullPath(_vault.VaultDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return target.Equals(vaultDir, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(vaultDir + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- path helpers

    private static string SanitizePathName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Folder" : name.Trim();
    }

    private static string UniqueFilePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }

        return Path.Combine(directory, Guid.NewGuid().ToString("N") + ext);
    }
}
