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
using Lockwell.Models;

namespace Lockwell.Services;

/// <summary>Manages multiple vault profiles stored as separate folders on disk.</summary>
public sealed class ProfileManager
{
    private readonly AppSettings _settings;

    public static string AppRoot => AppDataPaths.AppRoot;

    public static string ProfilesRoot => AppDataPaths.ProfilesRoot;

    public ProfileManager(AppSettings settings) => _settings = settings;

    public IReadOnlyList<VaultProfile> Profiles => _settings.Profiles;

    public VaultProfile? ActiveProfile =>
        _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId)
        ?? _settings.Profiles.FirstOrDefault();

    public void EnsureInitialized()
    {
        if (_settings.Profiles.Count > 0)
        {
            if (_settings.ActiveProfileId is null ||
                _settings.Profiles.All(p => p.Id != _settings.ActiveProfileId))
                _settings.ActiveProfileId = _settings.Profiles[0].Id;
            return;
        }

        _settings.Profiles.Add(new VaultProfile
        {
            Name = "Default",
            VaultDir = AppRoot,
        });
        _settings.ActiveProfileId = _settings.Profiles[0].Id;
        _settings.Save();
    }

    public VaultManager CreateActiveVaultManager()
    {
        var profile = ActiveProfile
                      ?? throw new InvalidOperationException("No active profile.");
        return new VaultManager(profile.VaultDir);
    }

    public void SetActive(VaultProfile profile)
    {
        if (_settings.Profiles.All(p => p.Id != profile.Id))
            throw new InvalidOperationException("Profile is not registered.");
        _settings.ActiveProfileId = profile.Id;
        _settings.Save();
    }

    public VaultProfile AddProfile(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Profile name is required.", nameof(name));

        string dir = Path.Combine(ProfilesRoot, Guid.NewGuid().ToString("N"));
        var profile = new VaultProfile { Name = trimmed, VaultDir = dir };
        _settings.Profiles.Add(profile);
        _settings.ActiveProfileId = profile.Id;
        _settings.Save();
        return profile;
    }

    public bool DeleteProfile(VaultProfile profile, bool deleteFiles)
    {
        if (_settings.Profiles.Count <= 1)
            return false;

        _settings.Profiles.RemoveAll(p => p.Id == profile.Id);
        if (_settings.ActiveProfileId == profile.Id)
            _settings.ActiveProfileId = _settings.Profiles[0].Id;
        _settings.Save();

        if (deleteFiles && Directory.Exists(profile.VaultDir))
        {
            try { Directory.Delete(profile.VaultDir, recursive: true); }
            catch { /* best effort */ }
        }

        return true;
    }

    public void RenameProfile(VaultProfile profile, string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Profile name is required.", nameof(name));
        if (_settings.Profiles.All(p => p.Id != profile.Id))
            throw new InvalidOperationException("Profile is not registered.");

        profile.Name = trimmed;
        _settings.Save();
    }

    public static bool ProfileHasVault(VaultProfile profile) =>
        File.Exists(Path.Combine(profile.VaultDir, "vault.lwv"));

    /// <summary>
    /// Reuse an empty profile when importing a backup, or create a new one.
    /// </summary>
    public VaultProfile PrepareProfileForBackupImport(string name, VaultProfile? reuseIfEmpty = null)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Profile name is required.", nameof(name));

        if (reuseIfEmpty is not null && !ProfileHasVault(reuseIfEmpty))
        {
            RenameProfile(reuseIfEmpty, trimmed);
            return reuseIfEmpty;
        }

        if (_settings.Profiles.Count == 1 && !ProfileHasVault(_settings.Profiles[0]))
        {
            RenameProfile(_settings.Profiles[0], trimmed);
            return _settings.Profiles[0];
        }

        return AddProfile(trimmed);
    }
}
