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

namespace Lockwell.Sync;

/// <summary>One device this vault has paired with.</summary>
public sealed class TrustedDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The name the user gave it. Editable, purely cosmetic.</summary>
    public string Name { get; set; } = "";

    /// <summary>Base64 of the peer's long-term public key. This is the real identity.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Phone, laptop, desktop. Only used to pick an icon.</summary>
    public string Kind { get; set; } = "phone";

    public DateTime PairedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>How many items have been sent to this device.</summary>
    public int ItemsSent { get; set; }

    /// <summary>
    /// How long this particular device may go quiet before its pairing is dropped, or
    /// null to follow the vault's own setting.
    ///
    /// Per device because devices are not equivalent. A phone carried everywhere earns a
    /// short leash; a desktop in the next room that is only switched on at weekends would
    /// be unpaired constantly by the same number. Chosen when the device is linked, while
    /// the user is thinking about what the device actually is, rather than buried in a
    /// setting they will never revisit.
    /// </summary>
    public int? ExpiryDaysOverride { get; set; }

    /// <summary>Never drop this pairing, whatever the vault's default says.</summary>
    public bool NeverExpires { get; set; }

    /// <summary>
    /// The short code both screens show at pairing time, so a human can confirm the
    /// two devices agreed on the same keys rather than trusting the network.
    /// </summary>
    public string Fingerprint => DeviceIdentity.FingerprintOf(Convert.FromBase64String(PublicKey));

    public int DaysSinceSeen => (int)(DateTime.UtcNow - LastSeenUtc).TotalDays;

    /// <summary>
    /// The window that actually applies to this device: its own if it has one, otherwise
    /// the vault's.
    /// </summary>
    public int EffectiveExpiryDays(int vaultDefault) =>
        Math.Max(1, ExpiryDaysOverride ?? vaultDefault);

    /// <summary>
    /// Days left before this pairing is dropped, or null when it never will be. Negative
    /// values mean it is already overdue and will go on the next look.
    /// </summary>
    public int? DaysUntilExpiry(int vaultDefault, bool vaultExpiryEnabled)
    {
        if (NeverExpires) return null;
        if (!vaultExpiryEnabled && ExpiryDaysOverride is null) return null;

        return EffectiveExpiryDays(vaultDefault) - DaysSinceSeen;
    }
}

/// <summary>
/// The list of devices this vault trusts, plus this device's own identity key.
///
/// Stored beside the vault rather than inside it, because the identity key has to be
/// readable to answer a connection before anyone has unlocked anything. It holds no
/// vault contents: a private key that proves "this is the same PC you paired with"
/// and a list of public keys. Losing it means re-pairing, not losing data.
/// </summary>
public sealed class TrustStore
{
    private readonly string _path;
    private TrustStoreFile _file = new();

