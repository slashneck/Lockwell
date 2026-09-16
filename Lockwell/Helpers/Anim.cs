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
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lockwell.Helpers;

/// <summary>
/// Small animation helpers used across the app for entrance and feedback motion.
/// Kept in one place so the motion language stays consistent.
/// </summary>
public static class Anim
{
    private static IEasingFunction EaseOut() => new CubicEase { EasingMode = EasingMode.EaseOut };
    private static IEasingFunction EaseIn() => new CubicEase { EasingMode = EasingMode.EaseIn };
    private static IEasingFunction BackOut() => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

    public static void FadeIn(UIElement el, double ms = 220, double beginMs = 0)
    {
        el.Opacity = 0;
        var a = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = EaseOut(),
        };
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    public static void SlideFadeIn(FrameworkElement el, double ms = 280, double dy = 16, double beginMs = 0)
    {
        el.Opacity = 0;
        var tt = new TranslateTransform(0, dy);
        el.RenderTransform = tt;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = EaseOut(),
        });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = EaseOut(),
        });
    }

    public static void PopIn(FrameworkElement el, double ms = 240, double beginMs = 0)
    {
        el.Opacity = 0;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var st = new ScaleTransform(0.94, 0.94);
        el.RenderTransform = st;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = EaseOut(),
        });
        var sc = new DoubleAnimation(0.94, 1, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = BackOut(),
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
    }

    public static void StaggerIn(System.Windows.Controls.Panel panel, double step = 45, double ms = 300, double dy = 14)
    {
        int i = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child is FrameworkElement fe)
                SlideFadeIn(fe, ms, dy, beginMs: i * step);
            i++;
        }
    }

    /// <summary>Animate opacity to an arbitrary target, for controls that rest partly visible.</summary>
    public static void FadeTo(UIElement el, double target, double ms = 140)
    {
        var a = new DoubleAnimation(el.Opacity, target, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            EasingFunction = EaseOut(),
        };
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    public static void FadeOut(UIElement el, Action? onDone = null, double ms = 150)
    {
        var a = new DoubleAnimation(el.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = EaseOut() };
        if (onDone is not null) a.Completed += (_, _) => onDone();
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>
    /// Dismiss motion: fade <paramref name="fade"/> out while <paramref name="scale"/>
    /// shrinks slightly. The counterpart to <see cref="PopIn"/>, so opening and closing
    /// feel like the same gesture rather than a hard cut.
    /// </summary>
    public static void PopOut(
        UIElement fade,
        FrameworkElement? scale = null,
        Action? onDone = null,
        double ms = 170,
        double toScale = 0.93)
    {
        if (scale is not null)
        {
            scale.RenderTransformOrigin = new Point(0.5, 0.5);
            var st = new ScaleTransform(1, 1);
            scale.RenderTransform = st;
            var sa = new DoubleAnimation(1, toScale, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                EasingFunction = EaseIn(),
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
        }

        var a = new DoubleAnimation(fade.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            EasingFunction = EaseIn(),
        };
        if (onDone is not null) a.Completed += (_, _) => onDone();
        fade.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>Clear any animation hold and transform so the next entrance starts clean.</summary>
    public static void ResetMotion(FrameworkElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.RenderTransform = Transform.Identity;
    }
}
