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
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Media.Imaging;
using Lockwell.Services;
using Lockwell.Views;

namespace Lockwell.Automation;

/// <summary>
/// Produces the store listing images.
///
/// It drives the real window: the same views, styles and rendering path the shipping
/// app uses. Captures come from RenderTargetBitmap over the live visual tree, so what
/// lands in the PNG is genuinely what Lockwell draws, not a mock-up of it.
///
/// The vault it opens is built from scratch by <see cref="DemoVault"/> in a temporary
/// folder. The real vault is never opened.
/// </summary>
internal static class StoreShots
{
    private const int WindowWidth = 1280;
    private const int WindowHeight = 860;
    private const int FrameWidth = 1920;
    private const int FrameHeight = 1080;

    private sealed record Frame(
        string Id,
        string Title,
        string TitleAccent,
        string Body,
        string[] Tags);

    private static readonly Frame[] Frames =
    {
        new("01-home",
            "Your vault,", "at a glance",
            "Everything lives on your PC, encrypted at rest. Open it and see exactly what you are holding.",
            new[] { "AES-256-GCM", "No cloud", "No accounts" }),

        new("02-unlock",
            "One password.", "No backdoor.",
            "Argon2id turns your passphrase into the key. There is no stored verifier and no way in without it.",
            new[] { "Argon2id", "256 MiB cost", "Recovery key" }),

        new("03-media",
            "A media vault", "that feels fast",
            "Photos, video and audio in folders you arrange yourself. Drag to sort, drag to file away.",
            new[] { "Drag and drop", "Custom order", "Folders" }),

        new("04-viewer",
            "View it", "without leaking it",
            "Images decode straight from memory to screen. Nothing is written to disk to show you a picture.",
            new[] { "In-memory decode", "Nothing cached" }),

        new("05-storage",
            "See what", "uses the space",
            "Drill from the whole vault down to a single file, biggest first, and deal with what you find.",
            new[] { "Per folder", "By file type" }),

        new("06-compress",
            "Shrink files", "on your terms",
            "Re-encode a photo, a track or a video using the encoders already on your PC. Preview before you commit.",
            new[] { "Runs offline", "Keeps originals", "Never automatic" }),

        new("07-entry",
            "Logins,", "kept quiet",
            "Secret fields stay hidden until you ask. Copy one and the clipboard clears itself behind you.",
            new[] { "Hidden by default", "Clipboard auto-clear" }),

        new("08-settings",
            "Tuned", "to how you work",
            "Auto-lock, capture protection, clipboard timeout and blurred previews. Sensible defaults you can change.",
            new[] { "Auto-lock", "Hidden from capture", "Your choices" }),

        new("09-empty",
            "Nothing here", "leaves your PC",
            "No telemetry, no analytics, no sync, no server. Lockwell talks to your disk and to nothing else.",
            new[] { "Zero network calls", "Local only" }),
    };

    public static async Task RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        string windowDir = Path.Combine(outDir, "window");
        Directory.CreateDirectory(windowDir);

        string root = AppDataPaths.AppRoot;
        Console.WriteLine($"[store] demo vault root: {root}");

        var settings = AppSettings.Load();
        settings.RunPreflight = false;
        settings.Profiles.Clear();
        settings.ActiveProfileId = null;

        // Thumbnail blur is a real privacy option and it is on by default, but for a
        // listing it just reads as a smudged grid. Show the gallery unblurred, which is
        // a setting anyone can flip, and make the blur its own frame instead.
        settings.BlurMediaThumbnails = false;

        // Skip the one-time "before you compress" explainer so the frame shows the
        // actual compression controls rather than a warning box.
        settings.CompressionWarningAcknowledged = true;

        var profiles = new ProfileManager(settings);
        profiles.EnsureInitialized();
        if (profiles.ActiveProfile is not null)
            profiles.ActiveProfile.Name = DemoVault.ProfileName;
        settings.Save();

        string vaultDir = profiles.ActiveProfile!.VaultDir;
        var vault = DemoVault.Build(vaultDir);

