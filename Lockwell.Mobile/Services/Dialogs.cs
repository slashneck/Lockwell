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

namespace Lockwell.Mobile.Services;

/// <summary>
/// Lockwell's own dialogs, replacing MAUI's DisplayAlert / DisplayPromptAsync /
/// DisplayActionSheet.
///
/// Those three call straight through to Android's AlertDialog, which is drawn by the
/// platform using the platform's theme. On this app's near-black pages that arrived as a
/// flat light-grey slab with none of Lockwell's shape, spacing or colour, and no amount of
/// MAUI styling reaches inside it because the view is not MAUI's to style.
///
/// So these are built out of ordinary MAUI controls and laid over the page instead. They
/// use the same palette, corner radius and type scale as everything else, and being real
/// in-tree views they can be animated and themed like any other part of the app.
///
/// The page is wrapped in a host grid the first time it shows a dialog, and keeps that
/// wrapper afterwards, so no reparenting happens on later calls and scroll position is
/// never disturbed.
/// </summary>
public static class Dialogs
{
    private const double CardMaxWidth = 360;

    // Pages already wrapped, so the wrap happens once each rather than per dialog.
    private static readonly Dictionary<Page, Grid> Hosts = new();

    // ------------------------------------------------------------- public API

    /// <summary>A statement with one way out. Mirrors DisplayAlert(title, message, ok).</summary>
    public static Task ShowAlert(this Page page, string title, string message, string ok = "OK") =>
        ShowConfirm(page, title, message, ok, cancel: null);

    /// <summary>
    /// A question with two answers. Returns true only for the accept button: dismissing by
    /// tapping outside or pressing back counts as declining, which is the safe reading for
    /// every destructive prompt in this app.
    /// </summary>
    public static async Task<bool> ShowConfirm(
        this Page page, string title, string message,
        string accept, string? cancel = "Cancel", bool destructive = false)
    {
        var done = new TaskCompletionSource<bool>();

        var card = BuildCard();
        var stack = (VerticalStackLayout)card.Content!;
        stack.Add(TitleLabel(title));
        if (message.Length > 0) stack.Add(MessageLabel(message));

        var buttons = ButtonRow();
        Overlay? overlay = null;

        if (cancel is not null)
        {
            buttons.Add(GhostButton(cancel, () =>
            {
                overlay!.Close();
                done.TrySetResult(false);
            }));
        }

        buttons.Add(PrimaryButton(accept, destructive, () =>
        {
            overlay!.Close();
            done.TrySetResult(true);
        }));

        stack.Add(buttons);

        overlay = new Overlay(page, card, dismissable: cancel is not null,
            onDismissed: () => done.TrySetResult(false));
        await overlay.ShowAsync();

        return await done.Task;
    }

    /// <summary>
    /// Ask for a line of text. Returns null if cancelled or dismissed, so callers can tell
    /// "left it empty" apart from "backed out" the same way DisplayPromptAsync allowed.
    /// </summary>
    /// <remarks>
    /// Parameter order deliberately mirrors MAUI's DisplayPromptAsync, so the call sites
    /// this replaced kept their named arguments unchanged.
    /// </remarks>
    public static async Task<string?> ShowPrompt(
        this Page page, string title, string message,
        string accept = "OK", string cancel = "Cancel",
        string placeholder = "", int maxLength = 60, string initialValue = "")
    {
        var done = new TaskCompletionSource<string?>();

        var card = BuildCard();
        var stack = (VerticalStackLayout)card.Content!;
        stack.Add(TitleLabel(title));
        if (message.Length > 0) stack.Add(MessageLabel(message));

        var entry = new Entry
        {
            Text = initialValue,
            Placeholder = placeholder,
            MaxLength = maxLength,
            TextColor = Theme.Color("TextColor"),
            PlaceholderColor = Theme.Color("Faint"),
            BackgroundColor = Colors.Transparent,
            FontSize = 15,
        };

        var field = new Border
        {
            BackgroundColor = Theme.Color("Bg1"),
            Stroke = new SolidColorBrush(Theme.Color("Stroke")),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(14, 2),
            Margin = new Thickness(0, 14, 0, 0),
            Content = entry,
        };
        stack.Add(field);

        var buttons = ButtonRow();
        Overlay? overlay = null;

        buttons.Add(GhostButton(cancel, () =>
        {
            overlay!.Close();
            done.TrySetResult(null);
        }));
        buttons.Add(PrimaryButton(accept, destructive: false, () =>
        {
            overlay!.Close();
            done.TrySetResult(entry.Text ?? "");
        }));
        stack.Add(buttons);

        overlay = new Overlay(page, card, dismissable: true,
            onDismissed: () => done.TrySetResult(null));
        await overlay.ShowAsync();

        entry.Focus();
        return await done.Task;
    }

