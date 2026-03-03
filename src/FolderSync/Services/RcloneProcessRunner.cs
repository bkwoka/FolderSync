using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

public partial class RcloneProcessRunner : IRcloneProcessRunner
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IRcloneBootstrapper _bootstrapper;
    private readonly IRcloneConfigManager _configManager;

    [GeneratedRegex(@"root_folder_id=([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FolderIdRegex();

    public RcloneProcessRunner(IRcloneBootstrapper bootstrapper, IRcloneConfigManager configManager)
    {
        _bootstrapper = bootstrapper;
        _configManager = configManager;
    }

    private static void SecureTempFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(true, false); 
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().Name,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var fileInfo = new FileInfo(path);
            fileInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
    }

    /// <summary>
    /// Extracts a map of RemoteName -> Token from a plaintext INI string.
    /// Used to detect if Rclone auto-refreshed any OAuth tokens during execution.
    /// </summary>
    private Dictionary<string, string> ExtractTokensFromIni(string iniContent)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = iniContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        string currentRemote = string.Empty;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                currentRemote = trimmed.Substring(1, trimmed.Length - 2);
                continue;
            }

            if (!string.IsNullOrEmpty(currentRemote) && trimmed.StartsWith("token", StringComparison.OrdinalIgnoreCase))
            {
                int eqIndex = trimmed.IndexOf('=');
                if (eqIndex > 0)
                {
                    string keyPart = trimmed.Substring(0, eqIndex).TrimEnd();
                    if (keyPart.Equals("token", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens[currentRemote] = trimmed.Substring(eqIndex + 1).Trim();
                    }
                }
            }
        }
        return tokens;
    }

    /// <summary>
    /// Compares the post-execution temp config with the pre-execution state.
    /// If Rclone refreshed any tokens, they are securely pushed back to the main vault.
    /// </summary>
    private async Task SyncRefreshedTokensAsync(string tempConfigPath, Dictionary<string, string> originalTokens)
    {
        if (!File.Exists(tempConfigPath)) return;
        
        string updatedIni = await File.ReadAllTextAsync(tempConfigPath);
        var updatedTokens = ExtractTokensFromIni(updatedIni);

        foreach (var kvp in updatedTokens)
        {
            string remoteName = kvp.Key;
            string newToken = kvp.Value;

            if (originalTokens.TryGetValue(remoteName, out string? oldToken) && newToken != oldToken)
            {
                Logger.Info("Detected OAuth token auto-refresh for remote '{0}'. Saving back to secure vault.", remoteName);
                await _configManager.UpdateTokenAsync(remoteName, newToken);
            }
        }
    }

    public async Task<string> ExecuteCommandAsync(string[] arguments, IEnumerable<string>? inputLines = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string plainIni = await _configManager.GetDecryptedConfigAsync(cancellationToken);
        var originalTokens = ExtractTokensFromIni(plainIni);

        string configDir = Path.GetDirectoryName(_configManager.GetConfigPath())!;
        string tempConfigPath = Path.Combine(configDir, $"temp_{Guid.NewGuid():N}.conf");

        await File.WriteAllTextAsync(tempConfigPath, plainIni, cancellationToken);
        SecureTempFile(tempConfigPath);

        try
        {
            var finalArgs = new List<string>(arguments);
            
            int configIdx = finalArgs.IndexOf("--config");
            if (configIdx >= 0 && configIdx < finalArgs.Count - 1)
            {
                finalArgs[configIdx + 1] = tempConfigPath;
            }
            else
            {
                finalArgs.Add("--config");
                finalArgs.Add(tempConfigPath);
            }

            var maskedArgs = finalArgs.Select(a => a.StartsWith("token=", StringComparison.OrdinalIgnoreCase) ? "token=[MASKED]" : $"\"{a}\"");
            Logger.Info("EXEC: rclone {Command}", string.Join(" ", maskedArgs.Where(a => !a.Contains("temp_")))); 

            var startInfo = new ProcessStartInfo
            {
                FileName = _bootstrapper.GetExecutablePath(),
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
                    try { if (!p.HasExited) p.Kill(true); }
                    catch (InvalidOperationException) { }
                    catch (Exception ex) { Logger.Warn(ex, "Failed to kill Rclone process during cancellation."); }
                }
            }, process);

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            if (inputLines != null)
            {
                var linesList = inputLines.ToList();
                await using var streamWriter = process.StandardInput;
                foreach (var line in linesList) await streamWriter.WriteLineAsync(line.AsMemory(), timeoutCts.Token);
                streamWriter.Close();
            }

            await Task.WhenAll(outputTask, errorTask);

            string output = outputTask.Result;
            string error = errorTask.Result;

            await process.WaitForExitAsync(timeoutCts.Token);

            // CRITICAL: Capture any tokens refreshed by Rclone before we delete the temp file
            await SyncRefreshedTokensAsync(tempConfigPath, originalTokens);

            if (process.ExitCode != 0)
            {
                if (process.ExitCode == 3 || process.ExitCode == 4)
                {
                    Logger.Debug("Rclone reported object not found (Code {0}).", process.ExitCode);
                }
                else
                {
                    Logger.Warn("Rclone returned error code {0}: {1}", process.ExitCode, error);
                }

                string humanFriendlyError = TranslateRcloneError(error, arguments);
                throw new InvalidOperationException(humanFriendlyError);
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            
            Logger.Error("Rclone process timed out after {Timeout} minutes and was killed.", (timeout ?? TimeSpan.FromMinutes(6)).TotalMinutes);
            throw new TimeoutException("Cloud operation hung and exceeded the time limit.");
        }
        finally
        {
            if (File.Exists(tempConfigPath))
            {
                try
                {
                    File.Delete(tempConfigPath);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "CRITICAL: Failed to delete temporary plaintext config file: {0}", tempConfigPath);
                }
            }
        }
    }

    public async Task<string> RunAuthorizationProcessAsync(CancellationToken cancellationToken)
    {
        Logger.Info("Starting Google Drive authorization flow.");
        var startInfo = new ProcessStartInfo
        {
            FileName = _bootstrapper.GetExecutablePath(),
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
                try { if (!p.HasExited) p.Kill(true); }
                catch (InvalidOperationException) { }
                catch (Exception ex) { Logger.Warn(ex, "Failed to kill Rclone authorization process."); }
            }
        }, process);

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return await outputTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private string TranslateRcloneError(string rawError, string[] arguments)
    {
        if (rawError.Contains("Error 404") && rawError.Contains("File not found"))
        {
            string commandLine = string.Join(" ", arguments);
            var matches = FolderIdRegex().Matches(commandLine);

            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    string folderId = match.Groups[1].Value;
                    if (rawError.Contains(folderId))
                    {
                        return $"CRITICAL ERROR: One of the configured Google Drive folders is missing (ID: {folderId}). " +
                               "The folder might have been deleted, moved to trash, or access permissions have expired. " +
                               "Please verify your Drive Management settings.";
                    }
                }
            }
        }
        return rawError;
    }
}
