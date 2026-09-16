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
    internal void AutomationHome() => ShowHome();

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
