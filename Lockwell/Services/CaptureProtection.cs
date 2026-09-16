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

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lockwell.Services;

/// <summary>
/// Asks Windows to exclude the Lockwell window from screen capture. When enabled,
/// the window shows up as a black rectangle (or nothing) in OBS, Discord screen
/// share, Snipping Tool, and most recorders.
///
/// Honest limit: this is a cooperative OS feature. It stops normal capture APIs,
/// not a phone camera pointed at the screen or kernel-level capture. It is a
/// strong default, not an absolute guarantee.
/// </summary>
public static class CaptureProtection
{
    private const uint WDA_NONE = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11; // Windows 10 2004+

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    public static bool Apply(Window window, bool exclude)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return false;
        return SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }
}
