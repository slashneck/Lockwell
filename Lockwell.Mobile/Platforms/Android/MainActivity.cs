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

using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace Lockwell.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    // Resize the window when the keyboard appears rather than sliding it up.
    // Panning leaves an overlay dialog where it was, so its buttons end up behind
    // the keyboard; resizing lets a centred dialog recentre in what is left.
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>
    /// Mark the window secure, which does two things worth having.
    ///
    /// It stops screenshots and screen recording of the app, and it stops Android putting
    /// a picture of the current screen in the task switcher. The second is the one that
    /// matters most and is the least obvious: without it, the phone keeps a thumbnail of
    /// whatever the vault was showing, and that thumbnail is visible to anyone who opens
    /// the recent-apps list without unlocking anything.
    ///
    /// This is Android's own protection and it is not absolute. The desktop app uses
    /// SetWindowDisplayAffinity for the same purpose and its threat model says the same
    /// thing: it stops ordinary capture, not a rooted device or a camera pointed at the
    /// screen.
    /// </summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

#if DEBUG
        // Debug builds leave the window capturable so the app can be driven and checked
        // with adb screenshots during development. Every Release build sets the flag, and
        // that is what ships. Verified on a device: with the flag set, `adb screencap`
        // returns an empty image and the recent-apps preview is blank.
        if (!System.Environment.GetCommandLineArgs().Contains("--secure-window")) return;
#endif

        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
    }
}
