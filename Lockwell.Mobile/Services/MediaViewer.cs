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

using Microsoft.Maui.Controls.Shapes;

namespace Lockwell.Mobile.Services;

/// <summary>
/// Viewing an item without it ever becoming a file.
///
/// This is the point of the whole feature, and the reason it is not simply a call to the
/// system viewer. Handing a photo to another app means writing the decrypted bytes to
/// storage first, and once they are there the media scanner can index them, a gallery
/// can show them, a cloud backup can take them and a file manager can list them. The
/// vault would be protecting a picture that is also sitting in plain sight.
///
/// So the bytes travel: encrypted attachment, decrypted into a byte array, decoded into a
/// bitmap, drawn on screen. No path, no file, no scanner entry. When the viewer closes,
/// the arrays are cleared and the image source is dropped, so a locked vault leaves
/// nothing decoded behind.
///
/// Writing a real file still happens for "Save a copy", which is a separate action the
/// user asks for in words, and which is supposed to produce a file.
/// </summary>
public sealed class MediaViewer
{
    private readonly Page _page;
    private readonly IImageDecoder? _decoder;
    private readonly Func<PhoneItem, byte[]> _read;
    private readonly Func<PhoneItem, Task> _onActions;
    private readonly Action _onClosed;

    private readonly List<PhoneItem> _items;
    private int _index;

    private Grid? _root;
    private Image? _image;
    private Label? _title;
    private Label? _detail;
    private Label? _position;
    private Label? _placeholder;
    private Button? _prevButton;
    private Button? _nextButton;

    private VideoSurface? _surface;
    private Grid? _transport;
    private Button? _playPause;
    private Slider? _seek;
    private Label? _clock;

    private IMediaPlayback? _playback;
    private IDispatcherTimer? _ticker;

    // True while the slider is being dragged, so the ticker does not fight the thumb.
    private bool _scrubbing;

    // The only copy of the decoded picture, held for as long as it is on screen.
    private byte[]? _decoded;

    private double _scale = 1;
    private double _panX;
    private double _panY;

    public MediaViewer(
        Page page,
        IEnumerable<PhoneItem> items,
        PhoneItem start,
        Func<PhoneItem, byte[]> read,
        Func<PhoneItem, Task> onActions,
        Action onClosed)
    {
        _page = page;
        _items = items.ToList();
        _read = read;
        _onActions = onActions;
        _onClosed = onClosed;
        _decoder = ServiceHelper.GetService<IImageDecoder>();

        _index = Math.Max(0, _items.FindIndex(i => i.Id == start.Id));
    }

    public async Task ShowAsync()
    {
        Build();

        Grid host = Dialogs.HostFor(_page);
        Grid.SetRowSpan(_root!, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_root!, Math.Max(1, host.ColumnDefinitions.Count));
        host.Add(_root!);

        _root!.Opacity = 0;
        Load();
        await _root.FadeTo(1, 160, Easing.CubicOut);
    }

    /// <summary>Rebuild the current item, after a rename or a change of position.</summary>
    public void Refresh() => Load();

