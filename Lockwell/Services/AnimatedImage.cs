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
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

// Both imaging stacks define BitmapDecoder and BitmapFrame. WPF's names win here for
// the types handed back to the UI; the Windows ones are spelled out where used.
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using Windows.Graphics.Imaging;

namespace Lockwell.Services;

/// <summary>One frame of an animated image, with how long it should stay on screen.</summary>
public sealed class AnimationFrame
{
    public required BitmapSource Image { get; init; }
    public required TimeSpan Delay { get; init; }
}

/// <summary>
/// Decodes multi-frame images so they actually animate.
///
/// WPF's own imaging shows the first frame of an animated WebP and stops, which is why
/// an animated WebP looked like a still picture. Windows' newer imaging stack knows how
/// to walk the frames, so this uses that and hands WPF a list of plain bitmaps to flip
/// through.
///
/// Everything happens on the decrypted bytes already in memory. Nothing is written to
/// disk, which is the same rule the rest of the image path follows.
/// </summary>
public static class AnimatedImage
{
    /// <summary>Formats where more than one frame is worth looking for.</summary>
    public static bool CouldBeAnimated(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".webp" or ".gif" or ".png" or ".avif" or ".heic";
    }

    /// <summary>
    /// Pull every frame out of an image. Returns null when the format has a single
    /// frame or cannot be decoded, so callers fall back to the normal still path.
    /// </summary>
    public static async Task<IReadOnlyList<AnimationFrame>?> TryDecodeAsync(byte[] data)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(data);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var decoder = await WinBitmapDecoder.CreateAsync(stream);
            if (decoder.FrameCount <= 1) return null;

            var frames = new List<AnimationFrame>((int)decoder.FrameCount);

            for (uint i = 0; i < decoder.FrameCount; i++)
            {
                Windows.Graphics.Imaging.BitmapFrame frame = await decoder.GetFrameAsync(i);

                var pixels = await frame.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                byte[] bytes = pixels.DetachPixelData();
                int width = (int)frame.PixelWidth;
                int height = (int)frame.PixelHeight;

                var bitmap = BitmapSource.Create(
                    width, height, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32,
                    null, bytes, width * 4);
                bitmap.Freeze(); // frozen so the UI thread can use it directly

                frames.Add(new AnimationFrame
                {
                    Image = bitmap,
                    Delay = DelayOf(frame),
                });
            }

            return frames;
        }
        catch
        {
            // An unreadable or single-frame image is not an error worth surfacing;
            // the caller just shows it as a still.
            return null;
        }
    }

    /// <summary>
    /// Per-frame delay from the file's own metadata, falling back to something sane.
    ///
    /// Very short delays are clamped the way browsers do: a lot of files in the wild
    /// claim 0 or 10ms, which no renderer honours, and playing them literally makes an
    /// animation run several times too fast.
    /// </summary>
    private static TimeSpan DelayOf(Windows.Graphics.Imaging.BitmapFrame frame)
    {
        const int fallbackMs = 100;
        const int minimumMs = 20;

        try
        {
            var properties = frame.BitmapProperties;

            // GIF stores this in hundredths of a second under its own namespace.
            foreach (string key in new[]
                     {
                         "/grctlext/Delay",
                         "/imgdesc/Delay",
                     })
            {
                try
                {
                    var value = properties.GetPropertiesAsync(new[] { key }).GetAwaiter().GetResult();
                    if (value.TryGetValue(key, out var property) && property.Value is ushort hundredths)
                    {
                        int ms = hundredths * 10;
                        return TimeSpan.FromMilliseconds(Math.Max(minimumMs, ms));
                    }
                }
                catch { /* property not present in this format */ }
            }
        }
        catch { /* no metadata at all */ }

        return TimeSpan.FromMilliseconds(fallbackMs);
    }
}
