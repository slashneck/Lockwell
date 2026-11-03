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
using System.Windows.Input;
using System.Windows.Threading;
using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Views;

/// <summary>
/// The fastest route through the app: one box that finds anything and does anything, plus
/// the keyboard shortcuts and small confirmations that go with it. The main thing anyone opens
/// a vault to do is find one item and copy one value, so that should take a shortcut, a few
/// letters and Enter.
/// </summary>
public partial class VaultShellView
{
    private sealed record SearchRow(
        string Glyph, string Title, string Detail, Action Open, Action? Copy = null);

    private void OpenSearch_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        if (_busyOperation) return;
        SearchOverlay.Visibility = Visibility.Visible;
        SearchInput.Text = "";
        RefreshSearchResults();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SearchInput.Focus();
            Keyboard.Focus(SearchInput);
        }));
    }

    private void CloseSearch()
    {
        SearchOverlay.Visibility = Visibility.Collapsed;
        SearchInput.Text = "";
    }

    private void SearchOverlay_MouseDown(object sender, MouseButtonEventArgs e) => CloseSearch();

    private void SearchCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e) => RefreshSearchResults();

    private void RefreshSearchResults()
    {
        string query = SearchInput.Text ?? "";
        SearchInputHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var rows = new List<SearchRow>();
        if (query.Trim().Length > 0 && _vault.IsUnlocked)
        {
            foreach (var hit in VaultSearch.Find(_vault.Data, query, _settings.HideNsfwMediaFolders))
                rows.Add(RowFor(hit));
        }

        rows.AddRange(MatchingCommands(query));

        SearchResults.ItemsSource = rows;
        SearchResults.SelectedIndex = rows.Count > 0 ? 0 : -1;
        SearchResults.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchNoResults.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private SearchRow RowFor(VaultSearch.Hit hit) => hit.Kind switch
    {
        VaultSearch.HitKind.Item => new SearchRow(
            GlyphForKind(hit.Entry!.Kind), hit.Title, hit.Subtitle,
            () => GoToEntry(hit.Entry),
            VaultSearch.PrimarySecret(hit.Entry) is { } secret
                ? () => CopyField(secret)
                : null),

        VaultSearch.HitKind.File => new SearchRow(
            hit.Entry!.Attachments.FirstOrDefault() is { } att ? MediaIcon(att) : "",
            hit.Title,
            hit.Entry.Attachments.FirstOrDefault() is { } first ? KindOfFile(first) : "File",
            () => GoToFile(hit.Entry)),

        VaultSearch.HitKind.Folder => new SearchRow(
            "", hit.Title, "Folder",
            () => NavigateToMediaFolder(hit.Folder!.CategoryId, hit.Folder.Id)),

        _ => new SearchRow(
            hit.Category!.Glyph, DisplayName(hit.Category), "Section",
            () => GoToSection(hit.Category.Id)),
    };

    private IEnumerable<SearchRow> MatchingCommands(string query)
    {
        var commands = new List<SearchRow>
        {
            new("", "New item", "Ctrl N", () => OpenEntryEditor(null)),
            new("", "Add files", "", () => { GoToFirstFiles(); AddMedia_Click(this, new RoutedEventArgs()); }),
            new("", "Settings", "Ctrl ,", () => ShowSettings(_settingsSection)),
            new("", "Devices", "", ShowDevices),
            new("", "Storage", "", ShowStorage),
            new("", "Back up the vault", "", ExportBackup),
            new("", "Lock", "Ctrl L", () => _onLock()),
        };

        string q = query.Trim();
        return q.Length == 0
            ? commands
            : commands.Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void SearchInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;

            case Key.Down:
                MoveSearchSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSearchSelection(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                if (SearchResults.SelectedItem is SearchRow row)
                {
                    bool copy = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                    if (copy && row.Copy is null)
                    {
                        ShowToast("This item has no secret to copy.");
                    }
                    else
                    {
                        CloseSearch();
                        (copy ? row.Copy! : row.Open)();
                    }
                }
                e.Handled = true;
                break;
        }
    }

    private void MoveSearchSelection(int delta)
    {
        int count = SearchResults.Items.Count;
        if (count == 0) return;
        SearchResults.SelectedIndex = (SearchResults.SelectedIndex + delta + count) % count;
        SearchResults.ScrollIntoView(SearchResults.SelectedItem);
    }

    private void SearchResults_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SearchResults, (DependencyObject)e.OriginalSource) is not ListBoxItem item)
            return;
        if (item.DataContext is not SearchRow row) return;

        e.Handled = true;
        CloseSearch();
        row.Open();
    }

    // ------------------------------------------------------------ going places

    private void GoToSection(string categoryId)
    {
        var category = _vault.Data.Categories.FirstOrDefault(c => c.Id == categoryId);
        if (category is { IsCustom: true } && _customSectionsCollapsed)
        {
            _customSectionsCollapsed = false;
            RefreshCategories();
        }

        var row = _sidebar.FirstOrDefault(r => r.Category.Id == categoryId);
        if (row is not null) CategoryList.SelectedItem = row;
    }

    private void GoToEntry(VaultEntry entry)
    {
        if (_vault.Data.Categories.FirstOrDefault(c => c.Id == entry.CategoryId) is { IsMedia: true })
        {
            GoToFile(entry);
            return;
        }

        GoToSection(entry.CategoryId);
        SearchBox.Text = "";
        RefreshEntries();

        var row = EntryList.Items.OfType<EntryRow>().FirstOrDefault(r => r.Entry.Id == entry.Id);
        if (row is not null)
        {
            EntryList.SelectedItem = row;
            EntryList.ScrollIntoView(row);
        }
    }

    private void GoToFile(VaultEntry entry)
    {
        GoToSection(entry.CategoryId);
        MediaSearchBox.Text = "";
        _currentMediaFolderId = entry.MediaFolderId;
        RefreshMediaGallery();

        // Things Lockwell can play open straight away. Anything else is only ever opened by
        // saving a decrypted copy, which should never happen as a side effect of searching,
        // so for those this stops at showing where the file is.
        var att = entry.Attachments.FirstOrDefault(a => IsImage(a) || IsVideo(a) || IsAudio(a));
        if (att is not null) OpenMediaItem(entry, att);
    }

    private void GoToFirstFiles()
    {
        var category = _vault.Data.Categories.FirstOrDefault(c => c.IsMedia);
        if (category is not null && !_mediaMode) GoToSection(category.Id);
    }

    private void CopyField(EntryField field)
    {
        ClipboardHelper.CopySecret(field.Value, _settings.ClipboardClearSeconds);
        ShowToast($"{field.Label} copied. Clears in {_settings.ClipboardClearSeconds} seconds.");
    }

    // ------------------------------------------------------------ shortcuts

    /// <summary>Called from the window, so shortcuts work wherever focus happens to be.</summary>
    internal void HandleGlobalKey(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;

        if (_busyOperation) return;

        if (ctrl && e.Key == Key.L)
        {
            e.Handled = true;
            _onLock();
            return;
        }

        if (Overlay.Visibility == Visibility.Visible
            || ImageViewer.Visibility == Visibility.Visible
            || SearchOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        bool typing = Keyboard.FocusedElement is TextBox or PasswordBox;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.K:
                    e.Handled = true;
                    OpenSearch();
                    return;

                case Key.N:
                    e.Handled = true;
                    AddEntry_Click(this, new RoutedEventArgs());
                    return;

                case Key.OemComma:
                    e.Handled = true;
                    ShowSettings(_settingsSection);
                    return;

                case Key.F:
                    e.Handled = true;
                    if (_mediaMode) MediaSearchBox.Focus();
                    else if (!_settingsMode && !_devicesMode && !_storageMode) SearchBox.Focus();
                    return;

                case Key.A when _mediaMode && !typing:
                    e.Handled = true;
                    SelectAllInView();
                    return;
            }
        }

        if (_mediaMode && !typing)
        {
            if (e.Key == Key.Escape && HasSelection)
            {
                e.Handled = true;
                ClearSelection();
            }
            else if (e.Key == Key.Delete && HasSelection)
            {
                e.Handled = true;
                DeleteSelection();
            }
        }
    }

    // ------------------------------------------------------------ small confirmations

    private DispatcherTimer? _toastTimer;

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastHost.Visibility = Visibility.Visible;
        ToastHost.Opacity = 1;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            ToastHost.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    // ------------------------------------------------------------ backup reminder

    private bool _backupNudgeDismissed;

    /// <summary>
    /// A backup status used to sit on the home screen whether it mattered or not. It now only
    /// appears when something is actually overdue, and an empty vault has nothing to back up.
    /// </summary>
    private void UpdateBackupNudge()
    {
        if (!_vault.IsUnlocked || _backupNudgeDismissed || _vault.Data.Entries.Count == 0)
        {
            BackupNudge.Visibility = Visibility.Collapsed;
            return;
        }

        DateTime? last = ResolveLastBackupUtc();
        int days = last is null ? 0 : (int)(DateTime.UtcNow - last.Value).TotalDays;

        if (last is null)
        {
            BackupNudgeText.Text = "No backup yet. If this drive fails, the vault goes with it.";
            BackupNudge.Visibility = Visibility.Visible;
        }
        else if (days >= 30)
        {
            BackupNudgeText.Text = $"Your last backup was {days} days ago.";
            BackupNudge.Visibility = Visibility.Visible;
        }
        else
        {
            BackupNudge.Visibility = Visibility.Collapsed;
        }
    }

    private void BackupNudge_Click(object sender, RoutedEventArgs e) => ExportBackup();

    private void BackupNudgeDismiss_Click(object sender, RoutedEventArgs e)
    {
        _backupNudgeDismissed = true;
        UpdateBackupNudge();
    }
}
