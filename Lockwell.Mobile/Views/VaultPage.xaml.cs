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

using System.Net.Sockets;
using Lockwell.Media;
using Lockwell.Mobile.Services;
using Lockwell.Sync;
using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace Lockwell.Mobile.Views;

/// <summary>
/// An open vault: what is in it, what is linked to it, and its settings.
///
/// This is a standalone vault app. Adding photos and files works with no PC anywhere in
/// the picture; the Devices tab is one feature among several, not a prerequisite.
/// </summary>
public partial class VaultPage : ContentPage
{
    private readonly VaultLibrary _library;
    private readonly VaultProfile _profile;
    private readonly PhoneVault _vault;
    private readonly IBiometricUnlock? _biometrics;
    private readonly PhoneTrustStore _trust;
    private readonly IImageDecoder? _decoder;

    /// <summary>Folder currently open, or null for the top level.</summary>
    private string? _folderId;

    /// <summary>
    /// Decoded thumbnails, keyed by item id. Held only while the vault is open: the
    /// page is discarded on lock, and with it every decoded picture. Nothing here is
    /// ever written to storage.
    /// </summary>
    private readonly Dictionary<string, ImageSource> _thumbnails = new();

    private MediaViewer? _viewer;

    /// <summary>
    /// What is currently being dragged. The drag data package carries the id too, but
    /// holding the object avoids looking it up again on every drag-over event, which
    /// fires continuously while a finger is moving.
    /// </summary>
    private PhoneItem? _draggingItem;
    private PhoneFolder? _draggingFolder;

    public VaultPage(VaultLibrary library, VaultProfile profile, PhoneVault vault)
    {
        InitializeComponent();

        _library = library;
        _profile = profile;
        _vault = vault;
        _biometrics = ServiceHelper.GetService<IBiometricUnlock>();
        _decoder = ServiceHelper.GetService<IImageDecoder>();
        _trust = new PhoneTrustStore(library.DirectoryFor(profile));

        HeaderName.Text = profile.Name;
        HeaderGlyph.Text = profile.Glyph;

        ShowSection(Section.Items);
        Refresh();

        // Leaving the app for long enough locks the vault. Subscribed here rather than in
        // App so the handler dies with the page: a locked vault has no page to lock.
        AppLock.Grace = GraceFromSettings();
        AppLock.ShouldLock += OnShouldLock;
    }

    private TimeSpan? GraceFromSettings()
    {
        int seconds = _vault.Data.LockAfterSecondsAway;
        if (seconds < 0) return null;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>The app came back after too long away. Lock, and go back to the vault list.</summary>
    private void OnShouldLock()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!_vault.IsUnlocked) return;

