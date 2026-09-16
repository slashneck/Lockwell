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

using Android.Hardware.Biometrics;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Lockwell.Mobile.Services;

namespace Lockwell.Mobile.Platforms.Android;

/// <summary>
/// Fingerprint and face unlock, using Android's own BiometricPrompt (API 28+) rather
/// than the AndroidX compatibility package.
///
/// The design that matters: an AES key is generated inside the Android Keystore and
/// marked <c>setUserAuthenticationRequired</c>. The OS refuses to let that key do any
/// work until a biometric check passes, and the key material itself never enters app
/// memory -- on devices with a secure element it never leaves the hardware at all.
/// That key wraps the vault's data key.
///
/// So a fingerprint does not "log you in" and the password is not stored anywhere. The
/// password still derives through Argon2id and remains the only route in if biometrics
/// are turned off or invalidated. This distinction is the whole point: the alternative
/// design -- check a fingerprint, then read a saved password from a file -- looks
/// identical to the user and is far weaker.
/// </summary>
public sealed class BiometricUnlock : IBiometricUnlock
{
    private const string Provider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmTagBits = 128;

    /// <summary>
    /// Each vault gets its own Keystore key. Sharing one across vaults would mean a
    /// single fingerprint could open all of them, which defeats the point of keeping
    /// separate vaults separate.
    /// </summary>
    private static string AliasFor(string vaultId) => "lockwell.vault." + vaultId;

    public bool IsEnabledFor(string keyFilePath) => File.Exists(keyFilePath);

