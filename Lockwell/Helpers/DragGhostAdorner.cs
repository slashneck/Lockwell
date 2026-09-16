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
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Lockwell.Helpers;

/// <summary>
/// A shrunken, semi-transparent copy of the tile being dragged that follows the
/// cursor, with a badge showing whether the spot under the pointer will accept the
/// drop. Lives on the AdornerLayer so it floats above the gallery without disturbing
/// layout, and never takes hit-tests (it would steal them from the real drop target).
/// </summary>
public sealed class DragGhostAdorner : Adorner
{
    private const double Scale = 0.55;
    private const double CursorOffset = 16;

    private readonly Canvas _canvas;
    private readonly Border _card;
    private readonly Border _badge;
    private readonly TextBlock _badgeGlyph;

    private Point _position;
    private bool _allowed = true;

    public DragGhostAdorner(UIElement adornedElement, FrameworkElement source)
        : base(adornedElement)
    {
        IsHitTestVisible = false;

        double w = Math.Max(source.ActualWidth, 40) * Scale;
        double h = Math.Max(source.ActualHeight, 40) * Scale;

        _card = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x2D, 0xD4, 0xBF)),
            Background = new VisualBrush(source) { Stretch = Stretch.Uniform },
            Opacity = 0.85,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.55,
                Color = Colors.Black,
            },
        };

        _badgeGlyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Child = _badgeGlyph,
        };

        _canvas = new Canvas { IsHitTestVisible = false };
        _canvas.Children.Add(_card);
        _canvas.Children.Add(_badge);
        AddVisualChild(_canvas);

        SetAllowed(true);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _canvas;

    protected override Size MeasureOverride(Size constraint)
    {
        _canvas.Measure(constraint);
        return constraint;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _canvas.Arrange(new Rect(finalSize));
        return finalSize;
    }

    /// <summary>Move the ghost. <paramref name="position"/> is relative to the adorned element.</summary>
    public void Update(Point position, bool allowed)
    {
        _position = position;
        if (allowed != _allowed) SetAllowed(allowed);

        Canvas.SetLeft(_card, _position.X + CursorOffset);
        Canvas.SetTop(_card, _position.Y + CursorOffset);
        Canvas.SetLeft(_badge, _position.X + CursorOffset + _card.Width - 13);
        Canvas.SetTop(_badge, _position.Y + CursorOffset - 9);
    }

    private void SetAllowed(bool allowed)
    {
        _allowed = allowed;
        _card.Opacity = allowed ? 0.85 : 0.5;
        _badge.Background = allowed
            ? new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF))
            : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        _badgeGlyph.Text = allowed ? "" : ""; // check / cancel
        _badgeGlyph.Foreground = allowed ? Brushes.Black : Brushes.White;
    }
}