            LockDown();
            try { await Navigation.PopToRootAsync(); }
            catch { /* already gone */ }
        });
    }

    private enum Section { Items, Devices, Settings }

    private Section _section = Section.Items;

    // --------------------------------------------------------------- tabs

    private void OnTabItems(object? sender, EventArgs e) => ShowSection(Section.Items);
    private void OnTabDevices(object? sender, EventArgs e) => ShowSection(Section.Devices);
    private void OnTabSettings(object? sender, EventArgs e) => ShowSection(Section.Settings);

    private void ShowSection(Section section)
    {
        _section = section;

        ItemsSection.IsVisible = section == Section.Items;
        DevicesSection.IsVisible = section == Section.Devices;
        SettingsSection.IsVisible = section == Section.Settings;

        // The add button belongs to the item list only.
        AddButton.IsVisible = section == Section.Items;

        Style(TabItems, TabItemsIcon, TabItemsText, section == Section.Items);
        Style(TabDevices, TabDevicesIcon, TabDevicesText, section == Section.Devices);
        Style(TabSettings, TabSettingsIcon, TabSettingsText, section == Section.Settings);

        if (section == Section.Devices) RefreshDevices();
        if (section == Section.Settings) RefreshSettings();
    }

    /// <summary>
    /// Mark one tab as the current one. The icon carries most of it: a dimmed emoji reads
    /// as inactive at a glance, where two similar shades of text do not.
    /// </summary>
    private static void Style(Border chip, Label icon, Label text, bool active)
    {
        chip.BackgroundColor = active ? Theme.Color("Bg3") : Colors.Transparent;
        chip.Stroke = new SolidColorBrush(active ? Theme.Color("Stroke") : Colors.Transparent);

        text.TextColor = Theme.Color(active ? "Accent" : "Muted");
        text.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;

        // Emoji ignore TextColor, so opacity is what separates the active icon from
        // the others.
        icon.Opacity = active ? 1.0 : 0.42;
    }

    // -------------------------------------------------------------- items

    /// <summary>How many tiles fit across. Three is the phone default; wider screens
    /// earn a fourth rather than stretching three to absurd sizes.</summary>
    private int Columns => Width > 700 ? 4 : 3;

    private void Refresh()
    {
        // Anything past its expiry goes before it is ever shown.
        _vault.PurgeExpiredItems();

        // A folder deleted underneath us must not leave the page pointing into nothing.
        if (_folderId is not null && !_vault.FolderExists(_folderId)) _folderId = null;

        var (folders, items) = _vault.Contents(_folderId);

        BuildBreadcrumb();
        BuildFolderRows(folders);
        BuildItemGrid(items);

        bool empty = folders.Count == 0 && items.Count == 0;
        EmptyCard.IsVisible = empty;
        if (empty)
        {
            bool atRoot = _folderId is null;
            EmptyTitle.Text = atRoot ? "Nothing in here yet" : "This folder is empty";
            EmptyBody.Text = atRoot
                ? "Add photos, videos or files from this phone. They are encrypted the moment they land here."
                : "Add something here, or move items into it from another folder.";
        }

        // A count on the row that lists things, so a folder says how much is in it without
        // anyone having to scroll to the end.
        int shown = folders.Count + items.Count;
        LibraryCount.Text = shown == 0
            ? ""
            : shown == 1 ? "1 item" : $"{shown} items";

        int total = _vault.Data.Items.Count;
        HeaderSummary.Text = total switch
        {
            0 => "Unlocked · empty",
            1 => "Unlocked · 1 item",
            _ => $"Unlocked · {total} items",
        };

        SortButton.Text = "Sort: " + ItemOrdering.Label(_vault.Data.SortBy) +
                          (_vault.Data.SortDescending ? " ↓" : " ↑");
    }

    /// <summary>Where you are, and a way back up. Hidden at the top level, where there
    /// is nothing to go back to and the row would only take space.</summary>
    private void BuildBreadcrumb()
    {
        Breadcrumb.Clear();

        var path = FolderTree.PathTo(_vault.Folders, _folderId);

        Breadcrumb.Add(Crumb("All items", _folderId is not null, () => OpenFolder(null),
            dropTarget: null, isRoot: true));

        foreach (PhoneFolder folder in path)
        {
            Breadcrumb.Add(new Label
            {
                Text = "›",
                FontSize = 13,
                TextColor = Theme.Color("Faint"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(2, 0),
            });

            bool isCurrent = folder.Id == _folderId;
            string id = folder.Id;
            Breadcrumb.Add(Crumb(folder.Name, !isCurrent, () => OpenFolder(id),
                dropTarget: id, isRoot: false));
        }
    }

    private View Crumb(string text, bool tappable, Action onTap, string? dropTarget, bool isRoot)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 13,
            FontAttributes = tappable ? FontAttributes.None : FontAttributes.Bold,
            TextColor = Theme.Color(tappable ? "Muted" : "TextColor"),
            VerticalOptions = LayoutOptions.Center,
        };

        // Wrapped so there is something with a background to highlight while a drag
        // hovers over it; a bare label has nothing to show the target with.
        var host = new Border
        {
            Content = label,
            Padding = new Thickness(7, 4),
            BackgroundColor = Colors.Transparent,
            Stroke = Colors.Transparent,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
        };

        if (tappable)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => onTap();
            host.GestureRecognizers.Add(tap);
        }

        // Dragging onto an ancestor moves the thing out of where it currently is, which
        // is the only way to get something back up a level by dragging.
        if (isRoot || dropTarget is not null)
        {
            var drop = new DropGestureRecognizer { AllowDrop = true };
            drop.DragOver += (_, e) =>
            {
                bool welcome = CanDropIntoLevel(dropTarget);
                e.AcceptedOperation = welcome ? DataPackageOperation.Copy : DataPackageOperation.None;
                Highlight(host, welcome);
            };
            drop.DragLeave += (_, _) => Highlight(host, false);
            drop.Drop += async (_, e) =>
            {
                Highlight(host, false);
                await DropIntoLevelAsync(dropTarget);
                e.Handled = true;
            };
            host.GestureRecognizers.Add(drop);
        }

        return host;
    }

    // ------------------------------------------------------- drag and drop

    private void ClearDrag()
    {
        _draggingItem = null;
        _draggingFolder = null;
    }

    /// <summary>Ring a target while something hovers over it, so the drop point is obvious.</summary>
    private void Highlight(Border target, bool on)
    {
        target.Stroke = on ? Theme.Color("Accent") : Theme.Color("Stroke");
        target.BackgroundColor = on ? Theme.Color("Bg3") : Theme.Color("Bg2");
    }

    private bool CanDropInto(PhoneFolder folder)
    {
        if (_draggingItem is not null) return _draggingItem.FolderId != folder.Id;

        if (_draggingFolder is not null)
        {
            if (_draggingFolder.Id == folder.Id) return false;
            if (_draggingFolder.ParentFolderId == folder.Id) return false;
            return FolderTree.CanMove(_vault.Folders, _draggingFolder.Id, folder.Id);
        }

        return false;
    }

    private bool CanDropIntoLevel(string? folderId)
    {
        if (_draggingItem is not null) return _draggingItem.FolderId != folderId;
        if (_draggingFolder is not null)
            return _draggingFolder.ParentFolderId != folderId &&
                   FolderTree.CanMove(_vault.Folders, _draggingFolder.Id, folderId);
        return false;
    }

    private async Task DropIntoFolderAsync(PhoneFolder folder)
    {
        if (_draggingItem is not null)
        {
            _vault.MoveItem(_draggingItem, folder.Id);
            ClearDrag();
            Refresh();
            return;
        }

        if (_draggingFolder is null) return;

        PhoneFolder moving = _draggingFolder;
        ClearDrag();

        if (!_vault.MoveFolder(moving, folder.Id))
        {
            await this.ShowAlert("Cannot move it there",
                "A folder cannot be put inside itself.", "OK");
            return;
        }

        Refresh();
    }

    private async Task DropIntoLevelAsync(string? folderId)
    {
        if (_draggingItem is not null)
        {
            _vault.MoveItem(_draggingItem, folderId);
            ClearDrag();
            Refresh();
            return;
        }

        if (_draggingFolder is null) return;

        PhoneFolder moving = _draggingFolder;
        ClearDrag();

        if (!_vault.MoveFolder(moving, folderId))
        {
            await this.ShowAlert("Cannot move it there",
                "A folder cannot be put inside itself.", "OK");
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Put the dragged item immediately before the one it was dropped on. This switches
    /// the library to a hand-arranged order, because a manual position means nothing
    /// under sorting by date or name and the drag would otherwise appear to do nothing.
    /// </summary>
    private async Task ArrangeBeforeAsync(PhoneItem target)
    {
        PhoneItem? moving = _draggingItem;
        ClearDrag();

        if (moving is null || moving.Id == target.Id) return;

        bool wasAutomatic = _vault.Data.SortBy != ItemSort.Custom;
        if (!_vault.ArrangeItem(moving, target, _folderId)) return;

        Refresh();

        if (wasAutomatic)
        {
            await this.ShowAlert("Arranged by hand",
                "Sorting is now set to \"My order\", so what you arrange stays put. " +
                "Pick another sort from the Sort button to go back to automatic order.",
                "OK");
        }
    }

    private void OpenFolder(string? folderId)
    {
        _folderId = folderId;
        Refresh();
    }

    private void BuildFolderRows(IReadOnlyList<PhoneFolder> folders)
    {
        FolderList.Clear();

        foreach (PhoneFolder folder in folders)
        {
            PhoneFolder captured = folder;
            int inside = _vault.CountInside(folder);

            var row = new Border
            {
                BackgroundColor = Theme.Color("Bg2"),
                Stroke = Theme.Color("Stroke"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Padding = new Thickness(14, 12),
            };

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
            };

            grid.Add(new Label
            {
                Text = "\U0001F4C1",
                FontSize = 20,
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 0, 12, 0),
            }, 0);

            var labels = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            labels.Add(new Label
            {
                Text = folder.Name,
                FontSize = 14,
                TextColor = Theme.Color("TextColor"),
                LineBreakMode = LineBreakMode.TailTruncation,
            });
            labels.Add(new Label
            {
                Text = inside == 1 ? "1 item" : $"{inside} items",
                FontSize = 11,
                TextColor = Theme.Color("Muted"),
            });
            grid.Add(labels, 1);

            var more = new Button
            {
                Text = "⋯",
                FontSize = 17,
                BackgroundColor = Colors.Transparent,
                TextColor = Theme.Color("Muted"),
                WidthRequest = 40,
                HeightRequest = 40,
                Padding = 0,
            };
            more.Clicked += async (_, _) => await FolderActionsAsync(captured);
            grid.Add(more, 2);

            row.Content = grid;

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => OpenFolder(captured.Id);
            row.GestureRecognizers.Add(tap);

            // A folder can be picked up and put inside another one.
            var dragFolder = new DragGestureRecognizer { CanDrag = true };
            dragFolder.DragStarting += (_, e) =>
            {
                _draggingFolder = captured;
                _draggingItem = null;
                e.Data.Properties["folder"] = captured.Id;
            };
            dragFolder.DropCompleted += (_, _) => ClearDrag();
            row.GestureRecognizers.Add(dragFolder);

            // And a folder is somewhere things can be dropped into.
            var drop = new DropGestureRecognizer { AllowDrop = true };
            drop.DragOver += (_, e) =>
            {
                bool welcome = CanDropInto(captured);
                e.AcceptedOperation = welcome ? DataPackageOperation.Copy : DataPackageOperation.None;
                Highlight(row, welcome);
            };
            drop.DragLeave += (_, _) => Highlight(row, false);
            drop.Drop += async (_, e) =>
            {
                Highlight(row, false);
                await DropIntoFolderAsync(captured);
                e.Handled = true;
            };
            row.GestureRecognizers.Add(drop);

            FolderList.Add(row);
        }
    }

    private void BuildItemGrid(IReadOnlyList<PhoneItem> items)
    {
        ItemGrid.Clear();
        ItemGrid.RowDefinitions.Clear();
        ItemGrid.ColumnDefinitions.Clear();

        if (items.Count == 0) return;

        // A gallery reads as a sheet of pictures, not a set of separate cards, so the
        // gutters are hairlines rather than the card spacing used elsewhere.
        ItemGrid.ColumnSpacing = 3;
        ItemGrid.RowSpacing = 3;

        int columns = Columns;
        for (int c = 0; c < columns; c++)
            ItemGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        int rows = (items.Count + columns - 1) / columns;
        for (int r = 0; r < rows; r++)
            ItemGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int n = 0; n < items.Count; n++)
        {
            View tile = BuildTile(items[n], items);
            ItemGrid.Add(tile, n % columns, n / columns);
        }
    }

    /// <summary>
    /// One square in the gallery. Images get a real preview, decoded from the vault into
    /// memory; everything else gets a glyph for its kind, so the grid still reads as a
    /// library rather than a wall of identical boxes.
    /// </summary>
    private View BuildTile(PhoneItem item, IReadOnlyList<PhoneItem> siblings)
    {
        PhoneItem captured = item;

        bool hasPicture = item.IsImage || item.IsVideo;

        var frame = new Border
        {
            BackgroundColor = Theme.Color("Bg2"),

            // A photo needs no frame drawn round it. Anything without a picture keeps a
            // faint edge so an empty-looking tile still reads as a thing.
            Stroke = new SolidColorBrush(hasPicture ? Colors.Transparent : Theme.Color("Stroke")),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = 0,
            HeightRequest = 124,
        };

        var stack = new Grid();

        // Images and videos both get a real preview. A video's is a frame pulled from
        // the file itself, which makes the grid read as a library rather than a wall of
        // identical icons.
        ImageSource? preview = item.IsImage || item.IsVideo ? ThumbnailFor(item) : null;

        if (preview is not null)
        {
            stack.Add(new Image
            {
                Source = preview,
                Aspect = Aspect.AspectFill,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
            });

            // A play glyph over the poster, so a video is not mistaken for a photo.
            if (item.IsVideo)
            {
                stack.Add(new Label
                {
                    Text = "▶",
                    FontSize = 22,
                    TextColor = Colors.White,
                    BackgroundColor = Color.FromRgba(0, 0, 0, 0.45),
                    Padding = new Thickness(9, 3),
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                });
            }
        }
        else
        {
            stack.Add(new Label
            {
                Text = item.IsVideo ? "\U0001F3AC" : item.IsAudio ? "\U0001F3B5" : "\U0001F4C4",
                FontSize = 30,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            });
        }

        // A picture is its own label. Printing a filename across every photo turns a
        // gallery into a file listing, so the caption is kept for the things that have no
        // preview and would otherwise be indistinguishable.
        if (!hasPicture)
        {
            stack.Add(new Label
            {
                Text = item.Title,
                FontSize = 10,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 2,
                TextColor = Theme.Color("Muted"),
                HorizontalTextAlignment = TextAlignment.Center,
                Padding = new Thickness(6, 4),
                VerticalOptions = LayoutOptions.End,
            });
        }

        // Where this item stands with a linked PC, read at a glance. A tick means the PC
        // has it; an arrow means it is waiting to go on the next sync.
        string? syncGlyph =
            item.OfferToPc ? "\u2191" :
            item.SentToPcUtc is not null ? "\u2713" : null;

        if (syncGlyph is not null)
        {
            stack.Add(new Label
            {
                Text = syncGlyph,
                FontSize = 10,
                FontAttributes = FontAttributes.Bold,
                TextColor = Theme.Color("Bg0"),
                BackgroundColor = Theme.Color(item.OfferToPc ? "Warning" : "Accent"),
                Padding = new Thickness(5, 1),
                Margin = new Thickness(4, 0, 0, 4),
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.End,
            });
        }

        // A pending expiry is the one thing that must never be a surprise, so it is shown
        // on the tile rather than only in the item's details.
        if (item.ExpiresUtc is not null)
        {
            TimeSpan left = item.ExpiresUtc.Value - DateTime.UtcNow;
            string text = left.TotalHours < 24
                ? $"{Math.Max(1, (int)left.TotalHours)}h"
                : $"{(int)left.TotalDays}d";

            stack.Add(new Label
            {
                Text = text,
                FontSize = 9,
                FontAttributes = FontAttributes.Bold,
                TextColor = Theme.Color("Bg0"),
                BackgroundColor = Theme.Color("Warning"),
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 6, 6, 0),
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start,
            });
        }

        // A tap opens the item, which is what a gallery tile should do. Actions get their
        // own small target rather than a double tap: MAUI fires the single-tap handler on
        // the way to a double, so the two would trip over each other on every press.
        var actions = new Button
        {
            Text = "\u22EF",
            FontSize = 14,
            BackgroundColor = Color.FromRgba(0, 0, 0, 0.42),
            TextColor = Colors.White,
            CornerRadius = 11,
            WidthRequest = 26,
            HeightRequest = 22,
            Padding = 0,
            Margin = new Thickness(0, 4, 4, 0),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
        };
        actions.Clicked += async (_, _) => await ItemActionsAsync(captured);

        // The expiry badge already sits in that corner, so move the menu opposite when
        // both are present rather than stacking them on top of each other.
        if (item.ExpiresUtc is not null)
        {
            actions.HorizontalOptions = LayoutOptions.Start;
            actions.Margin = new Thickness(5, 5, 0, 0);
        }

        stack.Add(actions);
        frame.Content = stack;

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OpenViewerAsync(captured, siblings);
        frame.GestureRecognizers.Add(tap);

        // Pick a tile up to move it into a folder, or to arrange it against another tile.
        var drag = new DragGestureRecognizer { CanDrag = true };
        drag.DragStarting += (_, e) =>
        {
            _draggingItem = captured;
            _draggingFolder = null;
            e.Data.Properties["item"] = captured.Id;

            // Fade the tile being carried, so it is obvious which one is in flight.
            frame.Opacity = 0.45;
        };
        drag.DropCompleted += (_, _) =>
        {
            frame.Opacity = 1;
            ClearDrag();
        };
        frame.GestureRecognizers.Add(drag);

        // Dropping one tile onto another arranges it before that one.
        var accept = new DropGestureRecognizer { AllowDrop = true };
        accept.DragOver += (_, e) =>
        {
            bool welcome = _draggingItem is not null && _draggingItem.Id != captured.Id;
            e.AcceptedOperation = welcome ? DataPackageOperation.Copy : DataPackageOperation.None;
            Highlight(frame, welcome);
        };
        accept.DragLeave += (_, _) => Highlight(frame, false);
        accept.Drop += async (_, e) =>
        {
            Highlight(frame, false);
            await ArrangeBeforeAsync(captured);
            e.Handled = true;
        };
        frame.GestureRecognizers.Add(accept);

        return frame;
    }

    /// <summary>
    /// A preview decoded straight from the vault into memory, cached for as long as this
    /// page lives. Nothing is written to storage, so no gallery, scanner or backup can
    /// ever see these pictures.
    /// </summary>
    private ImageSource? ThumbnailFor(PhoneItem item)
    {
        if (_thumbnails.TryGetValue(item.Id, out ImageSource? cached)) return cached;

        try
        {
            byte[] plain = _vault.ReadItem(item);

            byte[]? small = item.IsVideo
                ? _decoder?.VideoPoster(plain, 320)
                : _decoder?.Thumbnail(plain, 320);

            // The full-size plaintext has done its job once a thumbnail exists.
            if (small is not null && !ReferenceEquals(small, plain))
                Array.Clear(plain, 0, plain.Length);

            if (small is null) return null;

            byte[] bytes = small;
            var source = ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
            _thumbnails[item.Id] = source;
            return source;
        }
        catch
        {
            // A preview that cannot be produced is a cosmetic problem; the tile falls
            // back to its glyph and the item still opens.
            return null;
        }
    }

    private async Task OpenViewerAsync(PhoneItem item, IReadOnlyList<PhoneItem> siblings)
    {
        var viewer = new MediaViewer(
            this, siblings, item,
            read: _vault.ReadItem,
            onActions: async viewed => await ItemActionsAsync(viewed),
            onClosed: () => _viewer = null);

        _viewer = viewer;
        await viewer.ShowAsync();
    }

    // ------------------------------------------------------------- folders

    private async void OnNewFolderClicked(object sender, EventArgs e)
    {
        string? name = await this.ShowPrompt("New folder",
            _folderId is null ? "Name it." : "It will be created inside the folder you are in.",
            placeholder: "e.g. Trip photos", maxLength: 60);

        if (string.IsNullOrWhiteSpace(name)) return;

        _vault.CreateFolder(name.Trim(), _folderId);
        Refresh();
    }

    private async Task FolderActionsAsync(PhoneFolder folder)
    {
        int inside = _vault.CountInside(folder);

        string? action = await this.ShowSheet(
            folder.Name, "Cancel", "Delete folder", "Open", "Rename", "Move");

        switch (action)
        {
            case "Open":
                OpenFolder(folder.Id);
                break;

            case "Rename":
                string? name = await this.ShowPrompt("Rename folder", "What should it be called?",
                    initialValue: folder.Name, maxLength: 60);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _vault.RenameFolder(folder, name.Trim());
                    Refresh();
                }
                break;

            case "Move":
                await MoveFolderAsync(folder);
                break;

            case "Delete folder":
                await DeleteFolderAsync(folder, inside);
                break;
        }
    }

    /// <summary>
    /// Removing a folder never removes what is in it by default. Tidying up a container
    /// should not be the thing that destroys the only copy of a photo, so the contents
    /// move up a level and the user is told so plainly. Deleting the contents too is a
    /// separate, differently worded choice.
    /// </summary>
    private async Task DeleteFolderAsync(PhoneFolder folder, int inside)
    {
        if (inside == 0)
        {
            bool sure = await this.ShowConfirm("Delete this folder?",
                $"\"{folder.Name}\" is empty, so nothing is lost.", "Delete", "Cancel",
                destructive: true);
            if (!sure) return;

            _vault.DeleteFolder(folder);
            Refresh();
            return;
        }

        string? choice = await this.ShowSheet(
            $"Delete \"{folder.Name}\"?", "Cancel", $"Delete the folder and all {inside} items",
            "Keep the items, remove the folder");

        if (choice == "Keep the items, remove the folder")
        {
            _vault.DeleteFolder(folder);
            Refresh();
            return;
        }

        if (choice is not null && choice.StartsWith("Delete the folder and", StringComparison.Ordinal))
        {
            bool sure = await this.ShowConfirm("Delete everything in it?",
                $"{inside} item(s) will be removed from the vault permanently. This cannot be undone.",
                "Delete them", "Cancel", destructive: true);

            if (!sure) return;

            _vault.DeleteFolderAndContents(folder);
            Refresh();
        }
    }

    private async Task MoveFolderAsync(PhoneFolder folder)
    {
        // Only somewhere it can legally go: a folder inside itself would orphan
        // everything below it.
        var options = new List<PhoneFolder>();
        foreach (PhoneFolder candidate in _vault.Folders)
        {
            if (candidate.Id == folder.Id) continue;
            if (candidate.Id == folder.ParentFolderId) continue;
            if (FolderTree.CanMove(_vault.Folders, folder.Id, candidate.Id)) options.Add(candidate);
        }

        var labels = new List<string>();
        if (folder.ParentFolderId is not null) labels.Add("All items (top level)");
        labels.AddRange(options.Select(f => f.Name));

        if (labels.Count == 0)
        {
            await this.ShowAlert("Nowhere to move it",
                "There is no other folder this one can go into.", "OK");
            return;
        }

        string? choice = await this.ShowSheet("Move to", "Cancel", null, labels.ToArray());
        if (choice is null) return;

        if (choice == "All items (top level)")
        {
            _vault.MoveFolder(folder, null);
            Refresh();
            return;
        }

        PhoneFolder? target = options.FirstOrDefault(f => f.Name == choice);
        if (target is null) return;

        if (!_vault.MoveFolder(folder, target.Id))
        {
            await this.ShowAlert("Cannot move it there",
                "A folder cannot be put inside itself.", "OK");
            return;
        }

        Refresh();
    }

    /// <summary>Pick a folder for an item, including the top level.</summary>
    private async Task MoveItemAsync(PhoneItem item)
    {
        var labels = new List<string>();
        if (item.FolderId is not null) labels.Add("All items (top level)");
        labels.AddRange(_vault.Folders.Where(f => f.Id != item.FolderId).Select(f => f.Name));

        if (labels.Count == 0)
        {
            await this.ShowAlert("No folders yet",
                "Make a folder first, then you can move things into it.", "OK");
            return;
        }

        string? choice = await this.ShowSheet("Move to", "Cancel", null, labels.ToArray());
        if (choice is null) return;

        if (choice == "All items (top level)")
        {
            _vault.MoveItem(item, null);
        }
        else
        {
            PhoneFolder? target = _vault.Folders.FirstOrDefault(f => f.Name == choice);
            if (target is null) return;
            _vault.MoveItem(item, target.Id);
        }

        Refresh();
    }

    private async void OnSortClicked(object sender, EventArgs e)
    {
        string current = ItemOrdering.Label(_vault.Data.SortBy);
        string direction = _vault.Data.SortDescending ? "Show oldest first" : "Show newest first";
        if (_vault.Data.SortBy != ItemSort.Added)
            direction = _vault.Data.SortDescending ? "Reverse (A to Z)" : "Reverse (Z to A)";

        // Reversing a hand-arranged order would undo the arranging, so it is not offered.
        if (_vault.Data.SortBy == ItemSort.Custom) direction = "";

        string? choice = await this.ShowSheet($"Sort by · currently {current}", "Cancel", null,
            "Date added", "Name", "Size", "Type", "My order", direction);
        // ShowSheet skips empty labels, so an unavailable direction simply does not appear.

        if (choice is null) return;

        if (direction.Length > 0 && choice == direction)
        {
            _vault.SetSort(_vault.Data.SortBy, !_vault.Data.SortDescending);
        }
        else
        {
            ItemSort sort = choice switch
            {
                "Name" => ItemSort.Name,
                "Size" => ItemSort.Size,
                "Type" => ItemSort.Kind,
                "My order" => ItemSort.Custom,
                _ => ItemSort.Added,
            };
            _vault.SetSort(sort, _vault.Data.SortDescending);
        }

        Refresh();
    }

    private async Task ItemActionsAsync(PhoneItem item)
    {
        string sendLabel = item.OfferToPc ? "Do not send to PC" : "Send to PC on next sync";
        string? action = await this.ShowSheet(
            item.Title, "Cancel", "Delete", "Details", "Move to folder", "Rename",
            "Save a copy to this phone", sendLabel);

        switch (action)
        {
            case var _ when action == sendLabel:
                item.OfferToPc = !item.OfferToPc;
                _vault.Save();
                Refresh();
                break;

            case "Details":
                await ShowDetailsAsync(item);
                break;

            case "Move to folder":
                await MoveItemAsync(item);
                break;

            case "Rename":
                string? name = await this.ShowPrompt("Rename", "New name",
                    initialValue: item.Title, maxLength: 60);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _vault.RenameItem(item, name.Trim());
                    _viewer?.Refresh();
                    Refresh();
                }
                break;

            case "Save a copy to this phone":
                await ExportAsync(item);
                break;

            case "Delete":
                bool sure = await this.ShowConfirm("Delete this item?",
                    $"\"{item.Title}\" is removed from the vault permanently.",
                    "Delete", "Cancel", destructive: true);
                if (sure)
                {
                    _vault.DeleteItem(item);
                    _thumbnails.Remove(item.Id);
                    _viewer?.Remove(item);
                    Refresh();
                }
                break;
        }
    }

    /// <summary>Everything known about an item, in one place rather than a squeezed caption.</summary>
    private async Task ShowDetailsAsync(PhoneItem item)
    {
        var lines = new List<string>
        {
            $"Name: {item.Title}",
            $"File: {item.FileName}",
            $"Type: {(item.MediaType.Length > 0 ? item.MediaType : "unknown")}",
            $"Size: {Format.Bytes(item.SizeBytes)}",
            $"Added: {item.AddedUtc.ToLocalTime():d MMM yyyy, HH:mm}",
            item.Origin == PhoneItemOrigin.ReceivedFromDevice
                ? "Origin: sent from a linked PC"
                : "Origin: added on this phone",
        };

        string folderName = item.FolderId is null
            ? "All items"
            : _vault.Folders.FirstOrDefault(f => f.Id == item.FolderId)?.Name ?? "All items";
        lines.Add($"Folder: {folderName}");

        if (item.ExpiresUtc is not null)
            lines.Add($"Expires: {item.ExpiresUtc.Value.ToLocalTime():d MMM yyyy, HH:mm}");

        if (item.OfferToPc) lines.Add("Queued to send to a PC on the next sync");
        if (item.SentToPcUtc is { } sent)
            lines.Add($"Sent to a PC: {sent.ToLocalTime():d MMM yyyy, HH:mm}");

        // Worth saying out loud on the screen that describes the item: an item added
        // here may be the only copy, which is why no automatic rule ever removes it.
        if (item.Origin == PhoneItemOrigin.CreatedHere)
            lines.Add("\nNothing automatic ever deletes this, because this phone may hold the only copy.");

        await this.ShowAlert("Details", string.Join("\n", lines), "Close");
    }

    /// <summary>
    /// Decrypt to a temp file and hand it to the system share sheet.
    ///
    /// This is the one path that deliberately puts plaintext on storage, because that is
    /// what "save a copy" means. Everything else in the app goes out of its way not to:
    /// previews and the viewer decode straight to memory. The file lands in the cache
    /// directory, which is cleared on lock and on launch, and Android removes it entirely
    /// on uninstall.
    /// </summary>
    private async Task ExportAsync(PhoneItem item)
    {
        try
        {
            byte[] data = _vault.ReadItem(item);
            Directory.CreateDirectory(MobileVaultPaths.TempDir);
            string path = Path.Combine(MobileVaultPaths.TempDir, item.FileName);
            await File.WriteAllBytesAsync(path, data);
            Array.Clear(data, 0, data.Length);

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = item.Title,
                File = new ShareFile(path),
            });
        }
        catch (Exception ex)
        {
            await this.ShowAlert("Could not export", ex.Message, "OK");
        }
    }

    private async void OnAddClicked(object sender, EventArgs e)
    {
        string? choice = await this.ShowSheet("Add to vault", "Cancel", null,
            "Photo or video", "Any file");
        if (choice is null or "Cancel") return;

        try
        {
            // The photo picker used to take exactly one item and refuse video outright,
            // which meant "Photo or video" could add neither several photos nor any video
            // at all. A file picker filtered to images and video does both.
            IEnumerable<FileResult?> picked = choice == "Photo or video"
                ? await FilePicker.Default.PickMultipleAsync(new PickOptions
                  {
                      PickerTitle = "Choose photos or videos",
                      FileTypes = new FilePickerFileType(
                          new Dictionary<DevicePlatform, IEnumerable<string>>
                          {
                              [DevicePlatform.Android] = new[] { "image/*", "video/*" },
                          }),
                  }) ?? Enumerable.Empty<FileResult>()
                : await FilePicker.Default.PickMultipleAsync() ?? Enumerable.Empty<FileResult>();

            var stored = new List<(PhoneItem Item, string? SourcePath)>();

            foreach (var file in picked)
            {
                if (file is null) continue;

                using Stream stream = await file.OpenReadAsync();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                byte[] bytes = buffer.ToArray();

                PhoneItem item = _vault.AddItem(bytes, file.FileName,
                    MediaTypes.For(file.FileName, file.ContentType),
                    Path.GetFileNameWithoutExtension(file.FileName),
                    _folderId);

                Array.Clear(bytes, 0, bytes.Length);
                stored.Add((item, file.FullPath));
            }

            if (stored.Count == 0) return;

            Refresh();
            await OfferToRemoveOriginalsAsync(stored);
        }
        catch (Exception ex)
        {
            await this.ShowAlert("Could not add", ex.Message, "OK");
        }
    }

    /// <summary>
    /// Offer to delete the phone's own copy of what was just added.
    ///
    /// A vault that protects a photo while the same photo sits in the gallery is not
    /// protecting much, so this exists. It is off unless asked for, per item rather than
    /// blanket, and it happens only after the encrypted copy has been written and read
    /// back successfully. Verifying first is not ceremony: deleting the original before
    /// confirming the vault copy is readable would turn a failed import into data loss,
    /// and this project's rule is that nothing destroys the only copy of something.
    ///
    /// Android also has the final say. From Android 11 the system asks the user itself
    /// before an app may delete something it did not create, which is why the phone shows
    /// its own confirmation after this one.
    /// </summary>
    private async Task OfferToRemoveOriginalsAsync(List<(PhoneItem Item, string? SourcePath)> stored)
    {
        var candidates = stored
            .Where(s => !string.IsNullOrEmpty(s.SourcePath) && File.Exists(s.SourcePath))
            .ToList();

        if (candidates.Count == 0) return;

        // What the picker hands back is often a copy Android made inside this app's own
        // cache, not the photo sitting in the gallery. Deleting that copy would remove
        // nothing the user cares about while looking like it had worked, so the two cases
        // are separated before anything is said to anyone.
        var real = candidates.Where(c => !MobileVaultPaths.IsOurs(c.SourcePath!)).ToList();
        var cachedOnly = candidates.Where(c => MobileVaultPaths.IsOurs(c.SourcePath!)).ToList();

        if (real.Count == 0)
        {
            // Tidy away our own scratch copies regardless: they are plaintext on disk.
            foreach ((PhoneItem _, string? temp) in cachedOnly)
            {
                try { File.Delete(temp!); } catch { /* cleared on lock and launch anyway */ }
            }

            await this.ShowAlert(
                "The phone still has its own copy",
                "Android handed Lockwell a copy of the picture rather than the original, so " +
                "the app cannot delete it from your gallery itself.\n\n" +
                "The vault copy is safe. To have only that one, delete the original from your " +
                "gallery or files app.",
                "OK");
            return;
        }

        var deletable = real;

        string what = deletable.Count == 1
            ? $"\"{deletable[0].Item.Title}\""
            : $"{deletable.Count} files";

        bool remove = await this.ShowConfirm(
            "Remove the copy on this phone?",
            $"{what} is now encrypted in the vault. The phone still has its own copy, " +
            "which any gallery or file manager can still open.\n\n" +
            "Deleting it leaves the vault holding the only copy.",
            "Delete the phone copy", "Keep both", destructive: true);

        if (!remove) return;

        int removed = 0;
        var failed = new List<string>();

        foreach ((PhoneItem item, string? path) in deletable)
        {
            // Read the encrypted copy back before touching the original. If this throws,
            // the vault copy is not trustworthy and the original must stay.
            try
            {
                byte[] check = _vault.ReadItem(item);
                bool ok = check.LongLength == item.SizeBytes;
                Array.Clear(check, 0, check.Length);

                if (!ok)
                {
                    failed.Add(item.Title);
                    continue;
                }
            }
            catch
            {
                failed.Add(item.Title);
                continue;
            }

            try
            {
                File.Delete(path!);
                removed++;
            }
            catch
            {
                // Scoped storage refuses deletion of files this app did not create unless
                // the system grants it. Report that rather than pretending it worked.
                failed.Add(item.Title);
            }
        }

        if (failed.Count > 0)
        {
            await this.ShowAlert(
                removed > 0 ? "Some copies are still on the phone" : "The phone copy is still there",
                (removed > 0 ? $"{removed} removed. " : "") +
                $"Android would not let Lockwell delete {failed.Count} of them. " +
                "They can be removed from your gallery or files app; the vault copy is safe either way.",
                "OK");
        }
        else if (removed > 0)
        {
            await this.ShowAlert("Removed from the phone",
                removed == 1
                    ? "The phone's own copy is gone. The vault now holds the only one."
                    : $"{removed} phone copies are gone. The vault now holds the only ones.",
                "OK");
        }
    }

    // ------------------------------------------------------------ devices

    private void RefreshDevices()
    {
        DeviceList.Clear();

        var devices = _trust.Devices.OrderByDescending(d => d.LastSeenUtc).ToList();

        // The empty state is a designed card in the markup rather than one assembled here,
        // so it can say something useful instead of "no PCs linked" in grey.
        NoDevicesCard.IsVisible = devices.Count == 0;

        foreach (var device in devices)
            DeviceList.Add(BuildDeviceCard(device));
    }

    private View BuildDeviceCard(LinkedPc device)
    {
        var card = new Border
        {
            BackgroundColor = Theme.Color("Bg2"),
            Stroke = Theme.Color("Stroke"),
            StrokeThickness = 1,
            Padding = new Thickness(14, 12),
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
        };

        grid.Add(new Label
        {
            Text = "\U0001F4BB",
            FontSize = 22,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 12, 0),
        }, 0);

        var labels = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        labels.Add(new Label
        {
            Text = device.Name,
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = Theme.Color("TextColor"),
        });
        labels.Add(new Label
        {
            Text = $"Linked {Format.Ago(device.LinkedUtc)} · seen {Format.Ago(device.LastSeenUtc)}",
            FontSize = 11,
            TextColor = Theme.Color("Muted"),
        });
        labels.Add(new Label
        {
            Text = device.Fingerprint,
            FontSize = 10,
            FontFamily = "Monospace",
            TextColor = Theme.Color("Faint"),
        });
        grid.Add(labels, 1);

        card.Content = grid;

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await DeviceActionsAsync(device);
        card.GestureRecognizers.Add(tap);

        return card;
    }

    private async Task DeviceActionsAsync(LinkedPc device)
    {
        string? action = await this.ShowSheet(device.Name, "Cancel", "Unlink", "Sync now", "Rename");

        switch (action)
        {
            case "Sync now":
                await SyncFromAsync(device);
                break;

            case "Rename":
                string? name = await this.ShowPrompt("Rename PC", "What should it be called?",
                    initialValue: device.Name, maxLength: 40);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    device.Name = name.Trim();
                    _trust.Touch(device, device.LastAddress);
                    RefreshDevices();
                }
                break;

            case "Unlink":
                bool sure = await this.ShowConfirm($"Unlink \"{device.Name}\"?",
                    "It will no longer be able to connect to this vault. Items already here stay " +
                    "where they are; unlinking does not reach out and delete anything.",
                    "Unlink", "Cancel");
                if (sure)
                {
                    _trust.Remove(device.Id);
                    RefreshDevices();
                }
                break;
        }
    }

    private async void OnLinkClicked(object sender, EventArgs e) =>
        await Navigation.PushAsync(new LinkPcPage(_trust, RefreshDevices));

    /// <summary>
    /// Connect to a linked PC and pull whatever it has queued for us.
    ///
    /// The phone dials out because a PC's address is stable and already stored from
    /// linking, whereas a phone's moves around. The PC only serves a device that
    /// authenticates as one it already trusts.
    /// </summary>
    private async Task SyncFromAsync(LinkedPc device)
    {
        string? address = device.LastAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            address = await this.ShowPrompt("PC address",
                "What address is your PC showing?", maxLength: 64);
            if (string.IsNullOrWhiteSpace(address)) return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var progress = new Progress<TransferProgress>(p =>
            HeaderSummary.Text = $"Receiving {p.Current}/{p.Total}: {p.Title}");

        try
        {
            HeaderSummary.Text = "Connecting...";

            using var client = new TcpClient();
            await client.ConnectAsync(address.Trim(), SyncProtocol.DefaultPort, cts.Token);
            using NetworkStream stream = client.GetStream();

            HandshakeResult handshake = await Handshake.InitiateAsync(
                stream, _trust.Identity,
                Convert.FromBase64String(device.PublicKey),
                HandshakeMode.Reconnect, cancellationToken: cts.Token);

            using var session = new SyncSession(stream, handshake);

            // --- inbound: take what the PC has queued for us ---
            TransferSummary summary = await Transfer.ReceiveAsync(
                session,
                decide: WhatToAccept,
                store: (item, contents) => StoreReceived(item, contents, device),
                progress,
                cts.Token);

            // --- outbound: offer back anything marked to send ---
            var toSend = _vault.Data.Items.Where(i => i.OfferToPc).ToList();
            var offered = toSend.Select(ToTransferItem).ToList();

            HeaderSummary.Text = offered.Count == 0 ? "Finishing..." : "Sending...";

            int sent = await Transfer.ServeAsync(
                session,
                _trust.SelfName,
                offered,
                // A fresh decrypt each time: the sender zeroes what it is handed.
                load: id => _vault.ReadItem(_vault.Data.Items.First(i => i.Id == id)),
                new Progress<TransferProgress>(p =>
                    HeaderSummary.Text = $"Sending {p.Current}/{p.Total}: {p.Title}"),
                cts.Token);

            // Sent items stop being offered, so a second sync does not re-send them.
            // Queued flag off, and a real record that it went. The first is about intent,
            // the second is what the library badge reads.
            foreach (var item in toSend)
            {
                item.OfferToPc = false;
                item.SentToPcUtc = DateTime.UtcNow;
            }

            _vault.MarkSynced();
            _trust.Touch(device, address.Trim());
            Refresh();

            string inbound = summary.Received == 0
                ? $"Nothing new to receive. {summary.Skipped} already here."
                : $"Received {summary.Received} item(s), {Format.Bytes(summary.Bytes)}.";
            string outbound = sent == 0 ? "" : $"\nSent {sent} item(s) to the PC.";

            await this.ShowAlert("Sync finished", inbound + outbound, "OK");
        }
        catch (OperationCanceledException)
        {
            await this.ShowAlert("Timed out",
                "The PC did not answer. Make sure it is waiting on the Devices screen and " +
                "both are on the same network.", "OK");
        }
        catch (SocketException)
        {
            await this.ShowAlert("Could not reach the PC",
                "Check that Lockwell is open on the PC and waiting to send.", "OK");
        }
        catch (HandshakeException ex)
        {
            await this.ShowAlert("Not verified", ex.Reason == HandshakeFailure.UnknownPeer
                ? "That PC no longer recognises this phone. Link it again."
                : "The PC could not be verified. Nothing was transferred.", "OK");
        }
        catch (Exception ex)
        {
            await this.ShowAlert("Sync failed", ex.Message, "OK");
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>
    /// Take anything we do not already hold. Matching on content hash means an item
    /// that is byte-identical to one already here is skipped without transferring it.
    /// </summary>
    private IReadOnlyList<string> WhatToAccept(IReadOnlyList<TransferItem> offered)
    {
        var have = _vault.Data.Items
            .Select(i => i.ContentHash)
            .Where(h => !string.IsNullOrEmpty(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return offered
            .Where(o => string.IsNullOrEmpty(o.ContentHash) || !have.Contains(o.ContentHash))
            .Select(o => o.Id)
            .ToList();
    }

    /// <summary>Describe one of our items for the PC, hashing its plaintext.</summary>
    private TransferItem ToTransferItem(PhoneItem item)
    {
        byte[] contents = _vault.ReadItem(item);
        try
        {
            return new TransferItem
            {
                Id = item.Id,
                Title = item.Title,
                FileName = item.FileName,
                MediaType = item.MediaType,
                SizeBytes = item.SizeBytes,
                ContentHash = Transfer.HashOf(contents),
            };
        }
        finally
        {
            Array.Clear(contents, 0, contents.Length);
        }
    }

    private void StoreReceived(TransferItem item, byte[] contents, LinkedPc from)
    {
        // Re-encrypted with this vault's own key. The PC's key never crossed the wire.
        var stored = _vault.AddItem(contents, item.FileName, item.MediaType, item.Title);

        stored.Origin = PhoneItemOrigin.ReceivedFromDevice;
        stored.FromDeviceFingerprint = from.Fingerprint;
        stored.ContentHash = item.ContentHash;
        stored.ExpiresUtc = item.ExpiresUtc;
        _vault.Save();
    }

    // ----------------------------------------------------------- settings

    /// <summary>
    /// Guards the biometric switch while its state is being set from code, so putting the
    /// toggle where it belongs does not read as the user having flipped it.
    /// </summary>
    private bool _settingBiometricSwitch;

    private void RefreshSettings()
    {
        bool on = _biometrics?.IsEnabledFor(_vault.BiometricKeyFile) ?? false;

        _settingBiometricSwitch = true;
        BiometricSwitch.IsToggled = on;
        _settingBiometricSwitch = false;

        BiometricSwitch.IsEnabled = _biometrics is not null;
        BiometricNote.Text = _biometrics is null
            ? "This phone has no fingerprint or face unlock available."
            : "Unlocks a key held by this phone's secure hardware. Your password still " +
              "works and is never stored.";

        RenameValue.Text = _profile.Name;
        StorageUsed.Text = Format.Bytes(_library.SizeOf(_profile));

        // The row says what it does; the value on the right says what it is set to. A
        // button whose label was the whole sentence made every setting look like an action.
        AutoLockValue.Text = _vault.Data.LockAfterSecondsAway switch
        {
            < 0 => "Never",
            0 => "Immediately",
            < 60 => $"{_vault.Data.LockAfterSecondsAway}s",
            _ => $"{_vault.Data.LockAfterSecondsAway / 60} min",
        };

        StaleWipeSwitch.IsToggled = _vault.Data.WipeReceivedIfStale;
        StaleWindowRow.IsVisible = _vault.Data.WipeReceivedIfStale;
        StaleWindowDivider.IsVisible = _vault.Data.WipeReceivedIfStale;
        StaleWindowValue.Text = $"{_vault.Data.StaleAfterDays} days";

        LastSyncLabel.Text = _vault.Data.LastSyncUtc is null
            ? "Never synced with a PC."
            : $"Last synced {Format.Ago(_vault.Data.LastSyncUtc.Value)}.";
    }

    /// <summary>
    /// The biometric row is a switch now rather than a button whose label flipped. Turning
    /// it on prompts; turning it off just forgets the wrapped key.
    /// </summary>
    private async void OnBiometricSwitchToggled(object sender, ToggledEventArgs e)
    {
        if (_settingBiometricSwitch) return;

        await ToggleBiometricsAsync();

        // Whatever actually happened, the switch is put back in step with reality: the
        // prompt may have been cancelled, or the hardware may have refused.
        RefreshSettings();
    }

    private void OnStaleWipeToggled(object sender, ToggledEventArgs e)
    {
        if (_vault.Data.WipeReceivedIfStale == e.Value) return;

        _vault.Data.WipeReceivedIfStale = e.Value;
        _vault.Save();
        RefreshSettings();
    }

    private async void OnStaleWindowClicked(object sender, EventArgs e)
    {
        string? choice = await this.ShowSheet("Wipe after how long without a sync?",
            "Cancel", null, "7 days", "14 days", "30 days", "90 days");

        if (choice is null or "Cancel") return;

        _vault.Data.StaleAfterDays = int.Parse(choice.Split(' ')[0]);
        _vault.Save();
        RefreshSettings();
    }

    /// <summary>
    /// How long the app may sit in the background before this vault locks.
    ///
    /// "Immediately" is offered but not the default: unlocking runs Argon2id at 256 MiB,
    /// which is what makes a stolen vault expensive and also what would make every glance
    /// at a notification cost several seconds. Anyone using fingerprint unlock skips that
    /// derivation and can afford the strictest setting comfortably.
    /// </summary>
    private async void OnAutoLockClicked(object sender, EventArgs e)
    {
        string? choice = await this.ShowSheet("Lock when you leave the app?", "Cancel", null,
            "Immediately", "After 30 seconds", "After 1 minute", "After 5 minutes", "Never");

        if (choice is null or "Cancel") return;

        _vault.Data.LockAfterSecondsAway = choice switch
        {
            "Immediately" => 0,
            "After 30 seconds" => 30,
            "After 1 minute" => 60,
            "After 5 minutes" => 300,
            "Never" => -1,
            _ => 60,
        };

        _vault.Save();
        AppLock.Grace = GraceFromSettings();
        RefreshSettings();
    }

    private async Task ToggleBiometricsAsync()
    {
        if (_biometrics is null) return;

        if (_biometrics.IsEnabledFor(_vault.BiometricKeyFile))
        {
            bool off = await this.ShowConfirm("Turn off fingerprint unlock?",
                "You will need your password every time. The vault itself is unaffected.",
                "Turn off", "Cancel");
            if (off)
            {
                _biometrics.Disable(_profile.Id, _vault.BiometricKeyFile);
                RefreshSettings();
            }
            return;
        }

        if (_biometrics.Check() != BiometricAvailability.Available)
        {
            await this.ShowAlert("Not available",
                "This phone has no fingerprint or face unlock set up yet.", "OK");
            return;
        }

        try
        {
            await _biometrics.EnableAsync(
                _profile.Id, _vault.BiometricKeyFile, _vault.ExportDataKeyForBiometricWrap());
            RefreshSettings();
        }
        catch (Exception ex)
        {
            await this.ShowAlert("Could not enable it", ex.Message, "OK");
        }
    }

    private async void OnChangePasswordClicked(object sender, EventArgs e)
    {
        string? next = await this.ShowPrompt("Change password",
            "New password, at least 10 characters", maxLength: 128);
        if (string.IsNullOrEmpty(next)) return;

        if (next.Length < 10)
        {
            await this.ShowAlert("Too short", "Use at least 10 characters.", "OK");
            return;
        }

        string? confirm = await this.ShowPrompt("Confirm", "Type it again", maxLength: 128);
        if (confirm != next)
        {
            await this.ShowAlert("Did not match", "Nothing was changed.", "OK");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using var secure = PhoneVault.ToSecure(next);
                _vault.ChangePassword(secure);
            });

            // The old wrapped key was tied to the old password, so it must go.
            if (_biometrics?.IsEnabledFor(_vault.BiometricKeyFile) == true)
            {
                _biometrics.Disable(_profile.Id, _vault.BiometricKeyFile);
                await this.ShowAlert("Password changed",
                    "Fingerprint unlock was turned off because it was tied to the old password. " +
                    "You can switch it back on from Settings.", "OK");
                RefreshSettings();
            }
            else
            {
                await this.ShowAlert("Password changed", "Your new password is active.", "OK");
            }
        }
        catch (Exception ex)
        {
            await this.ShowAlert("Could not change it", ex.Message, "OK");
        }
    }

    private async void OnRenameClicked(object sender, EventArgs e)
    {
        string? name = await this.ShowPrompt("Rename vault", "What should it be called?",
            initialValue: _profile.Name, maxLength: 40);
        if (string.IsNullOrWhiteSpace(name)) return;

        _library.Rename(_profile.Id, name.Trim());
        _profile.Name = name.Trim();
        HeaderName.Text = _profile.Name;
    }

    private async void OnDeleteVaultClicked(object sender, EventArgs e)
    {
        bool sure = await this.ShowConfirm($"Delete \"{_profile.Name}\"?",
            "Everything in this vault is deleted permanently. Without the password it was " +
            "unreadable anyway, so there is nothing to recover afterwards.",
            "Continue", "Cancel");
        if (!sure) return;

        string? typed = await this.ShowPrompt("Type the vault name",
            $"To confirm, type: {_profile.Name}",
            accept: "Delete forever", cancel: "Cancel", maxLength: 60);

        if (typed?.Trim() != _profile.Name)
        {
            if (typed is not null)
                await this.ShowAlert("Not deleted", "That did not match, so nothing was changed.", "OK");
            return;
        }

        _vault.Lock();
        if (_biometrics is not null) _biometrics.Disable(_profile.Id, _vault.BiometricKeyFile);
        _library.Delete(_profile.Id);

        await Navigation.PopToRootAsync();
    }

    // -------------------------------------------------------------- lock

    private async void OnLockClicked(object sender, EventArgs e)
    {
        LockDown();
        await Navigation.PopToRootAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        // A viewer open over the page takes the back press first, so backing out of a
        // photo returns to the grid rather than closing the whole vault.
        if (_viewer is not null)
        {
            _viewer.Close();
            _viewer = null;
            return true;
        }

        // Leaving the page must lock, or the vault stays open behind the list.
        LockDown();
        return base.OnBackButtonPressed();
    }

    /// <summary>
    /// Lock, and leave nothing decoded behind.
    ///
    /// The thumbnails in this page are decrypted pictures held in memory. They never
    /// reach storage, but a locked vault whose previews are still in memory is not really
    /// locked, so they go at the same moment the key does. Same discipline the temp-file
    /// path already follows on lock and on launch.
    /// </summary>
    private void LockDown()
    {
        AppLock.ShouldLock -= OnShouldLock;
        AppLock.Reset();

        _viewer?.Close();
        _viewer = null;

        _thumbnails.Clear();
        MobileVaultPaths.PurgeTemp();
        _vault.Lock();
    }
}
