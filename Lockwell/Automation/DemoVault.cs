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
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Automation;

/// <summary>
/// Builds a throwaway vault full of invented content for the listing screenshots.
///
/// Every byte here is generated in this file. No file, photo, account or folder
/// belonging to the person running this is read, copied or referenced. The vault is
/// written to whatever LOCKWELL_USER_DATA points at, which the harness sets to a
/// temporary folder, so the real vault is never even opened.
/// </summary>
internal static class DemoVault
{
    public const string ProfileName = "Demo Vault";
    public const string Password = "correct-horse-battery-staple-demo";

    public static SecureString DemoPassword()
    {
        var s = new SecureString();
        foreach (char c in Password) s.AppendChar(c);
        s.MakeReadOnly();
        return s;
    }

    public static VaultManager Build(string vaultDir)
    {
        Directory.CreateDirectory(vaultDir);
        var vault = new VaultManager(vaultDir);

        using (var pw = DemoPassword())
            vault.CreateVault(pw, withRecoveryKey: true);

        var data = vault.Data;
        var media = data.Categories.First(c => c.IsMedia);

        AddLogins(data);
        AddNotes(data);
        AddMedia(vault, data, media.Id);

        data.LastExportUtc = DateTime.UtcNow.AddDays(-6).AddHours(-3);
        vault.Save();
        return vault;
    }

    // ------------------------------------------------------------- entries

    private static void AddLogins(VaultData data)
    {
        // Fictional services on reserved .test / .example domains, which by RFC 2606
        // can never be real sites. Passwords are visibly fake placeholders.
        Add(data, "Email accounts", EntryKind.Login, "Northwind Mail", true,
            ("Username", "demo@northwind.test", false),
            ("Password", "9xKq-Tarn-4417-Vell", true),
            ("Recovery", "demo.backup@northwind.test", false));

        Add(data, "Email accounts", EntryKind.Login, "Fabrikam Webmail", false,
            ("Username", "sam.rivers@fabrikam.example", false),
            ("Password", "Ptr7-Solace-2290", true));

        Add(data, "Game Platforms", EntryKind.Login, "Aurora Games", true,
            ("Username", "riverhawk_88", false),
            ("Password", "Zbq4-Lantern-7731", true),
            ("2FA backup", "4821 9930 5517", true));

        Add(data, "Game Platforms", EntryKind.Login, "Pixelforge Store", false,
            ("Username", "riverhawk", false),
            ("Password", "Wm3d-Copper-8842", true));

        Add(data, "Social", EntryKind.Login, "Lumen Social", false,
            ("Username", "riverhawk", false),
            ("Password", "Qs2p-Willow-6604", true));

        // A twelve-word phrase, so the numbered grid can be seen doing its job. These are
        // ordinary dictionary words in a demo vault and are not a real seed phrase.
        Add(data, "Crypto", EntryKind.CryptoWallet, "Cold wallet", true,
            ("Address", "bc1qexampleaddressnotrealdonotuse0000", false),
            ("Seed phrase",
             "house stadium pantry cable tires waffle brick shoe remote sword magic offense",
             true),
            ("Private key", "0xexampleprivatekeynotrealdonotuse", true));

        // One section the user made themselves, so the folding group has something in it.
        Add(data, "Homelab", EntryKind.Login, "Router admin", false,
            ("Username", "admin", false),
            ("Password", "Hn8t-Beacon-1195", true),
            ("Address", "192.168.1.1", false));
    }

    private static void AddNotes(VaultData data)
    {
        var note = Add(data, "Secure notes", EntryKind.SecureNote, "Router details", false,
            ("Admin page", "192.168.0.1", false),
            ("Password", "Vg5m-Anchor-2073", true));
        note.Notes = "Placeholder note for the demo vault. Nothing here is real.";

        var wifi = Add(data, "Secure notes", EntryKind.SecureNote, "Home Wi-Fi", false,
            ("Network", "Northwind-Guest", false),
            ("Password", "Ld6k-Meadow-9928", true));
        wifi.Notes = "Invented network name, invented key.";
    }

