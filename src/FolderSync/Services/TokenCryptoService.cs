using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FolderSync.Services.Interfaces;

namespace FolderSync.Services;

public class TokenCryptoService : ITokenCryptoService
{
    private const string MskVaultKey = "rclone-msk";
    private const string EncPrefix = "enc:";
    
    private readonly ISecretVault _vault;
    private byte[]? _cachedMsk;

    public TokenCryptoService(ISecretVault vault)
    {
        _vault = vault;
    }

    /// <summary>
    /// Retrieves the Master Secret Key from the vault, generating a new one if it doesn't exist.
    /// The MSK is cached in memory for the lifetime of the service to reduce I/O and DPAPI overhead.
    /// </summary>
    private byte[] GetOrGenerateMsk()
    {
        if (_cachedMsk != null) return _cachedMsk;

        var secret = _vault.RetrieveSecret(MskVaultKey);
        if (secret != null)
        {
            _cachedMsk = secret;
            return _cachedMsk;
        }

        // Generate a new 256-bit (32 bytes) Master Secret Key using a Cryptographically Secure PRNG
        byte[] newMsk = RandomNumberGenerator.GetBytes(32);
        _vault.StoreSecret(MskVaultKey, newMsk);
        _cachedMsk = newMsk;
        
        return _cachedMsk;
    }

    public string EncryptToken(string plainToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainToken);

        byte[] msk = GetOrGenerateMsk();
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plainToken);
        
        // AES-GCM requires a unique nonce for every encryption operation using the same key.
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[plaintextBytes.Length];

        using (var aes = new AesGcm(msk, tag.Length))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        // Canonical format: NONCE(12) || TAG(16) || CIPHERTEXT
        using var ms = new MemoryStream();
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(ciphertext);

        string base64 = Convert.ToBase64String(ms.ToArray());
        return $"{EncPrefix}{base64}";
    }

    public string DecryptToken(string encryptedToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedToken);

        if (!encryptedToken.StartsWith(EncPrefix))
        {
            // Fallback for backward compatibility during the migration phase.
            // If the token is not encrypted, return it as-is.
            return encryptedToken;
        }

        string base64 = encryptedToken.Substring(EncPrefix.Length);
        byte[] payload = Convert.FromBase64String(base64);

        if (payload.Length < 28) throw new CryptographicException("Encrypted token payload is too short.");

        byte[] nonce = payload.AsSpan(0, 12).ToArray();
        byte[] tag = payload.AsSpan(12, 16).ToArray();
        byte[] ciphertext = payload.AsSpan(28).ToArray();
        byte[] plaintext = new byte[ciphertext.Length];

        byte[] msk = GetOrGenerateMsk();

        using (var aes = new AesGcm(msk, tag.Length))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
