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
using Lockwell.Helpers;
using Lockwell.Sync;

namespace Lockwell.Views;

/// <summary>
/// The Devices section: link a phone, see what is linked, and unlink.
///
/// Linking is a feature of Lockwell, not a requirement for it. Everything else in the
/// app works with nothing ever linked, and this screen says so rather than implying the
/// vault is incomplete without a phone.
/// </summary>
public partial class VaultShellView
{
    private TrustStore? _trust;
    private bool _devicesMode;

    /// <summary>
    /// Trust data lives beside the vault, not inside it: a connection has to be answered
    /// before anyone has unlocked anything. It holds no vault contents.
    /// </summary>
    private TrustStore Trust => _trust ??= new TrustStore(Path.Combine(_vault.VaultDir, "sync"));

    private void Devices_Click(object sender, RoutedEventArgs e) => ShowDevices();

    private void ShowDevices()
    {
        _homeMode = false;
        _storageMode = false;
        _devicesMode = true;
        _mediaMode = false;
        _currentCategory = null;
        CategoryList.SelectedItem = null;

        HomePanel.Visibility = Visibility.Collapsed;
        StoragePanel.Visibility = Visibility.Collapsed;
        ListColumn.Visibility = Visibility.Collapsed;
        DetailColumn.Visibility = Visibility.Collapsed;
        MediaPanel.Visibility = Visibility.Collapsed;

        RefreshDevices();

        DevicesPanel.Visibility = Visibility.Visible;
        Anim.SlideFadeIn(DevicesPanel, 320, 16);
    }

    private void RefreshDevices()
    {
        // Expired links are dropped when this screen opens, so the list is never a
        // stale picture of who can still connect.
        Trust.PruneExpired();

        DeviceRows.Children.Clear();

        var devices = Trust.Devices.OrderByDescending(d => d.LastSeenUtc).ToList();
        NoDevicesCard.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int i = 0;
        foreach (var device in devices)
        {
            var row = BuildDeviceRow(device);
            DeviceRows.Children.Add(row);
            Anim.SlideFadeIn(row, 260, 10, i * 30);
            i++;
        }

        ExpiryToggle.IsChecked = Trust.ExpiryEnabled;
        ExpiryTitle.Text = Trust.ExpiryEnabled
            ? $"Drop links that go quiet for {Trust.ExpiryDays} days"
            : "Links never expire unless a device has its own window";

        DevicesCount.Text = devices.Count switch
        {
            0 => "",
            1 => "1 device",
            var n => $"{n} devices",
        };
        DevicesHeading.Visibility = devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        SelfDeviceName.Text = "This PC is " + Trust.SelfName;
        SelfFingerprint.Text = Trust.Identity.Fingerprint;

        UpdateDeviceBadge();
    }

    private void UpdateDeviceBadge()
    {
        int count = _trust?.Devices.Count ?? 0;
        DeviceCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceCountText.Text = count.ToString();
    }

    private Border BuildDeviceRow(TrustedDevice device)
    {
        var host = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource("GlassStroke"),
            BorderThickness = new Thickness(1),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = device.Kind == "phone" ? "" : "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 20,
            Foreground = (Brush)FindResource("Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        });

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = device.Name,
            Foreground = (Brush)FindResource("Text"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });

        string seen = device.DaysSinceSeen switch
        {
            <= 0 => "Last connected today",
            1 => "Last connected yesterday",
            var d => $"Last connected {d} days ago",
        };
        labels.Children.Add(new TextBlock
        {
            Text = $"{seen}  ·  linked {device.PairedUtc.ToLocalTime():d MMM yyyy}",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
        });

        // How long this pairing has left, on the row rather than buried in a setting.
        // A link that is about to lapse should say so before it does, not afterwards.
        int? daysLeft = device.DaysUntilExpiry(Trust.ExpiryDays, Trust.ExpiryEnabled);
        string expiryText;
        string expiryColour;

        if (daysLeft is null)
        {
            expiryText = device.NeverExpires ? "Never expires" : "No expiry set";
            expiryColour = "Faint";
        }
        else if (daysLeft <= 0)
        {
            expiryText = "Expired \u2014 will be removed";
            expiryColour = "Danger";
        }
        else if (daysLeft <= 3)
        {
            expiryText = daysLeft == 1 ? "Expires tomorrow" : $"Expires in {daysLeft} days";
            expiryColour = "Warning";
        }
        else
        {
            expiryText = $"Expires in {daysLeft} days without a connection";
            expiryColour = "Faint";
        }

        labels.Children.Add(new TextBlock
        {
            Text = expiryText,
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            Foreground = (Brush)FindResource(expiryColour),
            Margin = new Thickness(0, 3, 0, 0),
        });
        labels.Children.Add(new TextBlock
        {
            Text = device.Fingerprint,
            Style = (Style)FindResource("Caption"),
            FontSize = 10,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)FindResource("Faint"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var send = new Button
        {
            Style = (Style)FindResource("AccentButton"),
            Content = "Send items",
            Height = 32,
            MinWidth = 96,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
        };
        send.Click += (_, _) => SendToDevice(device);

        var rename = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Rename",
            Height = 32,
            MinWidth = 82,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
        };
        rename.Click += (_, _) => RenameDevice(device);

        actions.Children.Add(send);

        var unlink = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Unlink",
            Height = 32,
            MinWidth = 78,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 11,
        };
        unlink.Click += (_, _) => UnlinkDevice(device);

        var expiry = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Expiry",
            Height = 32,
            MinWidth = 76,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "How long this device may go quiet before the link is dropped",
        };
        expiry.Click += (_, _) => EditDeviceExpiry(device);

