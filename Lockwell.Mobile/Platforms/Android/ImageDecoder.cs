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

using Android.Graphics;
using Lockwell.Mobile.Services;
using AndroidExif = Android.Media.ExifInterface;
using Stream = System.IO.Stream;

namespace Lockwell.Mobile.Platforms.Android;

/// <summary>
/// Android's own bitmap decoder, driven entirely from byte arrays.
///
/// Every path here takes bytes and returns bytes. Nothing is given a file path, because
/// there is no file: the image exists only as the plaintext of an encrypted attachment,
/// held in memory for as long as it is on screen.
///
/// Two details that would otherwise show up as visible bugs:
///
/// Sampling is done in two passes. The first decodes only the header to learn the real
/// dimensions, which costs almost nothing, and the second decodes for real at a power-of-
/// two reduction. Decoding a 12-megapixel photo at full size to draw it 120 pixels wide is
/// how a photo grid runs out of memory.
///
/// Orientation comes from EXIF, not from the pixels. Phone cameras record the sensor
/// image plus a rotation tag, so a portrait photo decoded naively appears on its side.
/// This uses the framework's own ExifInterface rather than the AndroidX package, which
/// keeps a dependency out of the project that was already turned down once for causing
/// version conflicts.
/// </summary>
public sealed class ImageDecoder : IImageDecoder
{
    public byte[]? Thumbnail(byte[] source, int maxEdge) => Decode(source, maxEdge, quality: 82);

    public byte[]? ForViewing(byte[] source, int maxEdge) => Decode(source, maxEdge, quality: 92);

    public (int Width, int Height) Measure(byte[] source)
    {
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(source, 0, source.Length, bounds);
            return (bounds.OutWidth, bounds.OutHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Pull a representative frame out of a video held in memory.
    ///
    /// MediaMetadataRetriever normally wants a path or a URL, either of which would mean
    /// writing the decrypted video to storage first. It also accepts a MediaDataSource,
    /// which is an object it reads bytes from, so the video stays where it is.
    ///
    /// The frame is taken a second in rather than at zero: the opening frame of a video is
    /// very often black, and a wall of black tiles is no better than a wall of icons.
    /// </summary>
    public byte[]? VideoPoster(byte[] source, int maxEdge)
    {
        if (source.Length == 0) return null;

        global::Android.Media.MediaMetadataRetriever? retriever = null;
        ByteArrayMediaDataSource? feed = null;

        try
        {
            feed = new ByteArrayMediaDataSource(source);
            retriever = new global::Android.Media.MediaMetadataRetriever();
            retriever.SetDataSource(feed);

            using Bitmap? frame =
                retriever.GetFrameAtTime(1_000_000, global::Android.Media.Option.ClosestSync)
                ?? retriever.GetFrameAtTime(0);

            if (frame is null) return null;

            // Scale down the same way a photo would be, so a 4K video does not become a
            // 4K bitmap on its way to a thumbnail.
            int longest = Math.Max(frame.Width, frame.Height);
            using Bitmap scaled = longest > maxEdge && longest > 0
                ? Bitmap.CreateScaledBitmap(
                    frame,
                    Math.Max(1, frame.Width * maxEdge / longest),
                    Math.Max(1, frame.Height * maxEdge / longest),
                    filter: true)!
                : frame;

            using var buffer = new MemoryStream();
            scaled.Compress(Bitmap.CompressFormat.Jpeg!, 82, buffer);
            return buffer.ToArray();
        }
        catch
        {
            // A format this device cannot decode is a missing thumbnail, not a failure
            // worth surfacing; the tile keeps its icon.
            return null;
        }
        finally
        {
            try { retriever?.Release(); } catch { }
            try { retriever?.Dispose(); } catch { }

            // Closing the feed wipes the copy of the video it was reading from.
            try { feed?.Close(); } catch { }
            try { feed?.Dispose(); } catch { }
        }
    }

    private static byte[]? Decode(byte[] source, int maxEdge, int quality)
    {
        if (source.Length == 0) return null;

        try
        {
            // Pass one: header only, to learn the size without allocating the pixels.
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(source, 0, source.Length, bounds);

            int width = bounds.OutWidth;
            int height = bounds.OutHeight;
            if (width <= 0 || height <= 0) return null;

            // Pass two: decode reduced by a power of two, which is the only reduction
            // BitmapFactory does cheaply.
            var options = new BitmapFactory.Options
            {
                InSampleSize = SampleSizeFor(width, height, maxEdge),
                InPreferredConfig = Bitmap.Config.Argb8888,
            };

            using Bitmap? decoded = BitmapFactory.DecodeByteArray(source, 0, source.Length, options);
            if (decoded is null) return null;

            using Bitmap oriented = ApplyOrientation(decoded, source);
            using var buffer = new MemoryStream();

            // JPEG cannot carry transparency, so anything with an alpha channel is kept
            // as PNG. Getting this wrong turns transparent corners black.
            Bitmap.CompressFormat format = decoded.HasAlpha
                ? Bitmap.CompressFormat.Png!
                : Bitmap.CompressFormat.Jpeg!;

            oriented.Compress(format, quality, buffer);
            return buffer.ToArray();
        }
        catch
        {
            // A file that is not really an image, or one this device cannot decode, is a
            // display problem and not a reason to bring the page down.
            return null;
        }
    }

    private static int SampleSizeFor(int width, int height, int maxEdge)
    {
        int sample = 1;
        while (width / (sample * 2) >= maxEdge || height / (sample * 2) >= maxEdge)
        {
            sample *= 2;
            if (sample >= 64) break;
        }
        return sample;
    }

    /// <summary>
    /// Rotate or flip to match the EXIF orientation tag. Returns the original bitmap
    /// unchanged when there is nothing to do, so the common case costs one tag read.
    /// </summary>
    private static Bitmap ApplyOrientation(Bitmap bitmap, byte[] source)
    {
        try
        {
            using Stream stream = new MemoryStream(source);
            var exif = new AndroidExif(stream);
            int orientation = exif.GetAttributeInt(
                AndroidExif.TagOrientation, (int)global::Android.Media.Orientation.Normal);

            var matrix = new Matrix();
            switch (orientation)
            {
                case (int)global::Android.Media.Orientation.Rotate90:
                    matrix.PostRotate(90);
                    break;
                case (int)global::Android.Media.Orientation.Rotate180:
                    matrix.PostRotate(180);
                    break;
                case (int)global::Android.Media.Orientation.Rotate270:
                    matrix.PostRotate(270);
                    break;
                case (int)global::Android.Media.Orientation.FlipHorizontal:
                    matrix.PostScale(-1, 1);
                    break;
                case (int)global::Android.Media.Orientation.FlipVertical:
                    matrix.PostScale(1, -1);
                    break;
                default:
                    return bitmap;
            }

            Bitmap? rotated = Bitmap.CreateBitmap(
                bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, filter: true);

            return rotated ?? bitmap;
        }
        catch
        {
            // No EXIF, or a format that has none. Showing it unrotated beats not at all.
            return bitmap;
        }
    }
}
