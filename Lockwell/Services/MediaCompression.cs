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
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace Lockwell.Services;

/// <summary>How aggressively to re-encode. Higher quality means a bigger file.</summary>
public enum CompressionQuality
{
    Maximum = 0,  // barely touched, small saving
    High = 1,
    Balanced = 2,
    Small = 3,
    Smallest = 4, // visibly/audibly degraded, big saving
}

public sealed class CompressionSettings
{
    public CompressionQuality Quality { get; set; } = CompressionQuality.Balanced;

    /// <summary>Optional cap on the longest image edge. 0 leaves the resolution alone.</summary>
    public int MaxImageDimension { get; set; }

    /// <summary>Re-encode PNG to JPEG. Much smaller, but drops transparency and metadata.</summary>
    public bool ConvertPngToJpeg { get; set; } = true;
}

public sealed class CompressionResult
{
    public required byte[] Data { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required long OriginalBytes { get; init; }
    public long NewBytes => Data.LongLength;

    /// <summary>Negative means the re-encode made the file bigger.</summary>
    public double SavedFraction =>
        OriginalBytes <= 0 ? 0 : 1.0 - ((double)NewBytes / OriginalBytes);

    /// <summary>
    /// Re-encoding does not always shrink a file. Converting an already-efficient
    /// image to JPEG, or an MP3 to a higher-bitrate MP3, can easily produce something
    /// larger -- and it is lossy either way, so you would pay quality for nothing.
    /// Callers must not offer to replace the original unless this is true.
    /// </summary>
    public bool IsWorthwhile => NewBytes < OriginalBytes && SavedFraction >= 0.05;
}

/// <summary>
/// Re-encodes a single attachment to make it smaller.
///
/// Everything happens in memory and on this machine. Images use the WPF encoders that
/// ship with .NET; audio and video use Windows.Media.Transcoding, which is part of
/// Windows itself. No third-party codec, nothing downloaded, and no network code
/// anywhere in this path -- the bytes never leave the process, and unlike playback
/// this never writes a decrypted file to disk.
///
/// Re-encoding is lossy and cannot be undone. Callers must confirm with the user first.
/// </summary>
public static class MediaCompression
{
    /// <summary>
    /// Whether shrinking this file before sending it is likely to be worth doing.
    ///
    /// Formats that are already compressed are left alone. Re-encoding a JPEG or an MP4 to
    /// save a few percent trades real quality for almost nothing, which is the same
    /// judgement <see cref="CompressionResult.IsWorthwhile"/> makes after the fact -- this
    /// just makes it before, so the question is only asked when the answer might be yes.
    ///
    /// Small files are skipped too: shaving a few kilobytes off something already tiny is
    /// not worth a decision.
    /// </summary>
    public static bool CouldShrink(string fileName, long sizeBytes)
    {
        const long floor = 512 * 1024;   // below this, nobody cares
        if (sizeBytes < floor) return false;

        string ext = Path.GetExtension(fileName).ToLowerInvariant();

        return ext switch
        {
            // Lossless or uncompressed: real savings available.
            ".png" or ".bmp" or ".tif" or ".tiff" => true,

            // Lossy already. Re-encoding costs quality for very little.
            ".jpg" or ".jpeg" or ".webp" or ".heic" or ".avif" => false,
            ".mp3" or ".aac" or ".m4a" or ".ogg" or ".opus" or ".wma" or ".flac" => false,
            ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" => false,

            _ => false,
        };
    }

    // ------------------------------------------------------------------ images