    /// <summary>
    /// A list of choices. Returns the chosen label, or null if cancelled, matching
    /// DisplayActionSheet so call sites keep comparing against the same strings.
    /// </summary>
    public static async Task<string?> ShowSheet(
        this Page page, string title, string cancel, string? destroy, params string[] options)
    {
        var done = new TaskCompletionSource<string?>();

        var card = BuildCard();
        var stack = (VerticalStackLayout)card.Content!;
        if (title.Length > 0) stack.Add(TitleLabel(title));

        Overlay? overlay = null;

        void Choose(string value)
        {
            overlay!.Close();
            done.TrySetResult(value);
        }

        var list = new VerticalStackLayout { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };

        foreach (string option in options)
        {
            if (string.IsNullOrEmpty(option)) continue;
            string captured = option;
            list.Add(SheetButton(option, Theme.Color("TextColor"), () => Choose(captured)));
        }

        if (destroy is not null)
        {
            list.Add(SheetButton(destroy, Theme.Color("Danger"), () => Choose(destroy)));
        }

        list.Add(SheetButton(cancel, Theme.Color("Muted"), () =>
        {
            overlay!.Close();
            done.TrySetResult(null);
        }));

        stack.Add(list);

        overlay = new Overlay(page, card, dismissable: true,
            onDismissed: () => done.TrySetResult(null));
        await overlay.ShowAsync();

        return await done.Task;
    }

    // ------------------------------------------------------------ the overlay

    /// <summary>
    /// One dialog on screen: a scrim over the page and a card above it. Holds the logic
    /// for putting itself into the page and taking itself back out again.
    /// </summary>
    private sealed class Overlay
    {
        private readonly Page _page;
        private readonly Grid _root;
        private readonly Border _card;
        private readonly BoxView _scrim;
        private readonly Action _onDismissed;
        private bool _closed;

        public Overlay(Page page, Border card, bool dismissable, Action onDismissed)
        {
            _page = page;
            _card = card;
            _onDismissed = onDismissed;

            _scrim = new BoxView
            {
                // Set both: the template's implicit BoxView style drives BackgroundColor,
                // and Color alone would leave that showing through.
                Color = Microsoft.Maui.Graphics.Color.FromRgba(0, 0, 0, 0.62),
                BackgroundColor = Microsoft.Maui.Graphics.Color.FromRgba(0, 0, 0, 0.62),
                Opacity = 0,
            };

            if (dismissable)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) =>
                {
                    Close();
                    _onDismissed();
                };
                _scrim.GestureRecognizers.Add(tap);
            }

