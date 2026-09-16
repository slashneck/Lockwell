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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lockwell.Helpers;
using Lockwell.Models;

namespace Lockwell.Views;

/// <summary>
/// The storage breakdown: a drill-down from the whole vault into sections, folders,
/// and finally individual files, always sorted largest first so the thing worth
/// dealing with is at the top.
///
/// Everything here is read-only. It reports what is already decrypted in memory while
/// the vault is unlocked and never modifies or moves anything.
/// </summary>
public partial class VaultShellView
{
    private enum StorageLevel { Root, Section, Folder }

    private sealed record StorageCrumb(StorageLevel Level, string? Id, string Label);

    private sealed record StorageRow(
        string Name,
        string Glyph,
        long Bytes,
        int ItemCount,
        StorageLevel? DrillTo,
        string? DrillId,
        VaultEntry? File);

    private readonly List<StorageCrumb> _storagePath = new();

    /// <summary>
    /// Group by file extension instead of by folder. Which format the bulk is stored in
    /// decides whether shrinking it is even worth doing: re-encoding an already-lossy
    /// MP3 costs quality for almost no saving, while WAV or FLAC compresses enormously.
    /// </summary>
    private bool _storageByType;

    /// <summary>
    /// True while the storage breakdown is on screen. Like <c>_homeMode</c>, this view
    /// has no sidebar selection, so refreshes must not auto-select a section.
    /// </summary>
    private bool _storageMode;

    private void StorageByLocation_Click(object sender, RoutedEventArgs e)
    {
        _storageByType = false;
        RefreshStorage();
    }

    private void StorageByType_Click(object sender, RoutedEventArgs e)
    {
        _storageByType = true;
        RefreshStorage();
    }

    private void UpdateStorageModeButtons()
    {
        StorageByLocationButton.Opacity = _storageByType ? 0.55 : 1;
        StorageByTypeButton.Opacity = _storageByType ? 1 : 0.55;
    }

    /// <summary>Every attachment in the vault, grouped by extension, biggest format first.</summary>
    private List<StorageRow> BuildTypeRows()
    {
        var groups = new Dictionary<string, (long Bytes, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _vault.Data.Entries)
        {
            foreach (var att in entry.Attachments)
            {
                string ext = System.IO.Path.GetExtension(att.FileName);
                ext = string.IsNullOrWhiteSpace(ext) ? "(no extension)" : ext.ToLowerInvariant();

                groups.TryGetValue(ext, out var acc);
                groups[ext] = (acc.Bytes + att.SizeBytes, acc.Count + 1);
            }
        }

        return groups
            .Select(g => new StorageRow(
                g.Key,
                TypeGlyph(g.Key),
                g.Value.Bytes,
                g.Value.Count,
                null,
                null,
                null))
            .ToList();
    }

    private static string TypeGlyph(string ext) => ext switch
    {
        ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".wma" => "",
        ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" => "",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "",
        _ => "",
    };

    /// <summary>
    /// Plain-language note on whether a format is worth re-encoding at all.
    /// Lossy formats are already squeezed; re-encoding them just loses quality twice.
    /// </summary>
    private static string? TypeAdvice(string ext) => ext switch
    {
        ".wav" => "uncompressed - huge savings available",
        ".flac" => "lossless - large savings available",
        ".png" => "lossless - large savings available",
        ".bmp" => "uncompressed - huge savings available",
        ".mp3" or ".aac" or ".m4a" or ".ogg" or ".wma" => "already compressed - little to gain",
        ".jpg" or ".jpeg" or ".webp" => "already compressed - little to gain",
        ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" => "already compressed - needs a video encoder",
        _ => null,
    };

    private void HomeStorage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowStorage();

    /// <summary>
    /// Redraw whichever view is currently on screen. Call this after changing data so
    /// an action never navigates the user somewhere they did not ask to go.
    /// </summary>
    private void RefreshCurrentView()
    {
        if (_storageMode) RefreshStorage();
        else if (_devicesMode) RefreshDevices();
        else if (_homeMode) RefreshHome();
        else if (_mediaMode) RefreshMediaGallery();
        else RefreshEntries();
    }

