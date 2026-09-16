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
/// A surface for video to draw on. Backed by a platform texture view through its handler;
/// on its own it is an empty rectangle, which is exactly what it should be when the item
/// being viewed is audio or a document.
/// </summary>
public sealed class VideoSurface : View
{
    /// <summary>
    /// Raised once the platform view actually has a texture to draw into. Starting
    /// playback before this fires gives the player a surface that is not there yet, and
    /// the video plays with sound but no picture.
    /// </summary>
    public event EventHandler? Ready;

    /// <summary>Called by the platform handler. Not for general use.</summary>
    public void NotifyReady() => Ready?.Invoke(this, EventArgs.Empty);

    /// <summary>True once <see cref="Ready"/> has fired at least once.</summary>
    public bool IsReady { get; private set; }

    public VideoSurface() => Ready += (_, _) => IsReady = true;
}

/// <summary>
/// Playing video and audio straight out of the vault, without the file ever existing.
///
/// This is the harder half of the same promise the image viewer makes. Handing a video to
/// a player normally means a path, and a path means writing the decrypted stream to
/// storage where the media scanner will index it, a gallery will list it, and a backup
/// may take it. For a vault that is the whole thing undone.
///
/// Android has an answer that most apps never reach for: a player can be given a
/// MediaDataSource, which is an object it calls for bytes, rather than a file to open. So
/// the decrypted bytes stay in memory and the player reads out of the array. No path
/// exists, so nothing can index it.
///
/// The bytes are wiped when playback stops, so a closed viewer leaves nothing decoded.
/// </summary>
public interface IMediaPlayback : IDisposable
{
    /// <summary>
    /// Hand over decrypted bytes and, for video, the surface to draw on. The array is
    /// held for as long as playback lasts and wiped by <see cref="Stop"/>.
    /// </summary>
    Task<bool> LoadAsync(byte[] data, VideoSurface? surface, bool isVideo);

    void Play();
    void Pause();
    void SeekTo(TimeSpan position);

    /// <summary>Stop, release the player, and wipe the decrypted bytes.</summary>
    void Stop();

    bool IsPlaying { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }

    /// <summary>Pixel size of the video, so the surface can be given the right shape.</summary>
    (int Width, int Height) VideoSize { get; }

    /// <summary>Raised when the item plays to its end.</summary>
    event EventHandler? Completed;
}