    public BiometricAvailability Check()
    {
        try
        {
            var context = global::Android.App.Application.Context;

            // BiometricManager.CanAuthenticate only exists from API 29. On Android 9 the
            // prompt itself works, so fall back to asking whether the hardware is there
            // and let the prompt report anything more specific.
            if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.Q)
            {
                bool hasSensor = context.PackageManager?.HasSystemFeature(
                    global::Android.Content.PM.PackageManager.FeatureFingerprint) ?? false;
                return hasSensor ? BiometricAvailability.Available : BiometricAvailability.NoHardware;
            }

            if (context.GetSystemService(global::Android.Content.Context.BiometricService)
                is not BiometricManager manager)
            {
                return BiometricAvailability.Unavailable;
            }

            return manager.CanAuthenticate() switch
            {
                BiometricCode.Success => BiometricAvailability.Available,
                BiometricCode.ErrorNoHardware => BiometricAvailability.NoHardware,
                BiometricCode.ErrorNoneEnrolled => BiometricAvailability.NotEnrolled,
                _ => BiometricAvailability.Unavailable,
            };
        }
        catch
        {
            return BiometricAvailability.Unavailable;
        }
    }

    public async Task<bool> EnableAsync(string vaultId, string keyFilePath, byte[] dataKey)
    {
        if (Check() != BiometricAvailability.Available) return false;

        CreateKeystoreKey(vaultId);

        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, LoadKeystoreKey(vaultId));

        Cipher? authorised = await PromptAsync(
            cipher,
            title: "Enable quick unlock",
            subtitle: "Confirm so Lockwell can use this phone's secure key.");

        if (authorised is null) return false;

        byte[] wrapped = authorised.DoFinal(dataKey)!;
        byte[] iv = authorised.GetIV()!;

        // [1 byte iv length][iv][wrapped key]. The IV is not secret; the Keystore key is.
        byte[] blob = new byte[1 + iv.Length + wrapped.Length];
        blob[0] = (byte)iv.Length;
        Buffer.BlockCopy(iv, 0, blob, 1, iv.Length);
        Buffer.BlockCopy(wrapped, 0, blob, 1 + iv.Length, wrapped.Length);

        MobileVaultPaths.EnsureCreated();
        File.WriteAllBytes(keyFilePath, blob);
        return true;
    }

    public async Task<byte[]?> UnlockAsync(string vaultId, string keyFilePath)
    {
        if (!IsEnabledFor(keyFilePath)) return null;

        byte[] blob;
        try { blob = File.ReadAllBytes(keyFilePath); }
        catch { return null; }

        if (blob.Length < 2) return null;
        int ivLength = blob[0];
        if (blob.Length < 1 + ivLength) return null;

        byte[] iv = blob[1..(1 + ivLength)];
        byte[] wrapped = blob[(1 + ivLength)..];

        Cipher cipher;
        try
        {
            cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, LoadKeystoreKey(vaultId), new GCMParameterSpec(GcmTagBits, iv));
        }
        catch (KeyPermanentlyInvalidatedException)
        {
            // A fingerprint was added or removed since setup, so Android destroyed the
            // key on purpose. Drop the wrapped copy and fall back to the password.
            Disable(vaultId, keyFilePath);
            return null;
        }
        catch
        {
            return null;
        }

        Cipher? authorised = await PromptAsync(
            cipher,
            title: "Unlock Lockwell",
            subtitle: "Use your fingerprint or face to open your vault.");

        if (authorised is null) return null;

        try { return authorised.DoFinal(wrapped); }
        catch { return null; }
    }

    public void Disable(string vaultId, string keyFilePath)
    {
        try { if (File.Exists(keyFilePath)) File.Delete(keyFilePath); }
        catch { /* best effort */ }

        try
        {
            var store = KeyStore.GetInstance(Provider)!;
            store.Load(null);
            if (store.ContainsAlias(AliasFor(vaultId))) store.DeleteEntry(AliasFor(vaultId));
        }
        catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- keystore

    private static void CreateKeystoreKey(string vaultId)
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.ContainsAlias(AliasFor(vaultId))) store.DeleteEntry(AliasFor(vaultId));

        var spec = new KeyGenParameterSpec.Builder(
                AliasFor(vaultId), KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm!)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone!)!
            .SetKeySize(256)!
            // The OS refuses to use this key without a fresh biometric check.
            .SetUserAuthenticationRequired(true)!
            // If enrolled fingerprints change, kill the key rather than trust a new one.
            .SetInvalidatedByBiometricEnrollment(true)!
            .Build();

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes!, Provider)!;
        generator.Init(spec);
        generator.GenerateKey();
    }

    private static IKey LoadKeystoreKey(string vaultId)
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        return store.GetKey(AliasFor(vaultId), null)!;
    }

    // ------------------------------------------------------------------ prompt

    private static Task<Cipher?> PromptAsync(Cipher cipher, string title, string subtitle)
    {
        var completion = new TaskCompletionSource<Cipher?>();

        var activity = Platform.CurrentActivity;
        if (activity is null)
        {
            completion.SetResult(null);
            return completion.Task;
        }

        var executor = activity.MainExecutor!;

        var prompt = new BiometricPrompt.Builder(activity)
            .SetTitle(title)!
            .SetSubtitle(subtitle)!
            // Every biometric prompt must offer a way out, or a failed sensor traps
            // the user with no route to their vault.
            .SetNegativeButton("Use password", executor,
                new DialogClickListener(() => completion.TrySetResult(null)))!
            .Build();

        var callback = new PromptCallback(completion);

        activity.RunOnUiThread(() => prompt.Authenticate(
            new BiometricPrompt.CryptoObject(cipher),
            new global::Android.OS.CancellationSignal(),
            executor,
            callback));

        return completion.Task;
    }

    private sealed class DialogClickListener : Java.Lang.Object, global::Android.Content.IDialogInterfaceOnClickListener
    {
        private readonly Action _onClick;
        public DialogClickListener(Action onClick) => _onClick = onClick;

        public void OnClick(global::Android.Content.IDialogInterface? dialog, int which) => _onClick();
    }

    private sealed class PromptCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<Cipher?> _completion;

        public PromptCallback(TaskCompletionSource<Cipher?> completion) => _completion = completion;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result)
        {
            _completion.TrySetResult(result?.CryptoObject?.Cipher);
        }

        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString)
        {
            _completion.TrySetResult(null);
        }

        // One bad finger is not the end of the prompt; Android keeps asking.
        public override void OnAuthenticationFailed() { }
    }
}
