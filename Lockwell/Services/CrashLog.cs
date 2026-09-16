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
using System.Text;

namespace Lockwell.Services;

/// <summary>Writes startup/runtime failures for tester support.</summary>
internal static class CrashLog
{
    public static string TempFilePath =>
        Path.Combine(Path.GetTempPath(), "Lockwell-last-crash.txt");

    public static string AppDataFilePath =>
        Path.Combine(ProfileManager.AppRoot, "last-crash.txt");

    public static string? Write(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(DateTimeOffset.Now.ToString("O"));
        sb.AppendLine(ex.ToString());

        foreach (var path in new[] { TempFilePath, AppDataFilePath })
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString());
                return path;
            }
            catch
            {
                // try next location
            }
        }

        return null;
    }
}
