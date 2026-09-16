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
using System.Windows;
using System.Windows.Threading;
using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Views;

public partial class VaultShellView
{
    private sealed class MediaImportPlan
    {
        public List<MediaFolder> Folders { get; } = new();
        public List<MediaFileImport> Files { get; } = new();
        public string? RootFolderId { get; set; }
    }

    private sealed class MediaFileImport
    {
        public required string Path { get; init; }
        public required string CategoryId { get; init; }
        public string? MediaFolderId { get; init; }
    }

    private sealed class MediaImportSummary
    {
        public int FilesAdded { get; set; }
        public int FoldersCreated { get; set; }
        public int FilesSkipped { get; set; }
        public string? LastCreatedRootFolderId { get; set; }
        public List<string> Errors { get; } = new();

        /// <summary>
        /// Where each imported file came from, so the originals can be offered for removal
        /// afterwards. Only files that were actually taken in successfully appear here: a
        /// failed import must never lead to the source being deleted.
        /// </summary>
        public List<string> ImportedFrom { get; } = new();
    }

    private static bool DirectoryTreeHasMedia(string dirPath) =>
        Directory.EnumerateFiles(dirPath).Any(IsMediaFilePath) ||
        Directory.EnumerateDirectories(dirPath).Any(DirectoryTreeHasMedia);

    private void PlanDirectoryTree(MediaImportPlan plan, string categoryId, string dirPath, string? parentFolderId)
    {
        if (!DirectoryTreeHasMedia(dirPath)) return;

        string folderName = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "Imported folder";

        var folder = new MediaFolder
        {
            Name = folderName,
            CategoryId = categoryId,
            ParentFolderId = parentFolderId,
            Order = NextFolderOrder(categoryId, parentFolderId),
        };
        plan.Folders.Add(folder);
        if (parentFolderId is null)
            plan.RootFolderId = folder.Id;

        foreach (string filePath in Directory.EnumerateFiles(dirPath).Where(IsMediaFilePath))
        {
            plan.Files.Add(new MediaFileImport
            {
                Path = filePath,
                CategoryId = categoryId,
                MediaFolderId = folder.Id,
            });
        }

        foreach (string subDir in Directory.EnumerateDirectories(dirPath))
            PlanDirectoryTree(plan, categoryId, subDir, folder.Id);

        var kindPaths = plan.Files
            .Where(f => f.MediaFolderId == folder.Id)
            .Select(f => f.Path)
            .ToList();
        if (kindPaths.Count > 0)
            folder.Kind = GuessFolderKindFromPaths(kindPaths);
    }

    private MediaImportPlan BuildDropImportPlan(string categoryId, IReadOnlyList<string> paths, string? parentFolderId)
    {
        var plan = new MediaImportPlan();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
                PlanDirectoryTree(plan, categoryId, path, parentFolderId);
            else if (File.Exists(path) && IsMediaFilePath(path))
            {
                plan.Files.Add(new MediaFileImport
                {
                    Path = path,
                    CategoryId = categoryId,
                    MediaFolderId = parentFolderId,
                });
            }
        }
        return plan;
    }

    private void ApplyImportPlanFolders(MediaImportPlan plan)
    {
        foreach (var folder in plan.Folders)
            _vault.Data.MediaFolders.Add(folder);
    }

    private async Task<MediaImportSummary> RunMediaImportAsync(string title, MediaImportPlan plan)
    {
        var summary = new MediaImportSummary
        {
            FoldersCreated = plan.Folders.Count,
            LastCreatedRootFolderId = plan.RootFolderId,
        };

        if (plan.Files.Count == 0 && plan.Folders.Count == 0)
            return summary;

        ShowProgress(title, plan.Files.Count > 0
            ? $"Preparing {plan.Files.Count} file(s)..."
            : "Preparing folders...");

        ApplyImportPlanFolders(plan);

        try
        {
            for (int i = 0; i < plan.Files.Count; i++)
            {
                MediaFileImport file = plan.Files[i];
                string fileName = Path.GetFileName(file.Path);
                UpdateProgress($"Encrypting {fileName}", i + 1, plan.Files.Count);

                try
                {
                    var imported = await Task.Run(() =>
                    {
                        lock (_vaultOpLock)
                        {
                            var entry = new VaultEntry
                            {
                                CategoryId = file.CategoryId,
                                MediaFolderId = file.MediaFolderId,
                                Kind = EntryKind.Media,
                                Title = Path.GetFileNameWithoutExtension(file.Path),
                                MediaSortOrder = file.MediaFolderId is not null
                                    ? NextMediaSortOrder(file.CategoryId, file.MediaFolderId)
                                    : 0,
                            };
                            var att = _vault.AddAttachment(file.Path);
                            return (entry, att);
                        }
                    });

                    imported.entry.Attachments.Add(imported.att);
                    _vault.Data.Entries.Add(imported.entry);
                    summary.FilesAdded++;
                    summary.ImportedFrom.Add(file.Path);
                }
                catch (Exception ex)
                {
                    summary.FilesSkipped++;
                    if (summary.Errors.Count < 5)
                        summary.Errors.Add($"{fileName}: {ex.Message}");
                }

                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            if (summary.FilesAdded > 0 || summary.FoldersCreated > 0)
                _vault.Save();

            string done = summary.FilesAdded == 1
                ? "Added 1 file."
                : $"Added {summary.FilesAdded} files.";
            if (summary.FoldersCreated > 0)
                done += $" Created {summary.FoldersCreated} folder{(summary.FoldersCreated == 1 ? "" : "s")}.";
            await FinishProgressAsync(done);
        }
        catch
        {
            HideProgress();
            throw;
        }

        return summary;
    }

    private void CompleteMediaImportUi(MediaImportSummary summary)
    {
        if (summary.FilesAdded == 0 && summary.FoldersCreated == 0) return;

        UpdateStats();
        RefreshCategories();
        if (summary.LastCreatedRootFolderId is not null)
            _currentMediaFolderId = summary.LastCreatedRootFolderId;
        RefreshMediaGallery();
    }

    private static void ShowMediaImportResult(MediaImportSummary summary)
    {
        if (summary.FilesSkipped == 0) return;

        string body = summary.FilesAdded > 0
            ? $"Added {summary.FilesAdded} file(s). {summary.FilesSkipped} could not be imported."
            : $"{summary.FilesSkipped} file(s) could not be imported.";

        if (summary.Errors.Count > 0)
            body += "\n\n" + string.Join("\n", summary.Errors);

        MessageBox.Show(body, "Lockwell");
    }
}