    private static VaultEntry Add(
        VaultData data, string categoryName, EntryKind kind, string title, bool favourite,
        params (string Label, string Value, bool Secret)[] fields)
    {
        var category = data.Categories.FirstOrDefault(c => c.Name == categoryName);

        if (category is null)
        {
            // A name that is not one of the presets is a section the user would have made,
            // so it is created as one and lands under "Your sections".
            category = new Category
            {
                Name = categoryName,
                Glyph = "",
                Order = data.Categories.Count,
                IsCustom = true,
            };
            data.Categories.Add(category);
        }

        var entry = new VaultEntry
        {
            CategoryId = category.Id,
            Kind = kind,
            Title = title,
            Favorite = favourite,
            CreatedUtc = DateTime.UtcNow.AddDays(-Random.Shared.Next(20, 400)),
            ModifiedUtc = DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 20)),
        };

        foreach (var (label, value, secret) in fields)
            entry.Fields.Add(new EntryField { Label = label, Value = value, IsSecret = secret });

        data.Entries.Add(entry);
        return entry;
    }

    // --------------------------------------------------------------- media

    private static void AddMedia(VaultManager vault, VaultData data, string mediaCategoryId)
    {
        var trip = NewFolder(data, mediaCategoryId, "Trip photos", MediaFolderKind.Photos, 0, favourite: true);
        var design = NewFolder(data, mediaCategoryId, "Design refs", MediaFolderKind.Photos, 1);
        var clips = NewFolder(data, mediaCategoryId, "Screen clips", MediaFolderKind.Mixed, 2);
        var voice = NewFolder(data, mediaCategoryId, "Voice notes", MediaFolderKind.Audio, 3);

        string[] tripNames =
        {
            "Harbour at dusk", "Cliff path", "Old town square", "Ferry crossing",
            "Morning market", "Lighthouse", "Rooftop view", "Coast road",
        };
        string[] designNames = { "Palette study", "Gradient test", "Type specimen", "Icon sheet" };
        string[] clipNames = { "Build walkthrough", "Bug repro", "Layout pass" };

        for (int i = 0; i < tripNames.Length; i++)
            AddImage(vault, data, mediaCategoryId, trip.Id, tripNames[i], i, 1400, 1000);

        for (int i = 0; i < designNames.Length; i++)
            AddImage(vault, data, mediaCategoryId, design.Id, designNames[i], i + 20, 1200, 1200);

        for (int i = 0; i < clipNames.Length; i++)
            AddImage(vault, data, mediaCategoryId, clips.Id, clipNames[i], i + 40, 1600, 900);

        AddAudio(vault, data, mediaCategoryId, voice.Id, "Meeting recap", 8);
        AddAudio(vault, data, mediaCategoryId, voice.Id, "Idea while walking", 5);

        // Two loose files at the gallery root, so that state is represented too.
        AddImage(vault, data, mediaCategoryId, null, "Scanned receipt", 60, 900, 1300);
        AddImage(vault, data, mediaCategoryId, null, "Whiteboard", 61, 1500, 1000);
    }

    private static MediaFolder NewFolder(
        VaultData data, string categoryId, string name, MediaFolderKind kind, int order,
        bool favourite = false)
    {
        var folder = new MediaFolder
        {
            CategoryId = categoryId,
            Name = name,
            Kind = kind,
            Order = order,
            IsFavorite = favourite,
            CreatedUtc = DateTime.UtcNow.AddDays(-30 + order),
        };
        data.MediaFolders.Add(folder);
        return folder;
    }

    private static void AddImage(
        VaultManager vault, VaultData data, string categoryId, string? folderId,
        string title, int seed, int width, int height)
    {
        byte[] png = GenerateImage(width, height, seed);
        var att = vault.AddAttachmentFromBytes(png, $"{Slug(title)}.png", "image/png");

        data.Entries.Add(new VaultEntry
        {
            CategoryId = categoryId,
            MediaFolderId = folderId,
            Kind = EntryKind.Media,
            Title = title,
            MediaSortOrder = seed,
            CreatedUtc = DateTime.UtcNow.AddDays(-Random.Shared.Next(3, 200)),
            ModifiedUtc = DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 3)),
            Attachments = { att },
        });
    }

    private static void AddAudio(
        VaultManager vault, VaultData data, string categoryId, string folderId,
        string title, int seconds)
    {
        byte[] wav = GenerateTone(seconds, seed: title.Length);
        var att = vault.AddAttachmentFromBytes(wav, $"{Slug(title)}.wav", "audio/wav");

        data.Entries.Add(new VaultEntry
        {
            CategoryId = categoryId,
            MediaFolderId = folderId,
            Kind = EntryKind.Media,
            Title = title,
            CreatedUtc = DateTime.UtcNow.AddDays(-Random.Shared.Next(3, 60)),
            ModifiedUtc = DateTime.UtcNow.AddDays(-1),
            Attachments = { att },
        });
    }

    private static string Slug(string title) =>
        title.ToLowerInvariant().Replace(' ', '-');

    /// <summary>
    /// An abstract gradient composition. Deliberately not a photograph of anything:
    /// it reads as real content in a thumbnail grid while being pure arithmetic.
    /// </summary>
    private static byte[] GenerateImage(int width, int height, int seed)
    {
        var rng = new Random(seed * 7919);

        double baseHue = rng.NextDouble() * 360;
        double hueShift = 40 + rng.NextDouble() * 90;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);

            var backdrop = new LinearGradientBrush(
                FromHsv(baseHue, 0.55, 0.30),
                FromHsv(baseHue + hueShift, 0.65, 0.12),
                new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(backdrop, null, rect);

            // A few soft blooms give the thumbnails visual variety.
            for (int i = 0; i < 4; i++)
            {
                double cx = rng.NextDouble() * width;
                double cy = rng.NextDouble() * height;
                double r = (0.18 + rng.NextDouble() * 0.42) * Math.Max(width, height);

                var bloom = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.5, 0.5),
                    Center = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                };
                var colour = FromHsv(baseHue + hueShift * rng.NextDouble() * 2, 0.7, 0.85);
                bloom.GradientStops.Add(new GradientStop(
                    Color.FromArgb(120, colour.R, colour.G, colour.B), 0));
                bloom.GradientStops.Add(new GradientStop(
                    Color.FromArgb(0, colour.R, colour.G, colour.B), 1));

                dc.DrawEllipse(bloom, null, new Point(cx, cy), r, r);
            }

            // A soft diagonal band, so shapes are not all round.
            var band = new LinearGradientBrush(
                Color.FromArgb(60, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                new Point(0, 0), new Point(1, 1));
            dc.PushOpacity(0.5);
            dc.DrawGeometry(band, null, new PathGeometry(new[]
            {
                new PathFigure(
                    new Point(0, height * 0.62),
                    new[] { new LineSegment(new Point(width, height * 0.28), true),
                            new LineSegment(new Point(width, height), true),
                            new LineSegment(new Point(0, height), true) },
                    true),
            }));
            dc.Pop();
        }

        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A short synthesised tone, so the audio entries are real playable files.</summary>
    private static byte[] GenerateTone(int seconds, int seed)
    {
        const int rate = 44100;
        int samples = seconds * rate;
        int dataBytes = samples * 2;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data".ToCharArray());
        w.Write(dataBytes);

        double f1 = 220 + (seed % 7) * 30;
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / rate;
            double env = Math.Min(1, t * 3) * Math.Max(0, 1 - t / seconds);
            double v = Math.Sin(2 * Math.PI * f1 * t) * 0.5
                     + Math.Sin(2 * Math.PI * f1 * 1.5 * t) * 0.2;
            w.Write((short)(v * env * 9000));
        }

        w.Flush();
        return ms.ToArray();
    }

    private static Color FromHsv(double hue, double sat, double val)
    {
        hue = ((hue % 360) + 360) % 360;
        double c = val * sat;
        double x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        double m = val - c;

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}