    private void StorageBack_Click(object sender, RoutedEventArgs e)
    {
        if (_storagePath.Count > 1)
        {
            _storagePath.RemoveAt(_storagePath.Count - 1);
            RefreshStorage();
            return;
        }
        ShowHome();
    }

    private void ShowStorage()
    {
        _homeMode = false;
        _storageMode = true;
        _devicesMode = false;
        _mediaMode = false;
        _currentCategory = null;
        CategoryList.SelectedItem = null;
        DevicesPanel.Visibility = Visibility.Collapsed;

        HomePanel.Visibility = Visibility.Collapsed;
        ListColumn.Visibility = Visibility.Collapsed;
        DetailColumn.Visibility = Visibility.Collapsed;
        MediaPanel.Visibility = Visibility.Collapsed;

        _storagePath.Clear();
        _storagePath.Add(new StorageCrumb(StorageLevel.Root, null, "Whole vault"));

        RefreshStorage();

        StoragePanel.Visibility = Visibility.Visible;
        Anim.SlideFadeIn(StoragePanel, 320, 16);
    }

    private void RefreshStorage()
    {
        if (!_vault.IsUnlocked) return;

        StorageRows.Children.Clear();
        StorageTotal.Text = FormatBytes(_vault.CalculateDiskSizeBytes());

        UpdateStorageModeButtons();

        var current = _storagePath[^1];
        var rows = _storageByType
            ? BuildTypeRows()
            : current.Level switch
            {
                StorageLevel.Section => BuildSectionRows(current.Id!),
                StorageLevel.Folder => BuildFolderRows(current.Id!),
                _ => BuildRootRows(),
            };

        rows = rows.OrderByDescending(r => r.Bytes).ToList();
        long shown = rows.Sum(r => r.Bytes);

        if (_storageByType)
        {
            StorageSubtitle.Text = "Every attachment grouped by format";
            StorageHint.Text = rows.Count == 0
                ? "No attachments stored yet."
                : "Formats already compressed gain little from re-encoding. "
                  + "Uncompressed and lossless formats are where the space is.";
        }
        else
        {
            StorageSubtitle.Text = current.Level == StorageLevel.Root
                ? "What is using space, biggest first"
                : $"{FormatBytes(shown)} in {current.Label}";

            StorageHint.Text = rows.Count == 0
                ? "Nothing stored here yet."
                : current.Level == StorageLevel.Root
                    ? "Attachments are almost always the bulk. Click a row to drill in."
                    // File sizes are the original size of each file. Encrypting adds only
                    // 28 bytes per attachment, so the two differ by a rounding error.
                    : "Click a folder to go deeper, or a file to open it.";
        }

        RefreshStorageBreadcrumb();
        StorageBreadcrumbHost.Visibility = _storageByType ? Visibility.Collapsed : Visibility.Visible;

        long max = rows.Count > 0 ? Math.Max(1, rows.Max(r => r.Bytes)) : 1;
        int i = 0;
        foreach (var row in rows)
        {
            var element = BuildStorageRow(row, max, shown);
            StorageRows.Children.Add(element);
            Anim.SlideFadeIn(element, 260, 10, i * 22);
            i++;
        }
    }

    // ------------------------------------------------------------- row sets

    private List<StorageRow> BuildRootRows()
    {
        var rows = new List<StorageRow>();

        foreach (var cat in _vault.Data.Categories)
        {
            var entries = _vault.Data.Entries.Where(e => e.CategoryId == cat.Id).ToList();
            long bytes = entries.Sum(EntrySizeBytes);

            rows.Add(new StorageRow(
                cat.Name,
                string.IsNullOrEmpty(cat.Glyph) ? "" : cat.Glyph,
                bytes,
                entries.Count,
                cat.IsMedia ? StorageLevel.Section : null,
                cat.Id,
                null));
        }

        // The vault file itself holds every login, note and field. It is usually tiny
        // next to media, but leaving it out would make the numbers not add up.
        long indexBytes = 0;
        try
        {
            var info = new System.IO.FileInfo(_vault.VaultFile);
            if (info.Exists) indexBytes = info.Length;
        }
        catch { /* informational only */ }

        rows.Add(new StorageRow(
            "Vault index",
            "",
            indexBytes,
            _vault.Data.Entries.Count,
            null,
            null,
            null));

        return rows;
    }

