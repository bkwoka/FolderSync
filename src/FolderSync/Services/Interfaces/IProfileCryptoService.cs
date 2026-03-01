using System.Threading.Tasks;

namespace FolderSync.Services.Interfaces;

public interface IProfileCryptoService
{
    Task ExportEncryptedProfileAsync(string password, string outputFilePath);
    Task ImportEncryptedProfileAsync(string password, string inputFilePath);
}