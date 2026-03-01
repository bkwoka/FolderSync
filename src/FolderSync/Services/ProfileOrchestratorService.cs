using System.Collections.Generic;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;

namespace FolderSync.Services;

public class ProfileOrchestratorService(
    IProfileCryptoService cryptoService,
    IConfigService configService) : IProfileOrchestratorService
{
    public Task ExportProfileAsync(string password, string outputFilePath)
    {
        return cryptoService.ExportEncryptedProfileAsync(password, outputFilePath);
    }

    public Task ImportProfileAsync(string password, string inputFilePath)
    {
        return cryptoService.ImportEncryptedProfileAsync(password, inputFilePath);
    }

    public async Task AutoRepairIntegrityAsync(List<RemoteInfo> corruptedRemotes)
    {
        var config = await configService.LoadConfigAsync();

        foreach (var corrupted in corruptedRemotes)
        {
            config.Remotes.RemoveAll(r => r.RcloneRemote == corrupted.RcloneRemote);
            if (config.MasterRemoteId == corrupted.FolderId) config.MasterRemoteId = null;
        }

        await configService.SaveConfigAsync(config);
    }
}