namespace FolderSync.Services.Interfaces;

/// <summary>
/// Defines a secure, OS-native storage mechanism for highly sensitive cryptographic keys.
/// </summary>
public interface ISecretVault
{
    void StoreSecret(string key, byte[] secret);
    byte[]? RetrieveSecret(string key);
    bool HasSecret(string key);
    void DeleteSecret(string key);
}
