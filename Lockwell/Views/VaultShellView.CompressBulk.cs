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
using System.Windows.Controls;
using System.Windows.Media;
using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Views;

/// <summary>
/// Compressing a whole folder in one go.
///
/// Bulk needs stricter handling than a single file. There is no practical way to
/// preview 200 results, so instead: the scope is stated exactly before starting, every
/// file is individually checked and skipped if re-encoding would not meaningfully help,
/// the run can be stopped at any point, and the summary reports what actually happened.
/// </summary>
public partial class VaultShellView
{
    private sealed record BulkTarget(VaultEntry Entry, AttachmentRef Attachment);

    /// <summary>Every compressible file in a folder, including its subfolders.</summary>
    private List<BulkTarget> CollectBulkTargets(string folderId)
    {
        var targets = new List<BulkTarget>();

        foreach (var entry in _vault.Data.Entries.Where(e => e.MediaFolderId == folderId))
        {
            var att = entry.Attachments.FirstOrDefault();
            if (att is null) continue;
            if (IsImage(att) || IsAudio(att) || IsVideo(att))
                targets.Add(new BulkTarget(entry, att));
        }

        foreach (var sub in _vault.Data.MediaFolders.Where(f => f.ParentFolderId == folderId))
            targets.AddRange(CollectBulkTargets(sub.Id));

        return targets;
    }

    private void BeginCompressFolder(MediaFolder folder)
    {
        if (_busyOperation) return;

        var targets = CollectBulkTargets(folder.Id);
        if (targets.Count == 0)
        {
            OverlayTitle.Text = "Compress folder";
            OverlayCard.MaxWidth = 520;
            OverlayBody.Children.Clear();
            _fieldEditors.Clear();
            AddCompressParagraph($"\"{folder.Name}\" has no images, audio or video to compress.");
            _overlaySave = CloseOverlay;
            ShowOverlay();
            return;
        }

        OverlayTitle.Text = $"Compress \"{folder.Name}\"";
        OverlayCard.MaxWidth = 600;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        long totalBytes = targets.Sum(t => t.Attachment.SizeBytes);

        OverlayBody.Children.Add(new TextBlock
        {
            Text = $"{targets.Count:N0} files  ·  {FormatBytes(totalBytes)}",
            Foreground = (Brush)FindResource("Text"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Includes everything inside subfolders.",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 12),
        });

        // Format breakdown, so it is obvious up front which files will actually benefit.
        var byExt = targets
            .GroupBy(t => Path.GetExtension(t.Attachment.FileName).ToLowerInvariant())
            .OrderByDescending(g => g.Sum(t => t.Attachment.SizeBytes))
            .ToList();

        foreach (var group in byExt.Take(6))
        {
            string ext = string.IsNullOrEmpty(group.Key) ? "(no extension)" : group.Key;
            string? advice = TypeAdvice(ext);
            bool poor = advice is not null && advice.StartsWith("already", StringComparison.Ordinal);

            var line = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new TextBlock
            {
                Text = $"{group.Count():N0} × {ext}",
                Style = (Style)FindResource("Caption"),
                FontSize = 11,
                Foreground = (Brush)FindResource(poor ? "Muted" : "Text"),
            };
            line.Children.Add(left);

            var right = new TextBlock
            {
                Text = poor
                    ? $"{FormatBytes(group.Sum(t => t.Attachment.SizeBytes))}  ·  likely skipped"
                    : FormatBytes(group.Sum(t => t.Attachment.SizeBytes)),
                Style = (Style)FindResource("Caption"),
                FontSize = 11,
                Foreground = (Brush)FindResource(poor ? "Muted" : "Accent"),
            };
            Grid.SetColumn(right, 1);
            line.Children.Add(right);

            OverlayBody.Children.Add(line);
        }

        // Quality.
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Quality",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 14, 0, 6),
        });

        var qualityPanel = new WrapPanel();
        var qualityButtons = new Dictionary<CompressionQuality, Button>();
        CompressionQuality chosen = _settings.CompressionQuality;

