namespace FolderSync.Services.Interfaces;

/// <summary>
/// Service responsible for encrypting and decrypting OAuth tokens at-rest 
/// using the Master Secret Key (MSK) stored in the OS-native vault.
/// </summary>
public interface ITokenCryptoService
{
    /// <summary>
    /// Encrypts a plaintext token into the canonical format: enc:Base64(Nonce || Tag || Ciphertext)
    /// </summary>
    string EncryptToken(string plainToken);

    /// <summary>
    /// Decrypts a token from the canonical format back to plaintext.
    /// </summary>
    string DecryptToken(string encryptedToken);
}