    public static byte[] CompressImage(byte[] source, CompressionSettings settings, bool asJpeg)
    {
        var decoded = new BitmapImage();
        using (var input = new MemoryStream(source, writable: false))
        {
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.StreamSource = input;

            // Downscaling at decode time is both faster and higher quality than
            // decoding full size and resampling afterwards.
            if (settings.MaxImageDimension > 0)
            {
                decoded.DecodePixelWidth = 0;
                decoded.DecodePixelHeight = 0;
            }
            decoded.EndInit();
        }
        decoded.Freeze();

        BitmapSource frame = decoded;
        if (settings.MaxImageDimension > 0)
        {
            double longest = Math.Max(decoded.PixelWidth, decoded.PixelHeight);
            if (longest > settings.MaxImageDimension)
            {
                double scale = settings.MaxImageDimension / longest;
                var scaled = new TransformedBitmap(decoded,
                    new System.Windows.Media.ScaleTransform(scale, scale));
                scaled.Freeze();
                frame = scaled;
            }
        }

        using var output = new MemoryStream();
        if (asJpeg)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality(settings.Quality) };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            encoder.Save(output);
        }
        else
        {
            // PNG is lossless: the only lever is resolution, handled above.
            var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            encoder.Save(output);
        }
        return output.ToArray();
    }

    private static int JpegQuality(CompressionQuality q) => q switch
    {
        CompressionQuality.Maximum => 95,
        CompressionQuality.High => 88,
        CompressionQuality.Balanced => 80,
        CompressionQuality.Small => 68,
        _ => 55,
    };

    /// <summary>True when the image has any pixel that is not fully opaque.</summary>
    public static bool HasTransparency(byte[] source)
    {
        try
        {
            var decoded = new BitmapImage();
            using (var input = new MemoryStream(source, writable: false))
            {
                decoded.BeginInit();
                decoded.CacheOption = BitmapCacheOption.OnLoad;
                decoded.StreamSource = input;
                decoded.EndInit();
            }
            decoded.Freeze();

            var converted = new FormatConvertedBitmap(decoded, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            int stride = converted.PixelWidth * 4;
            byte[] pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            for (int i = 3; i < pixels.Length; i += 4)
                if (pixels[i] != 255) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    // --------------------------------------------------------- audio / video

    public static async Task<byte[]> CompressAudioAsync(byte[] source, CompressionQuality quality)
    {
        var profile = MediaEncodingProfile.CreateMp3(quality switch
        {
            CompressionQuality.Maximum => AudioEncodingQuality.High,
            CompressionQuality.High => AudioEncodingQuality.High,
            CompressionQuality.Balanced => AudioEncodingQuality.Medium,
            CompressionQuality.Small => AudioEncodingQuality.Low,
            _ => AudioEncodingQuality.Low,
        });

        return await TranscodeAsync(source, profile);
    }

    public static async Task<byte[]> CompressVideoAsync(byte[] source, CompressionQuality quality)
    {
        var profile = MediaEncodingProfile.CreateMp4(quality switch
        {
            CompressionQuality.Maximum => VideoEncodingQuality.HD1080p,
            CompressionQuality.High => VideoEncodingQuality.HD720p,
            CompressionQuality.Balanced => VideoEncodingQuality.HD720p,
            CompressionQuality.Small => VideoEncodingQuality.Wvga,
            _ => VideoEncodingQuality.Vga,
        });

        return await TranscodeAsync(source, profile);
    }

    /// <summary>
    /// Re-encode a video into something WPF's media player can actually open.
    ///
    /// Why this is needed at all is worth writing down, because it looks like a bug and
    /// is not one. WPF's MediaElement is built on DirectShow, the older of the two
    /// Windows media stacks. The codecs Windows ships for WebM, VP9 and AV1 register with
    /// Media Foundation, the newer one. So a WebM file can be perfectly decodable by the
    /// machine and still fail in MediaElement, which is exactly what "could not play this
    /// file" was reporting without explaining.
    ///
    /// MediaTranscoder is Media Foundation, so it can read what MediaElement cannot. This
    /// converts to H.264 in MP4, which DirectShow has always understood.
    ///
    /// The result is for viewing only and is never saved: the stored item keeps its
    /// original bytes. Conversion is capped at 1080p, so a larger source is scaled down
    /// for playback while the file itself keeps its full resolution.
    /// </summary>
    public static async Task<byte[]> ConvertForPlaybackAsync(byte[] source) =>
        await TranscodeAsync(source, MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p));

    /// <summary>
    /// Stream-to-stream transcode entirely in RAM. Deliberately not the file-based
    /// overload: that would put a decrypted copy on disk, which is exactly what we
    /// avoid everywhere except live playback.
    /// </summary>
    private static async Task<byte[]> TranscodeAsync(byte[] source, MediaEncodingProfile profile)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(source);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        input.Seek(0);

        using var output = new InMemoryRandomAccessStream();

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        PrepareTranscodeResult prepared =
            await transcoder.PrepareStreamTranscodeAsync(input, output, profile);

        if (!prepared.CanTranscode)
            throw new NotSupportedException($"Windows cannot re-encode this file ({prepared.FailureReason}).");

        await prepared.TranscodeAsync();

        output.Seek(0);
        var result = new byte[output.Size];
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(result);
        }
        return result;
    }
}
