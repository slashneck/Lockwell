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
using System.Windows.Media;
using Lockwell.Services;

namespace Lockwell;

/// <summary>
/// The app's own message box. It shares a name and its call shape with
/// <see cref="System.Windows.MessageBox"/> on purpose: everything under the Lockwell namespace
/// resolves to this one first, so no call site in the app can reach the stock white Windows
/// dialog by accident.
///
/// Two behaviours are stricter than the stock box. A warning that asks a question defaults
/// to the safe answer, so Enter on a destructive prompt never destroys anything. And the
/// window takes the same capture protection as the main window, because a dialog that names
/// a file should not appear in a recording that the rest of the app is hidden from.
/// </summary>
public static class MessageBox
{
    /// <summary>Set by the main window, so dialogs follow the screen-capture setting.</summary>
    public static bool ExcludeFromCapture { get; set; } = true;

    public static MessageBoxResult Show(string message) =>
        Show(message, "Lockwell", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons) =>
        Show(message, title, buttons, MessageBoxImage.None);

    public static MessageBoxResult Show(
        string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        bool warns = image is MessageBoxImage.Warning or MessageBoxImage.Error;
        MessageBoxResult fallback = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => warns ? MessageBoxResult.Cancel : MessageBoxResult.OK,
            MessageBoxButton.YesNo => warns ? MessageBoxResult.No : MessageBoxResult.Yes,
            _ => MessageBoxResult.Yes,
        };

        return Show(message, title, buttons, image, fallback);
    }

    public static MessageBoxResult Show(
        string message, string title, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        var options = buttons switch
        {
            MessageBoxButton.OK => new[] { ("OK", MessageBoxResult.OK) },
            MessageBoxButton.OKCancel => new[] { ("Cancel", MessageBoxResult.Cancel), ("OK", MessageBoxResult.OK) },
            MessageBoxButton.YesNo => new[] { ("No", MessageBoxResult.No), ("Yes", MessageBoxResult.Yes) },
            _ => new[] { ("Cancel", MessageBoxResult.Cancel), ("No", MessageBoxResult.No), ("Yes", MessageBoxResult.Yes) },
        };

        MessageBoxResult escape = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };

        bool danger = image is MessageBoxImage.Warning or MessageBoxImage.Error
                      && buttons != MessageBoxButton.OK;

        return Present(heading: null, message, options, defaultResult, escape,
            danger ? options[^1].Item2 : null);
    }

    /// <summary>
    /// A question that says exactly what the button does, rather than Yes or No.
    /// Returns true only for the confirm button. Cancel is the default.
    /// </summary>
    public static bool Confirm(string heading, string message, string confirmLabel, bool danger = false)
    {
        var options = new[] { ("Cancel", MessageBoxResult.Cancel), (confirmLabel, MessageBoxResult.OK) };
        var result = Present(heading, message, options, MessageBoxResult.Cancel, MessageBoxResult.Cancel,
            danger ? MessageBoxResult.OK : null);
        return result == MessageBoxResult.OK;
    }

    private static MessageBoxResult Present(
        string? heading,
        string message,
        (string Label, MessageBoxResult Result)[] options,
        MessageBoxResult defaultResult,
        MessageBoxResult escapeResult,
        MessageBoxResult? dangerResult)
    {
        // A failure early enough that the theme never loaded must still be able to say so.
        if (Application.Current is null || !Application.Current.Resources.Contains("H2"))
        {
            return System.Windows.MessageBox.Show(
                string.IsNullOrEmpty(heading) ? message : heading + "\n\n" + message, "Lockwell",
                options.Length > 1 ? MessageBoxButton.OKCancel : MessageBoxButton.OK);
        }

        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return Application.Current.Dispatcher.Invoke(() =>
                Present(heading, message, options, defaultResult, escapeResult, dangerResult));
        }

        Style Style(string key) => (Style)Application.Current.Resources[key];
        Brush Brush(string key) => (Brush)Application.Current.Resources[key];

        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? (Application.Current.MainWindow is { IsLoaded: true } main ? main : null);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            Width = 440,
            Title = "Lockwell",
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            WindowStartupLocation = owner is not null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        if (owner is not null) window.Owner = owner;

        var result = escapeResult;
        var stack = new StackPanel();

        if (heading is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = heading,
                Style = Style("H2"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        if (!string.IsNullOrEmpty(message))
        {
            stack.Children.Add(new TextBlock
            {
                Text = message,
                Style = Style(heading is null ? "Body" : "Caption"),
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };

        Button? focus = null;
        foreach (var (label, value) in options)
        {
            string key = value == dangerResult
                ? "DangerButton"
                : value == options[^1].Result ? "AccentButton" : "GhostButton";

            var button = new Button
            {
                Style = Style(key),
                Content = label,
                MinWidth = 84,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = value == defaultResult,
            };
            button.Click += (_, _) =>
            {
                result = value;
                window.Close();
            };
            buttons.Children.Add(button);
            if (value == defaultResult) focus = button;
        }
        stack.Children.Add(buttons);

        window.Content = new Border
        {
            Background = Brush("Bg2"),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#3A424E")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(22, 20, 22, 18),
            Child = stack,
        };

        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            result = escapeResult;
            window.Close();
        };
        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) window.DragMove();
        };
        window.SourceInitialized += (_, _) => CaptureProtection.Apply(window, ExcludeFromCapture);
        window.Loaded += (_, _) => focus?.Focus();

        window.ShowDialog();
        return result;
    }
}
