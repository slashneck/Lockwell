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
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Views;

/// <summary>
/// The look and behaviour of the Files page: tiles and rows, selection, and the menus that
/// replaced a row of permanent buttons on every tile.
///
/// One action bar appears when something is selected, and the same actions are on right-click
/// and on a small menu button, so nothing is more than one step away while the page itself stays
/// a page of files rather than a page of buttons.
/// </summary>
public partial class VaultShellView
{
    private readonly HashSet<string> _selectedFolderIds = new();
    private readonly HashSet<string> _selectedEntryIds = new();

    private bool HasSelection => _selectedFolderIds.Count + _selectedEntryIds.Count > 0;

    private bool ListView => _settings.FilesListView;

    // ---------------------------------------------------------------- tiles

    private Border BuildFolderTile(MediaFolder folder, int fileCount)
    {
        var tile = new Border
        {
            Width = 216,
            Height = 54,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("Bg1"),
            BorderBrush = TileBorder(folder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = folder,
            AllowDrop = true,
        };
        FitToRowIfListing(tile);

        tile.DragOver += MediaFolderTile_DragOver;
        tile.DragLeave += MediaFolderTile_DragLeave;
        tile.Drop += MediaFolderTile_Drop;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = FolderKindIcon(folder.Kind),
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 16,
            Foreground = (Brush)FindResource("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new TextBlock
        {
            Text = folder.Name,
            Foreground = (Brush)FindResource("Text"),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ListView ? 500 : 120,
        });
        if (folder.IsFavorite)
        {
            name.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 10,
                Foreground = (Brush)FindResource("Warning"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        if (folder.IsNsfw)
        {
            name.Children.Add(new TextBlock
            {
                Text = "NSFW",
                Foreground = (Brush)FindResource("Danger"),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        labels.Children.Add(name);
        labels.Children.Add(new TextBlock
        {
            Text = fileCount == 1 ? "1 file" : $"{fileCount} files",
            Foreground = (Brush)FindResource("Faint"),
            FontSize = 12,
        });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        var more = MakeMoreButton(() => ShowFolderMenu(tile, folder));
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);

        tile.Child = grid;

        WireTile(tile, MediaFolderDragFormat, folder.Id,
            toggle: () => ToggleSelection(folder),
            open: () => OpenMediaFolder(folder.Id),
            menu: () => ShowFolderMenu(tile, folder));

        return tile;
    }

    private Border BuildMediaTile(VaultEntry entry, AttachmentRef att)
    {
        bool list = ListView;

        var tile = new Border
        {
            Width = list ? double.NaN : 168,
            Height = list ? 46 : 172,
            Margin = list ? new Thickness(0, 0, 0, 4) : new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("Bg1"),
            BorderBrush = TileBorder(entry),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = entry,
            AllowDrop = true,
        };
        FitToRowIfListing(tile);

        tile.DragOver += MediaEntryTile_DragOver;
        tile.DragLeave += MediaEntryTile_DragLeave;
        tile.Drop += MediaEntryTile_Drop;

        var grid = new Grid();

        // The icon comes first and the image second, and both sit directly in this grid.
        // ApplyThumbnail finds them by type once a thumbnail has decrypted.
        var icon = new TextBlock
        {
            Text = MediaIcon(att),
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = list ? 16 : 26,
            Foreground = (Brush)FindResource("Faint"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var img = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };

        var title = new TextBlock
        {
            Text = entry.Title,
            Foreground = (Brush)FindResource("Text"),
            FontSize = list ? 13 : 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var more = MakeMoreButton(() => ShowFileMenu(tile, entry, att));

        if (list)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            img.Width = 32;
            img.Height = 32;
            img.Clip = new RectangleGeometry(new Rect(0, 0, 32, 32), 5, 5);
            grid.Children.Add(icon);
            grid.Children.Add(img);

            Grid.SetColumn(title, 1);
            grid.Children.Add(title);

            var meta = new TextBlock
            {
                Text = $"{KindOfFile(att)}  ·  {FormatSize(att.SizeBytes)}",
                Foreground = (Brush)FindResource("Faint"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 8, 0),
            };
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);

            Grid.SetColumn(more, 3);
            grid.Children.Add(more);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

            // A WPF Border does not clip its child to its corners, so the image is clipped
            // to the tile's inner rounded top edge itself.
            img.Clip = new RectangleGeometry(new Rect(0, 0, 166, 138), 7, 7);
            grid.Children.Add(icon);
            grid.Children.Add(img);

            // What a tile is when there is no picture to show it by.
            if (!IsImage(att))
            {
                icon.Margin = new Thickness(0, 0, 0, 16);
                var kind = new TextBlock
                {
                    Text = KindOfFile(att),
                    Foreground = (Brush)FindResource("Faint"),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 22),
                };
                grid.Children.Add(kind);
            }

            title.Margin = new Thickness(10, 0, 10, 2);
            Grid.SetRow(title, 1);
            grid.Children.Add(title);

            more.HorizontalAlignment = HorizontalAlignment.Right;
            more.VerticalAlignment = VerticalAlignment.Top;
            more.Margin = new Thickness(0, 4, 4, 0);
            more.Background = (Brush)FindResource("Bg1");
            more.Visibility = Visibility.Hidden;
            tile.MouseEnter += (_, _) => more.Visibility = Visibility.Visible;
            tile.MouseLeave += (_, _) => more.Visibility = Visibility.Hidden;
            grid.Children.Add(more);
        }

        // On a linked device, so a folder shows at a glance what the phone already has.
        if (entry.IsSyncedAnywhere)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("Muted"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 8, 0, 0),
                ToolTip = entry.SyncedToDevices.Count == 1
                    ? "On one linked device"
                    : $"On {entry.SyncedToDevices.Count} linked devices",
            });
        }

        if (entry.Attachments.Count > 1)
            tile.ToolTip = $"{entry.Attachments.Count} files on this item";

        tile.Child = grid;

        WireTile(tile, MediaEntryDragFormat, entry.Id,
            toggle: () => ToggleSelection(entry),
            open: () => OpenMediaItem(entry, att),
            menu: () => ShowFileMenu(tile, entry, att));

        return tile;
    }

    /// <summary>In list view a tile takes the full width of the page.</summary>
    private void FitToRowIfListing(Border tile)
    {
        if (!ListView) return;

        tile.Margin = new Thickness(0, 0, 0, 4);
        tile.Height = tile.Tag is MediaFolder ? 46 : tile.Height;
        tile.SetBinding(WidthProperty, new Binding(nameof(ScrollViewer.ViewportWidth))
        {
            Source = MediaScroller,
        });
    }

    private Button MakeMoreButton(Action open)
    {
        var more = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "",
            FontSize = 12,
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "More",
        };
        more.Click += (_, e) =>
        {
            e.Handled = true;
            open();
        };
        return more;
    }

