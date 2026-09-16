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
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lockwell.Models;
using Lockwell.Services;
using Lockwell.Sync;

namespace Lockwell.Views;

/// <summary>
/// Sending items to a linked device.
///
/// The PC waits and the phone connects, for a practical reason: a phone's address moves
/// around, while the PC's is stable and already stored on the phone from linking. So
/// the PC queues what it is willing to hand over and listens; the phone pulls.
///
/// Queueing is not sending. Nothing leaves until a device connects, authenticates as
/// one this vault already trusts, and asks for it by name.
/// </summary>
public partial class VaultShellView
{
    private CancellationTokenSource? _serveCancel;
    private TcpListener? _serveListener;

    /// <summary>Items the user picked for this session. Cleared when the screen closes.</summary>
    private readonly List<VaultEntry> _sendQueue = new();

    private void SendToDevice(TrustedDevice device)
    {
        var media = _vault.Data.Entries
            .Where(e => e.Attachments.Count > 0)
            .OrderByDescending(e => e.ModifiedUtc)
            .ToList();

        if (media.Count == 0)
        {
            OverlayTitle.Text = "Nothing to send";
            OverlayCard.MaxWidth = 480;
            OverlayBody.Children.Clear();
            _fieldEditors.Clear();
            AddCompressParagraph("This vault has no files in it yet.");
            OverlaySave.Visibility = Visibility.Collapsed;
            _overlaySave = null;
            ShowOverlay();
            return;
        }

        _sendQueue.Clear();
        _sendBrowseFolderId = null;

        OverlayTitle.Text = $"Send to {device.Name}";
        OverlayCard.MaxWidth = 760;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        AddCompressParagraph(
            "Pick what this device may receive. Nothing is sent until the phone connects " +
            "and asks for it.");

        // Where you are, so folders can be walked into and back out of.
        _sendBreadcrumb = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 8),
        };
        OverlayBody.Children.Add(_sendBreadcrumb);

        // The picker itself. Tiles rather than a list of names, because a name is often
        // the one thing a file does not have: a camera roll is full of things called
        // IMG_4821, and picking from those blind is guesswork.
        _sendGrid = new WrapPanel { Orientation = Orientation.Horizontal };

        var scroller = new ScrollViewer
        {
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _sendGrid,
        };
        OverlayBody.Children.Add(scroller);

        _sendSelectionText = new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        OverlayBody.Children.Add(_sendSelectionText);

        BuildSendBrowser();

        // Optional expiry. The phone enforces this locally on its own clock, so it
        // works even if this PC is never seen again.
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Remove from the phone after",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 16, 0, 6),
        });

        var expiryPanel = new WrapPanel();
        var expiryButtons = new Dictionary<int, Button>();
        int expiryDays = 0; // 0 = never

        foreach (var (label, days) in new[]
                 {
                     ("Never", 0), ("1 day", 1), ("7 days", 7), ("30 days", 30),
                 })
        {
            var chip = MakeChip(label, days == 0);
            int captured = days;
            chip.Click += (_, _) =>
            {
                expiryDays = captured;
                foreach (var pair in expiryButtons)
                    StyleChip(pair.Value, pair.Key == captured);
            };
            expiryButtons[days] = chip;
            expiryPanel.Children.Add(chip);
        }
        OverlayBody.Children.Add(expiryPanel);

        var status = new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        OverlayBody.Children.Add(status);

        OverlaySave.Visibility = Visibility.Visible;
        OverlaySave.Content = "Wait for device";

        _overlaySave = () =>
        {
            // Emphatically do NOT clear here. The queue is filled as tiles are picked, so
            // clearing at this point throws away the user's whole selection and then
            // reports it as empty -- which is exactly what it did after the picker was
            // rebuilt and the old "collect the checkboxes" step was removed.
            if (_sendQueue.Count == 0)
            {
                ShowInlineOverlayError("Pick at least one file to send.");
                return;
            }

            OverlaySave.IsEnabled = false;
            OverlaySave.Content = "Waiting...";
            status.Text = $"Ready to send {_sendQueue.Count} file(s). On your phone, open this " +
                          "vault, go to Devices and tap the PC, then choose Sync now.";

            _ = ServeQueueAsync(device, status, expiryDays);
        };

        ShowOverlay();
    }

    /// <summary>
    /// Listen for the linked device, verify it, then serve whatever it asks for from
    /// the queue. One connection, then the listener closes again.
    /// </summary>
    private async Task ServeQueueAsync(TrustedDevice device, TextBlock status, int expiryDays)
    {
        StopServing();
        _serveCancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken token = _serveCancel.Token;

        // Snapshot the queue: the dialog may close while we are waiting.
        var queued = _sendQueue.ToList();
        byte[] expectedKey = Convert.FromBase64String(device.PublicKey);

        try
        {
            _serveListener = new TcpListener(IPAddress.Any, SyncProtocol.DefaultPort);
            _serveListener.Start();

            using TcpClient client = await _serveListener.AcceptTcpClientAsync(token);
            using NetworkStream stream = client.GetStream();

            await Dispatcher.InvokeAsync(() => status.Text = "Device connected. Verifying...");

            // Reconnect mode: no code, and only the device we are sending to is allowed.
            HandshakeResult handshake = await Handshake.RespondAsync(
                stream, Trust.Identity, HandshakeMode.Reconnect,
                isTrusted: key => SyncProtocol.ConstantTimeEquals(key, expectedKey),
                cancellationToken: token);

            using var session = new SyncSession(stream, handshake);

            var offered = queued.Select(e => ToTransferItem(e, expiryDays)).ToList();

            var progress = new Progress<TransferProgress>(p =>
                Dispatcher.Invoke(() =>
                    status.Text = $"Sending {p.Current} of {p.Total}: {p.Title}"));

            // --- outbound: hand over what the phone asks for ---
            int sent = await Transfer.ServeAsync(
                session,
                Trust.SelfName,
                offered,
                load: id => LoadEntryBytes(queued, id),
                progress,
                token);

            // Mark what actually went, not what was queued. A device that never asked for
            // an item does not have it, and saying otherwise would make the indicator a
            // record of intentions rather than of facts.
            if (sent > 0)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (VaultEntry entry in queued)
                        entry.SyncedToDevices[device.Id] = DateTime.UtcNow;

                    _vault.Save();
                    RefreshEntries();
                });
            }

            // --- inbound: take anything the phone has queued for us ---
            await Dispatcher.InvokeAsync(() => status.Text = "Checking for items from the phone...");

            TransferSummary inbound = await Transfer.ReceiveAsync(
                session,
                // Accept anything we do not already hold, matched on contents.
                decide: items =>
                {
                    var have = _vault.Data.Entries
                        .SelectMany(e => e.Attachments)
                        .Select(a => a.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    return items.Where(i => !have.Contains(i.Id)).Select(i => i.Id).ToList();
                },
                store: (item, contents) => Dispatcher.Invoke(() => StoreFromDevice(item, contents)),
                progress: new Progress<TransferProgress>(p =>
                    Dispatcher.Invoke(() => status.Text = $"Receiving {p.Current} of {p.Total}: {p.Title}")),
                token);

            if (inbound.Received > 0)
            {
                _vault.Save();
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateStats();
                    RefreshCategories();
                });
            }

            Trust.Touch(expectedKey);

            await Dispatcher.InvokeAsync(() =>
            {
                string outText = sent == 0
                    ? "The device already had everything you offered."
                    : $"Sent {sent} file(s) to {device.Name}.";
                string inText = inbound.Received == 0
                    ? ""
                    : $" Received {inbound.Received} file(s) back.";
                status.Text = outText + inText;
                OverlaySave.Content = "Done";
                OverlaySave.IsEnabled = true;
                _overlaySave = CloseOverlay;
                RefreshDevices();
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (Overlay.Visibility == Visibility.Visible)
                    status.Text = "Timed out waiting for the device.";
                OverlaySave.IsEnabled = true;
                OverlaySave.Content = "Close";
                _overlaySave = CloseOverlay;
            });
        }
        catch (HandshakeException ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                status.Text = ex.Reason == HandshakeFailure.UnknownPeer
                    ? "A device tried to connect but is not linked to this vault. Nothing was sent."
                    : "The device could not be verified. Nothing was sent.";
                OverlaySave.IsEnabled = true;
                OverlaySave.Content = "Close";
                _overlaySave = CloseOverlay;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                status.Text = "Transfer failed: " + ex.Message;
                OverlaySave.IsEnabled = true;
                OverlaySave.Content = "Close";
                _overlaySave = CloseOverlay;
            });
        }
        finally
        {
            StopServing();
        }
    }

    private TransferItem ToTransferItem(VaultEntry entry, int expiryDays)
    {
        var att = entry.Attachments[0];

        // Hashing needs the plaintext, so this decrypts once per offered item. The
        // receiver uses it to skip anything it already holds, which usually saves far
        // more work than the hashing costs.
        byte[] contents = _vault.ReadAttachment(att);
        try
        {
            return new TransferItem
            {
                Id = entry.Id,
                Title = entry.Title,
                FileName = att.FileName,
                MediaType = att.MediaType,
                SizeBytes = att.SizeBytes,
                ContentHash = Transfer.HashOf(contents),

                // The chosen expiry travels with the item. This was accepted as a
                // parameter and then never written, so picking "1 day" on the PC did
                // nothing at all: the phone received an item with no expiry and kept it
                // forever. The receiver enforces this on its own clock, so it still works
                // if this PC is never seen again.
                ExpiresUtc = expiryDays > 0
                    ? DateTime.UtcNow.AddDays(expiryDays)
                    : null,
            };
        }
        finally
        {
            Array.Clear(contents, 0, contents.Length);
        }
    }

    /// <summary>
    /// Store an item the phone sent up. Re-encrypted with this vault's key, and filed
    /// into the media section so it turns up where the user expects.
    /// </summary>
    private void StoreFromDevice(TransferItem item, byte[] contents)
    {
        var category = ResolveMediaCategory() ?? _vault.Data.Categories[0];

        var attachment = _vault.AddAttachmentFromBytes(
            contents, item.FileName, item.MediaType);

        _vault.Data.Entries.Add(new VaultEntry
        {
            CategoryId = category.Id,
            Kind = EntryKind.Media,
            Title = string.IsNullOrWhiteSpace(item.Title) ? item.FileName : item.Title,
            Attachments = { attachment },
        });
    }

    /// <summary>
    /// The bytes to hand over for one queued item.
    ///
    /// When the user asked for a smaller copy, the shrinking happens here and nowhere else.
    /// That is deliberate: this produces the bytes that go down the wire and returns them,
    /// and it never writes anything back to the vault. The vault keeps the file it always
    /// had, at full quality, and the smaller version exists only for the length of this
    /// transfer -- it is an array that is handed to the sender and wiped by it, so there is
    /// no compressed copy to clean up afterwards and none to accidentally keep.
    ///
    /// If shrinking fails, or turns out not to be worth it, the original is sent. A
    /// transfer that works beats a transfer that is slightly smaller.
    /// </summary>
    private byte[] LoadEntryBytes(IReadOnlyList<VaultEntry> queued, string entryId)
    {
        var entry = queued.FirstOrDefault(e => e.Id == entryId)
                    ?? throw new InvalidOperationException("That item is not in the queue.");

        AttachmentRef att = entry.Attachments[0];
        byte[] original = _vault.ReadAttachment(att);

        if (!_compressOnSend) return original;
        if (!MediaCompression.CouldShrink(att.FileName, att.SizeBytes)) return original;

        try
        {
            var settings = new CompressionSettings
            {
                Quality = _settings.CompressionQuality,
                MaxImageDimension = _settings.CompressionMaxImageDimension,
            };

            // PNG and BMP shrink dramatically as JPEG, but only when there is no
            // transparency to lose.
            bool asJpeg = !MediaCompression.HasTransparency(original);
            byte[] smaller = MediaCompression.CompressImage(original, settings, asJpeg);

            // The same guard the rest of the app uses: a re-encode that saves almost
            // nothing is not worth the quality it costs.
            if (smaller.Length >= original.Length ||
                1.0 - (double)smaller.Length / original.Length < 0.05)
            {
                Array.Clear(smaller, 0, smaller.Length);
                return original;
            }

            // The full-size plaintext has done its job.
            Array.Clear(original, 0, original.Length);
            return smaller;
        }
        catch
        {
            // Anything that cannot be re-encoded is sent as it is.
            return original;
        }
    }

    private void StopServing()
    {
        try { _serveListener?.Stop(); } catch { /* already down */ }
        _serveListener = null;

        try { _serveCancel?.Cancel(); } catch { /* already cancelled */ }
        _serveCancel?.Dispose();
        _serveCancel = null;
    }
    /// <summary>Folder currently being browsed in the send picker, or null for the top.</summary>
    private string? _sendBrowseFolderId;

    private StackPanel? _sendBreadcrumb;
    private WrapPanel? _sendGrid;
    private TextBlock? _sendSelectionText;

    /// <summary>
    /// Draw the picker for wherever the user currently is.
    ///
    /// This replaced a flat list of titles with checkboxes. The problem with that list was
    /// not that it looked plain, it was that it could not answer the only question being
    /// asked: what am I about to send? A camera roll is full of files called IMG_4821, and
    /// a column of those is guesswork. Tiles with real thumbnails, arranged in the folders
    /// the vault already has, answer it by looking.
    /// </summary>
    private void BuildSendBrowser()
    {
        if (_sendGrid is null || _sendBreadcrumb is null) return;

        _sendGrid.Children.Clear();
        _sendBreadcrumb.Children.Clear();

        BuildSendBreadcrumb();

        var folders = _vault.Data.MediaFolders
            .Where(f => f.ParentFolderId == _sendBrowseFolderId)
            .OrderBy(f => f.Order)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (MediaFolder folder in folders)
            _sendGrid.Children.Add(BuildSendFolderTile(folder));

        var entries = EntriesInSendFolder(_sendBrowseFolderId);
        foreach (VaultEntry entry in entries)
            _sendGrid.Children.Add(BuildSendItemTile(entry));

        if (folders.Count == 0 && entries.Count == 0)
        {
            _sendGrid.Children.Add(new TextBlock
            {
                Text = "Nothing here.",
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(4, 10, 0, 0),
            });
        }

        UpdateSendSelectionText();
    }

    private void BuildSendBreadcrumb()
    {
        if (_sendBreadcrumb is null) return;

        void Crumb(string text, string? target, bool current)
        {
            var link = new TextBlock
            {
                Text = text,
                Style = (Style)FindResource("Caption"),
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource(current ? "Text" : "Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = current ? null : System.Windows.Input.Cursors.Hand,
            };

            if (!current)
            {
                link.MouseLeftButtonUp += (_, _) =>
                {
                    _sendBrowseFolderId = target;
                    BuildSendBrowser();
                };
            }

            _sendBreadcrumb!.Children.Add(link);
        }

        Crumb("All media", null, _sendBrowseFolderId is null);

        foreach (MediaFolder folder in SendFolderPath(_sendBrowseFolderId))
        {
            _sendBreadcrumb.Children.Add(new TextBlock
            {
                Text = "  \u203A  ",
                Style = (Style)FindResource("Caption"),
                Foreground = (Brush)FindResource("Faint"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            Crumb(folder.Name, folder.Id, folder.Id == _sendBrowseFolderId);
        }
    }

    /// <summary>Top-level folder down to the one open, for the breadcrumb.</summary>
    private List<MediaFolder> SendFolderPath(string? folderId)
    {
        var path = new List<MediaFolder>();
        string? current = folderId;

        for (int guard = 0; guard < 64 && current is not null; guard++)
        {
            MediaFolder? folder = _vault.Data.MediaFolders.FirstOrDefault(f => f.Id == current);
            if (folder is null) break;

            path.Insert(0, folder);
            current = folder.ParentFolderId;
        }

        return path;
    }

    private List<VaultEntry> EntriesInSendFolder(string? folderId) =>
        _vault.Data.Entries
            .Where(e => e.Attachments.Count > 0 && e.MediaFolderId == folderId)
            .OrderByDescending(e => e.ModifiedUtc)
            .Take(300)
            .ToList();

    /// <summary>Every entry inside a folder and everything below it.</summary>
    private List<VaultEntry> EntriesUnderFolder(string folderId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { folderId };
        var queue = new Queue<string>();
        queue.Enqueue(folderId);

        for (int guard = 0; queue.Count > 0 && guard < 4096; guard++)
        {
            string parent = queue.Dequeue();
            foreach (MediaFolder child in _vault.Data.MediaFolders.Where(f => f.ParentFolderId == parent))
            {
                if (!ids.Add(child.Id)) continue;
                queue.Enqueue(child.Id);
            }
        }

        return _vault.Data.Entries
            .Where(e => e.Attachments.Count > 0 &&
                        e.MediaFolderId is not null && ids.Contains(e.MediaFolderId))
            .ToList();
    }

    private Border BuildSendFolderTile(MediaFolder folder)
    {
        List<VaultEntry> inside = EntriesUnderFolder(folder.Id);
        bool allPicked = inside.Count > 0 && inside.All(e => _sendQueue.Contains(e));

        var tile = new Border
        {
            Width = 132,
            Height = 132,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource(allPicked ? "Accent" : "GlassStroke"),
            BorderThickness = new Thickness(allPicked ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = "\uE8B7",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 26,
            Foreground = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = folder.Name,
            Style = (Style)FindResource("Caption"),
            Foreground = (Brush)FindResource("Text"),
            FontSize = 12,
            MaxWidth = 116,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = allPicked
                ? $"all {inside.Count} picked"
                : inside.Count == 1 ? "1 file" : $"{inside.Count} files",
            Style = (Style)FindResource("Caption"),
            FontSize = 10,
            Foreground = (Brush)FindResource(allPicked ? "Accent" : "Faint"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
        });

        tile.Child = stack;
        tile.ToolTip = "Click to open. Right-click to pick the whole folder.";

        // Left click walks in; right click takes the lot. Two obvious verbs on one target,
        // rather than a separate control for "send this whole folder".
        tile.MouseLeftButtonUp += (_, _) =>
        {
            _sendBrowseFolderId = folder.Id;
            BuildSendBrowser();
        };

        tile.MouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;

            if (allPicked) _sendQueue.RemoveAll(inside.Contains);
            else foreach (VaultEntry entry in inside)
                if (!_sendQueue.Contains(entry)) _sendQueue.Add(entry);

            BuildSendBrowser();
        };

        return tile;
    }

    private Border BuildSendItemTile(VaultEntry entry)
    {
        AttachmentRef att = entry.Attachments[0];
        bool picked = _sendQueue.Contains(entry);

        var tile = new Border
        {
            Width = 132,
            Height = 132,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("Bg1"),
            BorderBrush = (Brush)FindResource(picked ? "Accent" : "GlassStroke"),
            BorderThickness = new Thickness(picked ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ClipToBounds = true,
        };

        var layers = new Grid();

        // Kind glyph underneath, so a tile is never blank while its thumbnail decrypts,
        // and stays meaningful for things that have no picture at all.
        layers.Children.Add(new TextBlock
        {
            Text = IsVideo(att) ? "\uE768" : IsAudio(att) ? "\uE8D6" : "\uEB9F",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 24,
            Foreground = (Brush)FindResource("Faint"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (IsImage(att))
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            layers.Children.Add(image);
            _ = LoadSendThumbnailAsync(image, att);
        }

        // Caption over a solid strip: a pale photo would swallow plain text.
        layers.Children.Add(new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
            Padding = new Thickness(7, 4, 7, 4),
            Child = new TextBlock
            {
                Text = entry.Title,
                Foreground = (Brush)FindResource("Text"),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        });

        var mark = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = (Brush)FindResource("Accent"),
            Visibility = picked ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("Bg0"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        layers.Children.Add(mark);

        tile.Child = layers;
        tile.ToolTip = $"{entry.Title}\n{FormatBytes(att.SizeBytes)}";

        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (_sendQueue.Contains(entry)) _sendQueue.Remove(entry);
            else _sendQueue.Add(entry);

            BuildSendBrowser();
        };

        return tile;
    }

    /// <summary>Decrypt and decode a thumbnail for the picker, reusing the gallery's cache.</summary>
    private async Task LoadSendThumbnailAsync(Image target, AttachmentRef att)
    {
        try
        {
            if (_thumbnailCache.TryGetValue(att.Id, out var cached))
            {
                target.Source = cached;
                return;
            }

            var bmp = await Task.Run(() =>
            {
                byte[] data = _vault.ReadAttachment(att);
                try
                {
                    var image = new BitmapImage();
                    using var ms = new MemoryStream(data);
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 264;
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                finally
                {
                    // The decrypted bytes existed only to be decoded.
                    Array.Clear(data, 0, data.Length);
                }
            });

            await Dispatcher.InvokeAsync(() =>
            {
                if (_vault.IsUnlocked) CacheThumbnail(att.Id, bmp);
                target.Source = bmp;
            });
        }
        catch { /* a tile without a picture still works */ }
    }

    private void UpdateSendSelectionText()
    {
        if (_sendSelectionText is null) return;

        if (_sendQueue.Count == 0)
        {
            _sendSelectionText.Text =
                "Nothing picked yet. Click a file to pick it, open a folder to look inside, " +
                "or right-click a folder to take everything in it.";
            return;
        }

        long bytes = _sendQueue.Sum(e => e.Attachments.Sum(a => a.SizeBytes));
        _sendSelectionText.Text =
            $"{_sendQueue.Count} picked \u00B7 {FormatBytes(bytes)}";
    }

    /// <summary>
    /// Queue one item for a device, offering to shrink it first when that would help.
    ///
    /// The compression question is asked here rather than left to the user to think of,
    /// because the moment you are sending something is exactly when its size matters and
    /// the one time nobody wants to go and find the compress button first.
    ///
    /// What it does NOT do is touch the original. Compressing for a transfer produces a
    /// temporary copy, that copy is what gets sent, and it is deleted afterwards. The
    /// vault keeps the file it always had, at full quality. Sending a smaller copy is a
    /// decision about this transfer, not about what you own.
    /// </summary>
    private void SendSingleEntry(VaultEntry entry, TrustedDevice device)
    {
        AttachmentRef? att = entry.Attachments.FirstOrDefault();
        if (att is null) return;

        OverlayTitle.Text = $"Send to {device.Name}";
        OverlayCard.MaxWidth = 560;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        AddCompressParagraph(
            $"\u201c{entry.Title}\u201d will be offered to {device.Name}. Nothing leaves this " +
            "PC until that device connects and asks for it.");

        var sizeLine = new TextBlock
        {
            Text = $"{att.FileName}  \u00B7  {FormatBytes(att.SizeBytes)}",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 12, 0, 0),
        };
        OverlayBody.Children.Add(sizeLine);

        // Only worth asking when there is something to gain. The same guard the rest of
        // the app uses: a re-encode that saves almost nothing is not worth the quality.
        bool worthCompressing = MediaCompression.CouldShrink(att.FileName, att.SizeBytes);
        CheckBox? compressBox = null;

        if (worthCompressing)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 16, 0, 0),
                Background = (Brush)FindResource("Bg2"),
                BorderBrush = (Brush)FindResource("GlassStroke"),
                BorderThickness = new Thickness(1),
            };

            var stack = new StackPanel();
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock
            {
                Text = "Send a smaller copy",
                Style = (Style)FindResource("Body"),
            });
            labels.Children.Add(new TextBlock
            {
                Text = "This file has not been compressed. A smaller copy is made just for " +
                       "this transfer, sent, and then deleted. The original in your vault is " +
                       "not touched and does not change.",
                Style = (Style)FindResource("Caption"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            row.Children.Add(labels);

            compressBox = new CheckBox
            {
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            };
            Grid.SetColumn(compressBox, 1);
            row.Children.Add(compressBox);

            stack.Children.Add(row);
            card.Child = stack;
            OverlayBody.Children.Add(card);
        }

        OverlaySave.Visibility = Visibility.Visible;
        OverlaySave.Content = "Queue it";

        _overlaySave = () =>
        {
            _sendQueue.Clear();
            _sendQueue.Add(entry);
            _compressOnSend = compressBox?.IsChecked == true;

            OverlaySave.IsEnabled = false;
            OverlaySave.Content = "Waiting...";

            sizeLine.Text = $"Ready to send. On your phone, open this vault, go to Devices " +
                            $"and tap {device.Name}, then choose Sync now.";

            _ = ServeQueueAsync(device, sizeLine, expiryDays: 0);
        };

        ShowOverlay();
    }

    /// <summary>
    /// Whether queued items should be shrunk for this transfer. Applies to the copy that
    /// is sent, never to what the vault holds.
    /// </summary>
    private bool _compressOnSend;

}
