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
using System.Windows.Input;

namespace Lockwell.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerViewModel _vm = new();
    private readonly bool _startPatchMode;
    private readonly string[]? _updateArgs;

    public MainWindow(bool startPatchMode = false, string[]? updateArgs = null)
    {
        _startPatchMode = startPatchMode;
        _updateArgs = updateArgs;
        InitializeComponent();
        DataContext = _vm;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeAsync().ConfigureAwait(true);

        if (_startPatchMode)
        {
            var request = UpdateFlow.ParseRequest(_updateArgs ?? []);
            _vm.StartPatchFromArgs(request);
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _vm.GoBack();

    private void Next_Click(object sender, RoutedEventArgs e) => _vm.GoNext();

    private void Install_Click(object sender, RoutedEventArgs e) => _vm.StartInstall();

    private async void Retry_Click(object sender, RoutedEventArgs e) =>
        await _vm.RetryInstallAsync().ConfigureAwait(true);

    private void Finish_Click(object sender, RoutedEventArgs e) => _vm.Finish(this);

    private void RunProgram_Click(object sender, RoutedEventArgs e) => _vm.RunProgram(this);

    private void BrowsePath_Click(object sender, RoutedEventArgs e) => _vm.BrowseInstallPath();

    private void ChooseInstall_Click(object sender, RoutedEventArgs e) => _vm.ChooseInstall();

    private void ChoosePatch_Click(object sender, RoutedEventArgs e) => _vm.ChoosePatch();

    private void Close_Click(object sender, RoutedEventArgs e) => TryCloseSetup();

    private void Cancel_Click(object sender, RoutedEventArgs e) => TryCloseSetup();

    private void TryCloseSetup()
    {
        if (_vm.IsBusy)
        {
            return;
        }

        var result = MessageBox.Show(
            "Cancel setup?",
            "Lockwell Setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            Close();
        }
    }
}
