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

namespace Lockwell.Services;

/// <summary>
/// User data root: %LocalAppData%\Lockwell
/// Settings, vault files, and profiles live here. Install and update replace
/// program files only; they never read or write this folder.
/// </summary>
internal static class AppDataPaths
{
    /// <summary>
    /// Redirects the whole data root elsewhere. Set only by the screenshot harness so
    /// it works against a throwaway vault; a normal launch never sets it, so the real
    /// vault at %LocalAppData%\Lockwell is untouchable from automation.
    /// </summary>
    private const string OverrideVariable = "LOCKWELL_USER_DATA";

    public static string AppRoot
    {
        get
        {
            string? redirect = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(redirect))
                return redirect;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Lockwell");
        }
    }

    public static string SettingsFilePath => Path.Combine(AppRoot, "settings.json");

    public static string ProfilesRoot => Path.Combine(AppRoot, "profiles");
}
