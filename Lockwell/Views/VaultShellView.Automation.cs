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
using Lockwell.Models;

namespace Lockwell.Views;

/// <summary>
/// A narrow hook so the screenshot harness can walk the real interface to each
/// screen. It only calls the same private methods the buttons call, so what gets
/// captured is the shipping UI rather than a rebuild of it.
///
/// Nothing here is reachable in a normal run: the harness is the only caller, and it
/// only starts when the app is launched with --store-shots.
/// </summary>
public partial class VaultShellView
{
    internal void AutomationAllItems() => CategoryList.SelectedIndex = 0;

    internal void AutomationSearch(string query)
    {
        OpenSearch();
        SearchInput.Text = query;
    }

    internal void AutomationCloseSearch() => CloseSearch();

    internal void AutomationSettingsBackup() => ShowSettings(SettingsSection.Backup);

    internal void AutomationSettingsSecurity() => ShowSettings(SettingsSection.Security);

    internal void AutomationFilesAsList(bool list)
    {
        _settings.FilesListView = list;
        RefreshMediaGallery();
    }

    internal void AutomationSelectFirstFiles()
    {
        foreach (var tile in MediaGrid.Children.OfType<System.Windows.Controls.Border>().Take(3))
            if (tile.Tag is VaultEntry entry) _selectedEntryIds.Add(entry.Id);
        RefreshSelectionUi();
    }

    internal void AutomationClearSelection() => ClearSelection();


    internal void AutomationStorage() => ShowStorage();

    internal void AutomationDevices()
    {
        // Also here, not just in AutomationFakeDevices: the empty-state capture shows the
        // "This PC is ..." line too, and it is taken first.
        Trust.SelfName = "Workshop PC";
        ShowDevices();
    }

    /// <summary>Open the send picker for the first linked device, to look at it.</summary>
    internal void AutomationSendPicker()
    {
        var device = Trust.Devices.FirstOrDefault();
        if (device is not null) SendToDevice(device);
    }

    /// <summary>
    /// Add devices that were never really paired, so the Devices screen can be looked at
    /// in the state that matters. The trust store lives beside the vault, and this is
    /// pointed at a throwaway one, so nothing real is touched.
    /// </summary>
    internal void AutomationFakeDevices()
    {
        // TrustStore falls back to Environment.MachineName, which would put the real name
        // of whichever machine produced the screenshots into the published images, next to
        // key fingerprints. A capture harness must never inherit anything about its host.
        Trust.SelfName = "Workshop PC";

        Trust.Add(Sync.DeviceIdentity.Create().PublicKey, "Pixel 8", "phone", expiryDays: 7);
        Trust.Add(Sync.DeviceIdentity.Create().PublicKey, "Work laptop", "laptop", expiryDays: 30);
        Trust.Add(Sync.DeviceIdentity.Create().PublicKey, "Studio PC", "desktop", neverExpires: true);
        RefreshDevices();
    }

