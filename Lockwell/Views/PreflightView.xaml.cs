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
using System.Windows.Media;
using Lockwell.Helpers;
using Lockwell.Services;

namespace Lockwell.Views;

public partial class PreflightView : UserControl
{
    private readonly PreflightScanner _scanner;
    private readonly Action _onContinue;
    private bool _hasFindings;

    public PreflightView(PreflightScanner scanner, Action onContinue)
    {
        InitializeComponent();
        _scanner = scanner;
        _onContinue = onContinue;
        Loaded += async (_, _) =>
        {
            try
            {
                Anim.PopIn(Card, 320);
                await RunScanAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                _onContinue();
            }
        };
    }

    private async System.Threading.Tasks.Task RunScanAsync()
    {
        ContinueButton.IsEnabled = false;
        TitleText.Text = "Checking your environment";
        SubtitleText.Text = "Looking for remote access, screen recorders, and active screen sharing before you unlock.";
        StatusGlyph.Text = "\uE9D9";
        StatusGlyph.Foreground = (Brush)FindResource("Accent");
        AcceptRisk.Visibility = Visibility.Collapsed;
        AcceptRisk.IsChecked = false;

        var findings = await System.Threading.Tasks.Task.Run(() => _scanner.Scan());

        _hasFindings = findings.Count > 0;
        FindingsList.ItemsSource = findings.Select(f => new FindingRow(f)).ToList();

        if (!_hasFindings)
        {
            StatusGlyph.Text = "\uE73E"; // checkmark
            StatusGlyph.Foreground = (Brush)FindResource("Accent");
            TitleText.Text = "No known risks detected";
            SubtitleText.Text = "We did not find remote-access tools, recorders, or chat apps that commonly screen-share. This is not proof that nobody can see your screen. Lockwell still hides its window from capture when that setting is on.";
            ContinueButton.IsEnabled = true;
        }
        else
        {
            StatusGlyph.Text = "\uE7BA"; // warning
            StatusGlyph.Foreground = (Brush)FindResource("Warning");
            bool hasDanger = findings.Any(f => f.Severity == ThreatSeverity.Danger);
            TitleText.Text = hasDanger ? "High-risk activity detected" : "Possible watchers detected";
            SubtitleText.Text = hasDanger
                ? "Screen sharing or remote access may be active. Stop it before unlocking, or confirm you accept the risk."
                : "These programs can see or share your screen. Close screen share or quit them before unlocking, or confirm you accept the risk.";
            AcceptRisk.Visibility = Visibility.Visible;
            ContinueButton.IsEnabled = false;
        }
    }

    private void AcceptRisk_Changed(object sender, RoutedEventArgs e)
    {
        if (_hasFindings)
            ContinueButton.IsEnabled = AcceptRisk.IsChecked == true;
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    private void Continue_Click(object sender, RoutedEventArgs e) => _onContinue();

    private sealed class FindingRow
    {
        public string DisplayName { get; }
        public string Reason { get; }
        public Brush SeverityBrush { get; }

        public FindingRow(PreflightFinding f)
        {
            DisplayName = f.DisplayName;
            Reason = f.Reason;
            SeverityBrush = f.Severity switch
            {
                ThreatSeverity.Danger => (Brush)Application.Current.FindResource("Danger"),
                ThreatSeverity.Warning => (Brush)Application.Current.FindResource("Warning"),
                _ => (Brush)Application.Current.FindResource("Muted"),
            };
        }
    }
}
