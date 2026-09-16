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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lockwell.Mobile.Services;

/// <summary>One vault on this phone. The name is cosmetic; the folder is the identity.</summary>
public sealed class VaultProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My vault";

    /// <summary>Icon glyph shown in the vault list, chosen by the user.</summary>
    public string Glyph { get; set; } = "\U0001F512";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Where this vault's files live, relative to the app's private storage.</summary>
    public string Folder { get; set; } = "";
}

/// <summary>
/// The list of vaults on this phone.
///
/// Mirrors the desktop's profile system: several separate vaults, each with its own
/// password and its own key. Separate vaults are genuinely separate -- unlocking one
/// tells you nothing about another, so "work" and "personal" can live on one phone
/// without one compromise exposing both.
///
/// This index is not secret. It holds names and folder paths, never keys or contents,
/// and it sits in the app's private storage which uninstalling removes entirely.
/// </summary>
public sealed class VaultLibrary
{
    private readonly string _path;
    private LibraryFile _file = new();

    public VaultLibrary()
    {
        MobileVaultPaths.EnsureCreated();
        _path = Path.Combine(MobileVaultPaths.Root, "vaults.json");
        Load();
        AdoptLegacyVault();
    }

    public IReadOnlyList<VaultProfile> Vaults => _file.Vaults;

    public bool IsEmpty => _file.Vaults.Count == 0;

    public VaultProfile? Active =>
        _file.Vaults.FirstOrDefault(v => v.Id == _file.ActiveId) ?? _file.Vaults.FirstOrDefault();

    public void SetActive(VaultProfile profile)
    {
        _file.ActiveId = profile.Id;
        profile.LastOpenedUtc = DateTime.UtcNow;
        Save();
    }

    public VaultProfile Create(string name, string glyph)
    {
        var profile = new VaultProfile
        {
            Name = name,
            Glyph = glyph,
            Folder = "vault-" + Guid.NewGuid().ToString("N")[..12],
        };

        Directory.CreateDirectory(DirectoryFor(profile));
        Directory.CreateDirectory(Path.Combine(DirectoryFor(profile), "attachments"));

        _file.Vaults.Add(profile);
        _file.ActiveId = profile.Id;
        Save();
        return profile;
    }

    public void Rename(string id, string name)
    {
        var profile = _file.Vaults.FirstOrDefault(v => v.Id == id);
        if (profile is null) return;
        profile.Name = name;
        Save();
    }

    /// <summary>
    /// Delete a vault and everything in it. Irreversible: without the password the
    /// contents were unreadable anyway, so there is nothing to salvage afterwards.
    /// </summary>
    public void Delete(string id)
    {
        var profile = _file.Vaults.FirstOrDefault(v => v.Id == id);
        if (profile is null) return;

        try
        {
            string dir = DirectoryFor(profile);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* the index entry still goes, so it stops being reachable */ }

        _file.Vaults.Remove(profile);
        if (_file.ActiveId == id) _file.ActiveId = _file.Vaults.FirstOrDefault()?.Id ?? "";
        Save();
    }

    public string DirectoryFor(VaultProfile profile) =>
        Path.Combine(MobileVaultPaths.Root, profile.Folder);

    public long SizeOf(VaultProfile profile)
    {
        string dir = DirectoryFor(profile);
        if (!Directory.Exists(dir)) return 0;

        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* skip */ }
            }
        }
        catch { /* best effort */ }
        return total;
    }

    /// <summary>
    /// Earlier builds kept a single vault directly in the app root. Move it into the
    /// library rather than orphaning it, so an existing vault survives the upgrade.
    /// </summary>
    private void AdoptLegacyVault()
    {
        string legacy = Path.Combine(MobileVaultPaths.Root, "vault.lwv");
        if (!File.Exists(legacy) || _file.Vaults.Count > 0) return;

        var profile = new VaultProfile
        {
            Name = "My vault",
            Folder = "vault-" + Guid.NewGuid().ToString("N")[..12],
        };

        string dir = DirectoryFor(profile);
        Directory.CreateDirectory(dir);

        try
        {
            File.Move(legacy, Path.Combine(dir, "vault.lwv"));

            string legacyAttachments = Path.Combine(MobileVaultPaths.Root, "attachments");
            if (Directory.Exists(legacyAttachments))
                Directory.Move(legacyAttachments, Path.Combine(dir, "attachments"));

            string legacyBiometric = Path.Combine(MobileVaultPaths.Root, "biometric.key");
            if (File.Exists(legacyBiometric))
                File.Move(legacyBiometric, Path.Combine(dir, "biometric.key"));
        }
        catch
        {
            return; // leave the old file alone rather than half-move it
        }

        _file.Vaults.Add(profile);
        _file.ActiveId = profile.Id;
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                byte[] bytes = File.ReadAllBytes(_path);
                _file = JsonSerializer.Deserialize(bytes, LibraryJson.Default.LibraryFile) ?? new LibraryFile();
            }
        }
        catch
        {
            _file = new LibraryFile();
        }
    }

    private void Save()
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_file, LibraryJson.Default.LibraryFile);
            string temp = _path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _path, overwrite: true);
        }
        catch { /* best effort */ }
    }
}

public sealed class LibraryFile
{
    public int Version { get; set; } = 1;
    public string ActiveId { get; set; } = "";
    public List<VaultProfile> Vaults { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LibraryFile))]
public partial class LibraryJson : JsonSerializerContext
{
}
