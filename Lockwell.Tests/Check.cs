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

using System.Security;

namespace Lockwell.Tests;

/// <summary>Tiny assertion helper. No framework, no dependency, readable output.</summary>
internal static class Check
{
    public static int Failures { get; private set; }

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    public static void Note(string text) => Console.WriteLine($"   {text}");

    public static void That(string label, bool passed)
    {
        if (passed)
        {
            Console.WriteLine($"  PASS  {label}");
            return;
        }
        Failures++;
        Console.WriteLine($"  FAIL  {label}");
    }

    /// <summary>A SecureString from a literal, for tests only.</summary>
    public static SecureString Secret(string value)
    {
        var s = new SecureString();
        foreach (char c in value) s.AppendChar(c);
        s.MakeReadOnly();
        return s;
    }

    /// <summary>A fresh throwaway directory that deletes itself.</summary>
    public static string Scratch(string tag)
    {
        string dir = Path.Combine(
            Path.GetTempPath(), $"lockwell-test-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }
}
