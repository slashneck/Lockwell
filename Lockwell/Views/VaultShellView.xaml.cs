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

using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Lockwell.Crypto;
using Lockwell.Helpers;
using Lockwell.Models;
using Lockwell.Sync;
using Lockwell.Services;
using WpfAnimatedGif;

namespace Lockwell.Views;

public partial class VaultShellView : UserControl
{
    private readonly VaultManager _vault;
    private readonly AppSettings _settings;
    private readonly ProfileManager _profiles;
    private readonly Action _onLock;
    private readonly Action _applySettings;
    private readonly Action _onSwitchProfile;

    private const string AllId = "__all__";
    private const string FavId = "__fav__";
    private const string MediaFilter =
        "Media files|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac;*.wma|All files|*.*";
    private const double OverlayDefaultMaxWidth = 560;

    private readonly ObservableCollection<CategoryRow> _sidebar = new();
    private CategoryRow? _currentCategory;
    private VaultEntry? _currentEntry;
    private bool _loaded;
    private bool _mediaMode;
    private string? _currentMediaFolderId;
    private Border? _mediaDropHighlight;
    private readonly List<ViewerMediaItem> _viewerPlaylist = new();
    private int _viewerIndex = -1;
    private string? _viewerFolderId;
    private AttachmentRef? _viewerCurrentAttachment;
    private VaultTempFiles.ScratchFile? _mediaScratch;

    /// <summary>
    /// A fixed frame rate chosen by the user, or null to use each frame's own delay.
    /// Kept for the session rather than saved: it is a way of looking at one file,
    /// not a property of it.
    /// </summary>
    /// <summary>
    /// Frames per second for animated images, or null to follow the file's own timings.
    ///
    /// Backed by settings rather than kept as plain state, because the rate belongs to how
    /// someone likes to watch things and not to one picture. It used to reset on every
    /// image, so choosing a speed and then moving to the next GIF put it straight back.
    /// </summary>
    private double? _animationFps
    {
        get => _settings.AnimationFramesPerSecond >= 0
            ? _settings.AnimationFramesPerSecond
            : null;
        set
        {
            // Negative is the stored form of "follow the file", since the setting is a
            // single number and null has nowhere to live in the file.
            _settings.AnimationFramesPerSecond = value is { } fps ? fps : -1;
            _settings.Save();
        }
    }

    /// <summary>Guards against converting the same file more than once per open.</summary>
    private bool _mediaConversionTried;
    private string? _viewerImageTempPath;
    private bool _mediaSeekDragging;
    private double? _mediaSeekPendingValue;
    private bool _mediaPlaying;
    private double _mediaVolume = 1.0;
    private bool _viewerClosing;
    private bool _overlayClosing;

    /// <summary>Tile actions stay faintly visible so users know they are there.</summary>
    private const double RestingActionOpacity = 0.45;
    private readonly DispatcherTimer _mediaTimer;

    private Action? _overlaySave;
    private readonly List<FieldEditor> _fieldEditors = new();
    private readonly List<string> _folderEditDeleteIds = new();
    private StackPanel? _fieldsHost;
    private StackPanel? _folderItemHost;

    public VaultShellView(
        VaultManager vault,
        AppSettings settings,
        ProfileManager profiles,
        Action onLock,
        Action applySettings,
        Action onSwitchProfile)
    {
        InitializeComponent();
        _vault = vault;
        _settings = settings;
        _profiles = profiles;
        _onLock = onLock;
        _applySettings = applySettings;
        _onSwitchProfile = onSwitchProfile;

        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mediaTimer.Tick += (_, _) => UpdateMediaSeekUi();

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            VaultTempFiles.PurgeAll();
            ClearThumbnailCache();
        };
        HookMediaDropTargets();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshCategories();
        UpdateStats();
        _loaded = true;

        // Land on Home rather than dropping straight into a list of everything.
        ShowHome();

        Anim.PopIn(SidebarPanel, 320);
        Anim.SlideFadeIn(MainWorkspace, 360, 20, 80);

