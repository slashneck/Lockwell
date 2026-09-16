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
using Lockwell.Sync;

namespace Lockwell.Mobile.Services;

/// <summary>A PC this vault has linked to.</summary>
public sealed class LinkedPc
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Base64 of the PC's long-term public key. This is the real identity.</summary>
    public string PublicKey { get; set; } = "";

    public DateTime LinkedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Last address it answered on, so a reconnect can try there first.</summary>
    public string LastAddress { get; set; } = "";

    public string Fingerprint => DeviceIdentity.FingerprintOf(Convert.FromBase64String(PublicKey));

    public int DaysSinceSeen => (int)(DateTime.UtcNow - LastSeenUtc).TotalDays;
}

/// <summary>
/// This phone's identity and the PCs it has linked to, stored per vault.
///
/// Per vault rather than per app on purpose: linking a work PC to a work vault should
/// not give it any relationship to a personal one. Each vault gets its own device
/// identity, so the two cannot even be correlated by public key.
///
/// The file holds a private identity key and a list of public keys. No vault contents,
/// and nothing that helps open the vault. Losing it means re-linking, not losing data.
/// </summary>
public sealed class PhoneTrustStore
{
    private readonly string _path;
    private TrustFile _file = new();

    public PhoneTrustStore(string vaultDirectory)
    {
        string dir = Path.Combine(vaultDirectory, "sync");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "devices.json");
        Load();
    }

    public IReadOnlyList<LinkedPc> Devices => _file.Devices;

    public DeviceIdentity Identity { get; private set; } = null!;

    /// <summary>What this phone calls itself when linking.</summary>
    public string SelfName
    {
        get => string.IsNullOrWhiteSpace(_file.SelfName) ? DeviceInfo.Current.Name : _file.SelfName;
        set { _file.SelfName = value; Save(); }
    }

    public LinkedPc Add(byte[] publicKey, string name, string address)
    {
        string encoded = Convert.ToBase64String(publicKey);
        var existing = _file.Devices.FirstOrDefault(d => d.PublicKey == encoded);

        if (existing is not null)
        {
            // Re-linking a known PC updates it rather than creating a duplicate.
            existing.Name = name;
            existing.LastSeenUtc = DateTime.UtcNow;
            existing.LastAddress = address;
            Save();
            return existing;
        }

        var device = new LinkedPc
        {
            Name = name,
            PublicKey = encoded,
            LastAddress = address,
        };
        _file.Devices.Add(device);
        Save();
        return device;
    }

    public void Remove(string id)
    {
        _file.Devices.RemoveAll(d => d.Id == id);
        Save();
    }

    public void Touch(LinkedPc device, string address)
    {
        device.LastSeenUtc = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(address)) device.LastAddress = address;
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                byte[] bytes = File.ReadAllBytes(_path);
                _file = JsonSerializer.Deserialize(bytes, PhoneTrustJson.Default.TrustFile) ?? new TrustFile();
            }
        }
        catch
        {
            _file = new TrustFile();
        }

        if (string.IsNullOrEmpty(_file.PrivateKey))
        {
            CreateIdentity();
        }
        else
        {
            try
            {
                Identity = DeviceIdentity.Import(Convert.FromBase64String(_file.PrivateKey));
            }
            catch
            {
                // A damaged key means re-linking, which is recoverable. Existing links
                // are dropped because they were made against a key we no longer hold.
                CreateIdentity();
                _file.Devices.Clear();
                Save();
            }
        }
    }

    private void CreateIdentity()
    {
        Identity = DeviceIdentity.Create();
        _file.PrivateKey = Convert.ToBase64String(Identity.ExportPrivateKey());
        _file.PublicKey = Convert.ToBase64String(Identity.PublicKey);
        Save();
    }

    private void Save()
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_file, PhoneTrustJson.Default.TrustFile);
            string temp = _path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _path, overwrite: true);
        }
        catch { /* the link list is rebuildable by re-linking */ }
    }
}

public sealed class TrustFile
{
    public int Version { get; set; } = 1;
    public string PrivateKey { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string SelfName { get; set; } = "";
    public List<LinkedPc> Devices { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TrustFile))]
public partial class PhoneTrustJson : JsonSerializerContext
{
}
