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

using System.Text.RegularExpressions;

namespace Lockwell.Tests;

/// <summary>
/// Every colour, style and font the desktop UI asks for by name actually exists.
///
/// This is here because of a real crash. A device row asked for a brush called "Faint",
/// which existed in the phone app's palette and never in the desktop one. FindResource
/// throws when a key is missing rather than falling back, so the Devices screen would have
/// died the first time anyone successfully linked a device. It never fired during
/// development for the worst possible reason: linking was broken, so the code path had
/// never once run.
///
/// That is the shape of bug this catches. A missing resource is invisible until the exact
/// screen that needs it is rendered, which may be a screen that only appears after
/// something else succeeds. Reading the source is the only way to find them all without
/// visiting every state of the app by hand.
/// </summary>
internal static class ResourceChecks
{
    public static void Run()
    {
        Check.Section("52. Every named resource the desktop UI uses exists");
        Scan("Lockwell", "desktop");

        // The setup program is a separate WPF app with its own resource dictionary, and
        // it fails the same way: a mistyped key is a crash on the step that uses it, not
        // a build error. Setup is also the one window every user is guaranteed to see.
        Check.Section("63. Every named resource the installer UI uses exists");
        Scan("Lockwell.Installer", "installer");
    }

    private static void Scan(string projectFolder, string label)
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            Check.Note("source tree not found next to the test binary; skipped");
            return;
        }

        string appDir = Path.Combine(root, projectFolder);
        if (!Directory.Exists(appDir))
        {
            Check.Note($"{label} project not found; skipped");
            return;
        }

        HashSet<string> defined = DefinedKeys(appDir);
        Check.That($"the {label} theme defines resources at all ({defined.Count} found)", defined.Count > 20);

        var missing = new SortedDictionary<string, string>(StringComparer.Ordinal);

        // FindResource("X") and TryFindResource("X") in code-behind.
        foreach (string file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"(?<!Try)FindResource\(\s*""([^""]+)""\s*\)"))
            {
                string key = m.Groups[1].Value;
                if (!defined.Contains(key)) missing.TryAdd(key, Path.GetFileName(file));
            }
        }

        // StaticResource references in XAML.
        foreach (string file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\{StaticResource\s+([A-Za-z0-9_.]+)\s*\}"))
            {
                string key = m.Groups[1].Value;
                if (!defined.Contains(key)) missing.TryAdd(key, Path.GetFileName(file));
            }
        }

        if (missing.Count == 0)
        {
            Check.That($"no {label} screen asks for a resource that does not exist", true);
            return;
        }

        foreach ((string key, string where) in missing)
            Check.That($"resource \"{key}\" is defined (used in {where})", false);
    }

    /// <summary>Keys declared anywhere in the desktop project's XAML.</summary>
    private static HashSet<string> DefinedKeys(string appDir)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""]+)"""))
                keys.Add(m.Groups[1].Value);
        }

        // Brushes WPF itself provides, which the app is entitled to use without declaring.
        foreach (string builtIn in new[]
                 {
                     "WindowBrush", "ControlBrush", "ControlTextBrush", "HighlightBrush",
                     "HighlightTextBrush", "GrayTextBrush",
                 })
        {
            keys.Add(builtIn);
        }

        return keys;
    }

    /// <summary>Walk up from the test binary until the solution file appears.</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (int depth = 0; depth < 8 && dir is not null; depth++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Lockwell.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