        // Deliberately last, deliberately not awaited, and it waits several seconds of its
        // own before touching the network. Whether a newer build exists is the least
        // urgent thing that happens on this screen.
        MaybeCheckForUpdatesAsync();
    }

    // ----------------------------------------------------- categories

    /// <summary>Sentinel id for the divider above user-made sections.</summary>
    private const string CustomHeaderId = "__custom_header__";

    private bool _customSectionsCollapsed;

    private void RefreshCategories()
    {
        string? keepId = _currentCategory?.Category.Id;

        _sidebar.Clear();
        _sidebar.Add(MakeRow(new Category { Id = AllId, Name = "All items", Glyph = "\uE71D" }));
        _sidebar.Add(MakeRow(new Category { Id = FavId, Name = "Favorites", Glyph = "\uE735" }));

        var ordered = _vault.Data.Categories.OrderBy(c => c.Order).ThenBy(c => c.Name).ToList();

        // The sections Lockwell ships with come first and always show. Sections the user
        // added go below a divider that folds, so a vault with a dozen of them does not
        // bury the handful that are always there.
        foreach (Category c in ordered.Where(c => !c.IsCustom))
            _sidebar.Add(MakeRow(c));

        var custom = ordered.Where(c => c.IsCustom).ToList();
        if (custom.Count > 0)
        {
            _sidebar.Add(new CategoryRow(
                new Category { Id = CustomHeaderId, Name = "Your sections", Glyph = "\uE8B7" },
                custom.Count)
            {
                IsHeader = true,
                IsCollapsed = _customSectionsCollapsed,
            });

            if (!_customSectionsCollapsed)
            {
                foreach (Category c in custom) _sidebar.Add(MakeRow(c));
            }
        }

        CategoryList.ItemsSource = _sidebar;

        if (keepId is not null)
        {
            var match = _sidebar.FirstOrDefault(r => r.Category.Id == keepId);
            if (match is not null)
            {
                CategoryList.SelectedItem = match;
                return;
            }
        }

        // Only fall back to the first section when we are actually browsing a section.
        // Home and Storage deliberately have no selection, and auto-selecting one here
        // would silently navigate the user to "All items" the next time anything
        // refreshed the sidebar -- compressing, deleting, renaming.
        if (_loaded && !_homeMode && !_storageMode && !_devicesMode &&
            CategoryList.SelectedItem is null && CategoryList.Items.Count > 0)
        {
            CategoryList.SelectedIndex = 0;
        }
    }

    // ----------------------------------------------------------------- home

    private bool _homeMode;

    private void HomeButton_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowHome();

    private void HomeExportBackup_Click(object sender, RoutedEventArgs e) => ExportBackup();

    private void ShowHome()
    {
        _homeMode = true;
        _storageMode = false;
        _devicesMode = false;
        _currentCategory = null;
        _mediaMode = false;
        _currentMediaFolderId = null;

        CategoryList.SelectedItem = null;
        ListColumn.Visibility = Visibility.Collapsed;
        DetailColumn.Visibility = Visibility.Collapsed;
        MediaPanel.Visibility = Visibility.Collapsed;
        StoragePanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Collapsed;

        RefreshHome();

        HomePanel.Visibility = Visibility.Visible;
        Anim.SlideFadeIn(HomePanel, 340, 18);
    }

    private void RefreshHome()
    {
        if (!_vault.IsUnlocked) return;

        HomeVaultName.Text = _profiles.ActiveProfile?.Name ?? "Vault";

        var mediaCategoryIds = _vault.Data.Categories
            .Where(c => c.IsMedia)
            .Select(c => c.Id)
            .ToHashSet();

        var mediaEntries = _vault.Data.Entries.Where(e => mediaCategoryIds.Contains(e.CategoryId)).ToList();
        int logins = _vault.Data.Entries.Count - mediaEntries.Count;
        int folders = _vault.Data.MediaFolders.Count;
        int sections = _vault.Data.Categories.Count;

        HomeItemCount.Text = logins.ToString("N0");
        HomeItemDetail.Text = sections == 1 ? "in 1 section" : $"across {sections} sections";

        HomeMediaCount.Text = mediaEntries.Count.ToString("N0");
        HomeMediaDetail.Text = folders == 1 ? "in 1 folder" : $"in {folders} folders";

        HomeVaultSize.Text = FormatBytes(_vault.CalculateDiskSizeBytes());

        UpdateBackupStatus();
    }

    /// <summary>
    /// The vault's own encrypted timestamp is authoritative. Older vaults only have the
    /// plaintext one recorded in settings.json, so fall back to it rather than losing
    /// that history -- but nothing writes a new plaintext timestamp any more.
    /// </summary>
    private DateTime? ResolveLastBackupUtc() =>
        (_vault.IsUnlocked ? _vault.Data.LastExportUtc : null) ?? _profiles.ActiveProfile?.LastBackupUtc;

    private void UpdateBackupStatus()
    {
        DateTime? last = ResolveLastBackupUtc();

        if (last is null)
        {
            HomeBackupTitle.Text = "No backup yet";
            // A backup does NOT protect against a forgotten password: the backup ZIP is
            // encrypted with that same password. Only the recovery key covers that case.
            // What a backup protects against is losing the drive or the file itself.
            HomeBackupDetail.Text = "A backup protects against drive failure or a lost PC. It stays encrypted.";
            HomeBackupIcon.Text = "\uE7BA";
            HomeBackupIcon.Foreground = (Brush)FindResource("Warning");
            return;
        }

        var local = last.Value.ToLocalTime();
        int days = (int)(DateTime.UtcNow - last.Value).TotalDays;

        HomeBackupTitle.Text = "Last backup";
        HomeBackupDetail.Text = days switch
        {
            <= 0 => $"Today, {local:HH:mm}",
            1 => $"Yesterday, {local:HH:mm}",
            < 30 => $"{days} days ago  ·  {local:d MMM yyyy}",
            _ => $"{local:d MMM yyyy}  ·  worth making a fresh one",
        };

        // Nudge, not nag: only turn amber once it is genuinely stale.
        bool stale = days >= 30;
        HomeBackupIcon.Text = stale ? "\uE7BA" : "\uE73E";
        HomeBackupIcon.Foreground = stale
            ? (Brush)FindResource("Warning")
            : (Brush)FindResource("Accent");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }

    private CategoryRow MakeRow(Category c)
    {
        int count = c.Id switch
        {
            AllId => CountAllItemsListRows(),
            FavId => _vault.Data.Entries.Count(e => e.Favorite),
            _ when c.IsMedia => CountMediaCategoryItems(c.Id),
            _ => _vault.Data.Entries.Count(e => e.CategoryId == c.Id),
        };
        return new CategoryRow(c, count);
    }

    private int CountAllItemsListRows()
    {
        int entries = _vault.Data.Entries.Count(e => string.IsNullOrEmpty(e.MediaFolderId));
        int folders = VisibleMediaFolders(forAllItems: true).Count();
        return entries + folders;
    }

    private int CountMediaCategoryItems(string categoryId)
    {
        int files = _vault.Data.Entries.Count(e => e.CategoryId == categoryId);
        int folders = _vault.Data.MediaFolders.Count(f => f.CategoryId == categoryId);
        return files + folders;
    }

    private int CountHiddenNsfwRootFolders(string categoryId)
    {
        if (!_settings.HideNsfwMediaFolders)
            return 0;

        return _vault.Data.MediaFolders.Count(f =>
            f.CategoryId == categoryId &&
            string.IsNullOrEmpty(f.ParentFolderId) &&
            f.IsNsfw &&
            (FolderTreeHasContent(f.Id) || _vault.Data.Entries.Any(e => e.MediaFolderId == f.Id)));
    }

    private IEnumerable<MediaFolder> VisibleMediaFolders(
        string? categoryId = null,
        string? parentFolderId = null,
        bool forAllItems = false)
    {
        IEnumerable<MediaFolder> query = _vault.Data.MediaFolders;
        if (categoryId is not null)
            query = query.Where(f => f.CategoryId == categoryId);

        if (forAllItems)
            query = query.Where(f => string.IsNullOrEmpty(f.ParentFolderId) && !f.IsNsfw);
        else
        {
            query = query.Where(f => (f.ParentFolderId ?? "") == (parentFolderId ?? ""));
            if (_settings.HideNsfwMediaFolders)
                query = query.Where(f => !f.IsNsfw);
        }

        return query;
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var row = CategoryList.SelectedItem as CategoryRow;
        if (row is null) return;

        // The divider is a row in the same list so selection stays in one place. Clicking
        // it folds the group rather than navigating, and the selection is put back where
        // it was so the header never looks like the section you are in.
        if (row.IsHeader)
        {
            _customSectionsCollapsed = !_customSectionsCollapsed;

            string? restore = _currentCategory?.Category.Id;
            RefreshCategories();

            CategoryList.SelectedItem = restore is null
                ? null
                : _sidebar.FirstOrDefault(r => r.Category.Id == restore);
            return;
        }

        // Picking a section leaves Home / Storage.
        _homeMode = false;
        _storageMode = false;
        _devicesMode = false;
        HomePanel.Visibility = Visibility.Collapsed;
        StoragePanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Collapsed;

        bool categoryChanged = _currentCategory?.Category.Id != row.Category.Id;
        _currentCategory = row;
        _mediaMode = IsMediaCategory(_currentCategory);

        if (_mediaMode)
        {
            if (categoryChanged) _currentMediaFolderId = null;
            ShowMediaMode();
        }
        else
        {
            ShowStandardMode();
            ListTitle.Text = _currentCategory?.Category.Name ?? "All items";
            ListSubtitle.Text = _currentCategory?.Category.Id == FavId
                ? "Starred entries"
                : "Browse your vault";
            RefreshEntries();
        }
    }

    private bool IsMediaCategory(CategoryRow? row)
    {
        if (row is null || row.Category.Id is AllId or FavId) return false;
        return row.Category.IsMedia;
    }

    private Category? ResolveMediaCategory()
    {
        if (_currentCategory is not null && _currentCategory.Category.IsMedia)
            return _currentCategory.Category;
        return _vault.Data.Categories.FirstOrDefault(c => c.IsMedia);
    }

    private void ShowMediaMode()
    {
        ListColumn.Visibility = Visibility.Collapsed;
        DetailColumn.Visibility = Visibility.Collapsed;
        MediaPanel.Visibility = Visibility.Visible;
        MediaPanel.Opacity = 0;
        Anim.FadeIn(MediaPanel, 280);
        MediaTitle.Text = _currentCategory?.Category.Name ?? "Media vault";
        RefreshMediaGallery();
    }

    private void MediaBreadcrumbNavigate(string? folderId)
    {
        _currentMediaFolderId = folderId;
        RefreshMediaGallery();
    }

    private void OpenMediaFolder(string folderId)
    {
        MediaSearchBox.Text = "";
        _currentMediaFolderId = folderId;
        RefreshMediaGallery();
    }

    private void ShowStandardMode()
    {
        MediaPanel.Visibility = Visibility.Collapsed;
        ListColumn.Visibility = Visibility.Visible;
        DetailColumn.Visibility = Visibility.Visible;
        ClearDetail();
    }

    // ----------------------------------------------------- entries

    private void RefreshEntries()
    {
        string query = (SearchBox.Text ?? "").Trim();
        SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool allItems = _currentCategory is { Category.Id: AllId };
        IEnumerable<VaultEntry> entries = _vault.Data.Entries;

        if (_currentCategory is { Category.Id: FavId })
            entries = entries.Where(en => en.Favorite);
        else if (_currentCategory is not null && !allItems)
            entries = entries.Where(en => en.CategoryId == _currentCategory.Category.Id);

        if (allItems)
            entries = entries.Where(en => string.IsNullOrEmpty(en.MediaFolderId));

        if (query.Length > 0)
            entries = entries.Where(en => MatchesQuery(en, query));

        var rows = new List<object>();
        rows.AddRange(entries
            .OrderByDescending(en => en.Favorite)
            .ThenBy(en => en.Title, StringComparer.OrdinalIgnoreCase)
            .Select(en => new EntryRow(en)));

        if (allItems)
        {
            var folderRows = VisibleMediaFolders(forAllItems: true)
                .Select(f =>
                {
                    var cat = _vault.Data.Categories.FirstOrDefault(c => c.Id == f.CategoryId);
                    int fileCount = CountFilesInFolderTree(f.Id);
                    return new FolderListRow(f, cat, fileCount);
                })
                .Where(r => query.Length == 0 || r.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Folder.IsFavorite)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase);

            rows.AddRange(folderRows);
            rows = rows
                .OrderByDescending(r => r is EntryRow er && er.Entry.Favorite || r is FolderListRow fr && fr.Folder.IsFavorite)
                .ThenBy(r => r is EntryRow er ? er.Title : ((FolderListRow)r).Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        EntryList.ItemsSource = rows;

        if (_currentEntry is not null && rows.OfType<EntryRow>().All(r => r.Entry.Id != _currentEntry.Id))
            ClearDetail();
    }

    private static bool MatchesQuery(VaultEntry e, string q)
    {
        if (e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        return e.Fields.Any(f => !f.IsSecret && f.Value.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshEntries();
        if (_mediaMode) RefreshMediaGallery();
    }

    /// <summary>
    /// Search is debounced: each refresh rebuilds every tile, so firing on every
    /// keystroke made typing rebuild the gallery once per character.
    /// </summary>
    private DispatcherTimer? _mediaSearchDebounce;

    private void MediaSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _mediaSearchDebounce ??= CreateMediaSearchDebounce();
        _mediaSearchDebounce.Stop();
        _mediaSearchDebounce.Start();
    }

    private DispatcherTimer CreateMediaSearchDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshMediaGallery();
        };
        return timer;
    }

    // ---------------------------------------------------------- media sorting

    private void MediaSortMode_Click(object sender, RoutedEventArgs e)
    {
        // Cycle through the modes; a menu would be more clicks for four options.
        _settings.MediaSortMode = _settings.MediaSortMode switch
        {
            MediaSortMode.Custom => MediaSortMode.Name,
            MediaSortMode.Name => MediaSortMode.Date,
            MediaSortMode.Date => MediaSortMode.Size,
            _ => MediaSortMode.Custom,
        };
        _settings.Save();
        UpdateMediaSortUi();
        RefreshMediaGallery();
    }

    private void MediaSortDirection_Click(object sender, RoutedEventArgs e)
    {
        _settings.MediaSortDescending = !_settings.MediaSortDescending;
        _settings.Save();
        UpdateMediaSortUi();
        RefreshMediaGallery();
    }

    private void UpdateMediaSortUi()
    {
        MediaSortModeButton.Content = _settings.MediaSortMode switch
        {
            MediaSortMode.Name => "Name",
            MediaSortMode.Date => "Age",
            MediaSortMode.Size => "Size",
            _ => "Custom",
        };

        // Direction is meaningless for a hand-arranged order.
        bool sortable = _settings.MediaSortMode != MediaSortMode.Custom;
        MediaSortDirectionButton.IsEnabled = sortable;
        MediaSortDirectionButton.Opacity = sortable ? 1 : 0.4;
        MediaSortDirectionButton.Content = _settings.MediaSortDescending ? "\uE74B" : "\uE74A";
        MediaSortDirectionButton.ToolTip = _settings.MediaSortMode switch
        {
            MediaSortMode.Name => _settings.MediaSortDescending ? "Z to A" : "A to Z",
            MediaSortMode.Date => _settings.MediaSortDescending ? "Oldest first" : "Newest first",
            MediaSortMode.Size => _settings.MediaSortDescending ? "Smallest first" : "Largest first",
            _ => "Drag tiles to arrange them",
        };
    }

    /// <summary>Favourites always float to the top, then the chosen sort applies.</summary>
    private List<MediaFolder> SortFolders(IEnumerable<MediaFolder> folders)
    {
        var ordered = folders.OrderByDescending(f => f.IsFavorite);
        bool desc = _settings.MediaSortDescending;

        return _settings.MediaSortMode switch
        {
            MediaSortMode.Name => (desc
                    ? ordered.ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    : ordered.ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                .ToList(),

            MediaSortMode.Date => (desc
                    ? ordered.ThenBy(f => f.CreatedUtc)
                    : ordered.ThenByDescending(f => f.CreatedUtc))
                .ToList(),

            MediaSortMode.Size => (desc
                    ? ordered.ThenBy(f => FolderTreeSizeBytes(f.Id))
                    : ordered.ThenByDescending(f => FolderTreeSizeBytes(f.Id)))
                .ToList(),

            _ => ordered
                .ThenBy(f => f.Order)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private List<VaultEntry> SortMediaEntries(IEnumerable<VaultEntry> entries)
    {
        bool desc = _settings.MediaSortDescending;

        return _settings.MediaSortMode switch
        {
            MediaSortMode.Name => (desc
                    ? entries.OrderByDescending(e => e.Title, StringComparer.OrdinalIgnoreCase)
                    : entries.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase))
                .ToList(),

            MediaSortMode.Date => (desc
                    ? entries.OrderBy(e => e.CreatedUtc)
                    : entries.OrderByDescending(e => e.CreatedUtc))
                .ToList(),

            MediaSortMode.Size => (desc
                    ? entries.OrderBy(EntrySizeBytes)
                    : entries.OrderByDescending(EntrySizeBytes))
                .ToList(),

            _ => entries
                .OrderBy(e => e.MediaSortOrder)
                .ThenByDescending(e => e.ModifiedUtc)
                .ToList(),
        };
    }

    private static long EntrySizeBytes(VaultEntry entry) =>
        entry.Attachments.Sum(a => a.SizeBytes);

    /// <summary>Total bytes in a folder including everything nested beneath it.</summary>
    private long FolderTreeSizeBytes(string folderId)
    {
        long total = _vault.Data.Entries
            .Where(e => e.MediaFolderId == folderId)
            .Sum(EntrySizeBytes);

        foreach (var sub in _vault.Data.MediaFolders.Where(f => f.ParentFolderId == folderId))
            total += FolderTreeSizeBytes(sub.Id);

        return total;
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is FolderListRow folderRow)
        {
            ClearDetail();
            NavigateToMediaFolder(folderRow.Category?.Id ?? folderRow.Folder.CategoryId, folderRow.Folder.Id);
            EntryList.SelectedItem = null;
            return;
        }

        if (EntryList.SelectedItem is EntryRow row) ShowDetail(row.Entry);
    }

    private void NavigateToMediaFolder(string categoryId, string folderId)
    {
        var row = _sidebar.FirstOrDefault(r => r.Category.Id == categoryId);
        if (row is null) return;

        CategoryList.SelectedItem = row;
        _currentMediaFolderId = folderId;
        RefreshMediaGallery();
    }

    /// <summary>
    /// The sidebar ENTRIES figure: everything the vault holds, media files included.
    ///
    /// This used to have two writers that disagreed. RefreshEntries overwrote it with the
    /// number of rows in the All items list -- loose entries plus visible root folders --
    /// so the same labelled number changed as you moved between sections, and whichever
    /// ran last won. It also duplicated the count already shown on the All items row two
    /// lines below it. One writer now, and it means what it says: it is the sum of the
    /// ITEMS and MEDIA figures on the home screen.
    /// </summary>
    private void UpdateStats() => StatEntries.Text = _vault.Data.Entries.Count.ToString("N0");

    // ----------------------------------------------------- detail

    private void ClearDetail()
    {
        _currentEntry = null;
        EmptyState.Visibility = Visibility.Visible;
        DetailScroller.Visibility = Visibility.Collapsed;
    }

    private void ShowDetail(VaultEntry entry)
    {
        _currentEntry = entry;
        EmptyState.Visibility = Visibility.Collapsed;
        DetailScroller.Visibility = Visibility.Visible;

        DetailTitle.Text = entry.Title;
        DetailKind.Text = KindLabel(entry.Kind);
        DetailGlyph.Text = GlyphForKind(entry.Kind);
        FavButton.Content = entry.Favorite ? "\uE735" : "\uE734";
        FavButton.Foreground = entry.Favorite
            ? (Brush)FindResource("Warning")
            : (Brush)FindResource("Muted");

        FieldsPanel.Children.Clear();
        foreach (var field in entry.Fields)
            FieldsPanel.Children.Add(BuildFieldRow(field));

        bool hasNotes = !string.IsNullOrWhiteSpace(entry.Notes);
        NotesHeader.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        NotesCard.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        NotesText.Text = entry.Notes;

        RefreshAttachments();
        Anim.PopIn(DetailCard, 260);
        Anim.StaggerIn(FieldsPanel, 40, 260, 10);
    }

    /// <summary>The one field label that gets the numbered word grid rather than a line of text.</summary>
    private const string SeedPhraseLabel = "Seed phrase";

    private static bool IsSeedPhrase(EntryField field) =>
        field.Label.Equals(SeedPhraseLabel, StringComparison.OrdinalIgnoreCase) ||
        field.Label.Contains("recovery phrase", StringComparison.OrdinalIgnoreCase) ||
        field.Label.Contains("mnemonic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A seed phrase, shown the way people actually check one: numbered words in a grid.
    ///
    /// Written out as a single line it is unreadable and unverifiable. Nobody can confirm
    /// word nine of twenty-four in a wall of text, and the whole reason to open a seed
    /// phrase is to read it against something else. So the words are numbered and laid out,
    /// which is also how every wallet presents them when you write one down.
    ///
    /// Hidden until asked for, and hidden again when the entry is left. This is the single
    /// most valuable thing the vault can hold: anyone who reads it owns the coins, with no
    /// password to change afterwards and nothing to revoke.
    /// </summary>
    private UIElement BuildSeedPhraseRow(EntryField field)
    {
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Background = (Brush)FindResource("Bg2"),
            Padding = new Thickness(16, 12, 12, 12),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var outer = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string[] words = SplitSeedWords(field.Value);

        var labels = new StackPanel();
        labels.Children.Add(new TextBlock
        {
            Text = field.Label.ToUpperInvariant(),
            Style = (Style)FindResource("Overline"),
            FontSize = 10,
        });
        labels.Children.Add(new TextBlock
        {
            Text = words.Length == 0 ? "Not set" : $"{words.Length} words \u00B7 hidden",
            Style = (Style)FindResource("Body"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        header.Children.Add(labels);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        bool revealed = false;

        var eye = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "\uE7B3",
            ToolTip = "Show / hide the words",
        };
        eye.Click += (_, _) =>
        {
            revealed = !revealed;
            grid.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
            eye.Content = revealed ? "\uED1A" : "\uE7B3";

            ((TextBlock)labels.Children[1]).Text = words.Length == 0
                ? "Not set"
                : revealed ? $"{words.Length} words" : $"{words.Length} words \u00B7 hidden";
        };
        if (words.Length > 0) actions.Children.Add(eye);

        if (!string.IsNullOrEmpty(field.Value))
        {
            var copy = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = "\uE8C8",
                ToolTip = "Copy the whole phrase",
            };
            copy.Click += (_, _) =>
            {
                ClipboardHelper.CopySecret(field.Value, _settings.ClipboardClearSeconds);
                copy.Content = "\uE73E";
                copy.Foreground = (Brush)FindResource("Accent");
            };
            actions.Children.Add(copy);
        }

        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        outer.Children.Add(header);

        // Three columns, which is how a twelve or twenty-four word phrase reads most
        // naturally and matches how wallets print them.
        const int columns = 3;
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int rows = (words.Length + columns - 1) / columns;
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < words.Length; i++)
        {
            var cell = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 10, 8),
            };

            cell.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = (Brush)FindResource("Faint"),
                FontSize = 11,
                MinWidth = 20,
                VerticalAlignment = VerticalAlignment.Center,
            });

            cell.Children.Add(new TextBlock
            {
                Text = words[i],
                Foreground = (Brush)FindResource("Text"),
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            Grid.SetColumn(cell, i % columns);
            Grid.SetRow(cell, i / columns);
            grid.Children.Add(cell);
        }

        outer.Children.Add(grid);
        card.Child = outer;
        return card;
    }

    /// <summary>
    /// Split a phrase into words, accepting whatever separators it was pasted with.
    /// Numbers are dropped, so a phrase copied out of a wallet as "1. house 2. stadium"
    /// does not end up numbered twice.
    /// </summary>
    private static string[] SplitSeedWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

        return value
            .Split(new[] { ' ', '\n', '\r', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().TrimEnd('.', ')'))
            .Where(w => w.Length > 0 && !w.All(char.IsDigit))
            .ToArray();
    }

    private UIElement BuildFieldRow(EntryField field)
    {
        if (field.IsSecret && IsSeedPhrase(field) && !string.IsNullOrWhiteSpace(field.Value))
            return BuildSeedPhraseRow(field);

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Background = (Brush)FindResource("Bg2"),
            Padding = new Thickness(16, 12, 12, 12),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock { Text = field.Label.ToUpperInvariant(), Style = (Style)FindResource("Overline"), FontSize = 10 });

        bool revealed = !field.IsSecret;
        var valueBlock = new TextBlock
        {
            Text = field.IsSecret ? new string('\u2022', 10) : field.Value,
            Style = (Style)FindResource("Body"),
            Margin = new Thickness(0, 4, 0, 0),
            FontFamily = field.IsSecret ? new FontFamily("Consolas") : (FontFamily)FindResource("UiFont"),
        };
        textStack.Children.Add(valueBlock);
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (field.IsSecret)
        {
            var eye = new Button { Style = (Style)FindResource("IconButton"), Content = "\uE7B3", ToolTip = "Show / hide" };
            eye.Click += (_, _) =>
            {
                revealed = !revealed;
                valueBlock.Text = revealed ? field.Value : new string('\u2022', 10);
                eye.Content = revealed ? "\uED1A" : "\uE7B3";
            };
            actions.Children.Add(eye);
        }

        if (!string.IsNullOrEmpty(field.Value))
        {
            var copy = new Button { Style = (Style)FindResource("IconButton"), Content = "\uE8C8", ToolTip = "Copy" };
            copy.Click += (_, _) =>
            {
                ClipboardHelper.CopySecret(field.Value, _settings.ClipboardClearSeconds);
                copy.Content = "\uE73E";
                copy.Foreground = (Brush)FindResource("Accent");
            };
            actions.Children.Add(copy);
        }

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        card.Child = grid;
        return card;
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEntry is null) return;
        _currentEntry.Favorite = !_currentEntry.Favorite;
        _currentEntry.ModifiedUtc = DateTime.UtcNow;
        _vault.Save();
        ShowDetail(_currentEntry);
        RefreshEntries();
        RefreshCategories();
    }

    // ----------------------------------------------------- attachments

    private void RefreshAttachments()
    {
        AttachmentsPanel.Children.Clear();
        if (_currentEntry is null) return;

        int i = 0;
        foreach (var att in _currentEntry.Attachments)
        {
            var tile = BuildAttachmentTile(att, () => OpenAttachment(att), () => DeleteAttachmentPrompt(att));
            AttachmentsPanel.Children.Add(tile);
            if (tile is FrameworkElement fe) Anim.SlideFadeIn(fe, 260, 12, i * 45);
            i++;
        }
    }

    /// <summary>
    /// One attached file, with its name hidden until asked for.
    ///
    /// The name is often the secret. "github-recovery-codes.txt" and
    /// "client_secret_8384...apps.googleusercontent.com.txt" say precisely what they are
    /// and roughly how valuable they are, to anyone who glances at the screen. A vault that
    /// hides the contents and then prints the filename underneath has given away the part
    /// that mattered for a shoulder-surfer.
    ///
    /// So a tile shows what kind of file it is and how big, and nothing else, until it is
    /// clicked. Clicking opens it out: the real name, and the two things you might want to
    /// do with it. Deleting used to be on a right-click, which is not an option anybody
    /// finds; it is a button now.
    /// </summary>
    private Border BuildAttachmentTile(AttachmentRef att, Action onOpen, Action onDelete)
    {
        var tile = new Border
        {
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource("GlassStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Width = 156,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var sp = new StackPanel();

        sp.Children.Add(new TextBlock
        {
            Text = MediaIcon(att),
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 22,
            Foreground = (Brush)FindResource("Accent"),
        });

        // Collapsed: the kind of thing it is, not which thing it is.
        var name = new TextBlock
        {
            Text = KindOfFile(att),
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 8, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        sp.Children.Add(name);

        sp.Children.Add(new TextBlock
        {
            Text = FormatSize(att.SizeBytes),
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var open = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Open",
            Height = 30,
            MinWidth = 62,
            FontSize = 11,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 6, 0),
        };
        open.Click += (_, args) => { args.Handled = true; onOpen(); };
        buttons.Children.Add(open);

        var remove = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Delete",
            Height = 30,
            MinWidth = 62,
            FontSize = 11,
            Padding = new Thickness(10, 0, 10, 0),
            Foreground = (Brush)FindResource("Danger"),
            BorderBrush = (Brush)FindResource("Danger"),
        };
        remove.Click += (_, args) => { args.Handled = true; onDelete(); };
        buttons.Children.Add(remove);

        sp.Children.Add(buttons);
        tile.Child = sp;

        bool open_ = false;
        tile.ToolTip = "Click to show what this is";

        tile.MouseLeftButtonUp += (_, _) =>
        {
            open_ = !open_;

            name.Text = open_ ? att.FileName : KindOfFile(att);
            name.TextWrapping = open_ ? TextWrapping.Wrap : TextWrapping.NoWrap;
            name.Foreground = (Brush)FindResource(open_ ? "Text" : "Muted");

            buttons.Visibility = open_ ? Visibility.Visible : Visibility.Collapsed;
            tile.Width = open_ ? 232 : 156;
            tile.BorderBrush = (Brush)FindResource(open_ ? "Accent" : "GlassStroke");
            tile.ToolTip = open_ ? null : "Click to show what this is";

            if (open_) Anim.SlideFadeIn(buttons, 180, 6);
        };

        return tile;
    }

    /// <summary>A description of the kind of file, standing in for its name while hidden.</summary>
    private static string KindOfFile(AttachmentRef att)
    {
        if (IsImage(att)) return "Image";
        if (IsVideo(att)) return "Video";
        if (IsAudio(att)) return "Audio";

        string ext = System.IO.Path.GetExtension(att.FileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "PDF document",
            ".txt" or ".md" or ".log" => "Text file",
            ".doc" or ".docx" or ".odt" => "Document",
            ".xls" or ".xlsx" or ".csv" => "Spreadsheet",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "Archive",
            ".json" or ".xml" or ".yml" or ".yaml" => "Data file",
            ".key" or ".pem" or ".p12" or ".pfx" => "Key file",
            "" => "File",
            _ => ext.TrimStart('.').ToUpperInvariant() + " file",
        };
    }

    private void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEntry is null) return;
        PickAndAttachFiles(_currentEntry);
        RefreshAttachments();
    }

    private void PickAndAttachFiles(VaultEntry entry)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add file to vault",
            Multiselect = true,
            Filter = "All supported|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.pdf;*.txt|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        int added = 0;
        foreach (var path in dlg.FileNames)
        {
            try
            {
                var att = _vault.AddAttachment(path);
                entry.Attachments.Add(att);
                added++;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not add \"{Path.GetFileName(path)}\": {ex.Message}", "Lockwell");
            }
        }

        if (added > 0)
        {
            entry.ModifiedUtc = DateTime.UtcNow;
            _vault.Save();
        }
    }

    // ----------------------------------------------------- media gallery

    private async void AddMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_busyOperation) return;

        var cat = ResolveMediaCategory();
        if (cat is null)
        {
            MessageBox.Show("No media section found. Create a section and mark it as media, or use the default Media vault.", "Lockwell");
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = _currentMediaFolderId is null ? "Add media to vault" : "Add files to folder",
            Multiselect = true,
            Filter = MediaFilter,
        };
        if (dlg.ShowDialog() != true) return;

        var plan = new MediaImportPlan();
        foreach (string path in dlg.FileNames.Where(IsMediaFilePath))
        {
            plan.Files.Add(new MediaFileImport
            {
                Path = path,
                CategoryId = cat.Id,
                MediaFolderId = _currentMediaFolderId,
            });
        }

        if (plan.Files.Count == 0)
        {
            MessageBox.Show("No supported photos, videos, or audio files were selected.", "Lockwell");
            return;
        }

        var summary = await RunMediaImportAsync("Adding files", plan);
        CompleteMediaImportUi(summary);
        ShowMediaImportResult(summary);
        OfferOriginalCleanup(summary.ImportedFrom);
    }

    private void CreateMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        var cat = ResolveMediaCategory();
        if (cat is null) return;

        OverlayTitle.Text = "New folder";
        OverlayCard.MaxWidth = 520;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();
        var nameBox = AddLabeledInputWithPlaceholder("Folder name", "e.g. Vacation pictures");

        _overlaySave = () =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) { ShowInlineOverlayError("Give the folder a name."); return; }
            _vault.Data.MediaFolders.Add(new MediaFolder
            {
                Name = name,
                CategoryId = cat.Id,
                ParentFolderId = _currentMediaFolderId,
                Order = NextFolderOrder(cat.Id, _currentMediaFolderId),
            });
            _vault.Save();
            CloseOverlay();
            RefreshMediaGallery();
        };
        ShowOverlay(nameBox);
    }

    private async void UploadMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busyOperation) return;

        var cat = ResolveMediaCategory();
        if (cat is null) return;

        var dlg = new OpenFolderDialog { Title = "Upload a folder of photos, videos, or audio" };
        if (dlg.ShowDialog() != true) return;

        var plan = new MediaImportPlan();
        PlanDirectoryTree(plan, cat.Id, dlg.FolderName, _currentMediaFolderId);

        if (plan.Files.Count == 0 && plan.Folders.Count == 0)
        {
            MessageBox.Show("No supported photos, videos, or audio files were found in that folder.", "Lockwell");
            return;
        }

        var summary = await RunMediaImportAsync("Uploading folder", plan);
        CompleteMediaImportUi(summary);
        ShowMediaImportResult(summary);
        OfferOriginalCleanup(summary.ImportedFrom);
    }

    private void HookMediaDropTargets()
    {
        MediaPanel.AllowDrop = true;
        MediaPanel.PreviewDragOver += MediaArea_PreviewDragOver;
        MediaPanel.DragOver += MediaArea_DragOver;
        MediaPanel.DragLeave += MediaArea_DragLeave;
        MediaPanel.Drop += MediaArea_Drop;
    }

    private void MediaArea_DragOver(object sender, DragEventArgs e)
    {
        if (!IsSupportedFileDrop(e.Data))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        MediaDropOverlay.Visibility = Visibility.Visible;
    }

    private void MediaArea_DragLeave(object sender, DragEventArgs e)
    {
        MediaDropOverlay.Visibility = Visibility.Collapsed;
        ClearMediaDropHighlight();
    }

    private void MediaArea_Drop(object sender, DragEventArgs e)
    {
        MediaDropOverlay.Visibility = Visibility.Collapsed;
        ClearMediaDropHighlight();
        if (!TryGetDroppedPaths(e.Data, out var paths)) return;
        e.Handled = true;
        ProcessMediaDrop(paths, _currentMediaFolderId);
    }

    // ------------------------------------------------- in-app drag and drop

    private const string MediaEntryDragFormat = "Lockwell.MediaEntryId";
    private const string MediaFolderDragFormat = "Lockwell.MediaFolderId";

    private Point _dragStartPoint;
    private bool _dragArmed;
    private bool _suppressTileClick;

    /// <summary>Where a drop will land relative to the tile under the cursor.</summary>
    private enum MediaDropIntent { None, Into, Before, After }

    private DragGhostAdorner? _dragGhost;
    private AdornerLayer? _dragGhostLayer;
    private Point _lastDragPoint;
    private bool _dragAllowed;

    private void BeginDragGhost(FrameworkElement source)
    {
        EndDragGhost();
        _dragGhostLayer = AdornerLayer.GetAdornerLayer(MediaPanel);
        if (_dragGhostLayer is null) return;
        _dragGhost = new DragGhostAdorner(MediaPanel, source);
        _dragGhostLayer.Add(_dragGhost);
    }

    private void EndDragGhost()
    {
        if (_dragGhost is not null)
            _dragGhostLayer?.Remove(_dragGhost);
        _dragGhost = null;
        _dragGhostLayer = null;
        _dragAllowed = false;
    }

    /// <summary>
    /// Tunnelling handler, so the ghost keeps following the cursor even over tiles
    /// whose own DragOver marks the event handled and stops it bubbling.
    /// </summary>
    private void MediaArea_PreviewDragOver(object sender, DragEventArgs e)
    {
        _lastDragPoint = e.GetPosition(MediaPanel);
        _dragGhost?.Update(_lastDragPoint, _dragAllowed);
    }

    private void SetDragAllowed(bool allowed)
    {
        _dragAllowed = allowed;
        _dragGhost?.Update(_lastDragPoint, allowed);
    }

    /// <summary>
    /// Edges of a tile mean "put it beside this one", the middle means "put it inside".
    /// Files have no inside, so they map their whole width to before/after.
    /// </summary>
    private static MediaDropIntent ComputeDropIntent(Border tile, DragEventArgs e, bool canDropInto)
    {
        double width = tile.ActualWidth;
        if (width <= 0) return canDropInto ? MediaDropIntent.Into : MediaDropIntent.Before;

        double frac = e.GetPosition(tile).X / width;
        if (canDropInto && frac is > 0.28 and < 0.72) return MediaDropIntent.Into;
        return frac < 0.5 ? MediaDropIntent.Before : MediaDropIntent.After;
    }

    private void SwitchToCustomSort()
    {
        if (_settings.MediaSortMode == MediaSortMode.Custom) return;
        // A hand-placed arrangement only shows up under Custom, so adopt it on reorder
        // rather than silently discarding what the user just did.
        _settings.MediaSortMode = MediaSortMode.Custom;
        _settings.MediaSortDescending = false;
        _settings.Save();
    }

    /// <summary>
    /// Makes a tile draggable. Uses the system drag threshold so a normal click still
    /// opens the item and only a deliberate hold-and-move starts a drag.
    /// </summary>
    private void AttachMediaDragSource(Border tile, string dataFormat, string id)
    {
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Reset per press, so a stuck flag can never swallow a later click.
            _suppressTileClick = false;
            _dragArmed = e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase;
            _dragStartPoint = e.GetPosition(null);
        };

        tile.PreviewMouseLeftButtonUp += (_, _) => _dragArmed = false;

        tile.MouseMove += (_, e) =>
        {
            if (!_dragArmed || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragArmed = false;
            _suppressTileClick = true;

            double original = tile.Opacity;
            tile.Opacity = 0.35;
            BeginDragGhost(tile);
            try
            {
                DragDrop.DoDragDrop(tile, new DataObject(dataFormat, id), DragDropEffects.Move);
            }
            finally
            {
                EndDragGhost();
                tile.Opacity = original;
                ClearMediaDropHighlight();
            }
        };
    }

    private bool CanDropInternal(IDataObject data, string? targetFolderId)
    {
        if (data.GetDataPresent(MediaEntryDragFormat)) return true;

        if (data.GetData(MediaFolderDragFormat) is string draggedId)
            return MediaFolderTree.CanReparent(_vault.Data.MediaFolders, draggedId, targetFolderId);

        return false;
    }

    private void MoveDraggedItemInto(IDataObject data, string? targetFolderId)
    {
        if (data.GetData(MediaEntryDragFormat) is string entryId)
        {
            var entry = _vault.Data.Entries.FirstOrDefault(en => en.Id == entryId);
            if (entry is null) return;
            if (string.Equals(entry.MediaFolderId ?? "", targetFolderId ?? "", StringComparison.Ordinal)) return;

            entry.MediaFolderId = targetFolderId;
            entry.ModifiedUtc = DateTime.UtcNow;
            _vault.Save();
            RefreshMediaGallery();
            return;
        }

        if (data.GetData(MediaFolderDragFormat) is string folderId)
        {
            if (!MediaFolderTree.CanReparent(_vault.Data.MediaFolders, folderId, targetFolderId)) return;

            var folder = _vault.Data.MediaFolders.First(f => f.Id == folderId);
            folder.ParentFolderId = targetFolderId;
            folder.Order = NextFolderOrder(folder.CategoryId, targetFolderId);
            _vault.Save();
            RefreshMediaGallery();
        }
    }

    private void MediaFolderTile_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (sender is not Border tile || tile.Tag is not MediaFolder folder)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (IsSupportedFileDrop(e.Data))
        {
            e.Effects = DragDropEffects.Copy;
            SetMediaDropHighlight(tile, MediaDropIntent.Into);
            return;
        }

        bool draggingFolder = e.Data.GetDataPresent(MediaFolderDragFormat);
        bool draggingEntry = e.Data.GetDataPresent(MediaEntryDragFormat);
        if (!draggingFolder && !draggingEntry)
        {
            e.Effects = DragDropEffects.None;
            SetDragAllowed(false);
            return;
        }

        bool canDropInto = CanDropInternal(e.Data, folder.Id);
        var intent = ComputeDropIntent(tile, e, canDropInto);

        // Only folders can be re-ordered against a folder tile; a file dropped on a
        // folder always means "put it in there".
        if (intent != MediaDropIntent.Into && draggingEntry)
            intent = canDropInto ? MediaDropIntent.Into : MediaDropIntent.None;

        bool allowed = intent switch
        {
            MediaDropIntent.Into => canDropInto,
            MediaDropIntent.Before or MediaDropIntent.After =>
                draggingFolder && CanReorderAgainst(e.Data, folder),
            _ => false,
        };

        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        SetDragAllowed(allowed);
        if (allowed) SetMediaDropHighlight(tile, intent);
        else ClearMediaDropHighlight();

        _lastDropIntent = allowed ? intent : MediaDropIntent.None;
    }

    private MediaDropIntent _lastDropIntent = MediaDropIntent.None;

    /// <summary>
    /// A folder may be placed next to another folder only if it could legally live at
    /// that level, which rules out dropping a parent beside its own descendant.
    /// </summary>
    private bool CanReorderAgainst(IDataObject data, MediaFolder target)
    {
        if (data.GetData(MediaFolderDragFormat) is not string movedId) return false;
        if (movedId == target.Id) return false;

        var moved = _vault.Data.MediaFolders.FirstOrDefault(f => f.Id == movedId);
        if (moved is null) return false;

        // Same level already: a pure reorder is always fine.
        if (string.Equals(moved.ParentFolderId ?? "", target.ParentFolderId ?? "", StringComparison.Ordinal))
            return true;

        return MediaFolderTree.CanReparent(_vault.Data.MediaFolders, movedId, target.ParentFolderId);
    }

    private void MediaEntryTile_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (sender is not Border tile || tile.Tag is not VaultEntry entry ||
            !e.Data.GetDataPresent(MediaEntryDragFormat) ||
            e.Data.GetData(MediaEntryDragFormat) as string == entry.Id)
        {
            e.Effects = DragDropEffects.None;
            SetDragAllowed(false);
            ClearMediaDropHighlight();
            _lastDropIntent = MediaDropIntent.None;
            return;
        }

        var intent = ComputeDropIntent(tile, e, canDropInto: false);
        e.Effects = DragDropEffects.Move;
        SetDragAllowed(true);
        SetMediaDropHighlight(tile, intent);
        _lastDropIntent = intent;
    }

    private void MediaEntryTile_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border tile && ReferenceEquals(tile, _mediaDropHighlight))
            ClearMediaDropHighlight();
    }

    private void MediaEntryTile_Drop(object sender, DragEventArgs e)
    {
        var intent = _lastDropIntent;
        ClearMediaDropHighlight();
        if (sender is not Border tile || tile.Tag is not VaultEntry entry) return;
        e.Handled = true;
        if (intent == MediaDropIntent.None) return;
        HandleInternalDrop(e.Data, intent, entry);
    }

    private void MediaFolderTile_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border tile && tile == _mediaDropHighlight)
            ClearMediaDropHighlight();
    }

    private void MediaFolderTile_Drop(object sender, DragEventArgs e)
    {
        ClearMediaDropHighlight();
        MediaDropOverlay.Visibility = Visibility.Collapsed;
        var intent = _lastDropIntent;
        _lastDropIntent = MediaDropIntent.None;

        if (sender is not Border tile || tile.Tag is not MediaFolder folder) return;
        e.Handled = true;

        if (TryGetDroppedPaths(e.Data, out var paths))
        {
            ProcessMediaDrop(paths, folder.Id);
            return;
        }

        if (intent == MediaDropIntent.None) return;
        HandleInternalDrop(e.Data, intent, folder);
    }

    private void SetMediaDropHighlight(Border tile) =>
        SetMediaDropHighlight(tile, MediaDropIntent.Into);

    private void SetMediaDropHighlight(Border tile, MediaDropIntent intent)
    {
        if (!ReferenceEquals(_mediaDropHighlight, tile))
            ClearMediaDropHighlight();

        _mediaDropHighlight = tile;
        tile.BorderBrush = (Brush)FindResource("Accent");

        // A full ring means "into this folder"; a thick edge means "insert here".
        tile.BorderThickness = intent switch
        {
            MediaDropIntent.Before => new Thickness(4, 1, 1, 1),
            MediaDropIntent.After => new Thickness(1, 1, 4, 1),
            _ => new Thickness(2),
        };
    }

    private void ClearMediaDropHighlight()
    {
        if (_mediaDropHighlight is null) return;

        if (_mediaDropHighlight.Tag is MediaFolder folder)
        {
            _mediaDropHighlight.BorderBrush = folder.IsFavorite
                ? (Brush)FindResource("Warning")
                : FolderKindBrush(folder.Kind);
            _mediaDropHighlight.BorderThickness = new Thickness(folder.IsFavorite ? 1.5 : 1);
        }
        else
        {
            _mediaDropHighlight.BorderBrush = (Brush)FindResource("GlassStroke");
            _mediaDropHighlight.BorderThickness = new Thickness(1);
        }

        _mediaDropHighlight = null;
    }

    // ------------------------------------------------------ reorder on drop

    private void ReorderFolder(string movedId, string targetId, bool insertAfter)
    {
        var moved = _vault.Data.MediaFolders.FirstOrDefault(f => f.Id == movedId);
        var target = _vault.Data.MediaFolders.FirstOrDefault(f => f.Id == targetId);
        if (moved is null || target is null || moved.Id == target.Id) return;

        // Dragged in from another level: join the target's level first.
        if (!string.Equals(moved.ParentFolderId ?? "", target.ParentFolderId ?? "", StringComparison.Ordinal))
        {
            if (!MediaFolderTree.CanReparent(_vault.Data.MediaFolders, movedId, target.ParentFolderId)) return;
            moved.ParentFolderId = target.ParentFolderId;
        }

        var siblings = _vault.Data.MediaFolders
            .Where(f => f.CategoryId == target.CategoryId)
            .Where(f => string.Equals(f.ParentFolderId ?? "", target.ParentFolderId ?? "", StringComparison.Ordinal))
            .OrderBy(f => f.Order)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Id)
            .ToList();

        var arranged = MediaOrdering.Reorder(siblings, movedId, targetId, insertAfter);
        for (int i = 0; i < arranged.Count; i++)
        {
            var f = _vault.Data.MediaFolders.FirstOrDefault(x => x.Id == arranged[i]);
            if (f is not null) f.Order = i;
        }

        SwitchToCustomSort();
        _vault.Save();
        RefreshMediaGallery();
    }

    private void ReorderEntry(string movedId, string targetId, bool insertAfter)
    {
        var moved = _vault.Data.Entries.FirstOrDefault(en => en.Id == movedId);
        var target = _vault.Data.Entries.FirstOrDefault(en => en.Id == targetId);
        if (moved is null || target is null || moved.Id == target.Id) return;

        if (!string.Equals(moved.MediaFolderId ?? "", target.MediaFolderId ?? "", StringComparison.Ordinal))
        {
            moved.MediaFolderId = target.MediaFolderId;
            moved.ModifiedUtc = DateTime.UtcNow;
        }

        var siblings = _vault.Data.Entries
            .Where(en => en.CategoryId == target.CategoryId)
            .Where(en => string.Equals(en.MediaFolderId ?? "", target.MediaFolderId ?? "", StringComparison.Ordinal))
            .OrderBy(en => en.MediaSortOrder)
            .ThenByDescending(en => en.ModifiedUtc)
            .Select(en => en.Id)
            .ToList();

        var arranged = MediaOrdering.Reorder(siblings, movedId, targetId, insertAfter);
        for (int i = 0; i < arranged.Count; i++)
        {
            var en = _vault.Data.Entries.FirstOrDefault(x => x.Id == arranged[i]);
            if (en is not null) en.MediaSortOrder = i;
        }

        SwitchToCustomSort();
        _vault.Save();
        RefreshMediaGallery();
    }

    /// <summary>Drop handling shared by folder tiles and file tiles.</summary>
    private void HandleInternalDrop(IDataObject data, MediaDropIntent intent, object targetTag)
    {
        bool after = intent == MediaDropIntent.After;

        if (intent == MediaDropIntent.Into && targetTag is MediaFolder into)
        {
            MoveDraggedItemInto(data, into.Id);
            return;
        }

        if (data.GetData(MediaFolderDragFormat) is string folderId && targetTag is MediaFolder targetFolder)
        {
            ReorderFolder(folderId, targetFolder.Id, after);
            return;
        }

        if (data.GetData(MediaEntryDragFormat) is string entryId)
        {
            if (targetTag is VaultEntry targetEntry)
                ReorderEntry(entryId, targetEntry.Id, after);
            else if (targetTag is MediaFolder folderTarget)
                MoveDraggedItemInto(data, folderTarget.Id);
        }
    }

    private static bool IsSupportedFileDrop(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop);

    private static bool TryGetDroppedPaths(IDataObject data, out string[] paths)
    {
        paths = Array.Empty<string>();
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] raw || raw.Length == 0) return false;
        paths = raw;
        return true;
    }

    private async void ProcessMediaDrop(IReadOnlyList<string> paths, string? parentFolderId)
    {
        if (_busyOperation) return;

        var cat = ResolveMediaCategory();
        if (cat is null)
        {
            MessageBox.Show("No media section found.", "Lockwell");
            return;
        }

        var plan = BuildDropImportPlan(cat.Id, paths, parentFolderId);
        if (plan.Files.Count == 0 && plan.Folders.Count == 0)
        {
            MessageBox.Show("No supported photos, videos, or audio files were found in the drop.", "Lockwell");
            return;
        }

        var summary = await RunMediaImportAsync("Importing dropped files", plan);
        CompleteMediaImportUi(summary);
        ShowMediaImportResult(summary);
        OfferOriginalCleanup(summary.ImportedFrom);
    }

    private int NextFolderOrder(string categoryId, string? parentFolderId) =>
        _vault.Data.MediaFolders.Count(f =>
            f.CategoryId == categoryId && (f.ParentFolderId ?? "") == (parentFolderId ?? ""));

    private bool FolderTreeHasContent(string folderId)
    {
        if (_vault.Data.Entries.Any(e => e.MediaFolderId == folderId)) return true;
        return _vault.Data.MediaFolders
            .Where(f => f.ParentFolderId == folderId)
            .Any(f => FolderTreeHasContent(f.Id));
    }

    private int CountFilesInFolderTree(string folderId)
    {
        int count = _vault.Data.Entries.Count(e => e.MediaFolderId == folderId);
        foreach (var sub in _vault.Data.MediaFolders.Where(f => f.ParentFolderId == folderId))
            count += CountFilesInFolderTree(sub.Id);
        return count;
    }

    private List<string> CollectMediaPathsInFolderTree(string folderId)
    {
        var paths = new List<string>();
        foreach (var entry in _vault.Data.Entries.Where(e => e.MediaFolderId == folderId))
        {
            var att = entry.Attachments.FirstOrDefault();
            if (att is not null) paths.Add(att.FileName);
        }
        foreach (var sub in _vault.Data.MediaFolders.Where(f => f.ParentFolderId == folderId))
            paths.AddRange(CollectMediaPathsInFolderTree(sub.Id));
        return paths;
    }

    private static List<MediaFolder> BuildFolderAncestorChain(IReadOnlyList<MediaFolder> folders, MediaFolder folder)
    {
        var chain = new List<MediaFolder>();
        var current = folder;
        var seen = new HashSet<string>();
        while (true)
        {
            chain.Add(current);
            if (string.IsNullOrEmpty(current.ParentFolderId)) break;
            if (!seen.Add(current.Id)) break;
            var parent = folders.FirstOrDefault(f => f.Id == current.ParentFolderId);
            if (parent is null) break;
            current = parent;
        }
        chain.Reverse();
        return chain;
    }

    private void RefreshMediaBreadcrumb()
    {
        MediaBreadcrumbHost.Children.Clear();

        if (_currentMediaFolderId is null)
        {
            MediaBreadcrumbHost.Visibility = Visibility.Collapsed;
            return;
        }

        var current = _vault.Data.MediaFolders.FirstOrDefault(f => f.Id == _currentMediaFolderId);
        if (current is null)
        {
            MediaBreadcrumbHost.Visibility = Visibility.Collapsed;
            return;
        }

        MediaBreadcrumbHost.Visibility = Visibility.Visible;
        MediaBreadcrumbHost.Children.Add(CreateBreadcrumbButton("Media vault", null));

        foreach (var folder in BuildFolderAncestorChain(_vault.Data.MediaFolders, current))
        {
            MediaBreadcrumbHost.Children.Add(CreateBreadcrumbSeparator());
            if (folder.Id == current.Id)
                MediaBreadcrumbHost.Children.Add(CreateBreadcrumbLabel(folder.Name));
            else
                MediaBreadcrumbHost.Children.Add(CreateBreadcrumbButton(folder.Name, folder.Id));
        }
    }

    private Button CreateBreadcrumbButton(string label, string? folderId)
    {
        var btn = new Button
        {
            Content = label,
            Style = (Style)FindResource("LinkButton"),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) => MediaBreadcrumbNavigate(folderId);

        // Breadcrumbs double as drop targets so you can drag an item back out of a
        // folder without navigating away from where you are.
        btn.AllowDrop = true;
        btn.DragOver += (_, e) =>
        {
            e.Handled = true;
            bool ok = CanDropInternal(e.Data, folderId);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            btn.Opacity = ok ? 0.65 : 1;
        };
        btn.DragLeave += (_, _) => btn.Opacity = 1;
        btn.Drop += (_, e) =>
        {
            e.Handled = true;
            btn.Opacity = 1;
            MoveDraggedItemInto(e.Data, folderId);
        };
        return btn;
    }

    private static TextBlock CreateBreadcrumbSeparator() =>
        new()
        {
            Text = " / ",
            Style = (Style)Application.Current.FindResource("Caption"),
            Foreground = (Brush)Application.Current.FindResource("Muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
        };

    private TextBlock CreateBreadcrumbLabel(string label) =>
        new()
        {
            Text = label,
            Style = (Style)FindResource("Caption"),
            Foreground = (Brush)FindResource("Text"),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };


    private static bool IsMediaFilePath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return IsImageExtension(ext) || IsVideoExtension(ext) || IsAudioExtension(ext);
    }

    private static bool IsImageExtension(string ext) =>
        ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    private static bool IsVideoExtension(string ext) =>
        ext is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi";

    private static bool IsAudioExtension(string ext) =>
        ext is ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".wma";

    private void RefreshMediaGallery()
    {
        _galleryGeneration++; // abandon thumbnail loads started for the previous view
        UpdateMediaSortUi();
        MediaGrid.Children.Clear();
        MediaFoldersGrid.Children.Clear();
        var cat = ResolveMediaCategory();
        if (cat is null)
        {
            MediaEmpty.Visibility = Visibility.Visible;
            MediaCount.Text = "0 files";
            return;
        }

        string query = (MediaSearchBox.Text ?? "").Trim();
        bool searching = query.Length > 0;
        MediaSearchPlaceholder.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;

        bool atRoot = _currentMediaFolderId is null;
        if (!searching)
        {
            MediaFolderEditButton.Visibility = atRoot ? Visibility.Collapsed : Visibility.Visible;
            RefreshMediaBreadcrumb();
        }
        else
        {
            MediaFolderEditButton.Visibility = Visibility.Collapsed;
            MediaBreadcrumbHost.Children.Clear();
            MediaBreadcrumbHost.Visibility = Visibility.Visible;
            MediaBreadcrumbHost.Children.Add(new TextBlock
            {
                Text = $"Search: {query}",
                Style = (Style)FindResource("Caption"),
                Foreground = (Brush)FindResource("Accent2"),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            });
        }

        var allInCategory = _vault.Data.Entries.Where(e => e.CategoryId == cat.Id).ToList();

        List<MediaFolder> folders;
        List<VaultEntry> items;

        if (searching)
        {
            folders = SortFolders(_vault.Data.MediaFolders
                .Where(f => f.CategoryId == cat.Id)
                .Where(f => !_settings.HideNsfwMediaFolders || !f.IsNsfw)
                .Where(f => FolderMatchesMediaQuery(f, query, allInCategory)));

            items = SortMediaEntries(allInCategory
                .Where(e => MediaEntryMatchesQuery(e, query)));
        }
        else
        {
            string? folderParentId = atRoot ? null : _currentMediaFolderId;
            folders = SortFolders(VisibleMediaFolders(cat.Id, folderParentId));

            items = SortMediaEntries(allInCategory
                .Where(e => atRoot ? string.IsNullOrEmpty(e.MediaFolderId) : e.MediaFolderId == _currentMediaFolderId));
        }

        MediaFoldersHeader.Visibility = folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        int fi = 0;
        foreach (var folder in folders)
        {
            int count = CountFilesInFolderTree(folder.Id);
            var tile = BuildFolderTile(folder, count);
            MediaFoldersGrid.Children.Add(tile);
            Anim.PopIn(tile, 260, fi * 45);
            fi++;
        }

        int folderCount = folders.Count;
        if (searching)
        {
            int total = folderCount + items.Count;
            MediaCount.Text = total == 1 ? "1 result" : $"{total} results";
        }
        else
        {
            MediaCount.Text = atRoot
                ? $"{folderCount} folder{(folderCount == 1 ? "" : "s")}, {items.Count} loose file{(items.Count == 1 ? "" : "s")}"
                : folders.Count > 0
                    ? $"{folders.Count} subfolder{(folders.Count == 1 ? "" : "s")}, {items.Count} file{(items.Count == 1 ? "" : "s")}"
                    : items.Count == 1 ? "1 file" : $"{items.Count} files";
        }

        bool hasContent = folderCount > 0 || items.Count > 0;
        MediaEmpty.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        MediaFilesHeader.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        int hiddenNsfw = !searching && atRoot && cat is not null
            ? CountHiddenNsfwRootFolders(cat.Id)
            : 0;
        if (hiddenNsfw > 0)
        {
            MediaCount.Text += hiddenNsfw == 1
                ? " (1 NSFW folder hidden in Settings)"
                : $" ({hiddenNsfw} NSFW folders hidden in Settings)";
        }

        int i = 0;
        foreach (var entry in items)
        {
            var att = entry.Attachments.FirstOrDefault();
            if (att is null) continue;

            var tile = BuildMediaTile(entry, att);
            MediaGrid.Children.Add(tile);
            Anim.PopIn(tile, 280, i * 50);
            i++;

            if (IsImage(att))
                _ = LoadThumbnailAsync(tile, att, _galleryGeneration);
        }
    }

    private static bool MediaEntryMatchesQuery(VaultEntry entry, string query)
    {
        if (entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var att = entry.Attachments.FirstOrDefault();
        return att is not null && att.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private bool FolderMatchesMediaQuery(MediaFolder folder, string query, IReadOnlyList<VaultEntry> entries)
    {
        if (folder.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (entries.Any(e => e.MediaFolderId == folder.Id && MediaEntryMatchesQuery(e, query))) return true;
        return _vault.Data.MediaFolders
            .Any(f => f.ParentFolderId == folder.Id && FolderMatchesMediaQuery(f, query, entries));
    }

    private Border BuildFolderTile(MediaFolder folder, int fileCount)
    {
        var kindBrush = FolderKindBrush(folder.Kind);
        var tile = new Border
        {
            Width = 180,
            Height = 160,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(FolderTintColor(folder.Kind)),
            BorderBrush = folder.IsFavorite ? (Brush)FindResource("Warning") : kindBrush,
            BorderThickness = new Thickness(folder.IsFavorite ? 1.5 : 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ClipToBounds = true,
            Tag = folder,
            AllowDrop = true,
        };
        tile.DragOver += MediaFolderTile_DragOver;
        tile.DragLeave += MediaFolderTile_DragLeave;
        tile.Drop += MediaFolderTile_Drop;

        var grid = new Grid();
        var icon = new TextBlock
        {
            Text = FolderKindIcon(folder.Kind),
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 36,
            Foreground = kindBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 24),
        };
        grid.Children.Add(icon);

        if (folder.IsFavorite)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "\uE735",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 14,
                Foreground = (Brush)FindResource("Warning"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10, 8, 0, 0),
            });
        }

        if (folder.IsNsfw)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "NSFW",
                Foreground = (Brush)FindResource("Danger"),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 10, 52),
            });
        }

        var caption = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x06, 0x08, 0x0D)),
            CornerRadius = new CornerRadius(0, 0, 16, 16),
            Padding = new Thickness(12, 8, 12, 10),
        };
        var capStack = new StackPanel();
        capStack.Children.Add(new TextBlock
        {
            Text = folder.Name,
            Foreground = (Brush)FindResource("Text"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        capStack.Children.Add(new TextBlock
        {
            Text = fileCount == 1 ? "1 file" : $"{fileCount} files",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        });
        caption.Child = capStack;
        grid.Children.Add(caption);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
        };
        var editBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "\uE70F",
            FontSize = 13,
            ToolTip = "Edit folder",
            Opacity = 0.85,
        };
        editBtn.Click += (_, e) =>
        {
            e.Handled = true;
            OpenEditMediaFolder(folder);
        };
        var deleteBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "\uE74D",
            FontSize = 13,
            ToolTip = "Delete folder",
            Opacity = 0.85,
        };
        deleteBtn.Click += (_, e) =>
        {
            e.Handled = true;
            DeleteMediaFolder(folder);
        };
        var compressFolderBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            // Escaped, not a literal glyph: a pasted Segoe MDL2 character can be
            // stripped in transit and the button then renders completely blank.
            Content = "\uE73F",
            FontSize = 13,
            ToolTip = "Compress every file in this folder",
            Opacity = 0.85,
        };
        compressFolderBtn.Click += (_, e) =>
        {
            e.Handled = true;
            BeginCompressFolder(folder);
        };

        actions.Children.Add(editBtn);
        actions.Children.Add(compressFolderBtn);
        actions.Children.Add(deleteBtn);
        grid.Children.Add(actions);

        tile.Child = grid;

        AttachMediaDragSource(tile, MediaFolderDragFormat, folder.Id);

        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_suppressTileClick) return; // this press turned into a drag
            if (e.OriginalSource is Button) return;
            OpenMediaFolder(folder.Id);
        };
        return tile;
    }

    private void MediaFolderEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMediaFolderId is null) return;
        var folder = _vault.Data.MediaFolders.FirstOrDefault(f => f.Id == _currentMediaFolderId);
        if (folder is not null) OpenEditMediaFolder(folder);
    }

    private void OpenEditMediaFolder(MediaFolder folder)
    {
        OverlayTitle.Text = "Edit folder";
        OverlayCard.MaxWidth = 640;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();
        _folderEditDeleteIds.Clear();

        var nameBox = AddLabeledInput("Folder name", folder.Name);

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Folder type",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 14, 0, 6),
        });
        var kindPanel = new WrapPanel();
        var kindButtons = new Dictionary<MediaFolderKind, Button>();
        MediaFolderKind selectedKind = folder.Kind;
        foreach (var kind in Enum.GetValues<MediaFolderKind>())
        {
            var chip = MakeChip(FolderKindLabel(kind), folder.Kind == kind);
            MediaFolderKind captured = kind;
            chip.Click += (_, _) =>
            {
                selectedKind = captured;
                foreach (var pair in kindButtons)
                    StyleChip(pair.Value, pair.Key == captured);
            };
            kindButtons[kind] = chip;
            kindPanel.Children.Add(chip);
        }
        OverlayBody.Children.Add(kindPanel);

        var nsfwToggle = AddToggle("Mark as NSFW", folder.IsNsfw);
        var favoriteToggle = AddToggle("Favorite folder", folder.IsFavorite);

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Files in folder",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 16, 0, 8),
        });
        _folderItemHost = new StackPanel();
        OverlayBody.Children.Add(_folderItemHost);

        var entries = _vault.Data.Entries
            .Where(e => e.MediaFolderId == folder.Id)
            .OrderBy(e => e.MediaSortOrder)
            .ThenByDescending(e => e.ModifiedUtc)
            .ToList();

        foreach (var entry in entries)
            AddFolderEditRow(entry);

        if (entries.Count == 0)
        {
            _folderItemHost.Children.Add(new TextBlock
            {
                Text = "No files in this folder yet.",
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        _overlaySave = () =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) { MessageBox.Show("Give the folder a name.", "Lockwell"); return; }

            folder.Name = name;
            folder.Kind = selectedKind;
            folder.IsNsfw = nsfwToggle.IsChecked == true;
            folder.IsFavorite = favoriteToggle.IsChecked == true;

            if (_folderItemHost is not null)
            {
                int order = 0;
                foreach (var child in _folderItemHost.Children)
                {
                    if (child is not Border row || row.Tag is not VaultEntry entry) continue;
                    if (row.Child is Grid g)
                    {
                        var titleBox = g.Children.OfType<TextBox>().FirstOrDefault();
                        if (titleBox is not null)
                        {
                            entry.Title = titleBox.Text.Trim();
                            if (entry.Title.Length == 0) entry.Title = "Untitled";
                        }
                    }
                    entry.MediaSortOrder = order++;
                }
            }

            foreach (string id in _folderEditDeleteIds)
            {
                var entry = _vault.Data.Entries.FirstOrDefault(e => e.Id == id);
                if (entry is null) continue;
                foreach (var att in entry.Attachments) _vault.DeleteAttachment(att);
                _vault.Data.Entries.Remove(entry);
            }

            _vault.Save();
            UpdateStats();
            RefreshCategories();
            CloseOverlay();
            RefreshMediaGallery();
        };
        ShowOverlay();
    }

    private void AddFolderEditRow(VaultEntry entry)
    {
        if (_folderItemHost is null) return;

        var row = new Border
        {
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource("GlassStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Tag = entry,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var att = entry.Attachments.FirstOrDefault();
        var typeIcon = new TextBlock
        {
            Text = att is not null ? MediaIcon(att) : "\uE7C3",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 16,
            Foreground = (Brush)FindResource("Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(typeIcon, 0);
        grid.Children.Add(typeIcon);

        var titleBox = new TextBox
        {
            Style = (Style)FindResource("InputBox"),
            Text = entry.Title,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleBox, 1);
        grid.Children.Add(titleBox);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var upBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "\uE70E",
            FontSize = 12,
            ToolTip = "Move up",
        };
        var downBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "\uE70D",
            FontSize = 12,
            ToolTip = "Move down",
        };
        var delBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "\uE74D",
            FontSize = 12,
            ToolTip = "Remove file",
        };

        upBtn.Click += (_, _) => MoveFolderEditRow(row, -1);
        downBtn.Click += (_, _) => MoveFolderEditRow(row, 1);
        delBtn.Click += (_, _) =>
        {
            if (MessageBox.Show($"Remove \"{entry.Title}\" from this folder?", "Lockwell",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _folderEditDeleteIds.Add(entry.Id);
            _folderItemHost.Children.Remove(row);
        };

        actions.Children.Add(upBtn);
        actions.Children.Add(downBtn);
        actions.Children.Add(delBtn);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        row.Child = grid;
        _folderItemHost.Children.Add(row);
    }

    private void MoveFolderEditRow(Border row, int delta)
    {
        if (_folderItemHost is null) return;
        int idx = _folderItemHost.Children.IndexOf(row);
        int newIdx = idx + delta;
        if (idx < 0 || newIdx < 0 || newIdx >= _folderItemHost.Children.Count) return;
        _folderItemHost.Children.RemoveAt(idx);
        _folderItemHost.Children.Insert(newIdx, row);
    }

    private void DeleteMediaFolder(MediaFolder folder)
    {
        int count = CountFilesInFolderTree(folder.Id);
        string msg = count > 0
            ? $"Delete folder \"{folder.Name}\" and all {count} file(s) inside? This cannot be undone."
            : $"Delete empty folder \"{folder.Name}\"?";
        if (MessageBox.Show(msg, "Lockwell", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        DeleteMediaFolderTree(folder);
        if (_currentMediaFolderId is not null &&
            !_vault.Data.MediaFolders.Any(f => f.Id == _currentMediaFolderId))
            _currentMediaFolderId = folder.ParentFolderId;
        _vault.Save();
        UpdateStats();
        RefreshCategories();
        RefreshMediaGallery();
    }

    private void DeleteMediaFolderTree(MediaFolder folder)
    {
        foreach (var child in _vault.Data.MediaFolders.Where(f => f.ParentFolderId == folder.Id).ToList())
            DeleteMediaFolderTree(child);

        foreach (var entry in _vault.Data.Entries.Where(e => e.MediaFolderId == folder.Id).ToList())
        {
            foreach (var att in entry.Attachments) _vault.DeleteAttachment(att);
            _vault.Data.Entries.Remove(entry);
        }

        _vault.Data.MediaFolders.Remove(folder);
    }

    private Border BuildMediaTile(VaultEntry entry, AttachmentRef att)
    {
        var tile = new Border
        {
            Width = 180,
            Height = 200,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(16),
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource("GlassStroke"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = entry,
            ClipToBounds = true,
            AllowDrop = true,
        };
        tile.DragOver += MediaEntryTile_DragOver;
        tile.DragLeave += MediaEntryTile_DragLeave;
        tile.Drop += MediaEntryTile_Drop;

        // A WPF Border does NOT clip its child to CornerRadius, so the caption bar's
        // corners used to overhang the tile's rounded edge and show a visible sliver.
        // Clip the content to the tile's inner rounded rect (size minus the 1px border).
        var grid = new Grid
        {
            Clip = new RectangleGeometry(new Rect(0, 0, 178, 198), 15, 15),
        };
        var icon = new TextBlock
        {
            Name = "FallbackIcon",
            Text = MediaIcon(att),
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 32,
            Foreground = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var img = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
        grid.Children.Add(icon);
        grid.Children.Add(img);

        // On a linked device. Media is what actually gets sent, so the mark belongs here
        // as much as on the entry list -- without it there is no way to look at a folder
        // and tell what the phone already has.
        if (entry.IsSyncedAnywhere)
        {
            grid.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 8, 0, 0),
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(999),
                Background = (Brush)FindResource("Accent"),
                ToolTip = entry.SyncedToDevices.Count == 1
                    ? "On one linked device"
                    : $"On {entry.SyncedToDevices.Count} linked devices",
                Child = new TextBlock
                {
                    Text = "",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 9,
                    Foreground = (Brush)FindResource("Bg0"),
                },
            });
        }

        // Gradient rather than a flat bar: the filename stays readable over bright
        // thumbnails without a hard edge cutting across the image.
        var caption = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new LinearGradientBrush(
                Color.FromArgb(0x00, 0x06, 0x08, 0x0D),
                Color.FromArgb(0xF0, 0x06, 0x08, 0x0D),
                new Point(0.5, 0),
                new Point(0.5, 1)),
            Padding = new Thickness(12, 18, 12, 10),
        };
        caption.Child = new TextBlock
        {
            Text = entry.Title,
            Foreground = (Brush)FindResource("Text"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        grid.Children.Add(caption);

        // Tile actions reveal on hover so the grid stays clean at rest.
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Opacity = RestingActionOpacity,
        };

        var renameBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "\uE8AC",
            FontSize = 13,
            ToolTip = "Rename",
            Opacity = 0.9,
        };
        renameBtn.Click += (_, e) =>
        {
            e.Handled = true;
            RenameMediaEntry(entry);
        };

        var deleteBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "\uE74D",
            FontSize = 13,
            ToolTip = "Delete",
            Opacity = 0.9,
        };
        deleteBtn.Click += (_, e) =>
        {
            e.Handled = true;
            DeleteMediaEntry(entry);
        };

        actions.Children.Add(renameBtn);

        if (IsImage(att) || IsAudio(att) || IsVideo(att))
        {
            var compressBtn = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = "\uE73F",
                FontSize = 13,
                ToolTip = "Compress",
                Opacity = 0.9,
            };
            compressBtn.Click += (_, e) =>
            {
                e.Handled = true;
                BeginCompress(entry);
            };
            actions.Children.Add(compressBtn);
        }

        actions.Children.Add(deleteBtn);
        grid.Children.Add(actions);

        // Visible at rest, brighter on hover. Fully hiding these until mouse-over meant
        // people never discovered the actions existed at all.
        tile.MouseEnter += (_, _) => Anim.FadeTo(actions, 1, 130);
        tile.MouseLeave += (_, _) => Anim.FadeTo(actions, RestingActionOpacity, 130);

        tile.Child = grid;

        AttachMediaDragSource(tile, MediaEntryDragFormat, entry.Id);

        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_suppressTileClick) return; // this press turned into a drag
            if (e.OriginalSource is Button) return;
            OpenMediaItem(entry, att);
        };
        return tile;
    }

    /// <summary>
    /// Decoded thumbnails, keyed by attachment id. Building one costs a full decrypt of
    /// the original file, so without this every gallery refresh -- including every
    /// keystroke in the search box -- re-decrypted every visible image.
    /// Frozen bitmaps, so they are safe to hand to the UI from any thread.
    /// Cleared on lock along with the rest of the decrypted state.
    /// </summary>
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = new();

    /// <summary>Insertion order, for evicting the oldest entry once the cache is full.</summary>
    private readonly Queue<string> _thumbnailCacheOrder = new();

    /// <summary>
    /// A decoded 360px thumbnail is roughly half a megabyte, so an unbounded cache
    /// would balloon on a large media vault. Bounded to a few hundred megabytes worst case.
    /// </summary>
    private const int ThumbnailCacheLimit = 240;

    /// <summary>Bumped on every refresh so in-flight loads for a stale view are dropped.</summary>
    private int _galleryGeneration;

    private void CacheThumbnail(string attachmentId, BitmapImage bmp)
    {
        if (_thumbnailCache.ContainsKey(attachmentId)) return;

        _thumbnailCache[attachmentId] = bmp;
        _thumbnailCacheOrder.Enqueue(attachmentId);

        while (_thumbnailCacheOrder.Count > ThumbnailCacheLimit)
            _thumbnailCache.Remove(_thumbnailCacheOrder.Dequeue());
    }

    /// <summary>
    /// Decoded thumbnails are plaintext vault content, so they must go when the vault
    /// locks. Anything less would undercut "locking clears memory".
    /// </summary>
    private void ClearThumbnailCache()
    {
        _thumbnailCache.Clear();
        _thumbnailCacheOrder.Clear();
    }

    private void ApplyThumbnail(Border tile, AttachmentRef att, BitmapImage bmp)
    {
        if (tile.Child is not Grid grid) return;
        var img = grid.Children.OfType<Image>().FirstOrDefault();
        var icon = grid.Children.OfType<TextBlock>().FirstOrDefault();
        if (img is null) return;

        img.Source = bmp;
        img.Visibility = Visibility.Visible;
        img.Effect = _settings.BlurMediaThumbnails && IsImage(att)
            ? new BlurEffect { Radius = 22, RenderingBias = RenderingBias.Quality }
            : null;
        if (icon is not null) icon.Visibility = Visibility.Collapsed;
    }

    private async Task LoadThumbnailAsync(Border tile, AttachmentRef att, int generation)
    {
        try
        {
            if (_thumbnailCache.TryGetValue(att.Id, out var cached))
            {
                ApplyThumbnail(tile, att, cached);
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
                    image.DecodePixelWidth = 360;
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze(); // cross-thread safe once frozen
                    return image;
                }
                finally
                {
                    Array.Clear(data, 0, data.Length);
                }
            });

            await Dispatcher.InvokeAsync(() =>
            {
                // The user may have navigated or typed again while this was decrypting.
                if (generation != _galleryGeneration) return;
                if (_vault.IsUnlocked) CacheThumbnail(att.Id, bmp);
                ApplyThumbnail(tile, att, bmp);
            });
        }
        catch { /* thumbnail optional */ }
    }

    private void RenameMediaEntry(VaultEntry entry)
    {
        OverlayTitle.Text = "Rename file";
        OverlayCard.MaxWidth = 520;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        var att = entry.Attachments.FirstOrDefault();
        if (att is not null)
        {
            OverlayBody.Children.Add(new TextBlock
            {
                Text = att.FileName,
                Style = (Style)FindResource("Caption"),
                Foreground = (Brush)FindResource("Muted"),
                Margin = new Thickness(0, 0, 0, 12),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        var nameBox = AddLabeledInputWithPlaceholder("Display name", "e.g. Beach sunset", entry.Title);

        _overlaySave = () =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) { ShowInlineOverlayError("Give the file a name."); return; }

            entry.Title = name;
            entry.ModifiedUtc = DateTime.UtcNow;
            _vault.Save();
            CloseOverlay();
            RefreshCurrentView();
        };
        ShowOverlay(nameBox);
    }

    private void DeleteMediaEntry(VaultEntry entry)
    {
        if (MessageBox.Show($"Delete \"{entry.Title}\" from the vault?", "Lockwell",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var att in entry.Attachments)
        {
            _vault.DeleteAttachment(att);
            _thumbnailCache.Remove(att.Id); // don't keep a deleted image decoded in memory
        }
        _vault.Data.Entries.Remove(entry);
        _vault.Save();
        UpdateStats();
        RefreshCategories();
        RefreshCurrentView();
    }

    private void OpenMediaItem(VaultEntry entry, AttachmentRef att)
    {
        BuildMediaViewerPlaylist(entry);
        _viewerIndex = _viewerPlaylist.FindIndex(x => x.Entry.Id == entry.Id);
        if (_viewerIndex < 0)
        {
            _viewerPlaylist.Clear();
            _viewerPlaylist.Add(new ViewerMediaItem(entry, att));
            _viewerIndex = 0;
        }

        _viewerFolderId = entry.MediaFolderId;

        if (IsImage(att))
            ShowViewerAt(_viewerIndex);
        else if (IsAudio(att) || IsVideo(att))
            ShowMediaAt(_viewerIndex);
        else
            OpenAttachment(att);
    }

    private void BuildMediaViewerPlaylist(VaultEntry current)
    {
        _viewerPlaylist.Clear();
        var cat = ResolveMediaCategory();
        if (cat is null)
        {
            _viewerPlaylist.Add(new ViewerMediaItem(current, current.Attachments.First()));
            return;
        }

        bool atRoot = _currentMediaFolderId is null;
        var entries = _vault.Data.Entries
            .Where(e => e.CategoryId == cat.Id)
            .Where(e => atRoot ? string.IsNullOrEmpty(e.MediaFolderId) : e.MediaFolderId == _currentMediaFolderId)
            .OrderBy(e => e.MediaSortOrder)
            .ThenByDescending(e => e.ModifiedUtc);

        foreach (var entry in entries)
        {
            var attachment = entry.Attachments.FirstOrDefault();
            if (attachment is not null && (IsImage(attachment) || IsAudio(attachment) || IsVideo(attachment)))
                _viewerPlaylist.Add(new ViewerMediaItem(entry, attachment));
        }

        if (_viewerPlaylist.Count == 0)
            _viewerPlaylist.Add(new ViewerMediaItem(current, current.Attachments.First()));
    }

    private async void ShowViewerAt(int index)
    {
        if (index < 0 || index >= _viewerPlaylist.Count) return;

        _viewerIndex = index;
        var item = _viewerPlaylist[index];
        _viewerCurrentAttachment = item.Attachment;

        try
        {
            PreparePhotoViewerUi();
            byte[] data = _vault.ReadAttachment(item.Attachment);

            CleanupViewerImage();
            if (IsGif(item.Attachment))
            {
                // Decoded straight from memory. This used to write the decrypted GIF to
                // %TEMP% purely to hand MediaElement-style APIs a path, which was an
                // avoidable disk exposure: BitmapImage takes a stream just fine.
                var gif = new BitmapImage();
                using (var ms = new MemoryStream(data))
                {
                    gif.BeginInit();
                    gif.CacheOption = BitmapCacheOption.OnLoad;
                    gif.StreamSource = ms;
                    gif.EndInit();
                }
                gif.Freeze();

                // Try the frame decoder first, so a GIF gets the same speed control as
                // every other animated format. WpfAnimatedGif remains the fallback: it
                // handles oddities in the wild that the general decoder does not.
                if (await TryPlayAnimationAsync(data))
                {
                    Array.Clear(data, 0, data.Length);
                }
                else
                {
                    Array.Clear(data, 0, data.Length);
                    ImageBehavior.SetRepeatBehavior(ViewerImage, RepeatBehavior.Forever);
                    ImageBehavior.SetAnimatedSource(ViewerImage, gif);
                }
            }
            else if (AnimatedImage.CouldBeAnimated(item.Attachment.FileName) &&
                     await TryPlayAnimationAsync(data))
            {
                // Handled: an animated WebP (or any other multi-frame image) is now
                // playing. WPF only ever showed the first frame of these.
                Array.Clear(data, 0, data.Length);
            }
            else
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(data))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                Array.Clear(data, 0, data.Length);
                ViewerImage.Source = bmp;
            }

            ViewerCaption.Text = item.Attachment.FileName;
            bool multiple = _viewerPlaylist.Count > 1;
            ViewerPosition.Text = multiple ? $"{index + 1} / {_viewerPlaylist.Count}" : "";
            ViewerPosition.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            ViewerPrevButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            ViewerNextButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            ViewerDownloadFolderButton.Visibility = string.IsNullOrEmpty(_viewerFolderId)
                ? Visibility.Collapsed
                : Visibility.Visible;
            ViewerCompressButton.Visibility = IsImage(item.Attachment)
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateAnimationSpeedUi();

            ViewerSendButton.Visibility = _trust is not null && Trust.Devices.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (ImageViewer.Visibility != Visibility.Visible)
            {
                ImageViewer.Visibility = Visibility.Visible;
                ImageViewer.Opacity = 0;
                Anim.FadeIn(ImageViewer, 220);
            }

            ImageViewer.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open file: " + ex.Message, "Lockwell");
        }
    }

    private void CleanupViewerImage()
    {
        StopAnimation();
        ImageBehavior.SetAnimatedSource(ViewerImage, null);
        ViewerImage.Source = null;
        VaultTempFiles.SecureDelete(_viewerImageTempPath);
        _viewerImageTempPath = null;
    }

    // ------------------------------------------------- animated still images

    private DispatcherTimer? _animationTimer;
    private IReadOnlyList<AnimationFrame>? _animationFrames;
    private int _animationIndex;

    /// <summary>
    /// Play a multi-frame image. Returns false when the file turns out to have a single
    /// frame, so the caller can show it as an ordinary picture instead.
    /// </summary>
    private async Task<bool> TryPlayAnimationAsync(byte[] data)
    {
        var frames = await AnimatedImage.TryDecodeAsync(data);
        if (frames is null || frames.Count < 2) return false;

        StopAnimation();
        _animationFrames = frames;
        _animationIndex = 0;

        ViewerImage.Source = frames[0].Image;

        // Per-frame timing rather than a fixed interval, because frame delays vary
        // within a single file and a fixed rate makes playback visibly wrong.
        // Start at the remembered rate. Twenty is the default because a great many files
        // declare timings no renderer honours -- 0ms and 10ms are common -- and a steady
        // rate looks closer to right than the file's own answer far more often than not.
        _animationTimer = new DispatcherTimer
        {
            Interval = _animationFps is { } fps && fps > 0
                ? TimeSpan.FromSeconds(1 / fps)
                : frames[0].Delay,
        };
        _animationTimer.Tick += (_, _) => AdvanceAnimation();

        // Zero means hold on one frame, which is a legitimate thing to want.
        if (_animationFps is not { } rate || rate > 0) _animationTimer.Start();

        UpdateAnimationSpeedUi();
        return true;
    }

    private void AdvanceAnimation()
    {
        if (_animationFrames is null || _animationTimer is null) return;

        _animationIndex = (_animationIndex + 1) % _animationFrames.Count;
        var frame = _animationFrames[_animationIndex];

        ViewerImage.Source = frame.Image;

        // A chosen rate overrides the file's own timing. Without an override each frame
        // keeps the delay it was authored with, which is what makes normal playback look
        // right when the file is honest about its timing.
        _animationTimer.Interval = _animationFps is { } fps && fps > 0
            ? TimeSpan.FromSeconds(1 / fps)
            : frame.Delay;
    }

    // ------------------------------------------------------- animation speed

    /// <summary>
    /// The rate this animation is currently running at.
    ///
    /// With an override it is simply that number. Without one it is derived from the
    /// frame delays the file declares, averaged, because frames can each carry a
    /// different delay and a single figure has to stand for the whole thing.
    /// </summary>
    private double CurrentAnimationFps()
    {
        if (_animationFps is { } chosen) return chosen;
        if (_animationFrames is null || _animationFrames.Count == 0) return 0;

        double totalSeconds = _animationFrames.Sum(f => f.Delay.TotalSeconds);
        if (totalSeconds <= 0) return 0;

        return _animationFrames.Count / totalSeconds;
    }

    private void UpdateAnimationSpeedUi()
    {
        bool animated = _animationFrames is not null && _animationFrames.Count > 1;

        ViewerSpeedButton.Visibility = animated ? Visibility.Visible : Visibility.Collapsed;
        if (!animated)
        {
            ViewerSpeedPopup.IsOpen = false;
            return;
        }

        double fps = CurrentAnimationFps();
        ViewerSpeedText.Text = _animationFps is null
            ? $"{fps:0.#} fps"
            : $"{fps:0.#} fps \u00B7 set";

        _suppressSpeedEvents = true;
        ViewerSpeedSlider.Value = Math.Clamp(Math.Round(fps), 0, 60);
        ViewerSpeedBox.Text = fps.ToString("0.#");
        _suppressSpeedEvents = false;
    }

    private bool _suppressSpeedEvents;

    private void ViewerSpeed_Click(object sender, RoutedEventArgs e)
    {
        UpdateAnimationSpeedUi();
        ViewerSpeedPopup.IsOpen = !ViewerSpeedPopup.IsOpen;

        if (ViewerSpeedPopup.IsOpen)
        {
            ViewerSpeedBox.Focus();
            ViewerSpeedBox.SelectAll();
        }
    }

    private void ViewerSpeedSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSpeedEvents) return;
        ApplyAnimationFps(e.NewValue);
    }

    private void ViewerSpeedBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CommitTypedFps();
        e.Handled = true;
    }

    private void ViewerSpeedBox_Commit(object sender, RoutedEventArgs e) => CommitTypedFps();

    private void CommitTypedFps()
    {
        if (_suppressSpeedEvents) return;

        // Accept both decimal separators: the box is typed into by hand and a comma is
        // what a German keyboard produces without thinking about it.
        string text = (ViewerSpeedBox.Text ?? "").Trim().Replace(',', '.');

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double fps))
        {
            UpdateAnimationSpeedUi();   // put back what it was
            return;
        }

        ApplyAnimationFps(Math.Clamp(fps, 0, 60));
    }

    /// <summary>
    /// Run the animation at a fixed rate. Zero pauses it, which is more useful than
    /// refusing the value: a paused animation is how you look at one frame.
    /// </summary>
    private void ApplyAnimationFps(double fps)
    {
        if (_animationFrames is null || _animationTimer is null) return;

        _animationFps = Math.Clamp(fps, 0, 60);

        if (_animationFps <= 0)
        {
            _animationTimer.Stop();
        }
        else
        {
            _animationTimer.Interval = TimeSpan.FromSeconds(1 / _animationFps.Value);
            _animationTimer.Start();
        }

        UpdateAnimationSpeedUi();
    }

    private void ViewerSpeedReset_Click(object sender, RoutedEventArgs e)
    {
        if (_animationFrames is null || _animationTimer is null) return;

        _animationFps = null;
        _animationTimer.Interval = _animationFrames[_animationIndex].Delay;
        _animationTimer.Start();

        UpdateAnimationSpeedUi();
    }

    private void StopAnimation()
    {
        _animationTimer?.Stop();
        _animationTimer = null;
        _animationFrames = null;
        _animationIndex = 0;

        // A chosen rate belongs to the animation being looked at, not to the viewer, so
        // moving to another item starts from that file's own timing again.
        _animationFps = null;

        if (ViewerSpeedButton is not null) ViewerSpeedButton.Visibility = Visibility.Collapsed;
        if (ViewerSpeedPopup is not null) ViewerSpeedPopup.IsOpen = false;
    }

    /// <summary>
    /// Rename the item on screen.
    ///
    /// This existed only in the folder listing before, which made renaming an unnamed file
    /// oddly circular: open it to see what it is, close it, find the same tile again in the
    /// list, then rename. The name belongs to the thing you are looking at.
    /// </summary>
    private void ViewerRename_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerIndex < 0 || _viewerIndex >= _viewerPlaylist.Count) return;

        VaultEntry entry = _viewerPlaylist[_viewerIndex].Entry;
        RenameMediaEntry(entry);

        // The caption behind the dialog should agree with the new name once saved.
        var previous = _overlaySave;
        _overlaySave = () =>
        {
            previous?.Invoke();
            ViewerCaption.Text = entry.Attachments.FirstOrDefault()?.FileName ?? entry.Title;
        };
    }

    /// <summary>
    /// Send the item on screen to a linked device.
    ///
    /// One device links straight through; several ask which. Either way this is the same
    /// queue the Devices screen uses, so nothing is sent until that device connects and
    /// asks for it.
    /// </summary>
    private void ViewerSend_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerIndex < 0 || _viewerIndex >= _viewerPlaylist.Count) return;

        VaultEntry entry = _viewerPlaylist[_viewerIndex].Entry;
        var devices = Trust.Devices.ToList();

        if (devices.Count == 0)
        {
            OverlayTitle.Text = "No devices linked";
            OverlayCard.MaxWidth = 480;
            OverlayBody.Children.Clear();
            _fieldEditors.Clear();
            AddCompressParagraph(
                "Link a phone first, from the Devices screen. Nothing can be sent until " +
                "there is somewhere to send it.");
            OverlaySave.Visibility = Visibility.Collapsed;
            _overlaySave = null;
            ShowOverlay();
            return;
        }

        if (devices.Count == 1)
        {
            SendSingleEntry(entry, devices[0]);
            return;
        }

        OverlayTitle.Text = "Send to which device?";
        OverlayCard.MaxWidth = 520;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        AddCompressParagraph($"“{entry.Title}” will be offered to the device you pick.");

        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (TrustedDevice device in devices)
        {
            TrustedDevice captured = device;
            var button = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Content = device.Name,
                Height = 42,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 0, 8),
            };
            button.Click += (_, _) =>
            {
                CloseOverlay();
                SendSingleEntry(entry, captured);
            };
            list.Children.Add(button);
        }

        OverlayBody.Children.Add(list);
        OverlaySave.Visibility = Visibility.Collapsed;
        _overlaySave = null;
        ShowOverlay();
    }

    private void ViewerPrev_Click(object sender, RoutedEventArgs e) => StepViewer(-1);

    private void ViewerNext_Click(object sender, RoutedEventArgs e) => StepViewer(1);

    private void ImageViewer_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Left)
        {
            StepViewer(-1);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Right)
        {
            StepViewer(1);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            CloseViewer_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Space && MediaTransportBar.Visibility == Visibility.Visible)
        {
            MediaPlayPause_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ImageViewer_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.XButton1)
        {
            StepViewer(-1);
            e.Handled = true;
        }
        else if (e.ChangedButton == System.Windows.Input.MouseButton.XButton2)
        {
            StepViewer(1);
            e.Handled = true;
        }
        else if (e.ChangedButton == System.Windows.Input.MouseButton.Left &&
                 ClickedViewerBackdrop(e.OriginalSource as DependencyObject))
        {
            // Click the empty space around the media to dismiss, like Discord's lightbox.
            CloseViewer_Click(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>
    /// True when a click landed on the dark backdrop rather than on the media itself
    /// or any of the viewer's controls.
    /// </summary>
    private bool ClickedViewerBackdrop(DependencyObject? source)
    {
        for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ImageViewer))
                return true; // reached the backdrop without passing through content
            if (ReferenceEquals(node, ViewerImage) ||
                ReferenceEquals(node, ViewerMedia) ||
                ReferenceEquals(node, AudioPlayerCard) ||
                ReferenceEquals(node, MediaTransportBar) ||
                ReferenceEquals(node, PhotoCaptionPanel) ||
                node is System.Windows.Controls.Primitives.ButtonBase or Slider)
            {
                return false;
            }
        }
        return false;
    }

    private void StepViewer(int delta)
    {
        if (_viewerPlaylist.Count <= 1) return;
        int next = (_viewerIndex + delta + _viewerPlaylist.Count) % _viewerPlaylist.Count;
        var attachment = _viewerPlaylist[next].Attachment;
        if (IsImage(attachment))
            ShowViewerAt(next);
        else
            ShowMediaAt(next);
    }

    private void ViewerDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerIndex >= 0 && _viewerIndex < _viewerPlaylist.Count)
            ExportAttachmentToDisk(_viewerPlaylist[_viewerIndex].Attachment);
        else if (_viewerCurrentAttachment is not null)
            ExportAttachmentToDisk(_viewerCurrentAttachment);
    }

    private void PreparePhotoViewerUi()
    {
        CleanupMediaPlayback();
        ViewerImage.Visibility = Visibility.Visible;
        ViewerMedia.Visibility = Visibility.Collapsed;
        AudioPlayerCard.Visibility = Visibility.Collapsed;
        MediaTransportBar.Visibility = Visibility.Collapsed;
        PhotoCaptionPanel.Visibility = Visibility.Visible;
    }

    private async void ShowMediaAt(int index)
    {
        if (index < 0 || index >= _viewerPlaylist.Count) return;

        _viewerIndex = index;
        var item = _viewerPlaylist[index];
        var att = item.Attachment;
        _viewerCurrentAttachment = att;

        try
        {
            CleanupMediaPlayback();
            CleanupViewerImage();

            string ext = Path.GetExtension(att.FileName);
            if (string.IsNullOrEmpty(ext))
                ext = IsAudio(att) ? ".mp3" : ".mp4";

            // Decrypting a multi-gigabyte video and writing it to the temp folder used
            // to run on the UI thread, which froze the whole window until it finished.
            // Do it on a worker and show a spinner while it runs.
            ShowMediaLoading(att.FileName);
            int requestedIndex = index;
            VaultTempFiles.ScratchFile scratch = await Task.Run(() =>
            {
                byte[] data = _vault.ReadAttachment(att);
                try { return VaultTempFiles.Write(ext, data); }
                finally { Array.Clear(data, 0, data.Length); }
            });

            // The user may have navigated away or closed the viewer while we decrypted.
            if (_viewerIndex != requestedIndex || ImageViewer.Visibility != Visibility.Visible && _viewerPlaylist.Count == 0)
            {
                scratch.Dispose();
                HideMediaLoading();
                return;
            }

            _mediaScratch = scratch;
            HideMediaLoading();

            bool isAudio = IsAudio(att);
            bool isVideo = IsVideo(att);

            ViewerImage.Visibility = Visibility.Collapsed;
            AudioPlayerCard.Visibility = isAudio ? Visibility.Visible : Visibility.Collapsed;
            AudioTitleText.Text = Path.GetFileNameWithoutExtension(att.FileName);
            ViewerMedia.Visibility = Visibility.Visible;
            PhotoCaptionPanel.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            ViewerCaption.Text = att.FileName;

            bool multiple = _viewerPlaylist.Count > 1;
            ViewerPosition.Text = multiple ? $"{index + 1} / {_viewerPlaylist.Count}" : "";
            ViewerPosition.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            ViewerPrevButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            ViewerNextButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            ViewerDownloadFolderButton.Visibility = string.IsNullOrEmpty(_viewerFolderId)
                ? Visibility.Collapsed
                : Visibility.Visible;
            ViewerCompressButton.Visibility = IsImage(att) || IsAudio(att) || IsVideo(att)
                ? Visibility.Visible
                : Visibility.Collapsed;

            MediaTransportBar.Visibility = Visibility.Visible;
            MediaPlayPauseButton.Content = "\uE769";
            _mediaPlaying = true;
            _mediaSeekPendingValue = null;
            _mediaSeekDragging = false;
            MediaSeekSlider.Value = 0;
            MediaTimeText.Text = "0:00 / 0:00";
            MediaVolumeSlider.Value = _mediaVolume;
            ViewerMedia.Volume = _mediaVolume;

            ViewerMedia.Source = new Uri(_mediaScratch.Path);
            ViewerMedia.Play();

            if (ImageViewer.Visibility != Visibility.Visible)
            {
                ImageViewer.Visibility = Visibility.Visible;
                ImageViewer.Opacity = 0;
                Anim.FadeIn(ImageViewer, 220);
            }

            _mediaTimer.Start();
            ImageViewer.Focus();
        }
        catch (Exception ex)
        {
            HideMediaLoading();
            MessageBox.Show("Could not play file: " + ex.Message, "Lockwell");
        }
    }

    private void ShowMediaLoading(string fileName)
    {
        MediaLoadingText.Text = fileName;
        MediaLoadingPanel.Visibility = Visibility.Visible;
        if (ImageViewer.Visibility != Visibility.Visible)
        {
            ImageViewer.Visibility = Visibility.Visible;
            ImageViewer.Opacity = 0;
            Anim.FadeIn(ImageViewer, 200);
        }
    }

    private void HideMediaLoading() => MediaLoadingPanel.Visibility = Visibility.Collapsed;

    private void MediaVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _mediaVolume = e.NewValue;
        if (MediaVolumeText is not null)
            MediaVolumeText.Text = $"{(int)Math.Round(_mediaVolume * 100)}%";
        if (ViewerMedia.Source is not null)
            ViewerMedia.Volume = _mediaVolume;
    }

    private void CleanupMediaPlayback()
    {
        _mediaTimer.Stop();
        try
        {
            ViewerMedia.Stop();
            ViewerMedia.Close();
            ViewerMedia.Source = null;
        }
        catch { /* best effort */ }

        _mediaScratch?.Dispose();
        _mediaScratch = null;
        _mediaPlaying = false;
        _mediaSeekDragging = false;
        _mediaSeekPendingValue = null;
    }

    private void ViewerMedia_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (ViewerMedia.NaturalDuration.HasTimeSpan)
            MediaSeekSlider.Maximum = ViewerMedia.NaturalDuration.TimeSpan.TotalSeconds;
        UpdateMediaSeekUi();
    }

    /// <summary>
    /// The player refused the file. Usually that is not a broken file and not a missing
    /// codec, but the older of Windows' two media stacks meeting a format only the newer
    /// one understands, so offer the conversion that fixes it rather than a dead end.
    /// </summary>
    private async void ViewerMedia_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_viewerCurrentAttachment is null)
        {
            MessageBox.Show("Could not play this file in Lockwell.", "Lockwell");
            CloseViewer_Click(sender, e);
            return;
        }

        AttachmentRef att = _viewerCurrentAttachment;

        // One attempt only. If the converted copy also fails, saying so plainly beats
        // converting the same file forever.
        if (_mediaConversionTried)
        {
            MessageBox.Show(
                "Windows could not play this file even after converting it.\n\n" +
                "The file itself is untouched in your vault, and Download still saves the " +
                "original.",
                "Lockwell");
            _mediaConversionTried = false;
            CloseViewer_Click(sender, e);
            return;
        }

        if (!IsVideo(att) && !IsAudio(att))
        {
            MessageBox.Show("Could not play this file in Lockwell.", "Lockwell");
            CloseViewer_Click(sender, e);
            return;
        }

        var answer = MessageBox.Show(
            $"Windows' media player cannot open {Path.GetExtension(att.FileName).ToUpperInvariant().TrimStart('.')} " +
            "files directly.\n\n" +
            "This is a limitation of the player, not of the file: Windows can decode it, " +
            "just not through the interface Lockwell plays with. Lockwell can convert a " +
            "copy in memory and play that instead.\n\n" +
            "The stored file is not changed, and nothing is written to disk that is not " +
            "already written for normal playback.\n\n" +
            "Convert and play it now?",
            "Lockwell", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            CloseViewer_Click(sender, e);
            return;
        }

        _mediaConversionTried = true;
        await PlayConvertedAsync(att);
    }

    /// <summary>Convert in memory, then hand the player the converted copy.</summary>
    private async Task PlayConvertedAsync(AttachmentRef att)
    {
        ShowMediaLoading("Converting " + att.FileName);

        try
        {
            byte[] converted = await Task.Run(async () =>
            {
                byte[] data = _vault.ReadAttachment(att);
                try { return await MediaCompression.ConvertForPlaybackAsync(data); }
                finally { Array.Clear(data, 0, data.Length); }
            });

            _mediaScratch?.Dispose();
            _mediaScratch = VaultTempFiles.Write(".mp4", converted);
            Array.Clear(converted, 0, converted.Length);

            HideMediaLoading();

            ViewerMedia.Source = new Uri(_mediaScratch.Path);
            ViewerMedia.Play();
            _mediaPlaying = true;
            MediaPlayPauseButton.Content = "";
        }
        catch (Exception ex)
        {
            HideMediaLoading();
            _mediaConversionTried = false;
            MessageBox.Show(
                "Lockwell could not convert this file.\n\n" + ex.Message +
                "\n\nThe file in your vault is unchanged.",
                "Lockwell");
            CloseViewer_Click(this, new RoutedEventArgs());
        }
    }

    private void ViewerMedia_MediaEnded(object sender, RoutedEventArgs e)
    {
        _mediaPlaying = false;
        MediaPlayPauseButton.Content = "\uE768";
        ViewerMedia.Stop();
        UpdateMediaSeekUi();
    }

    private void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerMedia.Source is null) return;

        if (ViewerMedia.NaturalDuration.HasTimeSpan &&
            ViewerMedia.Position >= ViewerMedia.NaturalDuration.TimeSpan)
        {
            ViewerMedia.Position = TimeSpan.Zero;
            ViewerMedia.Play();
            _mediaPlaying = true;
            MediaPlayPauseButton.Content = "\uE769";
            return;
        }

        if (_mediaPlaying)
        {
            ViewerMedia.Pause();
            _mediaPlaying = false;
            MediaPlayPauseButton.Content = "\uE768";
        }
        else
        {
            ViewerMedia.Play();
            _mediaPlaying = true;
            MediaPlayPauseButton.Content = "\uE769";
        }
    }

    private void MediaSeekSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _mediaSeekDragging = true;

    private void MediaSeekSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _mediaSeekDragging = false;
        ApplyMediaSeek(MediaSeekSlider.Value);
    }

    private void MediaSeekSlider_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_mediaSeekDragging) return;
        _mediaSeekDragging = false;
        ApplyMediaSeek(MediaSeekSlider.Value);
    }

    private void MediaSeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaSeekDragging)
            ApplyMediaSeek(e.NewValue);
    }

    private void ApplyMediaSeek(double seconds)
    {
        if (!ViewerMedia.NaturalDuration.HasTimeSpan) return;

        seconds = Math.Clamp(seconds, 0, MediaSeekSlider.Maximum);
        _mediaSeekPendingValue = seconds;
        MediaSeekSlider.Value = seconds;

        var target = TimeSpan.FromSeconds(seconds);
        ViewerMedia.Position = target;

        var duration = ViewerMedia.NaturalDuration.TimeSpan;
        MediaTimeText.Text = $"{FormatMediaTime(target)} / {FormatMediaTime(duration)}";
    }

    private void UpdateMediaSeekUi()
    {
        if (!ViewerMedia.NaturalDuration.HasTimeSpan) return;

        var duration = ViewerMedia.NaturalDuration.TimeSpan;

        if (_mediaSeekPendingValue is double pending)
        {
            if (!_mediaSeekDragging && Math.Abs(ViewerMedia.Position.TotalSeconds - pending) < 0.45)
                _mediaSeekPendingValue = null;
            else
            {
                MediaSeekSlider.Value = pending;
                MediaTimeText.Text =
                    $"{FormatMediaTime(TimeSpan.FromSeconds(pending))} / {FormatMediaTime(duration)}";
                return;
            }
        }

        if (!_mediaSeekDragging)
            MediaSeekSlider.Value = ViewerMedia.Position.TotalSeconds;

        MediaTimeText.Text =
            $"{FormatMediaTime(ViewerMedia.Position)} / {FormatMediaTime(duration)}";
    }

    private static string FormatMediaTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void ViewerDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewerFolderId)) return;
        var folder = _vault.Data.MediaFolders.FirstOrDefault(f => f.Id == _viewerFolderId);
        if (folder is null) return;

        var dlg = new OpenFolderDialog { Title = "Choose where to save the folder" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            int exported = ExportMediaFolderToDisk(folder, dlg.FolderName);
            MessageBox.Show(
                exported == 1
                    ? "Exported 1 file. Remember it is now unprotected on disk."
                    : $"Exported {exported} files. Remember they are now unprotected on disk.",
                "Lockwell");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not export folder: " + ex.Message, "Lockwell");
        }
    }

    private void ExportAttachmentToDisk(AttachmentRef att)
    {
        var save = new SaveFileDialog { FileName = att.FileName };
        if (save.ShowDialog() != true) return;

        try
        {
            byte[] data = _vault.ReadAttachment(att);
            File.WriteAllBytes(save.FileName, data);
            Array.Clear(data, 0, data.Length);
            MessageBox.Show(
                "A decrypted copy was saved. Remember it is now unprotected on disk.",
                "Lockwell");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save file: " + ex.Message, "Lockwell");
        }
    }

    private int ExportMediaFolderToDisk(MediaFolder folder, string destinationRoot)
    {
        string safeName = SanitizePathName(folder.Name);
        string targetDir = UniqueDirectoryPath(destinationRoot, safeName);
        Directory.CreateDirectory(targetDir);

        int exported = 0;
        foreach (var entry in _vault.Data.Entries.Where(e => e.MediaFolderId == folder.Id))
            exported += ExportEntryToDirectory(entry, targetDir);

        foreach (var sub in _vault.Data.MediaFolders.Where(f => f.ParentFolderId == folder.Id))
            exported += ExportMediaFolderToDisk(sub, targetDir);

        return exported;
    }

    private int ExportEntryToDirectory(VaultEntry entry, string directory)
    {
        var att = entry.Attachments.FirstOrDefault();
        if (att is null) return 0;

        string path = UniqueFilePath(directory, att.FileName);
        byte[] data = _vault.ReadAttachment(att);
        File.WriteAllBytes(path, data);
        Array.Clear(data, 0, data.Length);
        return 1;
    }

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

    private static string UniqueDirectoryPath(string parent, string folderName)
    {
        string path = Path.Combine(parent, folderName);
        if (!Directory.Exists(path)) return path;

        for (int i = 2; i < 1000; i++)
        {
            path = Path.Combine(parent, $"{folderName} ({i})");
            if (!Directory.Exists(path)) return path;
        }

        return Path.Combine(parent, Guid.NewGuid().ToString("N"));
    }

    private void OpenAttachment(AttachmentRef att)
    {
        try
        {
            if (IsImage(att))
            {
                if (_currentEntry is not null)
                    OpenMediaItem(_currentEntry, att);
                else
                {
                    _viewerPlaylist.Clear();
                    _viewerPlaylist.Add(new ViewerMediaItem(new VaultEntry(), att));
                    _viewerIndex = 0;
                    _viewerFolderId = null;
                    ShowViewerAt(0);
                }
                return;
            }

            if (IsAudio(att) || IsVideo(att))
            {
                if (_currentEntry is not null)
                    OpenMediaItem(_currentEntry, att);
                else
                {
                    _viewerPlaylist.Clear();
                    _viewerPlaylist.Add(new ViewerMediaItem(new VaultEntry(), att));
                    _viewerIndex = 0;
                    _viewerFolderId = null;
                    ShowMediaAt(0);
                }
                return;
            }

            byte[] data = _vault.ReadAttachment(att);
            var save = new SaveFileDialog { FileName = att.FileName };
                if (save.ShowDialog() == true)
                {
                    File.WriteAllBytes(save.FileName, data);
                    MessageBox.Show(
                        "A decrypted copy was saved. Remember it is now unprotected on disk.",
                        "Lockwell");
                }
            Array.Clear(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open file: " + ex.Message, "Lockwell");
        }
    }

    private void DeleteAttachmentPrompt(AttachmentRef att)
    {
        if (_currentEntry is null) return;
        if (MessageBox.Show($"Remove \"{att.FileName}\" from the vault?", "Lockwell",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _vault.DeleteAttachment(att);
        _currentEntry.Attachments.Remove(att);
        _currentEntry.ModifiedUtc = DateTime.UtcNow;
        _vault.Save();
        RefreshAttachments();
    }

    private void CloseViewer_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerClosing) return;
        _viewerClosing = true;

        CleanupMediaPlayback();
        HideMediaLoading();

        // Scale whichever surface is actually on screen; the backdrop only fades, so
        // the app behind never appears to shrink with it.
        FrameworkElement? content =
            AudioPlayerCard.Visibility == Visibility.Visible ? AudioPlayerCard :
            ViewerMedia.Visibility == Visibility.Visible ? ViewerMedia :
            ViewerImage;

        Anim.PopOut(ImageViewer, content, () =>
        {
            ImageViewer.Visibility = Visibility.Collapsed;
            CleanupViewerImage();
            Anim.ResetMotion(ImageViewer);
            Anim.ResetMotion(content);
            _viewerPlaylist.Clear();
            _viewerIndex = -1;
            _viewerFolderId = null;
            _viewerCurrentAttachment = null;
            _viewerClosing = false;
        });
    }

    // ----------------------------------------------------- add / edit entry

    private void AddEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaMode) { AddMedia_Click(sender, e); return; }
        OpenEntryEditor(null);
    }

    private void EditEntry_Click(object sender, RoutedEventArgs e) => OpenEntryEditor(_currentEntry);

    private void OpenEntryEditor(VaultEntry? existing)
    {
        bool isNew = existing is null;
        var working = existing ?? new VaultEntry { Kind = DefaultKindForCurrent() };
        OverlayTitle.Text = isNew ? "New entry" : "Edit entry";

        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        var titleBox = AddLabeledInput("Title", working.Title);

        OverlayBody.Children.Add(new TextBlock { Text = "Type", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 14, 0, 6) });
        var kindPanel = new WrapPanel();
        EntryKind selectedKind = working.Kind;
        var chipButtons = new Dictionary<EntryKind, Button>();
        foreach (EntryKind kind in new[] { EntryKind.Login, EntryKind.SecureNote, EntryKind.CryptoWallet, EntryKind.Card, EntryKind.Media })
        {
            var chip = MakeChip(KindLabel(kind), kind == selectedKind);
            chipButtons[kind] = chip;
            chip.Click += (_, _) =>
            {
                selectedKind = kind;
                foreach (var kv in chipButtons) StyleChip(kv.Value, kv.Key == kind);
                if (isNew) RebuildFieldEditors(DefaultFields(kind));
            };
            kindPanel.Children.Add(chip);
        }
        OverlayBody.Children.Add(kindPanel);

        OverlayBody.Children.Add(new TextBlock { Text = "Fields", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 16, 0, 6) });
        var fieldsHost = new StackPanel();
        OverlayBody.Children.Add(fieldsHost);
        _fieldsHost = fieldsHost;

        RebuildFieldEditors(working.Fields.Count > 0 ? working.Fields : DefaultFields(working.Kind));

        var addFieldBtn = new Button { Style = (Style)FindResource("LinkButton"), Content = "+ Add field", Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        addFieldBtn.Click += (_, _) => AddFieldEditor(new EntryField { Label = "Field", Value = "" });
        OverlayBody.Children.Add(addFieldBtn);

        var notesBox = AddLabeledInput("Notes", working.Notes, multiline: true, topMargin: 16);

        _overlaySave = () =>
        {
            string title = titleBox.Text.Trim();
            if (title.Length == 0) { MessageBox.Show("Give the entry a title.", "Lockwell"); return; }

            working.Title = title;
            working.Kind = selectedKind;
            working.Notes = notesBox.Text;
            working.Fields = _fieldEditors
                .Select(fe => fe.ToField())
                .Where(f => f.Label.Length > 0 || f.Value.Length > 0)
                .ToList();
            working.ModifiedUtc = DateTime.UtcNow;

            if (isNew)
            {
                working.CategoryId = TargetCategoryId();
                _vault.Data.Entries.Add(working);
            }
            _vault.Save();
            UpdateStats();
            RefreshCategories();
            CloseOverlay();
            if (_mediaMode) RefreshMediaGallery();
            else { RefreshEntries(); ShowDetail(working); }
        };

        ShowOverlay();
    }

    private void RebuildFieldEditors(IEnumerable<EntryField> fields)
    {
        if (_fieldsHost is null) return;
        _fieldsHost.Children.Clear();
        _fieldEditors.Clear();
        foreach (var f in fields) AddFieldEditor(new EntryField { Label = f.Label, Value = f.Value, IsSecret = f.IsSecret });
    }

    private void AddFieldEditor(EntryField field)
    {
        if (_fieldsHost is null) return;
        var editor = new FieldEditor(field, FindResource);
        editor.RemoveRequested += () =>
        {
            _fieldsHost.Children.Remove(editor.Root);
            _fieldEditors.Remove(editor);
        };
        _fieldEditors.Add(editor);
        _fieldsHost.Children.Add(editor.Root);
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEntry is null) return;
        if (MessageBox.Show($"Delete \"{_currentEntry.Title}\"? This cannot be undone.", "Lockwell",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (var att in _currentEntry.Attachments) _vault.DeleteAttachment(att);
        _vault.Data.Entries.Remove(_currentEntry);
        _vault.Save();
        UpdateStats();
        RefreshCategories();
        ClearDetail();
        RefreshEntries();
    }

    // ----------------------------------------------------- category / settings / lock

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        OverlayTitle.Text = "New section";
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();
        var nameBox = AddLabeledInput("Name", "");
        var mediaToggle = AddToggle("Media section (photos, videos, and audio)", false);
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Media sections show a gallery instead of a login list.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        _overlaySave = () =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) { MessageBox.Show("Give the section a name.", "Lockwell"); return; }
            _vault.Data.Categories.Add(new Category
            {
                Name = name,
                Glyph = mediaToggle.IsChecked == true ? "\uEB9F" : "\uE8B7",
                Order = _vault.Data.Categories.Count,
                IsMedia = mediaToggle.IsChecked == true,

                // Made here rather than shipped, so it belongs under "Your sections".
                IsCustom = true,
            });
            _vault.Save();

            // Unfold, or the section someone just made would appear to vanish.
            _customSectionsCollapsed = false;
            RefreshCategories();
            CloseOverlay();
        };
        ShowOverlay();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        OverlayTitle.Text = "Settings";
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        var capture = AddToggle("Hide window from screen capture", _settings.ExcludeFromCapture);
        OverlayBody.Children.Add(new TextBlock { Text = "Window appears blank in OBS, Discord share, and recorders.", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 10) });
        var preflight = AddToggle("Run environment scan on launch", _settings.RunPreflight);
        var blurMedia = AddToggle("Blur photos until opened", _settings.BlurMediaThumbnails);
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Thumbnails stay blurred in the gallery. Full image shows only when you open it.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        var hideNsfw = AddToggle("Hide NSFW media folders", _settings.HideNsfwMediaFolders);
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Folders marked NSFW stay hidden in the media gallery. They never appear in All items.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        var lockOnMinimize = AddToggle("Lock when minimized", _settings.LockOnMinimize);
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Locks immediately when you minimize the window. Turn off if you need the vault open while using another app.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        var shredOriginals = AddToggle("Offer to remove originals after importing", _settings.OfferShredAfterImport);
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "After a file is safely in the vault, ask whether to overwrite and delete the copy " +
                   "it came from. Always asks, never automatic. It defeats undelete tools; on an SSD it " +
                   "cannot guarantee the old bytes are gone from the drive, which is what disk encryption " +
                   "is for.",
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        var lockBox = AddLabeledInput("Auto-lock after (minutes)", _settings.AutoLockMinutes.ToString(), topMargin: 14);
        var clipBox = AddLabeledInput("Clear clipboard after (seconds)", _settings.ClipboardClearSeconds.ToString(), topMargin: 14);

        AddUpdateSettings();
        AddAboutSettings();

        OverlayBody.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Stroke"), Margin = new Thickness(0, 18, 0, 14) });
        OverlayBody.Children.Add(new TextBlock { Text = "Backup", Style = (Style)FindResource("H2") });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Export creates an encrypted ZIP you can store on USB or cloud. Import adds new items only. Nothing is overwritten.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 4, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        var profile = _profiles.ActiveProfile;
        DateTime? lastBackup = ResolveLastBackupUtc();
        string backupStatus;
        Brush backupBrush = (Brush)FindResource("Muted");
        if (lastBackup is null)
        {
            backupStatus = "No backup recorded yet. A backup protects against drive failure, not a forgotten password.";
            backupBrush = (Brush)FindResource("Warning");
        }
        else
        {
            var ago = DateTime.UtcNow - lastBackup.Value;
            backupStatus = ago.TotalDays >= 1
                ? $"Last backup: {lastBackup.Value.ToLocalTime():g} ({(int)ago.TotalDays} day{((int)ago.TotalDays == 1 ? "" : "s")} ago)"
                : $"Last backup: {lastBackup.Value.ToLocalTime():g} (today)";
            if (ago.TotalDays > 30)
                backupBrush = (Brush)FindResource("Warning");
        }

        OverlayBody.Children.Add(new TextBlock
        {
            Text = backupStatus,
            Style = (Style)FindResource("Body"),
            Foreground = backupBrush,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        var exportBtn = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Export backup...",
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
            Margin = new Thickness(0, 0, 0, 8),
        };
        exportBtn.Click += (_, _) => ExportBackup();
        OverlayBody.Children.Add(exportBtn);

        var importBtn = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Import backup...",
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
            Margin = new Thickness(0, 0, 0, 4),
        };
        importBtn.Click += (_, _) => BeginImportBackup();
        OverlayBody.Children.Add(importBtn);

        OverlayBody.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Stroke"), Margin = new Thickness(0, 18, 0, 14) });
        OverlayBody.Children.Add(new TextBlock { Text = "Profiles", Style = (Style)FindResource("H2") });
        string profileName = _profiles.ActiveProfile?.Name ?? "Default";
        OverlayBody.Children.Add(new TextBlock
        {
            Text = $"Current profile: {profileName}",
            Style = (Style)FindResource("Body"),
            Margin = new Thickness(0, 4, 0, 10),
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Each profile is a separate vault with its own master password.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        var renameVaultBtn = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Rename vault...",
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
            Margin = new Thickness(0, 0, 0, 8),
        };
        renameVaultBtn.Click += (_, _) => BeginRenameVault();
        OverlayBody.Children.Add(renameVaultBtn);
        var switchProfileBtn = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Switch profile...",
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
        };
        switchProfileBtn.Click += (_, _) =>
        {
            CloseOverlay();
            _onLock();
            _onSwitchProfile();
        };
        OverlayBody.Children.Add(switchProfileBtn);

        OverlayBody.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Stroke"), Margin = new Thickness(0, 18, 0, 14) });
        OverlayBody.Children.Add(new TextBlock { Text = "Master password", Style = (Style)FindResource("H2") });
        OverlayBody.Children.Add(new TextBlock { Text = "Changing it re-wraps the same data. Your entries stay intact.", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 8) });
        var newPw = AddLabeledPassword("New master password");
        var confirmPw = AddLabeledPassword("Confirm new password", topMargin: 10);

        OverlayBody.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Stroke"), Margin = new Thickness(0, 18, 0, 14) });
        OverlayBody.Children.Add(new TextBlock { Text = "Vault location", Style = (Style)FindResource("Caption") });
        OverlayBody.Children.Add(new TextBlock { Text = _vault.VaultDir, Style = (Style)FindResource("Body"), Margin = new Thickness(0, 2, 0, 0), FontSize = 12 });

        _overlaySave = () =>
        {
            _settings.ExcludeFromCapture = capture.IsChecked == true;
            _settings.RunPreflight = preflight.IsChecked == true;
            _settings.BlurMediaThumbnails = blurMedia.IsChecked == true;
            _settings.HideNsfwMediaFolders = hideNsfw.IsChecked == true;
            _settings.LockOnMinimize = lockOnMinimize.IsChecked == true;
            _settings.OfferShredAfterImport = shredOriginals.IsChecked == true;
            _updateSettingsCommit?.Invoke();
            if (int.TryParse(lockBox.Text, out int m) && m >= 1) _settings.AutoLockMinutes = m;
            if (int.TryParse(clipBox.Text, out int s) && s >= 5) _settings.ClipboardClearSeconds = s;
            _applySettings();

            using (SecureString newPassword = newPw.SecurePassword)
            using (SecureString confirmPassword = confirmPw.SecurePassword)
            {
                if (newPassword.Length > 0 || confirmPassword.Length > 0)
                {
                    if (newPassword.Length < 10) { MessageBox.Show("New password must be at least 10 characters.", "Lockwell"); return; }
                    if (!SecurePassword.SecureEquals(newPassword, confirmPassword)) { MessageBox.Show("New passwords do not match.", "Lockwell"); return; }
                    _vault.ChangePassword(newPassword);
                    MessageBox.Show("Master password changed.", "Lockwell");
                }
            }
            CloseOverlay();
            RefreshCategories();
            if (_mediaMode) RefreshMediaGallery();
            else RefreshEntries();
        };
        ShowOverlay();
    }

    private void BeginRenameVault()
    {
        var profile = _profiles.ActiveProfile;
        if (profile is null) return;

        CloseOverlay();
        OverlayTitle.Text = "Rename vault";
        OverlayCard.MaxWidth = 560;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "This only changes the name shown in Lockwell on this device.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        var nameBox = AddLabeledInput("Vault name", profile.Name);

        _overlaySave = () =>
        {
            try
            {
                _profiles.RenameProfile(profile, nameBox.Text);
                CloseOverlay();
                MessageBox.Show("Vault renamed.", "Lockwell");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Lockwell");
            }
        };
        ShowOverlay();
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => _onLock();

    private async void ExportBackup()
    {
        if (_busyOperation) return;

        string stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var dlg = new SaveFileDialog
        {
            Title = "Export encrypted backup",
            FileName = $"Lockwell-backup-{stamp}.zip",
            Filter = "Lockwell backup|*.zip|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        ShowProgress("Exporting backup", "Starting...");
        try
        {
            var progress = CreateUiProgressReporter();
            await Task.Run(() => VaultBackupService.Export(_vault, dlg.FileName, progress));

            // The timestamp now lives inside the encrypted vault (set by Export), so it
            // is no longer copied into plaintext settings.json where anyone could read
            // how often this vault gets backed up.
            RefreshHome();

            await FinishProgressAsync("Backup saved.");
            MessageBox.Show(
                "Backup saved. The ZIP stays encrypted. Keep it with your master password or recovery key.",
                "Lockwell");
        }
        catch (Exception ex)
        {
            HideProgress();
            MessageBox.Show("Could not export backup: " + ex.Message, "Lockwell");
        }
    }

    private async void RunBackupImportAsync(string zipPath, SecureString password)
    {
        if (_busyOperation)
        {
            password.Dispose();
            return;
        }

        ShowProgress("Importing backup", "Starting...");
        try
        {
            var progress = CreateUiProgressReporter();
            VaultMergeResult result = await Task.Run(() =>
                VaultBackupService.ImportMerge(_vault, zipPath, password, progress));

            await FinishProgressAsync("Import complete.");
            RefreshCategories();
            if (_mediaMode) RefreshMediaGallery();
            else RefreshEntries();

            MessageBox.Show(
                $"Import complete.\nAdded {result.EntriesAdded} entries, {result.FoldersAdded} folders, {result.AttachmentsCopied} files.\nSkipped {result.EntriesSkipped} duplicates.",
                "Lockwell");
        }
        catch (CryptographicException)
        {
            HideProgress();
            MessageBox.Show("Wrong password or backup file is damaged.", "Lockwell");
        }
        catch (Exception ex)
        {
            HideProgress();
            MessageBox.Show("Could not import backup: " + ex.Message, "Lockwell");
        }
        finally
        {
            password.Dispose();
        }
    }

    private void BeginImportBackup()
    {
        var pick = new OpenFileDialog
        {
            Title = "Import encrypted backup",
            Filter = "Lockwell backup|*.zip|All files|*.*",
        };
        if (pick.ShowDialog() != true) return;

        CloseOverlay();
        OverlayTitle.Text = "Import backup";
        OverlayCard.MaxWidth = 560;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Enter the master password for this backup file.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "New entries are added to your vault. Existing items are never replaced.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        var pwBox = AddLabeledPassword("Backup master password");

        string zipPath = pick.FileName;
        _overlaySave = () =>
        {
            SecureString password = pwBox.SecurePassword;
            if (password.Length == 0)
            {
                password.Dispose();
                MessageBox.Show("Enter the backup password.", "Lockwell");
                return;
            }

            CloseOverlay();
            RunBackupImportAsync(zipPath, password);
        };
        ShowOverlay();
    }

    // ----------------------------------------------------- overlay

    private void ShowOverlay(Control? focusTarget = null)
    {
        ClearInlineOverlayError();

        // A close animation may still be running. Cancel it so its completion callback
        // cannot collapse the overlay we are opening, and so the opacity hold from the
        // previous animation is released before we start a new one.
        _overlayClosing = false;
        Anim.ResetMotion(Overlay);
        Anim.ResetMotion(OverlayCard);

        Overlay.Visibility = Visibility.Visible;
        Anim.FadeIn(Overlay, 150);
        Anim.PopIn(OverlayCard, 240);

        if (focusTarget is not null)
        {
            // Focus only after layout settles. Focusing while the card is still animating
            // makes the ScrollViewer's bring-into-view fight the entrance transform, which
            // shows up as the card flickering several times on open.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                focusTarget.Focus();
                if (focusTarget is TextBox tb) tb.SelectAll();
            }));
        }
    }

    private void CloseOverlay()
    {
        if (_overlayClosing || Overlay.Visibility != Visibility.Visible) return;
        _overlayClosing = true;

        Anim.PopOut(Overlay, OverlayCard, () =>
        {
            // A new ShowOverlay may have superseded this close mid-animation.
            if (!_overlayClosing) return;
            _overlayClosing = false;
            Overlay.Visibility = Visibility.Collapsed;
            Anim.ResetMotion(Overlay);
            Anim.ResetMotion(OverlayCard);
            OverlayCard.MaxWidth = OverlayDefaultMaxWidth;
            OverlaySave.Content = "Save"; // dialogs may relabel it; always reset
            _overlaySave = null;
            _folderItemHost = null;
            _folderEditDeleteIds.Clear();
        }, 130);
    }

    private void OverlayCancel_Click(object sender, RoutedEventArgs e) => CloseOverlay();
    private void OverlaySave_Click(object sender, RoutedEventArgs e) => _overlaySave?.Invoke();

    /// <summary>Validation feedback inside the dialog instead of a Windows message box.</summary>
    private void ShowInlineOverlayError(string message)
    {
        OverlayErrorText.Text = message;
        OverlayErrorBanner.Visibility = Visibility.Visible;
        Anim.FadeIn(OverlayErrorBanner, 160);
    }

    private void ClearInlineOverlayError() =>
        OverlayErrorBanner.Visibility = Visibility.Collapsed;

    private void Overlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            CloseOverlay();
            return;
        }

        // Enter commits, except in a multi-line box where it should insert a newline.
        if (e.Key == System.Windows.Input.Key.Enter &&
            !(e.OriginalSource is TextBox { AcceptsReturn: true }))
        {
            e.Handled = true;
            _overlaySave?.Invoke();
        }
    }

    private TextBox AddLabeledInput(string label, string value, bool multiline = false, double topMargin = 0)
    {
        OverlayBody.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, topMargin, 0, 6) });
        var box = new TextBox { Style = (Style)FindResource("InputBox"), Text = value };
        if (multiline)
        {
            box.Height = 90;
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.VerticalContentAlignment = VerticalAlignment.Top;
            box.Padding = new Thickness(12, 8, 12, 8);
        }
        OverlayBody.Children.Add(box);
        return box;
    }

    /// <summary>
    /// Labelled input with greyed placeholder text that is a real hint, not prefilled
    /// content the user has to delete before typing.
    /// </summary>
    private TextBox AddLabeledInputWithPlaceholder(
        string label, string placeholder, string value = "", double topMargin = 0)
    {
        OverlayBody.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, topMargin, 0, 6),
        });

        var box = new TextBox { Style = (Style)FindResource("InputBox"), Text = value };
        var hint = new TextBlock
        {
            Text = placeholder,
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 13,
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) =>
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var host = new Grid();
        host.Children.Add(box);
        host.Children.Add(hint);
        OverlayBody.Children.Add(host);
        return box;
    }

    private PasswordBox AddLabeledPassword(string label, double topMargin = 0)
    {
        OverlayBody.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, topMargin, 0, 6) });
        var box = new PasswordBox { Style = (Style)FindResource("InputPassword") };
        OverlayBody.Children.Add(box);
        return box;
    }

    private CheckBox AddToggle(string label, bool isChecked)
    {
        var cb = new CheckBox { Content = label, IsChecked = isChecked, Foreground = (Brush)FindResource("Text"), Margin = new Thickness(0, 6, 0, 4) };
        OverlayBody.Children.Add(cb);
        return cb;
    }

    private Button MakeChip(string text, bool selected)
    {
        var b = new Button { Content = text, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 8, 8) };
        StyleChip(b, selected);
        return b;
    }

    private void StyleChip(Button b, bool selected)
    {
        b.Style = (Style)FindResource("GhostButton");
        b.Height = 34;
        b.MinWidth = 70;
        b.Padding = new Thickness(14, 0, 14, 0);
        b.Foreground = selected ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
    }

    // ----------------------------------------------------- helpers

    private string TargetCategoryId()
    {
        if (_currentCategory is not null && _currentCategory.Category.Id is not (AllId or FavId))
            return _currentCategory.Category.Id;
        return _vault.Data.Categories.FirstOrDefault(c => !c.IsMedia)?.Id
               ?? _vault.Data.Categories.FirstOrDefault()?.Id
               ?? "";
    }

    private EntryKind DefaultKindForCurrent()
    {
        if (_currentCategory is not null && _currentCategory.Category.Id is not (AllId or FavId))
        {
            if (_currentCategory.Category.IsMedia) return EntryKind.Media;
            var name = _currentCategory.Category.Name.ToLowerInvariant();
            if (name.Contains("crypto")) return EntryKind.CryptoWallet;
            if (name.Contains("bank") || name.Contains("finance") || name.Contains("card")) return EntryKind.Card;
            if (name.Contains("note")) return EntryKind.SecureNote;
        }
        return EntryKind.Login;
    }

    private static List<EntryField> DefaultFields(EntryKind kind) => kind switch
    {
        EntryKind.Login => new()
        {
            new EntryField { Label = "Username / email", Value = "" },
            new EntryField { Label = "Password", Value = "", IsSecret = true },
            new EntryField { Label = "Website", Value = "" },
        },
        // Only the two things actually worth locking away, plus where they point. A
        // wallet's name is not a secret and an exchange login belongs in a Login section,
        // so neither earns a field here.
        EntryKind.CryptoWallet => new()
        {
            new EntryField { Label = "Address", Value = "" },
            new EntryField { Label = SeedPhraseLabel, Value = "", IsSecret = true },
            new EntryField { Label = "Private key", Value = "", IsSecret = true },
        },
        EntryKind.Card => new()
        {
            new EntryField { Label = "Cardholder", Value = "" },
            new EntryField { Label = "Number", Value = "", IsSecret = true },
            new EntryField { Label = "Expiry", Value = "" },
            new EntryField { Label = "CVV", Value = "", IsSecret = true },
        },
        EntryKind.Media => new()
        {
            new EntryField { Label = "Description", Value = "" },
        },
        _ => new(),
    };

    private static string KindLabel(EntryKind kind) => kind switch
    {
        EntryKind.Login => "Login",
        EntryKind.SecureNote => "Note",
        EntryKind.CryptoWallet => "Crypto",
        EntryKind.Card => "Card",
        EntryKind.Media => "Media",
        _ => "Item",
    };

    private static string GlyphForKind(EntryKind kind) => kind switch
    {
        EntryKind.Login => "\uE77B",
        EntryKind.SecureNote => "\uE70B",
        EntryKind.CryptoWallet => "\uE8D7",
        EntryKind.Card => "\uE825",
        EntryKind.Media => "\uEB9F",
        _ => "\uE8F4",
    };

    private static bool IsImage(AttachmentRef att) => att.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    private static bool IsGif(AttachmentRef att) =>
        att.MediaType.Equals("image/gif", StringComparison.OrdinalIgnoreCase) ||
        att.FileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    private static bool IsVideo(AttachmentRef att) => att.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    private static bool IsAudio(AttachmentRef att) => att.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    private static string MediaIcon(AttachmentRef att) => att switch
    {
        _ when IsImage(att) => "\uEB9F",
        _ when IsVideo(att) => "\uE768",
        _ when IsAudio(att) => "\uE189",
        _ => "\uE7C3",
    };

    private static string FolderKindIcon(MediaFolderKind kind) => kind switch
    {
        MediaFolderKind.Photos => "\uEB9F",
        MediaFolderKind.Video => "\uE768",
        MediaFolderKind.Audio => "\uE189",
        _ => "\uE8B7",
    };

    private static string FolderKindLabel(MediaFolderKind kind) => kind switch
    {
        MediaFolderKind.Photos => "Photos",
        MediaFolderKind.Video => "Video",
        MediaFolderKind.Audio => "Audio",
        _ => "Mixed",
    };

    private Brush FolderKindBrush(MediaFolderKind kind) => kind switch
    {
        MediaFolderKind.Photos => (Brush)FindResource("Accent"),
        MediaFolderKind.Video => (Brush)FindResource("Accent2"),
        MediaFolderKind.Audio => (Brush)FindResource("Violet"),
        _ => (Brush)FindResource("Muted"),
    };

    private static Color FolderTintColor(MediaFolderKind kind) => kind switch
    {
        MediaFolderKind.Photos => Color.FromArgb(0x38, 0x2D, 0xD4, 0xBF),
        MediaFolderKind.Video => Color.FromArgb(0x38, 0x38, 0xBD, 0xF8),
        MediaFolderKind.Audio => Color.FromArgb(0x38, 0x8B, 0x5C, 0xF6),
        _ => Color.FromArgb(0xFF, 0x11, 0x15, 0x1F),
    };

    private int NextMediaSortOrder(string categoryId, string folderId) =>
        _vault.Data.Entries
            .Where(e => e.CategoryId == categoryId && e.MediaFolderId == folderId)
            .Select(e => e.MediaSortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private static MediaFolderKind GuessFolderKindFromPaths(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return MediaFolderKind.Mixed;
        bool allImages = list.All(p => IsImageExtension(Path.GetExtension(p).ToLowerInvariant()));
        bool allVideo = list.All(p => IsVideoExtension(Path.GetExtension(p).ToLowerInvariant()));
        bool allAudio = list.All(p => IsAudioExtension(Path.GetExtension(p).ToLowerInvariant()));
        if (allImages) return MediaFolderKind.Photos;
        if (allVideo) return MediaFolderKind.Video;
        if (allAudio) return MediaFolderKind.Audio;
        return MediaFolderKind.Mixed;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    // ----------------------------------------------------- display rows

    private sealed class CategoryRow
    {
        public Category Category { get; }
        public string Name => Category.Name;
        public string Glyph => Category.Glyph;
        public string CountLabel { get; }

        /// <summary>
        /// True for the "Your sections" divider rather than a real section. It is a row in
        /// the same list because the sidebar is one ItemsSource, and giving it its own
        /// control would mean two lists that have to agree about selection.
        /// </summary>
        public bool IsHeader { get; init; }

        /// <summary>Whether the group under this header is folded away.</summary>
        public bool IsCollapsed { get; init; }

        public CategoryRow(Category category, int count) =>
            (Category, CountLabel) = (category, count > 0 ? count.ToString() : "");
    }

    private sealed class ViewerMediaItem
    {
        public VaultEntry Entry { get; }
        public AttachmentRef Attachment { get; }

        public ViewerMediaItem(VaultEntry entry, AttachmentRef attachment) =>
            (Entry, Attachment) = (entry, attachment);
    }

    private sealed class FolderListRow
    {
        public MediaFolder Folder { get; }
        public Category? Category { get; }
        public string Title => Folder.Name;
        public string Subtitle { get; }
        public string Glyph { get; }
        public Visibility FavoriteVisibility => Folder.IsFavorite ? Visibility.Visible : Visibility.Collapsed;

        public FolderListRow(MediaFolder folder, Category? category, int fileCount)
        {
            Folder = folder;
            Category = category;
            Glyph = FolderKindIcon(folder.Kind);
            string section = category?.Name ?? "Media";
            Subtitle = fileCount == 1 ? $"1 file · {section}" : $"{fileCount} files · {section}";
        }
    }

    private sealed class EntryRow
    {
        public VaultEntry Entry { get; }
        public string Title => Entry.Title;
        public string Subtitle { get; }
        public string Glyph { get; }
        public Visibility FavoriteVisibility => Entry.Favorite ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Shown when a linked device has actually been handed this item.</summary>
        public Visibility SyncedVisibility =>
            Entry.IsSyncedAnywhere ? Visibility.Visible : Visibility.Collapsed;

        public string SyncedTooltip => Entry.SyncedToDevices.Count == 1
            ? "On one linked device"
            : $"On {Entry.SyncedToDevices.Count} linked devices";

        public EntryRow(VaultEntry entry)
        {
            Entry = entry;
            Glyph = GlyphForKind(entry.Kind);
            var firstVisible = entry.Fields.FirstOrDefault(f => !f.IsSecret && !string.IsNullOrWhiteSpace(f.Value));
            Subtitle = firstVisible?.Value ?? KindLabel(entry.Kind);
        }
    }

    private sealed class FieldEditor
    {
        public Border Root { get; }
        private readonly TextBox _label;
        private readonly TextBox _value;
        private readonly CheckBox _secret;
        public event Action? RemoveRequested;

        public FieldEditor(EntryField field, Func<object, object> findResource)
        {
            Root = new Border
            {
                Background = (Brush)findResource("Bg2"),
                BorderBrush = (Brush)findResource("Stroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var stack = new StackPanel();
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _label = new TextBox { Style = (Style)findResource("InputBox"), Text = field.Label, Height = 34 };
            Grid.SetColumn(_label, 0);
            top.Children.Add(_label);

            var remove = new Button { Style = (Style)findResource("IconButton"), Content = "\uE74D", ToolTip = "Remove field" };
            remove.Click += (_, _) => RemoveRequested?.Invoke();
            Grid.SetColumn(remove, 1);
            top.Children.Add(remove);
            stack.Children.Add(top);

            _value = new TextBox { Style = (Style)findResource("InputBox"), Text = field.Value, Height = 34, Margin = new Thickness(0, 6, 0, 0) };
            stack.Children.Add(_value);

            _secret = new CheckBox
            {
                Content = "Hide this value (treat as secret)",
                IsChecked = field.IsSecret,
                Foreground = (Brush)findResource("Muted"),
                Margin = new Thickness(2, 6, 0, 0),
                FontSize = 12,
            };
            stack.Children.Add(_secret);
            Root.Child = stack;
        }

        public EntryField ToField() => new()
        {
            Label = _label.Text.Trim(),
            Value = _value.Text,
            IsSecret = _secret.IsChecked == true,
        };
    }
}
