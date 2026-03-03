using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FolderSync.Helpers;

/// <summary>
/// Cross-platform helper for displaying native OS message boxes.
/// Used strictly for fatal errors that occur before the Avalonia UI engine is initialized.
/// </summary>
public static class NativeDialogHelper
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>
    /// Displays a native error dialog based on the host operating system.
    /// </summary>
    public static void ShowError(string title, string message)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // 0x10 is the hexadecimal flag for MB_ICONERROR (Red X symbol)
                MessageBox(IntPtr.Zero, message, title, 0x10);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Fallback sequence for common Linux Desktop Environments (GNOME/KDE)
                try 
                { 
                    Process.Start(new ProcessStartInfo { FileName = "zenity", Arguments = $"--error --title=\"{title}\" --text=\"{message}\"", UseShellExecute = false }); 
                }
                catch 
                { 
                    Process.Start(new ProcessStartInfo { FileName = "kdialog", Arguments = $"--error \"{message}\" --title \"{title}\"", UseShellExecute = false }); 
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // AppleScript injection for native macOS dialogs
                string script = $"display dialog \"{message}\" with title \"{title}\" buttons {{\"OK\"}} default button 1 with icon stop";
                Process.Start(new ProcessStartInfo { FileName = "osascript", Arguments = $"-e '{script}'", UseShellExecute = false });
            }
        }
        catch
        {
            // Failsafe: If the OS fails to render the native dialog, we silently swallow the exception
            // to ensure the application continues its shutdown sequence safely. The error is already logged.
        }
    }
}
