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

/// <summary>
/// Responsible exclusively for OS-level process execution, I/O stream management,
/// and timeout handling for the Rclone binary. Implements the Temp Config Pattern.
/// </summary>
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

    /// <summary>
    /// Applies strict OS-level permissions to the temporary configuration file 
    /// to prevent unauthorized access by other users or processes.
    /// </summary>
    private static void SecureTempFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();
            
            // Disable inheritance and remove existing rules
            security.SetAccessRuleProtection(true, false); 
            
            // Grant FullControl ONLY to the current user
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().Name,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
                
            fileInfo.SetAccessControl(security);
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var fileInfo = new FileInfo(path);
            // chmod 600: Read/Write for owner only
            fileInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
    }

    public async Task<string> ExecuteCommandAsync(string[] arguments, IEnumerable<string>? inputLines = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Generate Temporary Plaintext Config
        string plainIni = await _configManager.GetDecryptedConfigAsync(cancellationToken);
        string configDir = Path.GetDirectoryName(_configManager.GetConfigPath())!;
        string tempConfigPath = Path.Combine(configDir, $"temp_{Guid.NewGuid():N}.conf");

        await File.WriteAllTextAsync(tempConfigPath, plainIni, cancellationToken);
        SecureTempFile(tempConfigPath);

        try
        {
            // 2. Prepare Arguments
            var finalArgs = new List<string>(arguments);
            
            // Ensure the temporary config is used instead of the default one
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
            Logger.Info("EXEC: rclone {Command}", string.Join(" ", maskedArgs.Where(a => !a.Contains("temp_")))); // Don't log temp paths

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

            // 3. Execute Process
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
            // 4. Guaranteed Cleanup (Zero-Trust)
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
