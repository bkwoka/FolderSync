using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FolderSync.Helpers;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Manages inter-process communication (IPC) to enforce a single application instance per OS user.
/// Acts as both a server (listener) and client (signal sender) using named pipes.
/// </summary>
public static class SingleInstanceManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static NamedPipeServerStream? _server;

    /// <summary>
    /// Generates a user-scoped pipe name for process isolation.
    /// </summary>
    private static string GetPipeName() => $"FolderSyncPipe_{Environment.UserName}";

    /// <summary>
    /// Attempts to connect to an existing pipe and send a wake-up signal.
    /// Returns true if another instance is already running (or assumed to be running) to halt this process.
    /// </summary>
    public static bool TrySendWakeUp()
    {
        string pipeName = GetPipeName();
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        try
        {
            client.Connect(500);
            using var writer = new StreamWriter(client);
            writer.WriteLine("WAKE_UP");
            writer.Flush();
            return true;
        }
        catch (TimeoutException)
        {
            // Standard behavior: No other instance is listening, confirming we are the primary process.
            return false;
        }
        catch (Exception ex)
        {
            // Fail-Safe for IPC: If we receive an error other than Timeout (e.g., UnauthorizedAccessException or IOException),
            // it indicates the pipe exists but access is restricted (e.g., permission mismatch).
            // We assume another instance is running and abort to prevent concurrent data corruption or "Split-Brain" states.
            Logger.Fatal(ex, "Critical IPC communication failure during Single-Instance check. Aborting startup to prevent concurrent data corruption.");

            string title = "Google AI Studio Sync - Startup Error";
            string msg = "Application is already running under a different user account or with different privileges (e.g., Run as Administrator).\n\n" +
                         "Please close the other instance or launch this program with matching privileges.";

            NativeDialogHelper.ShowError(title, msg);

            return true;
        }
    }

    /// <summary>
    /// Starts an asynchronous background listener that reacts to wake-up signals.
    /// The cycle is bound to the application lifetime via a CancellationToken.
    /// </summary>
    /// <param name="mainWindow">The primary application window to restore on wake-up.</param>
    /// <param name="appCancellationToken">Token that signals the application shutdown.</param>
    public static void StartListening(Window mainWindow, CancellationToken appCancellationToken)
    {
        string pipeName = GetPipeName();

        Task.Run(async () =>
        {
            while (!appCancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_server != null) await _server.DisposeAsync();

                    _server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.CurrentUserOnly);

                    // Connection wait is now linked to the app lifecycle
                    await _server.WaitForConnectionAsync(appCancellationToken);

                    using var reader = new StreamReader(_server);
                    string? msg = await reader.ReadLineAsync(appCancellationToken);

                    if (msg == "WAKE_UP")
                    {
                        Logger.Info("Received WAKE_UP signal from a new instance. Restoring window...");
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (mainWindow.WindowState == WindowState.Minimized)
                                mainWindow.WindowState = WindowState.Normal;
                            mainWindow.Show();
                            mainWindow.Activate();
                            mainWindow.Topmost = true;
                            mainWindow.Topmost = false;
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("IPC listener worker shutting down gracefully.");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "IPC Pipe Server encountered an error. Restarting in 1s...");
                    await Task.Delay(1000, CancellationToken.None);
                }
            }
        }, appCancellationToken);
    }
}