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

namespace Lockwell.Mobile.Services;

public enum BiometricAvailability
{
    Available,
    NoHardware,
    NotEnrolled,
    Unavailable,
}

/// <summary>
/// Fingerprint or face unlock for a vault.
///
/// The security model, which must not be implemented any other way: biometrics do not
/// replace the password and do not store it. A key is generated inside the Android
/// Keystore -- hardware-backed where the device supports it -- and marked as requiring
/// user authentication. That key wraps the vault's data key. A successful fingerprint
/// check is what authorises the Keystore to use the key; the key material itself never
/// enters app memory.
///
/// The password still exists, still derives through Argon2id, and remains the only way
/// in if biometrics are turned off or invalidated. Turning them off just deletes the
/// wrapped copy.
///
/// Every method takes the vault's own key-file path, because each vault on the phone
/// gets its own Keystore key. Unlocking one with a fingerprint must never unlock another.
/// </summary>
public interface IBiometricUnlock
{
    BiometricAvailability Check();

    /// <summary>True if this specific vault has biometric unlock set up.</summary>
    bool IsEnabledFor(string keyFilePath);

    /// <summary>
    /// Wrap this vault's data key with a Keystore key that requires biometric auth.
    /// Prompts the user to confirm.
    /// </summary>
    Task<bool> EnableAsync(string vaultId, string keyFilePath, byte[] dataKey);

    /// <summary>
    /// Prompt for biometrics and return the unwrapped data key, or null if cancelled
    /// or authentication failed.
    /// </summary>
    Task<byte[]?> UnlockAsync(string vaultId, string keyFilePath);

    /// <summary>Forget this vault's wrapped key. The password path is unaffected.</summary>
    void Disable(string vaultId, string keyFilePath);
}
