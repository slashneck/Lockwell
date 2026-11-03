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

        // Built on the UI thread, so the background half only decrypts attachments and never
        // touches the vault's own collections.
        var plan = FolderExporter.BuildPlan(_vault.Data, folder);

        if (plan.Files.Count == 0 && plan.Directories.Count == 0 && plan.VaultOnlyItems.Count == 0)
        {
            MessageBox.Show(
                $"\"{folder.Name}\" has nothing in it to export.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string countText = plan.Files.Count == 1 ? "1 file" : $"{plan.Files.Count} files";
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

        // Plain copies in the vault's own folder would sit beside the encrypted files.
        if (IsInsideVaultStorage(destinationRoot))
        {
            MessageBox.Show(
                "That location is inside Lockwell's own storage. Pick somewhere else.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var token = ShowCancellableProgress($"Exporting {folder.Name}", "Preparing...");
        FolderExporter.Result result;

        try
        {
            var uiProgress = new Progress<OperationProgress>(ApplyOperationProgress);
            result = await Task.Run(
                () => FolderExporter.Run(plan, folder.Name, destinationRoot, ReadForExport, uiProgress, token),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HideProgress();
            MessageBox.Show("Could not export the folder: " + ex.Message, "Lockwell",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await FinishProgressAsync(
            result.Cancelled ? "Stopped." : $"Exported {result.Exported} of {plan.Files.Count}.")
            .ConfigureAwait(true);

        ReportExportOutcome(folder, plan, result);
    }

    private byte[] ReadForExport(AttachmentRef attachment)
    {
        lock (_vaultOpLock)
            return _vault.ReadAttachment(attachment);
    }

    // ---------------------------------------------------------------- the report

    private void ReportExportOutcome(
        MediaFolder folder, FolderExporter.Plan plan, FolderExporter.Result result)
    {
        if (result.Cancelled)
        {
            MessageBox.Show(
                $"Stopped after {result.Exported} of {plan.Files.Count} files.\n\n"
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
                $"Exported {result.Exported} of {plan.Files.Count} files to:\n{result.Destination}\n\n"
                + $"{result.Failures.Count} could not be written:\n{shown}\n\n"
                + "Nothing was removed from the vault.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (plan.VaultOnlyItems.Count > 0)
        {
            string shown = string.Join("\n", plan.VaultOnlyItems.Take(5));
            if (plan.VaultOnlyItems.Count > 5)
                shown += $"\n...and {plan.VaultOnlyItems.Count - 5} more.";

            MessageBox.Show(
                $"Exported all {plan.Files.Count} files to:\n{result.Destination}\n\n"
                + "These items also hold notes or fields, which exist only inside the vault:\n"
                + shown + "\n\n"
                + "So this folder cannot be removed as part of the export. The exported files "
                + "are decrypted and unprotected.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OfferToRemoveExportedFolder(folder, plan, result);
    }

    /// <summary>
    /// The second half of moving a folder somewhere else. Only reached when the exporter
    /// says this is a complete, read-back copy, and it defaults to No.
    /// </summary>
    private void OfferToRemoveExportedFolder(
        MediaFolder folder, FolderExporter.Plan plan, FolderExporter.Result result)
    {
        if (!result.IsCompleteCopyOf(plan)) return;

        string summary = plan.Files.Count == 1
            ? "Exported 1 file to:"
            : $"Exported all {plan.Files.Count} files to:";

        var answer = MessageBox.Show(
            $"{summary}\n{result.Destination}\n\n"
            + "Every file was read back and matches. They are decrypted and unprotected there "
            + "now.\n\n"
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
}
