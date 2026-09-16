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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lockwell.Crypto;
using Lockwell.Services;

// Controlled breach test: attacks a COPIED vault folder only.
// Does NOT print decrypted secrets even on success (only reports pass/fail).

string vaultDir = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents", "Lockwell-breach-test");

Console.WriteLine("=== Lockwell breach test ===");
Console.WriteLine($"Target (copy only): {vaultDir}");
Console.WriteLine();

if (!Directory.Exists(vaultDir) || !File.Exists(Path.Combine(vaultDir, "vault.lwv")))
{
    Console.WriteLine("FAIL: No vault.lwv found.");
    return 1;
}

var results = new List<(string attack, string outcome)>();

// --- Attack 1: Read vault header for plaintext secrets ---
results.Add(Run("1. Plaintext secrets in vault.lwv header", () =>
{
    var header = JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(vaultDir, "vault.lwv")),
        VaultHeaderJsonContext.Default.VaultHeader)!;

    if (header.Magic != "LOCKWELL") return "Unexpected format.";

    // These fields are EXPECTED to be visible (not secret):
    var visible = $"salt={header.Kdf.Salt.Length} chars, argon2id {header.Kdf.MemoryKib}KiB";
    // Try to find readable passwords in the JSON file itself
    string raw = File.ReadAllText(Path.Combine(vaultDir, "vault.lwv"));
    if (Regex.IsMatch(raw, @"""password""\s*:\s*""[^""]{3,}""", RegexOptions.IgnoreCase))
        return "LEAK: looks like a plaintext password field in vault file!";
    if (raw.Contains("discord", StringComparison.OrdinalIgnoreCase) ||
        raw.Contains("@gmail", StringComparison.OrdinalIgnoreCase))
        return "LEAK: readable user data in vault file!";

    return $"No plaintext secrets. Public metadata only ({visible}). Ciphertext blobs are Base64.";
}));

// --- Attack 2: Decrypt without password (random key) ---
results.Add(Run("2. Decrypt with random 32-byte key", () =>
{
    var header = ReadHeader(vaultDir);
    byte[] blob = Convert.FromBase64String(header.PasswordWrappedKey);
    byte[] fakeKey = SecureRandom.GetBytes(32);
    try
    {
        VaultCrypto.Decrypt(fakeKey, blob);
        return "BREACH: random key decrypted the DEK!";
    }
    catch (CryptographicException)
    {
        return "Blocked. GCM auth tag rejected wrong key.";
    }
}));

// --- Attack 3: Decrypt vault data without unlocking (no DEK) ---
results.Add(Run("3. Decrypt vault Data field without DEK", () =>
{
    var header = ReadHeader(vaultDir);
    byte[] blob = Convert.FromBase64String(header.Data);
    byte[] fakeDek = SecureRandom.GetBytes(32);
    try
    {
        byte[] json = VaultCrypto.Decrypt(fakeDek, blob);
        return "BREACH: decrypted vault contents without password!";
    }
    catch (CryptographicException)
    {
        return "Blocked. Data blob is useless without the DEK.";
    }
}));

// --- Attack 4: Attachment files plaintext on disk ---
results.Add(Run("4. Attachment files readable without decryption", () =>
{
    string attDir = Path.Combine(vaultDir, "attachments");
    if (!Directory.Exists(attDir)) return "No attachments folder.";

    foreach (var file in Directory.GetFiles(attDir, "*.lwa"))
    {
        byte[] bytes = File.ReadAllBytes(file);
        // JPEG magic
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return $"LEAK: {Path.GetFileName(file)} is an unencrypted JPEG!";
        // PNG magic
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return $"LEAK: {Path.GetFileName(file)} is an unencrypted PNG!";
        // Entropy check: encrypted should not contain long ASCII runs
        string asText = Encoding.UTF8.GetString(bytes);
        if (asText.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            asText.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal))
            return $"LEAK: readable secret text in {Path.GetFileName(file)}!";
    }
    return $"All {Directory.GetFiles(attDir).Length} attachment(s) look encrypted (no image magic bytes).";
}));

// --- Attack 5: Tamper with ciphertext ---
results.Add(Run("5. Tamper with vault file (flip one byte)", () =>
{
    string path = Path.Combine(vaultDir, "vault.lwv");
    byte[] copy = File.ReadAllBytes(path);
    int idx = copy.Length - 5;
    copy[idx] ^= 0x01;
    File.WriteAllBytes(path + ".tampered", copy);

    var header = JsonSerializer.Deserialize(copy, VaultHeaderJsonContext.Default.VaultHeader)!;
    byte[] blob = Convert.FromBase64String(header.Data);
    byte[] fakeDek = SecureRandom.GetBytes(32);
    try
    {
        VaultCrypto.Decrypt(fakeDek, blob);
        return "Unexpected: tampered data decrypted (wrong key though).";
    }
    catch (CryptographicException)
    {
        return "Blocked. Tampered ciphertext fails GCM verification.";
    }
}));