    public TrustStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "devices.json");
        Load();
    }

    public IReadOnlyList<TrustedDevice> Devices => _file.Devices;

    /// <summary>This device's long-term key pair, created once and reused.</summary>
    public DeviceIdentity Identity { get; private set; } = null!;

    /// <summary>What this device calls itself when pairing.</summary>
    public string SelfName
    {
        get => string.IsNullOrWhiteSpace(_file.SelfName) ? Environment.MachineName : _file.SelfName;
        set { _file.SelfName = value; Save(); }
    }

    /// <summary>Automatically drop pairings that have gone quiet for this long.</summary>
    public int ExpiryDays
    {
        get => _file.ExpiryDays;
        set { _file.ExpiryDays = value; Save(); }
    }

    public bool ExpiryEnabled
    {
        get => _file.ExpiryEnabled;
        set { _file.ExpiryEnabled = value; Save(); }
    }

    public bool IsTrusted(byte[] publicKey)
    {
        string encoded = Convert.ToBase64String(publicKey);
        return _file.Devices.Any(d => d.PublicKey == encoded);
    }

    public TrustedDevice? Find(byte[] publicKey)
    {
        string encoded = Convert.ToBase64String(publicKey);
        return _file.Devices.FirstOrDefault(d => d.PublicKey == encoded);
    }

    public TrustedDevice Add(byte[] publicKey, string name, string kind,
        int? expiryDays = null, bool neverExpires = false)
    {
        var existing = Find(publicKey);
        if (existing is not null)
        {
            // Re-pairing a known device updates it rather than creating a duplicate.
            existing.Name = name;
            existing.LastSeenUtc = DateTime.UtcNow;
            existing.ExpiryDaysOverride = expiryDays;
            existing.NeverExpires = neverExpires;
            Save();
            return existing;
        }

        var device = new TrustedDevice
        {
            Name = name,
            Kind = kind,
            PublicKey = Convert.ToBase64String(publicKey),
            ExpiryDaysOverride = expiryDays,
            NeverExpires = neverExpires,
        };
        _file.Devices.Add(device);
        Save();
        return device;
    }

    /// <summary>Change how long this one device may go quiet.</summary>
    public void SetExpiry(string id, int? days, bool never)
    {
        var device = _file.Devices.FirstOrDefault(d => d.Id == id);
        if (device is null) return;

        device.ExpiryDaysOverride = days;
        device.NeverExpires = never;
        Save();
    }

    public void Rename(string id, string name)
    {
        var device = _file.Devices.FirstOrDefault(d => d.Id == id);
        if (device is null) return;
        device.Name = name;
        Save();
    }

    public void Remove(string id)
    {
        _file.Devices.RemoveAll(d => d.Id == id);
        Save();
    }

    public void Touch(byte[] publicKey)
    {
        var device = Find(publicKey);
        if (device is null) return;
        device.LastSeenUtc = DateTime.UtcNow;
        Save();
    }

    /// <summary>
    /// Drop pairings that have gone quiet past the expiry window. A device that has not
    /// connected in a month is more likely gone than idle, and a stale trust entry is a
    /// standing invitation.
    /// </summary>
    public int PruneExpired()
    {
        // Each device is judged against its own window. A device with one set is honoured
        // even when the vault-wide setting is off, because choosing a window for one
        // device is a clearer statement of intent than a global default it overrides.
        int removed = _file.Devices.RemoveAll(d =>
        {
            int? left = d.DaysUntilExpiry(_file.ExpiryDays, _file.ExpiryEnabled);
            return left is <= 0;
        });

        if (removed > 0) Save();
        return removed;
    }

    // --------------------------------------------------------------- storage

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                byte[] bytes = File.ReadAllBytes(_path);
                _file = JsonSerializer.Deserialize(bytes, TrustStoreJson.Default.TrustStoreFile) ?? new TrustStoreFile();
            }
        }
        catch
        {
            _file = new TrustStoreFile();
        }

        if (string.IsNullOrEmpty(_file.PrivateKey))
        {
            CreateFreshIdentity();
        }
        else
        {
            try
            {
                Identity = DeviceIdentity.Import(Convert.FromBase64String(_file.PrivateKey));
            }
            catch
            {
                // A damaged key means re-pairing, which is recoverable. Silently
                // limping along with a broken identity is not. Existing trust entries
                // are dropped because they were paired against a key we no longer hold.
                CreateFreshIdentity();
                _file.Devices.Clear();
                Save();
            }
        }
    }

    private void CreateFreshIdentity()
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
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_file, TrustStoreJson.Default.TrustStoreFile);
            string temp = _path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _path, overwrite: true);
        }
        catch { /* the trust list is rebuildable by re-pairing */ }
    }
}

public sealed class TrustStoreFile
{
    public int Version { get; set; } = 1;
    public string PrivateKey { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string SelfName { get; set; } = "";
    public bool ExpiryEnabled { get; set; } = true;
    public int ExpiryDays { get; set; } = 30;
    public List<TrustedDevice> Devices { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TrustStoreFile))]
public partial class TrustStoreJson : JsonSerializerContext
{
}
