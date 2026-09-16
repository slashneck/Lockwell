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

using System.Windows.Media.Imaging;
using Lockwell.Services;

namespace Lockwell.Tests;

/// <summary>
/// Compression must never make a file worse. The critical property is that running a
/// folder repeatedly cannot degrade already-efficient files through generation loss.
/// </summary>
internal static class CompressionChecks
{
    public static void Run()
    {
        WorthShrinkingBeforeSending();
        Check.Section("12. Compression");

        byte[] png = MakePng(1024);
        Check.Note($"source PNG 1024x1024: {png.Length / 1024:N0} KB");

        foreach (var q in Enum.GetValues<CompressionQuality>())
        {
            byte[] outBytes = MediaCompression.CompressImage(
                png, new CompressionSettings { Quality = q }, asJpeg: true);
            double saved = 1.0 - ((double)outBytes.Length / png.Length);
            Check.Note($"PNG->JPEG {q,-9}: {outBytes.Length / 1024,5:N0} KB ({saved * 100:0.#}% smaller)");
            Check.That($"image compresses at {q}", outBytes.Length > 0 && outBytes.Length < png.Length);
        }

        byte[] resized = MediaCompression.CompressImage(
            png, new CompressionSettings { Quality = CompressionQuality.Balanced, MaxImageDimension = 512 },
            asJpeg: true);

        var decoded = new BitmapImage();
        using (var ms = new MemoryStream(resized))
        {
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.StreamSource = ms;
            decoded.EndInit();
        }
        Check.That($"resizing produces a valid image ({decoded.PixelWidth}x{decoded.PixelHeight})",
            decoded.PixelWidth == 512 && decoded.PixelHeight == 512);

        Check.That("opaque PNG reports no transparency", !MediaCompression.HasTransparency(png));
        Check.That("transparent PNG is detected (JPEG would flatten it)",
            MediaCompression.HasTransparency(MakeTransparentPng(64)));

        BulkSafety(png);
    }

    private static void BulkSafety(byte[] png)
    {
        Check.Section("13. Bulk compression safety");
        Check.Note("Running a folder must not degrade files that are already efficient.");

        byte[] jpeg = MediaCompression.CompressImage(
            png, new CompressionSettings { Quality = CompressionQuality.Balanced }, asJpeg: true);
        var first = Result(jpeg, png.Length);
        Check.That("first pass (PNG -> JPEG) is applied", first.IsWorthwhile);

        byte[] again = MediaCompression.CompressImage(
            jpeg, new CompressionSettings { Quality = CompressionQuality.Balanced }, asJpeg: true);
        Check.That("second pass on an already-compressed file is SKIPPED",
            !Result(again, jpeg.Length).IsWorthwhile);

        byte[] current = jpeg;
        int applied = 0;
        for (int run = 0; run < 5; run++)
        {
            byte[] candidate = MediaCompression.CompressImage(
                current, new CompressionSettings { Quality = CompressionQuality.Balanced }, asJpeg: true);
            if (Result(candidate, current.Length).IsWorthwhile) { current = candidate; applied++; }
        }
        Check.That("repeated bulk runs stop re-encoding (no generation-loss spiral)", applied == 0);

        Check.That("a 3% saving is rejected as not worthwhile",
            !Result(new byte[970], 1000).IsWorthwhile);
        Check.That("a 60% saving is accepted", Result(new byte[400], 1000).IsWorthwhile);
        Check.That("a result LARGER than the original is rejected",
            !Result(new byte[1500], 1000).IsWorthwhile);
    }

    private static CompressionResult Result(byte[] data, long originalBytes) => new()
    {
        Data = data,
        FileName = "x.jpg",
        MediaType = "image/jpeg",
        OriginalBytes = originalBytes,
    };

    /// <summary>Smooth gradients plus a soft blob, so it behaves like a photograph.</summary>
    private static byte[] MakePng(int size)
    {
        int stride = size * 4;
        byte[] pixels = new byte[stride * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * stride + x * 4;
                double dx = (x - size / 2.0) / size;
                double dy = (y - size / 2.0) / size;
                double blob = Math.Exp(-(dx * dx + dy * dy) * 6);
                pixels[i + 0] = (byte)(40 + 180 * ((double)x / size));
                pixels[i + 1] = (byte)(30 + 200 * ((double)y / size) * blob);
                pixels[i + 2] = (byte)(60 + 170 * blob);
                pixels[i + 3] = 255;
            }
        }
        return Encode(size, stride, pixels);
    }

    private static byte[] MakeTransparentPng(int size)
    {
        int stride = size * 4;
        byte[] pixels = new byte[stride * size];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = 100; pixels[i + 1] = 150; pixels[i + 2] = 200;
            pixels[i + 3] = 128; // half transparent
        }
        return Encode(size, stride, pixels);
    }

    private static byte[] Encode(int size, int stride, byte[] pixels)
    {
        var bmp = BitmapSource.Create(size, size, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Whether it is worth offering to shrink a file before sending it.
    ///
    /// The judgement that matters is the refusal. Re-encoding something already lossy --
    /// a JPEG, an MP4, an MP3 -- costs real quality to save a few percent, so the question
    /// should not even be asked. Asking and defaulting to yes would quietly degrade every
    /// photo a person ever sent.
    /// </summary>
    private static void WorthShrinkingBeforeSending()
    {
        Check.Section("54. Offering to shrink a file before sending it");

        const long big = 4 * 1024 * 1024;

        Check.That("a PNG is worth shrinking", MediaCompression.CouldShrink("shot.png", big));
        Check.That("so is a bitmap", MediaCompression.CouldShrink("scan.bmp", big));
        Check.That("and a TIFF", MediaCompression.CouldShrink("page.tiff", big));

        Check.That("a JPEG is left alone", !MediaCompression.CouldShrink("photo.jpg", big));
        Check.That("so is a WebP", !MediaCompression.CouldShrink("sticker.webp", big));
        Check.That("and a HEIC", !MediaCompression.CouldShrink("iphone.heic", big));
        Check.That("video is left alone", !MediaCompression.CouldShrink("clip.mp4", big));
        Check.That("so is audio", !MediaCompression.CouldShrink("song.mp3", big));
        Check.That("and a lossless audio format, which re-encoding would ruin",
            !MediaCompression.CouldShrink("master.flac", big));

        Check.That("an unknown extension is not touched",
            !MediaCompression.CouldShrink("notes.bin", big));

        // Nobody wants a decision about a file that is already tiny.
        Check.That("a small PNG is not worth asking about",
            !MediaCompression.CouldShrink("icon.png", 40 * 1024));
        Check.That("but a large one is",
            MediaCompression.CouldShrink("icon.png", big));

        Check.That("case does not matter", MediaCompression.CouldShrink("SHOT.PNG", big));
    }

}