        actions.Children.Add(rename);
        actions.Children.Add(expiry);
        actions.Children.Add(unlink);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        host.Child = grid;
        return host;
    }

    // ------------------------------------------------------------- actions

    private void RenameDevice(TrustedDevice device)
    {
        OverlayTitle.Text = "Rename device";
        OverlayCard.MaxWidth = 480;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        var nameBox = AddLabeledInputWithPlaceholder("Device name", "e.g. Work phone", device.Name);

        _overlaySave = () =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) { ShowInlineOverlayError("Give the device a name."); return; }

            Trust.Rename(device.Id, name);
            CloseOverlay();
            RefreshDevices();
        };

        ShowOverlay(nameBox);
    }

    /// <summary>
    /// Choose how long one device may go quiet.
    ///
    /// Per device because devices are not alike: a phone that lives in a pocket earns a
    /// short window, while a machine switched on at weekends would be unpaired constantly
    /// by the same number.
    /// </summary>
    private void EditDeviceExpiry(TrustedDevice device)
    {
        OverlayTitle.Text = "Expiry for " + device.Name;
        OverlayCard.MaxWidth = 520;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        AddCompressParagraph(
            "If this device does not connect within the chosen window, the link is dropped " +
            "and it has to be paired again. Nothing on the device is deleted by this: it " +
            "simply stops being able to reach this vault.");

        var options = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

        RadioButton Option(string text, bool selected)
        {
            var radio = new RadioButton
            {
                Content = text,
                GroupName = "deviceExpiry",
                IsChecked = selected,
                Foreground = (Brush)FindResource("Text"),
                Margin = new Thickness(0, 0, 0, 10),
            };
            options.Children.Add(radio);
            return radio;
        }

        bool usesVault = !device.NeverExpires && device.ExpiryDaysOverride is null;

        RadioButton followVault = Option(
            $"Follow the vault setting ({(Trust.ExpiryEnabled ? Trust.ExpiryDays + " days" : "no expiry")})",
            usesVault);
        RadioButton week = Option("7 days", device.ExpiryDaysOverride == 7);
        RadioButton fortnight = Option("14 days", device.ExpiryDaysOverride == 14);
        RadioButton month = Option("30 days", device.ExpiryDaysOverride == 30);
        RadioButton quarter = Option("90 days", device.ExpiryDaysOverride == 90);
        RadioButton never = Option("Never expire", device.NeverExpires);

        OverlayBody.Children.Add(options);

        OverlaySave.Visibility = Visibility.Visible;
        OverlaySave.Content = "Save";

        _overlaySave = () =>
        {
            int? days = null;
            bool neverExpires = false;

            if (week.IsChecked == true) days = 7;
            else if (fortnight.IsChecked == true) days = 14;
            else if (month.IsChecked == true) days = 30;
            else if (quarter.IsChecked == true) days = 90;
            else if (never.IsChecked == true) neverExpires = true;
            else if (followVault.IsChecked != true) days = null;

            Trust.SetExpiry(device.Id, days, neverExpires);
            CloseOverlay();
            RefreshDevices();
        };

        ShowOverlay();
    }

    private void UnlinkDevice(TrustedDevice device)
    {
        OverlayTitle.Text = "Unlink this device?";
        OverlayCard.MaxWidth = 520;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        AddCompressParagraph(
            $"\"{device.Name}\" will no longer be able to connect to this vault. " +
            "You can link it again later with a new code.");

        AddCompressParagraph(
            "Items already on that device stay there, encrypted with its own key. " +
            "Unlinking here does not reach out and delete them.");

        OverlaySave.Content = "Unlink";
        _overlaySave = () =>
        {
            Trust.Remove(device.Id);
            CloseOverlay();
            RefreshDevices();
        };

        ShowOverlay();
    }

    private void ExpiryToggle_Click(object sender, RoutedEventArgs e)
    {
        Trust.ExpiryEnabled = ExpiryToggle.IsChecked == true;
        RefreshDevices();
    }

    private void RenameSelf_Click(object sender, RoutedEventArgs e)
    {
        OverlayTitle.Text = "Rename this PC";
        OverlayCard.MaxWidth = 480;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        AddCompressParagraph("This is the name your phone will show when it links to this PC.");
        var nameBox = AddLabeledInputWithPlaceholder("Name", "e.g. Desktop", Trust.SelfName);

        _overlaySave = () =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) { ShowInlineOverlayError("Give this PC a name."); return; }

            Trust.SelfName = name;
            CloseOverlay();
            RefreshDevices();
        };

        ShowOverlay(nameBox);
    }
}
