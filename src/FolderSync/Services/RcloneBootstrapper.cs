using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Responsible for downloading, verifying, and installing the Rclone executable.
/// </summary>
public class RcloneBootstrapper(IHttpClientFactory httpClientFactory) : IRcloneBootstrapper
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public string GetExecutablePath()
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConstants.AppDataFolderName, AppConstants.RcloneBinFolderName);
        string ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        return Path.Combine(baseDir, $"rclone{ext}");
    }

    /// <inheritdoc />
    public bool IsInstalled() => File.Exists(GetExecutablePath());

    /// <inheritdoc />
    public async Task InstallAsync(Action<double> progressCallback, CancellationToken cancellationToken = default)
    {
        string version = AppConstants.RcloneTargetVersion;
        // macOS support added
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";

        string platformKey = $"{os}-{arch}";
        string zipName = $"rclone-{version}-{platformKey}.zip";
        string url = $"https://github.com/rclone/rclone/releases/download/{version}/{zipName}";

        string exePath = GetExecutablePath();
        string binDir = Path.GetDirectoryName(exePath)!;
        if (!Directory.Exists(binDir)) Directory.CreateDirectory(binDir);
        string zipPath = Path.Combine(binDir, zipName);

        using var client = httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
            AppConstants.DownloadBufferSize, true);

        byte[] buffer = new byte[AppConstants.DownloadBufferSize];
        int read;
        long totalRead = 0;
        while ((read = await contentStream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
            if (totalBytes.HasValue) progressCallback((double)totalRead / totalBytes.Value * 100);
        }

        await fileStream.FlushAsync(cancellationToken);
        fileStream.Close();

        // Use cryptographic streaming for hash verification to minimize memory footprint and avoid Large Object Heap (LOH) pressure.
        if (AppConstants.RcloneHashes.TryGetValue(platformKey, out string? expectedHash))
        {
            using var zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                AppConstants.DownloadBufferSize, true);
            byte[] hashBytes = await SHA256.HashDataAsync(zipStream, cancellationToken);
            string actualHash = Convert.ToHexString(hashBytes).ToLower();
            if (actualHash != expectedHash)
                throw new CryptographicException(
                    $"Security violation: Rclone ZIP hash mismatch! (Expected: {expectedHash}, Got: {actualHash})");
        }

        string extractDir = Path.Combine(binDir, "extracted");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        try
        {
            // Terminate existing rclone processes from this application's bin directory to allow file movement
            foreach (var p in Process.GetProcessesByName("rclone"))
            {
                try
                {
                    string fullPath = p.MainModule?.FileName ?? "";
                    if (fullPath.Contains(binDir, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(true);
                        p.WaitForExit(1000);
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    /* Access denied for other users - ignore and proceed */
                }
                catch
                {
                    /* Ignore other exceptions while terminating processes */
                }
            }
        }
        catch
        {
            /* Global exception handler for the termination process */
        }

        string extractedExePath = Path.Combine(extractDir, $"rclone-{version}-{platformKey}",
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "rclone.exe" : "rclone");
        if (File.Exists(exePath)) File.Delete(exePath);
        File.Move(extractedExePath, exePath);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var fileInfo = new FileInfo(exePath);
            fileInfo.UnixFileMode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute;
        }

        Directory.Delete(extractDir, true);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        Logger.Info("Rclone v{0} installed and verified successfully.", version);
    }
}