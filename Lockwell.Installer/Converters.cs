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

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lockwell.Installer;

public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int step || !int.TryParse(parameter?.ToString(), out var target))
        {
            return Visibility.Collapsed;
        }

        return step == target ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToInstallTitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "Finished" : "Installing";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToInstallSubtitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? "Close the installer or run Lockwell."
            : "Downloading and installing…";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Where a step in the side rail sits relative to the one being shown.
/// </summary>
internal enum StepState
{
    Done,
    Current,
    Pending,
}

internal static class StepStates
{
    public static StepState For(object[] values)
    {
        if (values.Length < 2 || values[0] is not int stepNumber || values[1] is not int currentStep)
        {
            return StepState.Pending;
        }

        if (stepNumber < currentStep) return StepState.Done;
        return stepNumber == currentStep ? StepState.Current : StepState.Pending;
    }

    /// <summary>
    /// Look a brush up by key rather than returning a literal colour, so the rail cannot
    /// drift away from the palette in InstallerTheme.xaml the way the old hard-coded
    /// values did.
    /// </summary>
    public static object Brush(string key) =>
        Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
}

/// <summary>Fill for the numbered marker beside a step in the rail.</summary>
public sealed class StepMarkerBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        StepStates.For(values) switch
        {
            StepState.Current => StepStates.Brush("InstallerAccentBrush"),
            StepState.Done => StepStates.Brush("InstallerAccentSoft"),
            _ => StepStates.Brush("InstallerBg2"),
        };

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Outline for that marker.</summary>
public sealed class StepMarkerStrokeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        StepStates.For(values) switch
        {
            StepState.Current => StepStates.Brush("InstallerAccent"),
            StepState.Done => StepStates.Brush("InstallerAccent"),
            _ => StepStates.Brush("InstallerStroke"),
        };

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colour of the number inside the marker.</summary>
public sealed class StepNumberBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        StepStates.For(values) switch
        {
            // The current marker is filled with the accent gradient, so its number has to
            // be dark to stay legible against it.
            StepState.Current => StepStates.Brush("InstallerBg0"),
            StepState.Done => StepStates.Brush("InstallerAccent"),
            _ => StepStates.Brush("InstallerFaint"),
        };

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colour of the step label next to the marker.</summary>
public sealed class StepLabelBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        StepStates.For(values) switch
        {
            StepState.Current => StepStates.Brush("InstallerText"),
            StepState.Done => StepStates.Brush("InstallerMuted"),
            _ => StepStates.Brush("InstallerFaint"),
        };

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
