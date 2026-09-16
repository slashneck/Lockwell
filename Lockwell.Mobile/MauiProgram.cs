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

using Lockwell.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace Lockwell.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			})
			.ConfigureMauiHandlers(handlers =>
			{
#if ANDROID
				// Video needs a real platform view to draw into; MAUI has no control for
				// one, so VideoSurface is backed by a texture view of our own.
				handlers.AddHandler<VideoSurface, Platforms.Android.VideoSurfaceHandler>();
#endif
			});

		// Biometrics are inherently platform code: the whole point is the Android
		// Keystore holding a key the OS will not release without a fingerprint.
#if ANDROID
		builder.Services.AddSingleton<IBiometricUnlock, Platforms.Android.BiometricUnlock>();
		builder.Services.AddSingleton<IImageDecoder, Platforms.Android.ImageDecoder>();

		// Playback is created per item rather than shared: a player holds the decrypted
		// bytes of whatever it is playing, and that should end when the item is closed.
		builder.Services.AddTransient<IMediaPlayback, Platforms.Android.MediaPlayback>();
#endif

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
