using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using System.Text.RegularExpressions;
using System.Text;
using FolderSync.Services.Interfaces;
using FolderSync.Models;

namespace FolderSync.Services;

public partial class RcloneService(IRcloneBootstrapper bootstrapper) : IRcloneService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly SemaphoreSlim _configLock = new(1, 1);

    [GeneratedRegex(@"\{.*?\}", RegexOptions.Singleline)]
    private static partial Regex JsonTokenRegex();

    [GeneratedRegex(@"root_folder_id=([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FolderIdRegex();

    private string GetConfigPath()
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConstants.AppDataFolderName);
        if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, AppConstants.RcloneConfigFileName);
    }

    public async Task<string> ExecuteCommandAsync(string[] arguments, IEnumerable<string>? inputLines = null,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var finalArgs = new List<string>(arguments) { "--config", GetConfigPath() };
        var maskedArgs = finalArgs.Select(a =>
            a.StartsWith("token=", StringComparison.OrdinalIgnoreCase) ? "token=[MASKED]" : $"\"{a}\"");
        Logger.Info("EXEC: rclone {Command}", string.Join(" ", maskedArgs));

        var startInfo = new ProcessStartInfo
        {
            FileName = bootstrapper.GetExecutablePath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = inputLines != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in finalArgs) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to initialize Rclone process.");

        var actualTimeout = timeout ?? TimeSpan.FromMinutes(6);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(actualTimeout);

        await using var ctr = timeoutCts.Token.Register(state =>
        {
            if (state is Process p)
            {
                try
                {
                    if (!p.HasExited) p.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex)
                {
                    Logger.Trace(ex, "Ignored error while killing Rclone process.");
                }
            }
        }, process);

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            if (inputLines != null)
            {
                var linesList = inputLines.ToList();
                Logger.Debug("Sending {0} lines to Rclone via standard input.", linesList.Count);
                await using var streamWriter = process.StandardInput;
                foreach (var line in linesList) await streamWriter.WriteLineAsync(line.AsMemory(), timeoutCts.Token);
                streamWriter.Close();
            }

            await Task.WhenAll(outputTask, errorTask);

            string output = outputTask.Result;
            string error = errorTask.Result;

            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                // ExitCode 3 = Directory not found
                // ExitCode 4 = Object (file) not found
                if (process.ExitCode == 3 || process.ExitCode == 4)
                {
                    Logger.Debug("Rclone reported object not found (Code {0}).", process.ExitCode);
                }
                else
                {
                    Logger.Warn("Rclone returned error code {0}: {1}", process.ExitCode, error);
                }

                // Refine the error message with contextual information to provide meaningful feedback
                // when critical Google Drive resources are missing.
                string humanFriendlyError = TranslateRcloneError(error, arguments);
                throw new InvalidOperationException(humanFriendlyError);
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Logger.Warn("Rclone process terminated by user request.");
                throw;
            }
            else
            {
                Logger.Error("Rclone process timed out after {Timeout} minutes and was killed.",
                    actualTimeout.TotalMinutes);
                throw new TimeoutException(
                    $"Cloud operation hung and exceeded the {actualTimeout.TotalMinutes} minute limit.");
            }
        }
        finally
        {
            if (process != null && !process.HasExited)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }
            }
        }
    }

    public async Task<List<RcloneItem>> ListItemsAsync(string path, bool dirsOnly = false,
        CancellationToken cancellationToken = default)
    {
        string[] args = ["lsjson", path, dirsOnly ? "--dirs-only" : "--files-only", "--drive-use-trash=false"];
        string json = await ExecuteCommandAsync(args, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize(json, RcloneJsonContext.Default.ListRcloneItem) ?? [];
        }
        catch (JsonException jex)
        {
            throw new FormatException($"Failed to parse Google Drive response for path {path}.", jex);
        }
    }

    public async Task<string> AuthorizeGoogleDrive(CancellationToken cancellationToken)
    {
        Logger.Info("Starting Google Drive authorization flow.");
        var startInfo = new ProcessStartInfo
        {
            FileName = bootstrapper.GetExecutablePath(),
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("authorize");
        startInfo.ArgumentList.Add("drive");

        using var process = Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to start Rclone authorization process.");

        await using var ctr = cancellationToken.Register(state =>
        {
            if (state is Process p)
            {
                try
                {
                    if (!p.HasExited) p.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex)
                {
                    Logger.Trace(ex, "Ignored error while killing Rclone process.");
                }
            }
        }, process);

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string output = await outputTask;
            var match = JsonTokenRegex().Match(output);
            if (match.Success) return match.Value;
            throw new Exception("Failed to retrieve Google token.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            if (process != null && !process.HasExited)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }
            }
        }
    }

    public async Task CreateRemote(string name, string tokenJson, CancellationToken cancellationToken = default)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            Logger.Info("Creating new remote directly via configuration file manipulation: {RemoteName}", name);
            string configPath = GetConfigPath();
            string configBlock = $"\n[{name}]\ntype = drive\nconfig_is_local = false\ntoken = {tokenJson}\n";
            await File.AppendAllTextAsync(configPath, configBlock, cancellationToken);
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<string> GetAccessTokenAsync(string rcloneRemote, CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync(["about", $"{rcloneRemote}:"], null, cancellationToken);
        string configJson = await ExecuteCommandAsync(["config", "dump"], null, cancellationToken);
        using var configDoc = JsonDocument.Parse(configJson);

        if (!configDoc.RootElement.TryGetProperty(rcloneRemote, out var remoteNode) ||
            !remoteNode.TryGetProperty("token", out var tokenNode))
        {
            throw new InvalidOperationException($"Missing OAuth token inside rclone config for {rcloneRemote}.");
        }

        using var tokenDoc = JsonDocument.Parse(tokenNode.GetString() ?? "{}");
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessTokenNode))
        {
            throw new InvalidOperationException("Invalid token JSON format.");
        }

        return accessTokenNode.GetString()!;
    }

    public async Task DeleteRemoteAsync(string remoteName, CancellationToken cancellationToken = default) =>
        await ExecuteCommandAsync(["config", "delete", remoteName], null, cancellationToken);

    public async Task<string> ReadFileContentAsync(string rcloneRemote, string folderId, string fileName,
        CancellationToken cancellationToken = default) =>
        await ExecuteCommandAsync(["cat", $"{rcloneRemote},root_folder_id={folderId}:{fileName}"], null,
            cancellationToken);

    public async Task<List<string>> GetConfiguredRemotesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string output = await ExecuteCommandAsync(["listremotes"], null, cancellationToken);
            return output.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.TrimEnd(':')).ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to retrieve the list of configured Rclone remotes.");
            return [];
        }
    }

    /// <summary>
    /// Translates raw Rclone error messages into user-friendly, context-aware technical descriptions.
    /// Scans command arguments for all folder IDs to identify which specific resource is missing.
    /// </summary>
    private string TranslateRcloneError(string rawError, string[] arguments)
    {
        // Intercept Google Drive API 404 (File not found) errors
        if (rawError.Contains("Error 404") && rawError.Contains("File not found"))
        {
            string commandLine = string.Join(" ", arguments);

            // Scan for all folder IDs used in the operation
            var matches = FolderIdRegex().Matches(commandLine);

            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    string folderId = match.Groups[1].Value;

                    // If a resource ID from the command matches the ID reported in the error message
                    if (rawError.Contains(folderId))
                    {
                        return $"CRITICAL ERROR: One of the configured Google Drive folders is missing (ID: {folderId}). " +
                               "The folder might have been deleted, moved to trash, or access permissions have expired. " +
                               "Please verify your Drive Management settings.";
                    }
                }
            }
        }

        // Fallback to the original error message if no specific pattern is identified
        return rawError;
    }
}