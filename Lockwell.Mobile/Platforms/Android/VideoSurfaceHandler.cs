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

using Android.Views;
using Lockwell.Mobile.Services;
using Microsoft.Maui.Handlers;

namespace Lockwell.Mobile.Platforms.Android;

/// <summary>
/// Gives <see cref="VideoSurface"/> a real Android view to draw into.
///
/// A TextureView rather than a SurfaceView, because a SurfaceView punches a hole through
/// the window and ignores the normal view hierarchy: it would sit on top of the viewer's
/// controls and refuse to fade with the rest of the overlay. A TextureView behaves like
/// any other view, which is what an in-app viewer needs.
/// </summary>
public sealed class VideoSurfaceHandler : ViewHandler<VideoSurface, TextureView>
{
    public static readonly IPropertyMapper<VideoSurface, VideoSurfaceHandler> Mapper =
        new PropertyMapper<VideoSurface, VideoSurfaceHandler>(ViewMapper);

    public VideoSurfaceHandler() : base(Mapper)
    {
    }

    protected override TextureView CreatePlatformView()
    {
        var view = new TextureView(Context);

        // Nothing has been rendered yet; an opaque view would flash a black rectangle over
        // the page before the first frame arrives.
        view.SetOpaque(false);

        view.SurfaceTextureListener = new Listener(VirtualView);
        return view;
    }

    /// <summary>
    /// Tells the MAUI view when its texture becomes usable, so playback can start at the
    /// right moment rather than guessing.
    /// </summary>
    private sealed class Listener : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        private readonly VideoSurface? _owner;

        public Listener(VideoSurface? owner) => _owner = owner;

        public void OnSurfaceTextureAvailable(global::Android.Graphics.SurfaceTexture surface, int width, int height)
            => _owner?.NotifyReady();

        public bool OnSurfaceTextureDestroyed(global::Android.Graphics.SurfaceTexture surface) => true;

        public void OnSurfaceTextureSizeChanged(global::Android.Graphics.SurfaceTexture surface, int width, int height)
        {
        }

        public void OnSurfaceTextureUpdated(global::Android.Graphics.SurfaceTexture surface)
        {
        }
    }
}