    private void WireTile(Border tile, string dragFormat, string id, Action toggle, Action open, Action menu)
    {
        AttachMediaDragSource(tile, dragFormat, id);

        tile.MouseEnter += (_, _) =>
        {
            if (!IsSelectedTag(tile.Tag)) tile.Background = (Brush)FindResource("Bg2");
        };
        tile.MouseLeave += (_, _) => tile.Background = (Brush)FindResource(IsSelectedTag(tile.Tag) ? "AccentSoft" : "Bg1");

        if (IsSelectedTag(tile.Tag)) tile.Background = (Brush)FindResource("AccentSoft");

        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_suppressTileClick) return; // this press turned into a drag
            if (e.OriginalSource is Button) return;

            // Ctrl adds to the selection, and once something is selected a plain click keeps
            // selecting, so a multi-select cannot be lost to a stray click that opens a file.
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || HasSelection)
            {
                toggle();
                return;
            }

            open();
        };

        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            menu();
        };
    }

    private bool IsSelectedTag(object? tag) => tag switch
    {
        MediaFolder folder => _selectedFolderIds.Contains(folder.Id),
        VaultEntry entry => _selectedEntryIds.Contains(entry.Id),
        _ => false,
    };

    private Brush TileBorder(object tag) =>
        (Brush)FindResource(IsSelectedTag(tag) ? "Accent" : "Stroke");

    // ---------------------------------------------------------------- menus

    private static MenuItem MenuEntry(string header, Action run, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    private ContextMenu? _lastMenu;

    private void OpenMenu(FrameworkElement target, params object[] items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (object item in items) menu.Items.Add(item);
        _lastMenu = menu;
        menu.IsOpen = true;
    }

    private void ShowFolderMenu(FrameworkElement target, MediaFolder folder)
    {
        OpenMenu(target,
            MenuEntry("Open", () => OpenMediaFolder(folder.Id)),
            MenuEntry(IsSelectedTag(folder) ? "Deselect" : "Select", () => ToggleSelection(folder)),
            new Separator(),
            MenuEntry("Edit", () => OpenEditMediaFolder(folder)),
            MenuEntry("Export", () => ExportMediaFolder(folder)),
            MenuEntry("Compress all files", () => BeginCompressFolder(folder)),
            new Separator(),
            MenuEntry("Delete", () => DeleteMediaFolder(folder)));
    }

    private void ShowFileMenu(FrameworkElement target, VaultEntry entry, AttachmentRef att)
    {
        bool playable = IsImage(att) || IsVideo(att) || IsAudio(att);

        OpenMenu(target,
            MenuEntry(playable ? "Open" : "Save a decrypted copy", () => OpenMediaItem(entry, att)),
            MenuEntry(IsSelectedTag(entry) ? "Deselect" : "Select", () => ToggleSelection(entry)),
            new Separator(),
            MenuEntry("Rename", () => RenameMediaEntry(entry)),
            MenuEntry("Compress", () => BeginCompress(entry), enabled: playable),
            MenuEntry("Save a decrypted copy", () => ExportAttachmentToDisk(att), enabled: playable),
            new Separator(),
            MenuEntry("Delete", () => DeleteMediaEntry(entry)));
    }

    private void AddMenu_Click(object sender, RoutedEventArgs e)
    {
        AddMenu.PlacementTarget = AddMediaButton;
        AddMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        AddMenu.IsOpen = true;
    }

    private void MediaViewToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.FilesListView = !_settings.FilesListView;
        _settings.Save();
        RefreshMediaGallery();
    }

    private void UpdateMediaViewToggle()
    {
        MediaViewToggle.Content = ListView ? "" : "";
        MediaViewToggle.ToolTip = ListView ? "Show as a grid" : "Show as a list";
    }

    // ---------------------------------------------------------------- selection

    private void ToggleSelection(MediaFolder folder)
    {
        if (!_selectedFolderIds.Add(folder.Id)) _selectedFolderIds.Remove(folder.Id);
        RefreshSelectionUi();
    }

    private void ToggleSelection(VaultEntry entry)
    {
        if (!_selectedEntryIds.Add(entry.Id)) _selectedEntryIds.Remove(entry.Id);
        RefreshSelectionUi();
    }

    private void SelectAllInView()
    {
        foreach (var tile in MediaFoldersGrid.Children.OfType<Border>())
            if (tile.Tag is MediaFolder folder) _selectedFolderIds.Add(folder.Id);
        foreach (var tile in MediaGrid.Children.OfType<Border>())
            if (tile.Tag is VaultEntry entry) _selectedEntryIds.Add(entry.Id);
        RefreshSelectionUi();
    }

    private void ClearSelection()
    {
        _selectedFolderIds.Clear();
        _selectedEntryIds.Clear();
        RefreshSelectionUi();
    }

    private void SelectionClear_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void RefreshSelectionUi()
    {
        int count = _selectedFolderIds.Count + _selectedEntryIds.Count;
        MediaSelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaSelectionText.Text = count == 1 ? "1 selected" : $"{count} selected";

        foreach (var tile in MediaFoldersGrid.Children.OfType<Border>().Concat(MediaGrid.Children.OfType<Border>()))
        {
            bool selected = IsSelectedTag(tile.Tag);
            tile.BorderBrush = (Brush)FindResource(selected ? "Accent" : "Stroke");
            tile.Background = (Brush)FindResource(selected ? "AccentSoft" : "Bg1");
        }
    }

    private List<MediaFolder> SelectedFolders() =>
        _vault.Data.MediaFolders.Where(f => _selectedFolderIds.Contains(f.Id)).ToList();

    private List<VaultEntry> SelectedEntries() =>
        _vault.Data.Entries.Where(e => _selectedEntryIds.Contains(e.Id)).ToList();

    private void SelectionDelete_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        var folders = SelectedFolders();
        var entries = SelectedEntries();
        int files = entries.Count + folders.Sum(f => CountFilesInFolderTree(f.Id));

        string heading = files + folders.Count == 1
            ? "Delete this item?"
            : $"Delete {folders.Count + entries.Count} selected items?";
        string body = files == 1
            ? "1 file will be permanently removed from the vault. This cannot be undone."
            : $"{files} files will be permanently removed from the vault. This cannot be undone.";

        if (!MessageBox.Confirm(heading, body, "Delete", danger: true)) return;

        RemoveFromVault(folders, entries);
        ClearSelection();
    }

    private void RemoveFromVault(IEnumerable<MediaFolder> folders, IEnumerable<VaultEntry> entries)
    {
        foreach (var entry in entries)
        {
            foreach (var att in entry.Attachments)
            {
                _vault.DeleteAttachment(att);
                _thumbnailCache.Remove(att.Id);
            }
            _vault.Data.Entries.Remove(entry);
        }

        foreach (var folder in folders)
        {
            // A selected folder may sit inside another selected folder, already gone.
            if (_vault.Data.MediaFolders.Contains(folder)) DeleteMediaFolderTree(folder);
        }

        if (_currentMediaFolderId is not null &&
            !_vault.Data.MediaFolders.Any(f => f.Id == _currentMediaFolderId))
        {
            _currentMediaFolderId = null;
        }

        _vault.Save();
        RefreshCategories();
        RefreshMediaGallery();
    }

    private void SelectionMove_Click(object sender, RoutedEventArgs e)
    {
        var folders = SelectedFolders();
        var entries = SelectedEntries();
        var category = ResolveMediaCategory();
        if (category is null) return;

        OverlayTitle.Text = "Move to";
        OverlayCard.MaxWidth = 460;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        var choices = new StackPanel();
        string? target = null;

        void AddChoice(string label, string? folderId, int depth)
        {
            bool allowed = folders.All(f => MediaFolderTree.CanReparent(_vault.Data.MediaFolders, f.Id, folderId));
            var radio = new RadioButton
            {
                Content = label,
                GroupName = "moveTarget",
                Margin = new Thickness(depth * 18, 0, 0, 10),
                IsEnabled = allowed,
                Opacity = allowed ? 1 : 0.4,
            };
            radio.Checked += (_, _) => target = folderId;
            if (folderId == _currentMediaFolderId) radio.IsChecked = allowed;
            choices.Children.Add(radio);
        }

        void AddChildren(string? parentId, int depth)
        {
            foreach (var folder in _vault.Data.MediaFolders
                         .Where(f => f.CategoryId == category.Id && (f.ParentFolderId ?? "") == (parentId ?? ""))
                         .Where(f => !_settings.HideNsfwMediaFolders || !f.IsNsfw)
                         .OrderBy(f => f.Order)
                         .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                AddChoice(folder.Name, folder.Id, depth);
                AddChildren(folder.Id, depth + 1);
            }
        }

        AddChoice(DisplayName(category), null, 0);
        AddChildren(null, 1);
        OverlayBody.Children.Add(choices);

        OverlaySave.Content = "Move";
        _overlaySave = () =>
        {
            foreach (var entry in entries)
            {
                entry.MediaFolderId = target;
                entry.ModifiedUtc = DateTime.UtcNow;
            }

            foreach (var folder in folders)
            {
                if (!MediaFolderTree.CanReparent(_vault.Data.MediaFolders, folder.Id, target)) continue;
                folder.ParentFolderId = target;
                folder.Order = NextFolderOrder(folder.CategoryId, target);
            }

            _vault.Save();
            CloseOverlay();
            ClearSelection();
            RefreshCategories();
            RefreshMediaGallery();
        };
        ShowOverlay();
    }

    private async void SelectionExport_Click(object sender, RoutedEventArgs e)
    {
        var folders = SelectedFolders();
        var entries = SelectedEntries();
        var plan = FolderExporter.BuildPlan(_vault.Data, folders, entries);

        string name = folders.Count == 1 && entries.Count == 0 ? folders[0].Name : "Lockwell export";
        await ExportPlanAsync(name, plan, () =>
        {
            RemoveFromVault(folders, entries);
            ClearSelection();
        }).ConfigureAwait(true);
    }
}