    private List<StorageRow> BuildSectionRows(string categoryId)
    {
        var rows = new List<StorageRow>();

        foreach (var folder in _vault.Data.MediaFolders
                     .Where(f => f.CategoryId == categoryId && string.IsNullOrEmpty(f.ParentFolderId)))
        {
            rows.Add(new StorageRow(
                folder.Name,
                "",
                FolderTreeSizeBytes(folder.Id),
                CountFilesInFolderTree(folder.Id),
                StorageLevel.Folder,
                folder.Id,
                null));
        }

        foreach (var entry in _vault.Data.Entries
                     .Where(e => e.CategoryId == categoryId && string.IsNullOrEmpty(e.MediaFolderId)))
        {
            rows.Add(FileRow(entry));
        }

        return rows;
    }

    private List<StorageRow> BuildFolderRows(string folderId)
    {
        var rows = new List<StorageRow>();

        foreach (var sub in _vault.Data.MediaFolders.Where(f => f.ParentFolderId == folderId))
        {
            rows.Add(new StorageRow(
                sub.Name,
                "",
                FolderTreeSizeBytes(sub.Id),
                CountFilesInFolderTree(sub.Id),
                StorageLevel.Folder,
                sub.Id,
                null));
        }

        foreach (var entry in _vault.Data.Entries.Where(e => e.MediaFolderId == folderId))
            rows.Add(FileRow(entry));

        return rows;
    }

    private StorageRow FileRow(VaultEntry entry)
    {
        var att = entry.Attachments.FirstOrDefault();
        return new StorageRow(
            string.IsNullOrWhiteSpace(entry.Title) ? att?.FileName ?? "Untitled" : entry.Title,
            att is not null && IsVideo(att) ? ""
                : att is not null && IsAudio(att) ? ""
                : att is not null && IsImage(att) ? ""
                : "",
            EntrySizeBytes(entry),
            0,
            null,
            null,
            entry);
    }

    // ----------------------------------------------------------------- ui