    public void Close()
    {
        if (_root is null) return;

        Grid host = Dialogs.HostFor(_page);
        Grid root = _root;
        _root = null;

        Forget();
        _onClosed();

        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            await root.FadeTo(0, 130);
            host.Remove(root);
        });
    }

    /// <summary>
    /// Drop whatever is currently decoded. Called on close and whenever the item changes,
    /// so only one item's plaintext is ever in memory at a time.
    /// </summary>
    private void Forget()
    {
        if (_image is not null) _image.Source = null;

        if (_decoded is not null)
        {
            Array.Clear(_decoded, 0, _decoded.Length);
            _decoded = null;
        }

        StopTicker();

        // Releasing the player closes its data source, which wipes the decrypted media.
        _playback?.Stop();
        _playback?.Dispose();
        _playback = null;

        if (_transport is not null) _transport.IsVisible = false;
        if (_surface is not null) _surface.IsVisible = false;
    }

    private void StopTicker()
    {
        if (_ticker is null) return;
        try { _ticker.Stop(); } catch { }
        _ticker = null;
    }

    // ------------------------------------------------------------------ content

    private void Load()
    {
        if (_items.Count == 0 || _root is null) return;

        _index = Math.Clamp(_index, 0, _items.Count - 1);
        PhoneItem item = _items[_index];

        Forget();
        ResetZoom();

        _title!.Text = item.Title;
        _detail!.Text = Describe(item);
        _position!.Text = _items.Count > 1 ? $"{_index + 1} of {_items.Count}" : "";

        bool several = _items.Count > 1;
        if (_prevButton is not null) _prevButton.IsVisible = several;
        if (_nextButton is not null) _nextButton.IsVisible = several;

        if (item.IsImage)
        {
            ShowImage(item);
            return;
        }

        if (item.IsVideo || item.IsAudio)
        {
            _ = PlayAsync(item);
            return;
        }

        _image!.IsVisible = false;
        _placeholder!.IsVisible = true;
        _placeholder.Text =
            "\U0001F4C4\n\nThis kind of file has no preview.\nUse Save a copy to open it in another app.";
    }

    /// <summary>
    /// Play video or audio without it ever becoming a file.
    ///
    /// The decrypted bytes go into a MediaDataSource, which is an object the platform
    /// player reads ranges from, rather than a path it opens. Nothing is written to
    /// storage, so nothing can be indexed by the media scanner, listed by a file manager,
    /// or picked up by a backup. Releasing the player wipes the array.
    /// </summary>
    private async Task PlayAsync(PhoneItem item)
    {
        _image!.IsVisible = false;
        _placeholder!.IsVisible = true;
        _placeholder.Text = item.IsVideo ? "\U0001F3AC\n\nOpening..." : "\U0001F3B5\n\nOpening...";

        IMediaPlayback? playback = ServiceHelper.GetService<IMediaPlayback>();
        if (playback is null)
        {
            _placeholder.Text = "\u26A0\n\nPlayback is not available on this device.";
            return;
        }

        byte[] plain;
        try
        {
            plain = _read(item);
        }
        catch (Exception ex)
        {
            _placeholder.Text = "\u26A0\n\nCould not open this item.\n" + ex.Message;
            return;
        }

        _playback = playback;
        _playback.Completed += (_, _) => MainThread.BeginInvokeOnMainThread(OnPlaybackEnded);

        if (item.IsVideo)
        {
            _surface!.IsVisible = true;

            // The texture has to exist before the player is given it, or the video plays
            // with sound and no picture.
            if (!_surface.IsReady)
            {
                var appeared = new TaskCompletionSource<bool>();
                void Handler(object? _, EventArgs __) => appeared.TrySetResult(true);
                _surface.Ready += Handler;

                await Task.WhenAny(appeared.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                _surface.Ready -= Handler;
            }
        }

        bool ok = await _playback.LoadAsync(plain, item.IsVideo ? _surface : null, item.IsVideo);

        // LoadAsync takes ownership of the array either way; on failure it has already
        // been wiped along with the player.
        if (!ok)
        {
            _placeholder.Text = item.IsVideo
                ? "\u26A0\n\nThis video could not be played on this device."
                : "\u26A0\n\nThis audio could not be played on this device.";
            _surface!.IsVisible = false;
            return;
        }

        _placeholder.IsVisible = false;
        _transport!.IsVisible = true;

        _seek!.Maximum = Math.Max(1, _playback.Duration.TotalSeconds);
        _seek.Value = 0;

        _playback.Play();
        UpdateTransport();
        StartTicker();
    }

    private void OnPlaybackEnded()
    {
        if (_playback is null) return;
        _playback.SeekTo(TimeSpan.Zero);
        UpdateTransport();
    }

    private void StartTicker()
    {
        StopTicker();

        _ticker = Application.Current?.Dispatcher.CreateTimer();
        if (_ticker is null) return;

        _ticker.Interval = TimeSpan.FromMilliseconds(400);
        _ticker.Tick += (_, _) => UpdateTransport();
        _ticker.Start();
    }

    private void UpdateTransport()
    {
        if (_playback is null || _transport is null) return;

        _playPause!.Text = _playback.IsPlaying ? "\u23F8" : "\u25B6";

        TimeSpan at = _playback.Position;
        TimeSpan total = _playback.Duration;

        if (!_scrubbing)
        {
            _seek!.Maximum = Math.Max(1, total.TotalSeconds);
            _seek.Value = Math.Clamp(at.TotalSeconds, 0, _seek.Maximum);
        }

        _clock!.Text = $"{Clock(at)} / {Clock(total)}";
    }

    private static string Clock(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";

    private void ShowImage(PhoneItem item)
    {
        _placeholder!.IsVisible = false;
        _image!.IsVisible = true;

        try
        {
            byte[] plain = _read(item);

            // An animated image has to reach the platform whole. Decoding it would flatten
            // it to a single frame, which is how an animation ends up looking like a still
            // picture. MAUI plays these itself once given the original bytes.
            bool animated = MediaTypes.IsAnimated(item.FileName, item.MediaType);

            // Decode down to something a screen can use. A full-resolution phone photo
            // is many times larger than any display, and decoding it whole is how a
            // viewer runs out of memory on the third or fourth picture.
            byte[]? shown = animated ? plain : (_decoder?.ForViewing(plain, 2048) ?? plain);

            // The decrypted original is finished with the moment it has been decoded.
            if (!ReferenceEquals(shown, plain)) Array.Clear(plain, 0, plain.Length);

            if (shown is null)
            {
                _image.IsVisible = false;
                _placeholder.IsVisible = true;
                _placeholder.Text = "⚠\n\nThis image could not be decoded.";
                return;
            }

            _decoded = shown;

            // FromStream is handed a factory because MAUI may ask for the bytes more than
            // once; each call needs its own reader over the same in-memory array.
            byte[] source = shown;
            _image.Source = ImageSource.FromStream(() => new MemoryStream(source, writable: false));

            // GIFs and animated WebPs loop; a still image ignores this entirely.
            _image.IsAnimationPlaying = animated;
        }
        catch (Exception ex)
        {
            _image.IsVisible = false;
            _placeholder.IsVisible = true;
            _placeholder.Text = "⚠\n\nCould not open this item.\n" + ex.Message;
        }
    }

    private static string Describe(PhoneItem item)
    {
        string detail = $"{Format.Bytes(item.SizeBytes)}  ·  added {Format.Ago(item.AddedUtc)}";

        if (item.Origin == PhoneItemOrigin.ReceivedFromDevice) detail += "  ·  from a PC";
        if (item.OfferToPc) detail += "  ·  queued for PC";

        if (item.ExpiresUtc is not null)
        {
            TimeSpan left = item.ExpiresUtc.Value - DateTime.UtcNow;
            detail += left.TotalHours < 24
                ? $"  ·  expires in {Math.Max(1, (int)left.TotalHours)}h"
                : $"  ·  expires in {(int)left.TotalDays}d";
        }

        return detail;
    }

    // --------------------------------------------------------------------- chrome

    private void Build()
    {
        _image = new Image
        {
            Aspect = Aspect.AspectFit,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };

        _placeholder = new Label
        {
            IsVisible = false,
            FontSize = 15,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            TextColor = Theme.Color("Muted"),
            Padding = new Thickness(32),
        };

        _surface = new VideoSurface
        {
            IsVisible = false,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };

        var stage = new Grid { BackgroundColor = Colors.Black };
        stage.Add(_surface);
        stage.Add(_image);
        stage.Add(_placeholder);

        AddGestures(stage);

        // Swiping works, but nothing on screen says so, and a video surface or the scrubber
        // can swallow a horizontal drag before the gesture ever fires. Visible arrows make
        // moving between items discoverable and reliable in the cases where swiping is not.
        _prevButton = StepButton("‹", LayoutOptions.Start, () => Step(-1));
        _nextButton = StepButton("›", LayoutOptions.End, () => Step(+1));
        stage.Add(_prevButton);
        stage.Add(_nextButton);

        BuildTransport();

        _title = new Label
        {
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Theme.Color("TextColor"),
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        _detail = new Label { FontSize = 11, TextColor = Theme.Color("Muted") };
        _position = new Label
        {
            FontSize = 11,
            TextColor = Theme.Color("Faint"),
            HorizontalTextAlignment = TextAlignment.End,
            VerticalOptions = LayoutOptions.Center,
        };

        var close = new Button
        {
            Text = "✕",
            FontSize = 17,
            BackgroundColor = Colors.Transparent,
            TextColor = Theme.Color("TextColor"),
            WidthRequest = 46,
            HeightRequest = 46,
            Padding = 0,
        };
        close.Clicked += (_, _) => Close();

        var more = new Button
        {
            Text = "⋯",
            FontSize = 19,
            BackgroundColor = Colors.Transparent,
            TextColor = Theme.Color("TextColor"),
            WidthRequest = 46,
            HeightRequest = 46,
            Padding = 0,
        };
        more.Clicked += async (_, _) =>
        {
            if (_items.Count == 0) return;
            await _onActions(_items[_index]);
        };

        var top = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Padding = new Thickness(6, 44, 6, 8),
            BackgroundColor = Theme.Color("Bg0"),
            ColumnSpacing = 4,
        };
        top.Add(close, 0);

        var heading = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        heading.Add(_title);
        heading.Add(_detail);
        top.Add(heading, 1);
        top.Add(more, 2);

        var bottom = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            Padding = new Thickness(20, 10, 20, 22),
            BackgroundColor = Theme.Color("Bg0"),
            RowSpacing = 6,
        };
        bottom.Add(_transport!, 0, 0);
        bottom.Add(_position, 0, 1);

        _root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
            BackgroundColor = Colors.Black,
        };
        _root.Add(top, 0, 0);
        _root.Add(stage, 0, 1);
        _root.Add(bottom, 0, 2);
    }

    /// <summary>Play, pause and a scrubber. Hidden unless the item is video or audio.</summary>
    private void BuildTransport()
    {
        _playPause = new Button
        {
            Text = "\u25B6",
            FontSize = 16,
            BackgroundColor = Theme.Color("Bg2"),
            TextColor = Theme.Color("Accent"),
            CornerRadius = 22,
            WidthRequest = 44,
            HeightRequest = 44,
            Padding = 0,
        };
        _playPause.Clicked += (_, _) =>
        {
            if (_playback is null) return;

            if (_playback.IsPlaying) _playback.Pause();
            else _playback.Play();

            UpdateTransport();
        };

        _seek = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            MinimumTrackColor = Theme.Color("Accent"),
            MaximumTrackColor = Theme.Color("Stroke"),
            ThumbColor = Theme.Color("Accent"),
            VerticalOptions = LayoutOptions.Center,
        };

        // While a finger is on the thumb the ticker must not write to Value, or the
        // slider snaps back under the finger every 400ms.
        _seek.DragStarted += (_, _) => _scrubbing = true;
        _seek.DragCompleted += (_, _) =>
        {
            _playback?.SeekTo(TimeSpan.FromSeconds(_seek!.Value));
            _scrubbing = false;
            UpdateTransport();
        };

        _clock = new Label
        {
            Text = "0:00 / 0:00",
            FontSize = 11,
            TextColor = Theme.Color("Muted"),
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = 92,
            HorizontalTextAlignment = TextAlignment.End,
        };

        _transport = new Grid
        {
            IsVisible = false,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 10,
        };
        _transport.Add(_playPause, 0);
        _transport.Add(_seek, 1);
        _transport.Add(_clock, 2);
    }

    private void AddGestures(View stage)
    {
        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += (_, e) =>
        {
            if (_image is null) return;

            if (e.Status == GestureStatus.Running)
            {
                _scale = Math.Clamp(_scale * e.Scale, 1, 6);
                _image.Scale = _scale;

                // Back at natural size there is nothing to pan to, so recentre rather
                // than leaving the picture stranded off to one side.
                if (Math.Abs(_scale - 1) < 0.01) ResetPan();
            }
        };
        stage.GestureRecognizers.Add(pinch);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, e) =>
        {
            if (_image is null || _scale <= 1.01) return;

            if (e.StatusType == GestureStatus.Running)
            {
                _image.TranslationX = _panX + e.TotalX;
                _image.TranslationY = _panY + e.TotalY;
            }
            else if (e.StatusType is GestureStatus.Completed or GestureStatus.Canceled)
            {
                _panX = _image.TranslationX;
                _panY = _image.TranslationY;
            }
        };
        stage.GestureRecognizers.Add(pan);

        // Double tap toggles between fitted and a useful zoom, which is quicker than
        // pinching for the common "read the small text" case.
        var doubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        doubleTap.Tapped += (_, _) =>
        {
            // While something is playing, a double tap means play/pause. Zooming a video
            // surface does nothing useful and stealing the gesture would be confusing.
            if (_playback is not null)
            {
                if (_playback.IsPlaying) _playback.Pause();
                else _playback.Play();
                UpdateTransport();
                return;
            }

            if (_image is null) return;
            if (_scale > 1.01) ResetZoom();
            else { _scale = 2.5; _image.Scale = _scale; }
        };
        stage.GestureRecognizers.Add(doubleTap);

        // Swiping moves between items, but only at natural size: while zoomed in, a
        // sideways drag means panning, and stealing it would make the zoom unusable.
        var left = new SwipeGestureRecognizer { Direction = SwipeDirection.Left };
        left.Swiped += (_, _) => Step(+1);
        stage.GestureRecognizers.Add(left);

        var right = new SwipeGestureRecognizer { Direction = SwipeDirection.Right };
        right.Swiped += (_, _) => Step(-1);
        stage.GestureRecognizers.Add(right);
    }

    /// <summary>A translucent arrow at the edge of the stage, for moving between items.</summary>
    private Button StepButton(string glyph, LayoutOptions side, Action onTap)
    {
        var button = new Button
        {
            Text = glyph,
            FontSize = 30,
            TextColor = Colors.White,
            BackgroundColor = Color.FromRgba(0, 0, 0, 0.32),
            CornerRadius = 26,
            WidthRequest = 52,
            HeightRequest = 52,
            Padding = 0,
            Margin = new Thickness(10, 0),
            HorizontalOptions = side,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
        };
        button.Clicked += (_, _) => onTap();
        return button;
    }

    private void Step(int by)
    {
        // While zoomed in, a sideways drag means panning, so swiping must not steal it.
        // The arrows still work, because tapping one is unambiguous.
        if (_scale > 1.01) return;
        if (_items.Count < 2) return;

        _index = (_index + by + _items.Count) % _items.Count;
        Load();
    }

    private void ResetZoom()
    {
        _scale = 1;
        if (_image is not null) _image.Scale = 1;
        ResetPan();
    }

    private void ResetPan()
    {
        _panX = 0;
        _panY = 0;
        if (_image is null) return;
        _image.TranslationX = 0;
        _image.TranslationY = 0;
    }

    /// <summary>Drop an item that has just been deleted, and move to the next one.</summary>
    public bool Remove(PhoneItem item)
    {
        int at = _items.FindIndex(i => i.Id == item.Id);
        if (at >= 0) _items.RemoveAt(at);

        if (_items.Count == 0)
        {
            Close();
            return false;
        }

        if (_index >= _items.Count) _index = _items.Count - 1;
        Load();
        return true;
    }
}