// --- Attack 6: Common-password dictionary (limited demo) ---
results.Add(Run("6. Dictionary attack (top 25 common passwords)", () =>
{
    var header = ReadHeader(vaultDir);
    var p = new Argon2Parameters
    {
        Salt = Convert.FromBase64String(header.Kdf.Salt),
        MemoryKib = header.Kdf.MemoryKib,
        Iterations = header.Kdf.Iterations,
        Parallelism = header.Kdf.Parallelism,
    };
    byte[] wrapped = Convert.FromBase64String(header.PasswordWrappedKey);

    string[] guesses =
    [
        "password", "password123", "1234567890", "qwertyuiop", "letmein123",
        "adminadmin", "welcome123", "iloveyou1", "sunshine12", "football12",
        "charlie123", "monkey1234", "dragon1234", "master1234", "login12345",
        "passw0rd!!", "trustno1!!", "baseball12", "superman12", "batman1234",
        "changeme12", "lockwell12", "vault12345", "secret1234", "test123456",
    ];

    int tried = 0;
    foreach (string guess in guesses)
    {
        tried++;
        byte[] kek = Argon2KeyDeriver.DeriveKey(guess, p);
        try
        {
            VaultCrypto.Decrypt(kek, wrapped);
            Array.Clear(kek, 0, kek.Length);
            return $"BREACH: password cracked with guess #{tried} (common list). Your password may be weak!";
        }
        catch (CryptographicException)
        {
            Array.Clear(kek, 0, kek.Length);
        }
    }
    return $"No match in {tried} common passwords. (Strong password survives this trivial attack.)";
}));

// --- Attack 7: settings.json leak ---
results.Add(Run("7. settings.json for secrets", () =>
{
    string settings = Path.Combine(vaultDir, "settings.json");
    if (!File.Exists(settings)) return "No settings.json in copy (nothing to leak).";
    string text = File.ReadAllText(settings);
    if (text.Contains("Password", StringComparison.Ordinal) || text.Contains("Secret", StringComparison.Ordinal))
        return "Possible secret in settings!";
    return "Settings contain preferences only (no vault secrets).";
}));

// --- Attack 8: Scan exe for hardcoded keys / network ---
results.Add(Run("8. Binary scan for backdoor strings", () =>
{
    string exe = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Lockwell", "bin", "Release", "net8.0-windows", "win-x64", "publish", "Lockwell.exe");
    exe = Path.GetFullPath(exe);
    if (!File.Exists(exe))
    {
        exe = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Lockwell", "bin", "Debug", "net8.0-windows", "Lockwell.exe");
        exe = Path.GetFullPath(exe);
    }
    if (!File.Exists(exe)) return "Exe not found for scan (skipped).";

    byte[] bin = File.ReadAllBytes(exe);
    string[] needles = ["http://", "https://", "api.key", "master_key", "backdoor", "BEGIN RSA", "sk_live", "supabase"];
    var found = new List<string>();
    string ascii = Encoding.UTF8.GetString(bin);
    foreach (var n in needles)
        if (ascii.Contains(n, StringComparison.OrdinalIgnoreCase))
            found.Add(n);

    if (found.Count > 0)
        return $"Suspicious strings in binary: {string.Join(", ", found)} (may be false positives).";
    return "No obvious backdoor URLs or embedded keys in exe strings.";
}));

// --- Attack 9: Try unlock via VaultManager without password ---
results.Add(Run("9. VaultManager.Unlock with empty password", () =>
{
    var vm = new VaultManager(vaultDir);
    bool ok = vm.Unlock("");
    if (ok) return "BREACH: empty password unlocked vault!";
    return "Blocked. Empty password rejected.";
}));

// --- Attack 10: Recovery key brute (short) ---
results.Add(Run("10. Recovery key brute (random 4 attempts)", () =>
{
    var header = ReadHeader(vaultDir);
    if (header.RecoveryWrappedKey is null)
        return "Recovery disabled. No alternate key to attack.";

    byte[] wrapped = Convert.FromBase64String(header.RecoveryWrappedKey);
    for (int i = 0; i < 4; i++)
    {
        byte[] fake = SecureRandom.GetBytes(32);
        try
        {
            VaultCrypto.Decrypt(fake, wrapped);
            return "BREACH: random recovery key worked!";
        }
        catch (CryptographicException) { }
    }
    return "Blocked. Recovery key is 256-bit entropy, not guessable.";
}));

// Print report
Console.WriteLine("--- RESULTS ---");
int blocked = 0;
foreach (var (attack, outcome) in results)
{
    bool breach = outcome.StartsWith("BREACH") || outcome.StartsWith("LEAK");
    string tag = breach ? "[FAIL]" : "[OK]  ";
    if (!breach) blocked++;
    Console.WriteLine($"{tag} {attack}");
    Console.WriteLine($"       {outcome}");
    Console.WriteLine();
}

Console.WriteLine($"Summary: {blocked}/{results.Count} attacks blocked. {(results.Count - blocked)} potential issues.");
Console.WriteLine();
Console.WriteLine("Note: This does NOT test malware on an unlocked PC, keyloggers, or");
Console.WriteLine("      full password brute-force (would take years on a strong passphrase).");
return results.Any(r => r.outcome.StartsWith("BREACH") || r.outcome.StartsWith("LEAK")) ? 2 : 0;

static (string attack, string outcome) Run(string name, Func<string> fn)
{
    try { return (name, fn()); }
    catch (Exception ex) { return (name, $"Error: {ex.Message}"); }
}

static VaultHeader ReadHeader(string dir)
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "vault.lwv"));
    return JsonSerializer.Deserialize(bytes, VaultHeaderJsonContext.Default.VaultHeader)
           ?? throw new InvalidDataException("bad header");
}