            // No padding on the root: the scrim has to reach every edge, including behind
            // the header and the tab bar, or the dimming stops short of the screen and the
            // dialog reads as a panel sitting on a lit page. The card takes the inset.
            _root = new Grid();
            _card.Margin = new Thickness(24);
            _root.Add(_scrim);
            _root.Add(_card);
        }

        public async Task ShowAsync()
        {
            Grid host = HostFor(_page);

            // Span every row and column of the host so the scrim covers the whole page,
            // including any fixed header or tab bar.
            Grid.SetRowSpan(_root, Math.Max(1, host.RowDefinitions.Count));
            Grid.SetColumnSpan(_root, Math.Max(1, host.ColumnDefinitions.Count));
            host.Add(_root);

            _card.Opacity = 0;
            _card.TranslationY = 14;

            await Task.WhenAll(
                _scrim.FadeTo(1, 140, Easing.CubicOut),
                _card.FadeTo(1, 180, Easing.CubicOut),
                _card.TranslateTo(0, 0, 180, Easing.CubicOut));
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;

            Grid host = HostFor(_page);

            // Fire and forget: the caller's result is already decided, and awaiting the
            // animation here would delay it for no benefit.
            _ = Task.Run(async () =>
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await Task.WhenAll(
                        _scrim.FadeTo(0, 120),
                        _card.FadeTo(0, 120),
                        _card.TranslateTo(0, 10, 120));
                    host.Remove(_root);
                });
            });
        }
    }

    /// <summary>
    /// The grid a page's overlays live in. Created once per page and kept, so showing a
    /// second dialog never reparents the page's real content.
    /// </summary>
    /// <remarks>
    /// Internal rather than private because the media viewer is an overlay too, and two
    /// competing wrappers around one page would fight over its content.
    /// </remarks>
    internal static Grid HostFor(Page page)
    {
        if (Hosts.TryGetValue(page, out Grid? existing)) return existing;

        var host = new Grid();

        if (page is ContentPage content)
        {
            View? original = content.Content;
            content.Content = null;

            if (original is not null) host.Add(original);
            content.Content = host;
        }

        Hosts[page] = host;

        // A page that goes away should not be held alive by this table.
        page.Unloaded += (_, _) => Hosts.Remove(page);

        return host;
    }

    // ------------------------------------------------------------- card parts

    private static Border BuildCard() => new()
    {
        BackgroundColor = Theme.Color("Bg2"),
        Stroke = new SolidColorBrush(Theme.Color("Stroke")),
        StrokeThickness = 1,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
        Padding = new Thickness(22, 20, 22, 18),
        MaximumWidthRequest = CardMaxWidth,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
        Content = new VerticalStackLayout { Spacing = 0 },
    };

    private static Label TitleLabel(string text) => new()
    {
        Text = text,
        FontSize = 17,
        FontAttributes = FontAttributes.Bold,
        TextColor = Theme.Color("TextColor"),
    };

    private static Label MessageLabel(string text) => new()
    {
        Text = text,
        FontSize = 13,
        LineHeight = 1.35,
        TextColor = Theme.Color("Muted"),
        Margin = new Thickness(0, 10, 0, 0),
    };

    private static HorizontalStackLayout ButtonRow() => new()
    {
        Spacing = 8,
        HorizontalOptions = LayoutOptions.End,
        Margin = new Thickness(0, 20, 0, 0),
    };

    private static Button PrimaryButton(string text, bool destructive, Action onTap)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = destructive ? Theme.Color("Danger") : Theme.Color("Accent"),
            TextColor = Theme.Color("Bg0"),
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            CornerRadius = 12,
            HeightRequest = 44,
            Padding = new Thickness(18, 0),
            MinimumWidthRequest = 96,
        };
        button.Clicked += (_, _) => onTap();
        return button;
    }

    private static Button GhostButton(string text, Action onTap)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = Colors.Transparent,
            TextColor = Theme.Color("Muted"),
            BorderColor = Theme.Color("Stroke"),
            BorderWidth = 1,
            FontSize = 14,
            CornerRadius = 12,
            HeightRequest = 44,
            Padding = new Thickness(18, 0),
            MinimumWidthRequest = 88,
        };
        button.Clicked += (_, _) => onTap();
        return button;
    }

    private static Button SheetButton(string text, Color textColor, Action onTap)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = Theme.Color("Bg1"),
            TextColor = textColor,
            BorderColor = Theme.Color("Stroke"),
            BorderWidth = 1,
            FontSize = 15,
            CornerRadius = 12,
            HeightRequest = 50,
            HorizontalOptions = LayoutOptions.Fill,
        };
        button.Clicked += (_, _) => onTap();
        return button;
    }
}