        var captures = new Dictionary<string, BitmapSource>();

        // The window has to be shown for WPF to lay it out and render it, but it does not
        // have to be shown to anyone. Parking it off the virtual screen keeps a capture
        // run from throwing a series of windows across whatever the user is doing.
        var window = new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Application.Current.Resources["Bg0"],
            ShowInTaskbar = false,
            Title = "Lockwell",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000,
        };
        // The capture renders this element, not the Window, so it needs its own
        // background. Without it the whole content area came out transparent, which
        // reads as a white page in the saved PNG.
        var host = new ContentControl
        {
            Background = (Brush)Application.Current.Resources["Bg0"],
        };
        window.Content = host;
        window.Show();
        await Settle(500);

        /* ---------------------------------------------------------- unlock */
        var locked = new VaultManager(vaultDir);
        host.Content = new UnlockView(locked, () => { }, DemoVault.ProfileName);
        await Settle(700);
        captures["02-unlock"] = Capture(window);

        /* ----------------------------------------------------------- shell */
        var shell = new VaultShellView(vault, settings, profiles,
            onLock: () => { }, applySettings: () => { }, onSwitchProfile: () => { });
        host.Content = shell;
        await Settle(1400);

        shell.AutomationHome();
        await Settle(900);
        captures["01-home"] = Capture(window);

        shell.AutomationSection("Media vault");
        await Settle(700);
        shell.AutomationOpenMediaFolder("Trip photos");
        await Settle(1600); // thumbnails decode off-thread
        captures["03-media"] = Capture(window);

        shell.AutomationOpenFirstMediaItem();
        await Settle(1500);
        captures["04-viewer"] = Capture(window);
        shell.AutomationCloseViewer();
        await Settle(700);

        shell.AutomationStorage();
        await Settle(900);
        shell.AutomationStorageInto("Trip photos");
        await Settle(800);
        captures["05-storage"] = Capture(window);

        shell.AutomationCompressFirstMediaItem();
        await Settle(1200);
        captures["06-compress"] = Capture(window);
        shell.AutomationCloseOverlay();
        await Settle(600);

        shell.AutomationSection("Email accounts");
        await Settle(700);
        shell.AutomationSelectFirstEntry();
        await Settle(800);
        captures["07-entry"] = Capture(window);

        shell.AutomationSection("Crypto");
        await Settle(700);
        shell.AutomationSelectFirstEntry();
        await Settle(800);
        captures["12-crypto"] = Capture(window);

        shell.AutomationSection("Secure notes");
        await Settle(700);
        shell.AutomationSelectFirstEntry();
        await Settle(900);
        captures["13-notes"] = Capture(window);

        shell.AutomationSettings();
        await Settle(1000);
        captures["08-settings"] = Capture(window);
        shell.AutomationCloseOverlay();
        await Settle(600);

        // Devices, with a few stand-ins so the screen is shown carrying something rather
        // than empty. They are written to the throwaway trust store beside the demo vault.
        shell.AutomationDevices();
        await Settle(700);
        captures["10-devices-empty"] = Capture(window);

        shell.AutomationFakeDevices();
        await Settle(700);
        captures["09-devices"] = Capture(window);

        shell.AutomationSendPicker();
        await Settle(1800);   // thumbnails decrypt off-thread
        captures["11-send"] = Capture(window);
        shell.AutomationCloseOverlay();
        await Settle(500);

        shell.AutomationSection("Gaming"); // deliberately never populated
        await Settle(800);
        captures["09-empty"] = Capture(window);

        /* --------------------------------------------------------- compose */
        foreach (var (id, shot) in captures)
            Save(Path.Combine(windowDir, $"{id}.png"), shot);
        Console.WriteLine($"[store] {captures.Count} window captures -> {windowDir}");

        foreach (var frame in Frames)
        {
            if (!captures.TryGetValue(frame.Id, out var shot))
            {
                Console.WriteLine($"[store] MISSING capture for {frame.Id}");
                continue;
            }

            var composed = Compose(frame, shot);
            Save(Path.Combine(outDir, $"{frame.Id}.png"), composed);
            Console.WriteLine($"[store] wrote {frame.Id}.png");
        }

        window.Close();
        Console.WriteLine($"[store] done -> {outDir}");
    }

    // ------------------------------------------------------------- capture

    private static BitmapSource Capture(Window window)
    {
        var target = (FrameworkElement)window.Content;
        int w = (int)Math.Round(target.ActualWidth);
        int h = (int)Math.Round(target.ActualHeight);

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(target);
        rtb.Freeze();
        return rtb;
    }

    private static void Save(string path, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    // ------------------------------------------------------------- compose

    /// <summary>
    /// Lays the capture into a 1920x1080 frame beside the copy. Every colour and font
    /// comes from the app's own resource dictionary, so the frame and the product are
    /// visibly the same piece of design.
    /// </summary>
    private static BitmapSource Compose(Frame frame, BitmapSource shot)
    {
        var res = Application.Current.Resources;
        var accent = (Brush)res["Accent"];
        var text = (Brush)res["Text"];
        var muted = (Brush)res["Muted"];
        var glassBg = (Brush)res["GlassBg"];
        var glassStroke = (Brush)res["GlassStroke"];

        const double CopyShare = 0.86;
        const double ShotShare = 1.14;
        const double ShotMarginRight = 70;

        var root = new Grid { Width = FrameWidth, Height = FrameHeight };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CopyShare, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ShotShare, GridUnitType.Star) });

        // Backdrop: the app's own base colour, lifted by two soft accent blooms.
        root.Background = (Brush)res["Bg0"];

        var bloom = new Grid();
        bloom.Children.Add(Bloom(0.10, 0.06, 1500, Color.FromRgb(0x2D, 0xD4, 0xBF), 0.16));
        bloom.Children.Add(Bloom(0.92, 0.95, 1300, Color.FromRgb(0x7C, 0x5C, 0xFF), 0.15));
        Grid.SetColumnSpan(bloom, 2);
        root.Children.Add(bloom);

        /* ---- copy ---- */
        var copy = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(120, 0, 60, 0),
        };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 34) };
        brand.Children.Add(new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(13),
            Background = glassBg,
            BorderBrush = glassStroke,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)res["IconFont"],
                FontSize = 20,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        brand.Children.Add(new TextBlock
        {
            Text = "Lockwell",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        });
        copy.Children.Add(brand);

        var headline = new TextBlock
        {
            FontSize = 68,
            FontWeight = FontWeights.Bold,
            Foreground = text,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 76,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        headline.Inlines.Add(new System.Windows.Documents.Run(frame.Title + " "));
        headline.Inlines.Add(new System.Windows.Documents.Run(frame.TitleAccent)
        {
            Foreground = (Brush)res["HeroBrush"],
        });
        copy.Children.Add(headline);

        copy.Children.Add(new TextBlock
        {
            Text = frame.Body,
            FontSize = 21,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 32,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 24, 0, 0),
            MaxWidth = 640,
        });

        var tags = new WrapPanel { Margin = new Thickness(0, 38, 0, 0) };
        foreach (string tag in frame.Tags)
        {
            tags.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(18, 9, 18, 9),
                Margin = new Thickness(0, 0, 10, 10),
                Background = glassBg,
                BorderBrush = glassStroke,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = tag,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = accent,
                },
            });
        }
        copy.Children.Add(tags);

        Grid.SetColumn(copy, 0);
        root.Children.Add(copy);

        /* ---- the capture ---- */
        // Fit inside the column on BOTH axes. Scaling to height alone let a wide
        // capture run off the right edge of the frame.
        double columnWidth = FrameWidth * (ShotShare / (CopyShare + ShotShare));
        double availableWidth = columnWidth - ShotMarginRight;
        const double availableHeight = 900;

        double scale = Math.Min(1.0, Math.Min(
            availableWidth / shot.PixelWidth,
            availableHeight / shot.PixelHeight));

        double shotWidth = Math.Floor(shot.PixelWidth * scale);
        double shotHeight = Math.Floor(shot.PixelHeight * scale);
        const double radius = 16;

        var picture = new Image
        {
            Source = shot,
            Width = shotWidth,
            Height = shotHeight,
            Stretch = Stretch.Fill,
            // A Border does not clip its child to CornerRadius, so clip explicitly or
            // the capture keeps square corners inside a rounded frame.
            Clip = new RectangleGeometry(new Rect(0, 0, shotWidth, shotHeight), radius, radius),
        };

        var shell = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, ShotMarginRight, 0),
            CornerRadius = new CornerRadius(radius),
            BorderBrush = glassStroke,
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 60,
                ShadowDepth = 24,
                Direction = 270,
                Opacity = 0.6,
                Color = Colors.Black,
            },
            Child = picture,
        };

        Grid.SetColumn(shell, 1);
        root.Children.Add(shell);

        /* ---- render ---- */
        root.Measure(new Size(FrameWidth, FrameHeight));
        root.Arrange(new Rect(0, 0, FrameWidth, FrameHeight));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(FrameWidth, FrameHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    private static UIElement Bloom(double x, double y, double size, Color colour, double opacity)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * opacity), colour.R, colour.G, colour.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, colour.R, colour.G, colour.B), 1));

        return new Ellipse
        {
            Width = size,
            Height = size,
            Fill = brush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(x * FrameWidth - size / 2, y * FrameHeight - size / 2, 0, 0),
            IsHitTestVisible = false,
        };
    }

    /// <summary>Let animations finish and the render thread catch up before capturing.</summary>
    /// <summary>
    /// Build the demo vault, open the shell on one screen, and leave the window up.
    ///
    /// Exists because RenderTargetBitmap does not composite translucent surfaces the way
    /// the real compositor does: low-alpha panels come out pale and washed, which made
    /// several perfectly good surfaces look broken in captures. Judging a dark, layered
    /// theme on those captures means designing against a picture the app never draws.
    /// </summary>
    public static async Task ShowPreviewAsync(string screen)
    {
        var settings = AppSettings.Load();
        settings.RunPreflight = false;
        settings.Profiles.Clear();
        settings.ActiveProfileId = null;
        settings.BlurMediaThumbnails = false;
        settings.CompressionWarningAcknowledged = true;

        var profiles = new ProfileManager(settings);
        profiles.EnsureInitialized();
        if (profiles.ActiveProfile is not null)
            profiles.ActiveProfile.Name = DemoVault.ProfileName;
        settings.Save();

        string vaultDir = profiles.ActiveProfile!.VaultDir;
        var vault = DemoVault.Build(vaultDir);

        var window = new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Brush)Application.Current.Resources["Bg0"],
            Title = "Lockwell UI preview",
        };

        var shell = new VaultShellView(vault, settings, profiles,
            onLock: () => { }, applySettings: () => { }, onSwitchProfile: () => { });
        window.Content = shell;
        window.Show();
        window.Activate();

        await Settle(1200);

        switch (screen.ToLowerInvariant())
        {
            case "storage":
                shell.AutomationStorage();
                await Settle(700);
                shell.AutomationStorageInto("Trip photos");
                break;

            case "media":
                shell.AutomationSection("Media vault");
                await Settle(700);
                shell.AutomationOpenMediaFolder("Trip photos");
                break;

            case "crypto":
                shell.AutomationSection("Crypto");
                await Settle(600);
                shell.AutomationSelectFirstEntry();
                break;

            case "devices":
                shell.AutomationDevices();
                await Settle(500);
                shell.AutomationFakeDevices();
                break;

            case "settings":
                shell.AutomationSettings();
                break;

            default:
                shell.AutomationHome();
                break;
        }

        await Settle(1400);
        Console.WriteLine("[preview] ready");
        Console.Out.Flush();
    }

    private static async Task Settle(int ms)
    {
        await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
}
