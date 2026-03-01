using System.Collections.Generic;
using System.Threading.Tasks;
using FolderSync.Models;

namespace FolderSync.Services.Interfaces;

public interface IProfileOrchestratorService
{
    Task ExportProfileAsync(string password, string outputFilePath);
    Task ImportProfileAsync(string password, string inputFilePath);
    Task AutoRepairIntegrityAsync(List<RemoteInfo> corruptedRemotes);
}