        foreach (var q in Enum.GetValues<CompressionQuality>())
        {
            var chip = MakeChip(QualityLabel(q), q == chosen);
            var captured = q;
            chip.Click += (_, _) =>
            {
                chosen = captured;
                foreach (var pair in qualityButtons)
                    StyleChip(pair.Value, pair.Key == captured);
            };
            qualityButtons[q] = chip;
            qualityPanel.Children.Add(chip);
        }
        OverlayBody.Children.Add(qualityPanel);

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Files that would not get meaningfully smaller are skipped automatically, "
                 + "so nothing loses quality for nothing.",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        // Bulk always replaces. Keeping originals here would leave a duplicate of every
        // single file, which is unusable at this scale -- so the warning is unavoidable.
        DateTime? lastBackup = ResolveLastBackupUtc();
        bool stale = lastBackup is null || (DateTime.UtcNow - lastBackup.Value).TotalDays >= 30;

        var warn = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 14, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF5, 0xC3, 0x5C)),
            BorderBrush = (Brush)FindResource("Warning"),
            BorderThickness = new Thickness(1),
        };
        var warnStack = new StackPanel();
        warnStack.Children.Add(new TextBlock
        {
            Text = $"This replaces all {targets.Count:N0} originals and cannot be undone.",
            Foreground = (Brush)FindResource("Text"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        warnStack.Children.Add(new TextBlock
        {
            Text = lastBackup is null
                ? "You have no backup. Export one first and a bad setting costs you nothing."
                : stale
                    ? $"Last backup was {(int)(DateTime.UtcNow - lastBackup.Value).TotalDays} days ago."
                    : $"Last backup: {lastBackup.Value.ToLocalTime():d MMM yyyy}.",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        warn.Child = warnStack;
        OverlayBody.Children.Add(warn);

        var confirm = AddToggle($"Yes, compress and replace {targets.Count:N0} files", false);
        confirm.Margin = new Thickness(0, 12, 0, 0);

        OverlaySave.Content = "Compress folder";

        _overlaySave = async () =>
        {
            if (confirm.IsChecked != true)
            {
                ShowInlineOverlayError("Tick the confirmation box first.");
                return;
            }

            _settings.CompressionQuality = chosen;
            _settings.Save();

            CloseOverlay();
            await RunBulkCompressionAsync(targets, chosen, folder.Name);
        };

        ShowOverlay();
    }

    private async Task RunBulkCompressionAsync(
        List<BulkTarget> targets, CompressionQuality quality, string folderName)
    {
        bool wasStorage = _storageMode;
        bool wasMedia = _mediaMode;

        CancellationToken token = ShowCancellableProgress("Compressing folder", folderName);

        int done = 0, compressed = 0, skipped = 0, failed = 0;
        long savedBytes = 0;
        bool stopped = false;

        try
        {
            foreach (var target in targets)
            {
                if (token.IsCancellationRequested) { stopped = true; break; }

                done++;
                UpdateProgress(target.Attachment.FileName, done, targets.Count);

                try
                {
                    var outcome = await CompressOneAsync(target, quality, token);
                    if (outcome is null) { skipped++; continue; }

                    savedBytes += outcome.Value;
                    compressed++;
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    break;
                }
                catch
                {
                    // One bad file must not abandon the rest of the folder.
                    failed++;
                }

                // Persist as we go, so stopping half way still keeps completed work.
                if (compressed > 0 && compressed % 10 == 0)
                    _vault.Save();
            }

            _vault.Save();
            HideProgress();

            ShowBulkSummary(folderName, compressed, skipped, failed, savedBytes, stopped);
        }
        catch (Exception ex)
        {
            HideProgress();
            MessageBox.Show("Bulk compression stopped: " + ex.Message, "Lockwell");
        }
        finally
        {
            UpdateStats();
            RefreshCategories();
            if (wasStorage) RefreshStorage();
            else if (wasMedia) RefreshMediaGallery();
            else RefreshCurrentView();
        }
    }

    /// <summary>
    /// Compress one file in place. Returns the bytes saved, or null when the file was
    /// left alone because re-encoding would not have helped.
    /// </summary>
    private async Task<long?> CompressOneAsync(
        BulkTarget target, CompressionQuality quality, CancellationToken token)
    {
        var att = target.Attachment;
        byte[] original = await Task.Run(() => _vault.ReadAttachment(att), token);

        try
        {
            token.ThrowIfCancellationRequested();

            CompressionResult result;
            var settings = new CompressionSettings
            {
                Quality = quality,
                MaxImageDimension = _settings.CompressionMaxImageDimension,
            };

            if (IsImage(att))
            {
                bool transparent = MediaCompression.HasTransparency(original);
                bool asJpeg = !transparent; // JPEG cannot carry an alpha channel
                byte[] data = await Task.Run(
                    () => MediaCompression.CompressImage(original, settings, asJpeg), token);
                result = new CompressionResult
                {
                    Data = data,
                    FileName = Path.GetFileNameWithoutExtension(att.FileName) + (asJpeg ? ".jpg" : ".png"),
                    MediaType = asJpeg ? "image/jpeg" : "image/png",
                    OriginalBytes = original.LongLength,
                };
            }
            else if (IsAudio(att))
            {
                byte[] data = await MediaCompression.CompressAudioAsync(original, quality);
                result = new CompressionResult
                {
                    Data = data,
                    FileName = Path.GetFileNameWithoutExtension(att.FileName) + ".mp3",
                    MediaType = "audio/mpeg",
                    OriginalBytes = original.LongLength,
                };
            }
            else
            {
                byte[] data = await MediaCompression.CompressVideoAsync(original, quality);
                result = new CompressionResult
                {
                    Data = data,
                    FileName = Path.GetFileNameWithoutExtension(att.FileName) + ".mp4",
                    MediaType = "video/mp4",
                    OriginalBytes = original.LongLength,
                };
            }

            // The per-file guard is what makes bulk safe: an already-efficient MP3 or
            // JPEG is left exactly as it was rather than being degraded for no gain.
            if (!result.IsWorthwhile) return null;

            var newAtt = _vault.AddAttachmentFromBytes(result.Data, result.FileName, result.MediaType);

            target.Entry.Attachments.Remove(att);
            target.Entry.Attachments.Add(newAtt);
            target.Entry.ModifiedUtc = DateTime.UtcNow;
            _vault.DeleteAttachment(att);
            _thumbnailCache.Remove(att.Id);

            return result.OriginalBytes - result.NewBytes;
        }
        finally
        {
            Array.Clear(original, 0, original.Length);
        }
    }

    private void ShowBulkSummary(
        string folderName, int compressed, int skipped, int failed, long savedBytes, bool stopped)
    {
        OverlayTitle.Text = stopped ? "Stopped" : "Folder compressed";
        OverlayCard.MaxWidth = 520;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        OverlayBody.Children.Add(new TextBlock
        {
            Text = savedBytes > 0 ? $"Saved {FormatBytes(savedBytes)}" : "Nothing was changed",
            Foreground = (Brush)FindResource("Text"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = $"in \"{folderName}\"",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 2, 0, 14),
        });

        AddSummaryLine("Compressed", $"{compressed:N0} files", "Accent");

        if (skipped > 0)
        {
            AddSummaryLine("Left alone", $"{skipped:N0} files", "Muted");
            OverlayBody.Children.Add(new TextBlock
            {
                Text = "These were already efficient. Re-encoding would have cost quality "
                     + "without saving space.",
                Style = (Style)FindResource("Caption"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 8),
            });
        }

        if (failed > 0)
            AddSummaryLine("Could not be read", $"{failed:N0} files", "Warning");

        if (stopped)
        {
            OverlayBody.Children.Add(new TextBlock
            {
                Text = "You stopped this part way. Files already compressed are saved; "
                     + "the rest are untouched.",
                Style = (Style)FindResource("Caption"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });
        }

        OverlaySave.Content = "Done";
        _overlaySave = CloseOverlay;
        ShowOverlay();
    }

    private void AddSummaryLine(string label, string value, string brushKey)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("Caption"),
            FontSize = 12,
        });

        var right = new TextBlock
        {
            Text = value,
            Foreground = (Brush)FindResource(brushKey),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        Grid.SetColumn(right, 1);
        row.Children.Add(right);

        OverlayBody.Children.Add(row);
    }
}
