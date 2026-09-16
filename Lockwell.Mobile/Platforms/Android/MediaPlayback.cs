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

using Android.Media;
using Android.Views;
using Lockwell.Mobile.Services;
using Stream = System.IO.Stream;

namespace Lockwell.Mobile.Platforms.Android;

/// <summary>
/// Feeds a player from a byte array instead of a file.
///
/// This is the piece that lets vault video and audio play without ever existing on disk.
/// Android's MediaPlayer normally wants a path or a URL, and giving it either would mean
/// writing the decrypted stream somewhere the media scanner can find it. A MediaDataSource
/// inverts that: the player asks this object for ranges of bytes, and the bytes come from
/// memory.
///
/// The array is wiped in <see cref="Close"/>, which the player calls when it is released,
/// so the decrypted media does not outlive playback.
/// </summary>
internal sealed class ByteArrayMediaDataSource : MediaDataSource
{
    private byte[]? _data;

    public ByteArrayMediaDataSource(byte[] data) => _data = data;

    public override long Size => _data?.LongLength ?? 0;

    public override int ReadAt(long position, byte[]? buffer, int offset, int size)
    {
        byte[]? data = _data;
        if (data is null || buffer is null) return -1;

        // -1 is how this contract says "end of stream".
        if (position >= data.LongLength) return -1;
        if (size <= 0) return 0;

        long remaining = data.LongLength - position;
        int count = (int)Math.Min(size, remaining);

        Array.Copy(data, position, buffer, offset, count);
        return count;
    }

    public override void Close()
    {
        byte[]? data = _data;
        _data = null;

        if (data is not null) Array.Clear(data, 0, data.Length);
    }

    protected override void Dispose(bool disposing)
    {
        Close();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Video and audio playback for the in-app viewer, sourced entirely from memory.
///
/// Nothing here is given a file path at any point. See <see cref="ByteArrayMediaDataSource"/>
/// for why that matters and how it is achieved.
/// </summary>
public sealed class MediaPlayback : IMediaPlayback
{
    private MediaPlayer? _player;
    private ByteArrayMediaDataSource? _source;
    private Surface? _surface;
    private bool _prepared;

    public event EventHandler? Completed;

    public bool IsPlaying
    {
        get
        {
            try { return _prepared && _player?.IsPlaying == true; }
            catch { return false; }
        }
    }

    public TimeSpan Position
    {
        get
        {
            try { return _prepared ? TimeSpan.FromMilliseconds(_player?.CurrentPosition ?? 0) : TimeSpan.Zero; }
            catch { return TimeSpan.Zero; }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            try { return _prepared ? TimeSpan.FromMilliseconds(Math.Max(0, _player?.Duration ?? 0)) : TimeSpan.Zero; }
            catch { return TimeSpan.Zero; }
        }
    }

    public (int Width, int Height) VideoSize
    {
        get
        {
            try { return (_player?.VideoWidth ?? 0, _player?.VideoHeight ?? 0); }
            catch { return (0, 0); }
        }
    }

    public async Task<bool> LoadAsync(byte[] data, VideoSurface? surface, bool isVideo)
    {
        Stop();

        try
        {
            _source = new ByteArrayMediaDataSource(data);
            _player = new MediaPlayer();

            _player.SetDataSource(_source);

            if (isVideo && surface is not null)
            {
                // The handler exposes the platform texture view; the player draws into a
                // Surface wrapping its texture.
                if (surface.Handler?.PlatformView is TextureView texture &&
                    texture.SurfaceTexture is { } surfaceTexture)
                {
                    _surface = new Surface(surfaceTexture);
                    _player.SetSurface(_surface);
                }
            }

            if (!isVideo)
            {
                _player.SetAudioAttributes(new AudioAttributes.Builder()!
                    .SetContentType(AudioContentType.Music)!
                    .SetUsage(AudioUsageKind.Media)!
                    .Build()!);
            }

            var ready = new TaskCompletionSource<bool>();

            _player.Prepared += (_, _) =>
            {
                _prepared = true;
                ready.TrySetResult(true);
            };

            _player.Error += (_, e) =>
            {
                ready.TrySetResult(false);
                e.Handled = true;
            };

            _player.Completion += (_, _) => Completed?.Invoke(this, EventArgs.Empty);

            _player.PrepareAsync();

            // A file the device cannot decode would otherwise leave the viewer waiting
            // for a Prepared event that never arrives.
            Task finished = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (finished != ready.Task)
            {
                Stop();
                return false;
            }

            bool ok = await ready.Task;
            if (!ok) Stop();
            return ok;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    public void Play()
    {
        try { if (_prepared) _player?.Start(); } catch { /* a released player is a no-op */ }
    }

    public void Pause()
    {
        try { if (_prepared && _player?.IsPlaying == true) _player.Pause(); } catch { }
    }

    public void SeekTo(TimeSpan position)
    {
        try { if (_prepared) _player?.SeekTo((int)position.TotalMilliseconds); } catch { }
    }

    public void Stop()
    {
        _prepared = false;

        try
        {
            _player?.Stop();
        }
        catch { /* not started, or already stopped */ }

        try
        {
            // Releasing the player closes the data source, which wipes the decrypted
            // bytes. Doing it in this order matters: closing first would pull the array
            // out from under a player still reading it.
            _player?.Release();
            _player?.Dispose();
        }
        catch { }
        finally
        {
            _player = null;
        }

        try { _source?.Close(); } catch { }
        try { _source?.Dispose(); } catch { }
        _source = null;

        try { _surface?.Release(); } catch { }
        try { _surface?.Dispose(); } catch { }
        _surface = null;
    }

    public void Dispose() => Stop();
}
