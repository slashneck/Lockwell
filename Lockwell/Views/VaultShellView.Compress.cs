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
using System.Windows.Media.Imaging;
using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Views;

/// <summary>
/// Per-item compression. The user picks one file, chooses how hard to squeeze it, sees
/// the real result before committing, and decides whether to replace the original or
/// keep both.
///
/// Nothing here is automatic and nothing runs in bulk. Re-encoding is lossy and cannot
/// be undone, so every path ends in an explicit confirmation showing the actual numbers.
/// </summary>
public partial class VaultShellView
{
    private VaultEntry? _compressEntry;
    private AttachmentRef? _compressAttachment;
    private byte[]? _compressOriginal;
    private CompressionResult? _compressPreview;

    /// <summary>Compress the item currently open in the viewer.</summary>
    private void ViewerCompress_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerIndex < 0 || _viewerIndex >= _viewerPlaylist.Count) return;
        var entry = _viewerPlaylist[_viewerIndex].Entry;

        CloseViewer_Click(sender, e);
        // Let the viewer finish closing before the dialog animates in.
        Dispatcher.BeginInvoke(() => BeginCompress(entry));
    }

    private void BeginCompress(VaultEntry entry)
    {
        var att = entry.Attachments.FirstOrDefault();
        if (att is null) return;

        if (!_settings.CompressionWarningAcknowledged)
        {
            ShowCompressionExplainer(entry);
            return;
        }

        OpenCompressDialog(entry, att);
    }

    /// <summary>
    /// Shown once, until dismissed for good. This explains the concept; it is not the
    /// confirmation. The confirmation, with real numbers, always appears.
    /// </summary>
    private void ShowCompressionExplainer(VaultEntry entry)
    {
        OverlayTitle.Text = "Before you compress";
        OverlayCard.MaxWidth = 560;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        AddCompressParagraph(
            "Compressing re-encodes the file to make it smaller. Some detail is thrown "
            + "away permanently to do that, and it cannot be undone.");

        AddCompressParagraph(
            "Everything happens on this PC. Nothing is uploaded, and the file is never "
            + "written to disk unencrypted while it is processed.");

        DateTime? lastBackup = ResolveLastBackupUtc();
        bool stale = lastBackup is null || (DateTime.UtcNow - lastBackup.Value).TotalDays >= 30;

        var backupNote = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 4),
            Background = new SolidColorBrush(Color.FromArgb(stale ? (byte)0x22 : (byte)0x14,
                stale ? (byte)0xF5 : (byte)0x2D, stale ? (byte)0xC3 : (byte)0xD4, stale ? (byte)0x5C : (byte)0xBF)),
            BorderBrush = (Brush)FindResource(stale ? "Warning" : "Accent"),
            BorderThickness = new Thickness(1),
        };
        backupNote.Child = new TextBlock
        {
            Text = lastBackup is null
                ? "You have no backup yet. Exporting one first means a bad setting costs you nothing."
                : stale
                    ? $"Your last backup was {(int)(DateTime.UtcNow - lastBackup.Value).TotalDays} days ago. Consider a fresh one first."
                    : $"Last backup: {lastBackup.Value.ToLocalTime():d MMM yyyy}. You have a recent copy to fall back on.",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        OverlayBody.Children.Add(backupNote);

        var dontShow = AddToggle("Do not explain this again", false);

        _overlaySave = () =>
        {
            if (dontShow.IsChecked == true)
            {
                _settings.CompressionWarningAcknowledged = true;
                _settings.Save();
            }

            var att = entry.Attachments.FirstOrDefault();
            CloseOverlay();
            if (att is not null) Dispatcher.BeginInvoke(() => OpenCompressDialog(entry, att));
        };

        OverlaySave.Content = "Continue";
        ShowOverlay();
    }

    private void AddCompressParagraph(string text) =>
        OverlayBody.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

    // ------------------------------------------------------------- the dialog

    private void OpenCompressDialog(VaultEntry entry, AttachmentRef att)
    {
        _compressEntry = entry;
        _compressAttachment = att;
        _compressPreview = null;
        _compressOriginal = null;

        OverlayTitle.Text = "Compress file";
        OverlayCard.MaxWidth = 600;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();
        OverlaySave.Content = "Save";

        bool isImage = IsImage(att);
        bool isAudio = IsAudio(att);
        bool isVideo = IsVideo(att);

        if (!isImage && !isAudio && !isVideo)
        {
            ShowInlineOverlayError("Only images, audio and video can be compressed.");
            ShowOverlay();
            return;
        }

        OverlayBody.Children.Add(new TextBlock
        {
            Text = att.FileName,
            Foreground = (Brush)FindResource("Text"),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = $"Currently {FormatBytes(att.SizeBytes)}",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 14),
        });

        // Quality picker.
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Quality",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 6),
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
                _compressPreview = null;
                UpdateCompressEstimate(null);
            };
            qualityButtons[q] = chip;
            qualityPanel.Children.Add(chip);
        }
        OverlayBody.Children.Add(qualityPanel);

        // Resolution cap, images only.
        TextBox? dimensionBox = null;
        if (isImage)
        {
            dimensionBox = AddLabeledInputWithPlaceholder(
                "Max width/height in pixels (optional)",
                "leave empty to keep original size",
                _settings.CompressionMaxImageDimension > 0
                    ? _settings.CompressionMaxImageDimension.ToString()
                    : "",
                topMargin: 14);
        }

        var keepOriginal = AddToggle("Keep the original and add the smaller copy separately",
            _settings.CompressionKeepOriginal);
        keepOriginal.Margin = new Thickness(0, 14, 0, 0);

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Turning this off replaces the file in place. That cannot be undone.",
            Style = (Style)FindResource("Caption"),
            FontSize = 10,
            Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(0, 2, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        // Result panel: filled in after a test run.
        _compressResultHost = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        OverlayBody.Children.Add(_compressResultHost);

        var testButton = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Test this setting",
            Height = 36,
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        testButton.Click += async (_, _) =>
        {
            testButton.IsEnabled = false;
            testButton.Content = "Working...";
            await RunCompressPreviewAsync(chosen, ParseDimension(dimensionBox));
            testButton.Content = "Test again";
            testButton.IsEnabled = true;
        };
        OverlayBody.Children.Add(testButton);

        UpdateCompressEstimate(null);

        _overlaySave = async () =>
        {
            // Remember the settings even if nothing is applied.
            _settings.CompressionQuality = chosen;
            _settings.CompressionMaxImageDimension = ParseDimension(dimensionBox);
            _settings.CompressionKeepOriginal = keepOriginal.IsChecked == true;
            _settings.Save();

            if (_compressPreview is null)
            {
                ShowInlineOverlayError("Run \"Test this setting\" first so you can see the result.");
                return;
            }

            if (!_compressPreview.IsWorthwhile)
            {
                ShowInlineOverlayError(
                    "This setting does not make the file meaningfully smaller. "
                    + "Try a lower quality, or leave this file alone.");
                return;
            }

            await ApplyCompressionAsync(keepOriginal.IsChecked == true);
        };

        ShowOverlay();
    }

    private StackPanel? _compressResultHost;

    private static int ParseDimension(TextBox? box)
    {
        if (box is null) return 0;
        return int.TryParse(box.Text.Trim(), out int v) && v >= 64 ? v : 0;
    }

    private static string QualityLabel(CompressionQuality q) => q switch
    {
        CompressionQuality.Maximum => "Maximum",
        CompressionQuality.High => "High",
        CompressionQuality.Balanced => "Balanced",
        CompressionQuality.Small => "Small",
        _ => "Smallest",
    };

    private async Task RunCompressPreviewAsync(CompressionQuality quality, int maxDimension)
    {
        if (_compressAttachment is null) return;
        var att = _compressAttachment;

        try
        {
            _compressOriginal ??= await Task.Run(() => _vault.ReadAttachment(att));
            byte[] original = _compressOriginal;

            var settings = new CompressionSettings
            {
                Quality = quality,
                MaxImageDimension = maxDimension,
            };

            CompressionResult result;

            if (IsImage(att))
            {
                bool transparent = MediaCompression.HasTransparency(original);
                bool asJpeg = !transparent; // JPEG has no alpha channel
                byte[] data = await Task.Run(() => MediaCompression.CompressImage(original, settings, asJpeg));
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

            _compressPreview = result;
            UpdateCompressEstimate(result);
        }
        catch (Exception ex)
        {
            _compressPreview = null;
            UpdateCompressEstimate(null);
            ShowInlineOverlayError("Could not compress this file: " + ex.Message);
        }
    }

    private void UpdateCompressEstimate(CompressionResult? result)
    {
        if (_compressResultHost is null) return;
        _compressResultHost.Children.Clear();
        if (result is null) return;

        ClearInlineOverlayError();

        bool good = result.IsWorthwhile;
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource(good ? "Accent" : "Warning"),
            BorderThickness = new Thickness(1),
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = good
                ? $"{FormatBytes(result.OriginalBytes)}  ->  {FormatBytes(result.NewBytes)}"
                : $"{FormatBytes(result.OriginalBytes)}  ->  {FormatBytes(result.NewBytes)}  (not worth it)",
            Foreground = (Brush)FindResource("Text"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = good
                ? $"{result.SavedFraction * 100:0.#}% smaller  ·  saves {FormatBytes(result.OriginalBytes - result.NewBytes)}"
                : "Re-encoding this file gains little or makes it larger. Detail would be lost for nothing.",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        // Visual check for images: the numbers alone do not tell you if it still looks right.
        if (_compressAttachment is not null && IsImage(_compressAttachment))
        {
            try
            {
                var preview = new BitmapImage();
                using (var ms = new MemoryStream(result.Data))
                {
                    preview.BeginInit();
                    preview.CacheOption = BitmapCacheOption.OnLoad;
                    preview.DecodePixelWidth = 420;
                    preview.StreamSource = ms;
                    preview.EndInit();
                }
                preview.Freeze();

                stack.Children.Add(new TextBlock
                {
                    Text = "Result preview",
                    Style = (Style)FindResource("Caption"),
                    FontSize = 10,
                    Margin = new Thickness(0, 12, 0, 6),
                });
                stack.Children.Add(new Image
                {
                    Source = preview,
                    MaxHeight = 220,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
            }
            catch { /* preview is a nicety */ }
        }

        card.Child = stack;
        _compressResultHost.Children.Add(card);
    }

    private async Task ApplyCompressionAsync(bool keepOriginal)
    {
        if (_compressEntry is null || _compressAttachment is null || _compressPreview is null) return;

        var entry = _compressEntry;
        var oldAtt = _compressAttachment;
        var result = _compressPreview;

        // Remember where the user was before anything refreshes, so finishing here
        // returns them to the same place instead of dumping them somewhere else.
        bool wasStorage = _storageMode;
        bool wasMedia = _mediaMode;

        CloseOverlay();
        ShowProgress("Compressing", result.FileName);

        try
        {
            var newAtt = await Task.Run(() => _vault.AddAttachmentFromBytes(
                result.Data, result.FileName, result.MediaType));

            if (keepOriginal)
            {
                // Separate entry, so the original stays exactly as it was.
                _vault.Data.Entries.Add(new VaultEntry
                {
                    CategoryId = entry.CategoryId,
                    MediaFolderId = entry.MediaFolderId,
                    Kind = EntryKind.Media,
                    Title = entry.Title + " (compressed)",
                    Attachments = { newAtt },
                });
            }
            else
            {
                entry.Attachments.Remove(oldAtt);
                entry.Attachments.Add(newAtt);
                entry.ModifiedUtc = DateTime.UtcNow;
                _vault.DeleteAttachment(oldAtt);
                _thumbnailCache.Remove(oldAtt.Id);
            }

            _vault.Save();
            await FinishProgressAsync($"Saved {FormatBytes(result.OriginalBytes - result.NewBytes)}.");

            UpdateStats();
            RefreshCategories();

            if (wasStorage) RefreshStorage();
            else if (wasMedia) RefreshMediaGallery();
            else RefreshCurrentView();
        }
        catch (Exception ex)
        {
            HideProgress();
            MessageBox.Show("Could not save the compressed file: " + ex.Message, "Lockwell");
        }
        finally
        {
            if (_compressOriginal is not null)
            {
                Array.Clear(_compressOriginal, 0, _compressOriginal.Length);
                _compressOriginal = null;
            }
            _compressPreview = null;
            _compressEntry = null;
            _compressAttachment = null;
        }
    }
}
