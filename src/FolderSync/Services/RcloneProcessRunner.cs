using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Responsible exclusively for OS-level process execution, I/O stream management,
/// and timeout handling for the Rclone binary.
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

    public async Task<string> ExecuteCommandAsync(string[] arguments, IEnumerable<string>? inputLines = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var finalArgs = new List<string>(arguments) { "--config", _configManager.GetConfigPath() };
        var maskedArgs = finalArgs.Select(a => a.StartsWith("token=", StringComparison.OrdinalIgnoreCase) ? "token=[MASKED]" : $"\"{a}\"");
        Logger.Info("EXEC: rclone {Command}", string.Join(" ", maskedArgs));

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
                try
                {
                    if (!p.HasExited) p.Kill(true);
                }
                catch (InvalidOperationException) { }
                catch (Exception ex)
                {
                    // Fail-Visible: Logging failed process cleanup upon cancellation to prevent zombie occurrences.
                    Logger.Warn(ex, "Failed to kill Rclone process during cancellation token trigger.");
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
                Logger.Error("Rclone process timed out after {Timeout} minutes and was killed.", actualTimeout.TotalMinutes);
                throw new TimeoutException($"Cloud operation hung and exceeded the {actualTimeout.TotalMinutes} minute limit.");
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
                catch (InvalidOperationException)
                {
                    // Expected if the process completes between the HasExited check and the Kill invocation.
                }
                catch (Exception ex)
                {
                    // Fail-Visible: Log any failure to terminate the process to prevent orphaned zombie processes.
                    Logger.Warn(ex, "Failed to forcefully terminate Rclone process. This might result in a zombie process.");
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
                try
                {
                    if (!p.HasExited) p.Kill(true);
                }
                catch (InvalidOperationException) { }
                catch (Exception ex)
                {
                    // Surface registration cleanup failures for better maintenance.
                    Logger.Warn(ex, "Failed to kill Rclone authorization process during cancellation.");
                }
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
        finally
        {
            if (process != null && !process.HasExited)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException) { }
                catch (Exception ex)
                {
                    // Maintain visibility into cleanup failures for long-running authorization processes.
                    Logger.Warn(ex, "Failed to forcefully terminate Rclone authorization process. This might result in a zombie process.");
                }
            }
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