    private Border BuildStorageRow(StorageRow row, long maxBytes, long totalShown)
    {
        bool drillable = row.DrillTo is not null && row.Bytes > 0;
        double share = totalShown > 0 ? (double)row.Bytes / totalShown : 0;

        var host = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource("GlassStroke"),
            BorderThickness = new Thickness(1),
            Cursor = drillable ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = row.Glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 16,
            Foreground = (Brush)FindResource(drillable ? "Accent" : "Muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = row.Name,
            Foreground = (Brush)FindResource("Text"),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // Proportional bar: makes the outlier obvious at a glance.
        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource(share >= 0.4 ? "Warning" : "Accent"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        track.Child = fill;
        track.SizeChanged += (_, _) =>
            fill.Width = Math.Max(0, track.ActualWidth * (maxBytes > 0 ? (double)row.Bytes / maxBytes : 0));
        labels.Children.Add(track);

        if (row.ItemCount > 0)
        {
            string caption = row.ItemCount == 1 ? "1 item" : $"{row.ItemCount:N0} items";

            string? advice = _storageByType ? TypeAdvice(row.Name) : null;
            if (advice is not null) caption += "  ·  " + advice;

            labels.Children.Add(new TextBlock
            {
                Text = caption,
                Style = (Style)FindResource("Caption"),
                FontSize = 10,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = advice is not null && advice.StartsWith("already", StringComparison.Ordinal)
                    ? (Brush)FindResource("Muted")
                    : advice is not null
                        ? (Brush)FindResource("Accent2")
                        : (Brush)FindResource("Muted"),
            });
        }

        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        var sizeStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        sizeStack.Children.Add(new TextBlock
        {
            Text = FormatBytes(row.Bytes),
            Foreground = (Brush)FindResource("Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        sizeStack.Children.Add(new TextBlock
        {
            Text = $"{share * 100:0.#}%",
            Style = (Style)FindResource("Caption"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        Grid.SetColumn(sizeStack, 2);
        grid.Children.Add(sizeStack);

        if (drillable)
        {
            var chevron = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            // A folder row shows a bulk action instead of the chevron; the whole row is
            // still clickable to drill in, so nothing is lost by replacing it.
            var folder = row.DrillTo == StorageLevel.Folder && row.DrillId is not null
                ? _vault.Data.MediaFolders.FirstOrDefault(f => f.Id == row.DrillId)
                : null;

            if (folder is not null)
            {
                var bulk = new Button
                {
                    Style = (Style)FindResource("GhostButton"),
                    Content = "Compress all",
                    Height = 30,
                    MinWidth = 106,
                    Padding = new Thickness(12, 0, 12, 0),
                    FontSize = 11,
                    Margin = new Thickness(14, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Compress every file in this folder, including subfolders",
                };
                bulk.Click += (_, e) =>
                {
                    e.Handled = true; // must not also drill into the folder
                    BeginCompressFolder(folder);
                };
                Grid.SetColumn(bulk, 3);
                grid.Children.Add(bulk);
            }
            else
            {
                Grid.SetColumn(chevron, 3);
                grid.Children.Add(chevron);
            }
        }
        else if (row.File is not null)
        {
            // This is the screen you come to looking for something to shrink, so the
            // action belongs on the row itself rather than hidden behind a hover.
            var fileAtt = row.File.Attachments.FirstOrDefault();
            if (fileAtt is not null && (IsImage(fileAtt) || IsAudio(fileAtt) || IsVideo(fileAtt)))
            {
                var compress = new Button
                {
                    Style = (Style)FindResource("GhostButton"),
                    Content = "Compress",
                    Height = 30,
                    MinWidth = 92,
                    Padding = new Thickness(12, 0, 12, 0),
                    FontSize = 11,
                    Margin = new Thickness(14, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Make this file smaller",
                };

                var file = row.File;
                compress.Click += (_, e) =>
                {
                    e.Handled = true; // must not also open the file
                    BeginCompress(file);
                };

                Grid.SetColumn(compress, 3);
                grid.Children.Add(compress);
            }
        }

        host.Child = grid;

        if (drillable)
        {
            host.MouseEnter += (_, _) => host.Background = (Brush)FindResource("GlassBg");
            host.MouseLeave += (_, _) => host.Background = (Brush)FindResource("Bg2");
            host.MouseLeftButtonUp += (_, _) =>
            {
                _storagePath.Add(new StorageCrumb(row.DrillTo!.Value, row.DrillId, row.Name));
                RefreshStorage();
            };
        }
        else if (row.File is not null)
        {
            var att = row.File.Attachments.FirstOrDefault();
            if (att is not null)
            {
                host.Cursor = System.Windows.Input.Cursors.Hand;
                host.MouseEnter += (_, _) => host.Background = (Brush)FindResource("GlassBg");
                host.MouseLeave += (_, _) => host.Background = (Brush)FindResource("Bg2");
                host.MouseLeftButtonUp += (_, _) => OpenMediaItem(row.File, att);
            }
        }

        return host;
    }

    private void RefreshStorageBreadcrumb()
    {
        StorageBreadcrumbHost.Children.Clear();

        for (int i = 0; i < _storagePath.Count; i++)
        {
            if (i > 0)
            {
                StorageBreadcrumbHost.Children.Add(new TextBlock
                {
                    Text = "  /  ",
                    Style = (Style)FindResource("Caption"),
                    Foreground = (Brush)FindResource("Muted"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            bool isLast = i == _storagePath.Count - 1;
            if (isLast)
            {
                StorageBreadcrumbHost.Children.Add(new TextBlock
                {
                    Text = _storagePath[i].Label,
                    Style = (Style)FindResource("Caption"),
                    Foreground = (Brush)FindResource("Text"),
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                continue;
            }

            int target = i;
            var link = new Button
            {
                Content = _storagePath[i].Label,
                Style = (Style)FindResource("LinkButton"),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            link.Click += (_, _) =>
            {
                _storagePath.RemoveRange(target + 1, _storagePath.Count - target - 1);
                RefreshStorage();
            };
            StorageBreadcrumbHost.Children.Add(link);
        }
    }
}
