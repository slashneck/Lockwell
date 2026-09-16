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
using System.Windows.Threading;
using Lockwell.Helpers;
using Lockwell.Services;

namespace Lockwell.Views;

public partial class VaultShellView
{
    private readonly object _vaultOpLock = new();
    private bool _busyOperation;

    /// <summary>Set for operations that can be stopped part-way, such as bulk compression.</summary>
    private CancellationTokenSource? _cancellableOperation;

    private void ProgressCancel_Click(object sender, RoutedEventArgs e)
    {
        ProgressCancelButton.IsEnabled = false;
        ProgressCancelButton.Content = "Stopping...";
        _cancellableOperation?.Cancel();
    }

    /// <summary>
    /// Show progress with a working Stop button. The returned token is cancelled when
    /// the user presses it; the caller must finish the item in flight cleanly.
    /// </summary>
    private CancellationToken ShowCancellableProgress(string title, string detail)
    {
        ShowProgress(title, detail);
        _cancellableOperation = new CancellationTokenSource();
        ProgressCancelButton.IsEnabled = true;
        ProgressCancelButton.Content = "Stop";
        ProgressCancelButton.Visibility = Visibility.Visible;
        return _cancellableOperation.Token;
    }

    private void ShowProgress(string title, string detail, bool indeterminate = true)
    {
        _busyOperation = true;
        // Do NOT disable ShellRoot here. A disabled WPF ListBox falls back to its
        // default (light) disabled background, which made the sidebar flash white
        // during uploads. ProgressOverlay already covers the whole shell and is
        // hit-test visible, so it swallows input on its own.
        MainWorkspace.IsHitTestVisible = false;
        SidebarPanel.IsHitTestVisible = false;
        ProgressTitle.Text = title;
        ProgressDetail.Text = detail;
        ProgressCount.Text = "";
        ProgressBar.IsIndeterminate = indeterminate;
        ProgressBar.Value = 0;
        ProgressOverlay.Visibility = Visibility.Visible;
        ProgressOverlay.Opacity = 0;
        Anim.FadeIn(ProgressOverlay, 160);
    }

    private void ApplyOperationProgress(OperationProgress report)
    {
        if (!string.IsNullOrEmpty(report.Title))
            ProgressTitle.Text = report.Title;
        ProgressDetail.Text = report.Detail;

        if (report.IsIndeterminate)
        {
            ProgressBar.IsIndeterminate = true;
            ProgressCount.Text = "";
            return;
        }

        ProgressBar.IsIndeterminate = false;
        if (report.Total > 0)
        {
            ProgressBar.Maximum = report.Total;
            ProgressBar.Value = Math.Clamp(report.Current, 0, report.Total);
            ProgressCount.Text = $"{report.Current} / {report.Total}";
        }
    }

    private void UpdateProgress(string detail, int current, int total)
    {
        ApplyOperationProgress(new OperationProgress
        {
            Detail = detail,
            Current = current,
            Total = total,
        });
    }

    private async Task FinishProgressAsync(string detail)
    {
        ProgressBar.IsIndeterminate = false;
        ProgressDetail.Text = detail;
        ProgressCount.Text = "";
        await Task.Delay(500);
        HideProgress();
    }

    private void HideProgress()
    {
        ProgressOverlay.Visibility = Visibility.Collapsed;
        MainWorkspace.IsHitTestVisible = true;
        SidebarPanel.IsHitTestVisible = true;
        ProgressBar.IsIndeterminate = false;
        ProgressCancelButton.Visibility = Visibility.Collapsed;
        _cancellableOperation?.Dispose();
        _cancellableOperation = null;
        _busyOperation = false;
    }

    private IProgress<OperationProgress> CreateUiProgressReporter() =>
        new Progress<OperationProgress>(p => Dispatcher.Invoke(() => ApplyOperationProgress(p)));
}