    internal void AutomationStorageInto(string folderName)
    {
        var folder = _vault.Data.MediaFolders
            .FirstOrDefault(f => f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return;

        _storagePath.Add(new StorageCrumb(StorageLevel.Folder, folder.Id, folder.Name));
        RefreshStorage();
    }

    internal void AutomationStorageByType()
    {
        _storageByType = true;
        RefreshStorage();
    }

    /// <summary>Select a sidebar section by its display name.</summary>
    internal void AutomationSection(string name)
    {
        var row = _sidebar.FirstOrDefault(r =>
            r.Category.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row is not null) CategoryList.SelectedItem = row;
    }

    internal void AutomationOpenMediaFolder(string folderName)
    {
        var folder = _vault.Data.MediaFolders
            .FirstOrDefault(f => f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is not null) OpenMediaFolder(folder.Id);
    }

    internal void AutomationOpenFirstMediaItem()
    {
        var entry = _vault.Data.Entries
            .Where(e => !string.IsNullOrEmpty(e.MediaFolderId))
            .FirstOrDefault(e => e.Attachments.Count > 0);
        if (entry is null) return;
        OpenMediaItem(entry, entry.Attachments[0]);
    }

    /// <summary>
    /// Open a real file through the path a click takes and report what the player made of it.
    /// </summary>
    internal async Task<string> AutomationPlay(string path)
    {
        var att = _vault.AddAttachment(path);
        var entry = new VaultEntry { Title = "Playback test", Attachments = { att } };
        _vault.Data.Entries.Add(entry);
        _currentEntry = entry;

        OpenAttachment(att);
        for (int i = 0; i < 60 && !ViewerMedia.NaturalDuration.HasTimeSpan; i++)
            await Task.Delay(250);

        string report = $"visible={ImageViewer.Visibility} opened={ViewerMedia.NaturalDuration.HasTimeSpan} "
                        + $"duration={(ViewerMedia.NaturalDuration.HasTimeSpan ? ViewerMedia.NaturalDuration.TimeSpan : TimeSpan.Zero)} "
                        + $"hasVideo={ViewerMedia.HasVideo} hasAudio={ViewerMedia.HasAudio} "
                        + $"volume={ViewerMedia.Volume} muted={ViewerMedia.IsMuted}";

        await Task.Delay(1500);
        report += $" positionAfter1500ms={ViewerMedia.Position}";
        return report;
    }

    /// <summary>The menus a tile offers, opened for real and handed back for capturing.</summary>
    internal System.Windows.Controls.ContextMenu? AutomationOpenFolderMenu()
    {
        var tile = MediaFoldersGrid.Children.OfType<System.Windows.Controls.Border>().FirstOrDefault();
        if (tile?.Tag is not MediaFolder folder) return null;
        ShowFolderMenu(tile, folder);
        return tile.ContextMenu ?? _lastMenu;
    }

    internal System.Windows.Controls.ContextMenu? AutomationOpenFileMenu()
    {
        var tile = MediaGrid.Children.OfType<System.Windows.Controls.Border>().FirstOrDefault();
        if (tile?.Tag is not VaultEntry entry) return null;
        ShowFileMenu(tile, entry, entry.Attachments[0]);
        return _lastMenu;
    }

    internal void AutomationOpenMoveDialog() => SelectionMove_Click(this, new RoutedEventArgs());

    internal void AutomationNewItem() => OpenEntryEditor(null);

    internal void AutomationToast(string text) => ShowToast(text);

    internal void AutomationShowEntryWithFiles(string title)
    {
        var entry = _vault.Data.Entries.FirstOrDefault(e => e.Title == title);
        if (entry is null) return;
        GoToEntry(entry);
    }

    internal void AutomationAttachDemoFiles(string title, params string[] paths)
    {
        var entry = _vault.Data.Entries.First(e => e.Title == title);
        foreach (string path in paths) entry.Attachments.Add(_vault.AddAttachment(path));
    }

    internal void AutomationCloseViewer() => CloseViewer_Click(this, new RoutedEventArgs());

    internal void AutomationCloseOverlay() => CloseOverlay();

    internal void AutomationSettings() => Settings_Click(this, new RoutedEventArgs());

    internal void AutomationCompressFirstMediaItem()
    {
        var entry = _vault.Data.Entries
            .Where(e => !string.IsNullOrEmpty(e.MediaFolderId))
            .FirstOrDefault(e => e.Attachments.Count > 0);
        if (entry is not null) BeginCompress(entry);
    }

    internal void AutomationSelectFirstEntry()
    {
        if (EntryList.Items.Count > 0) EntryList.SelectedIndex = 0;
    }

    internal VaultEntry? AutomationFirstMediaEntry() =>
        _vault.Data.Entries
            .Where(e => !string.IsNullOrEmpty(e.MediaFolderId))
            .FirstOrDefault(e => e.Attachments.Count > 0);
}
