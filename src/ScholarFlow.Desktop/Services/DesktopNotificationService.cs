using System.Diagnostics;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Desktop.Services;

/// <summary>
/// Cross-platform OS notification dispatcher (Linux notify-send / libnotify, Windows Toast via WinRT PowerShell).
/// </summary>
public sealed class DesktopNotificationService
{
    public void SendSystemNotification(AppNotification notification)
    {
        if (notification is null)
            return;

        TryShow(notification.Title, notification.Message);
    }

    public static void TryShow(string title, string message)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    var psi = new ProcessStartInfo("notify-send")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("--app-name=ScholarFlow");
                    psi.ArgumentList.Add(title);
                    psi.ArgumentList.Add(message);
                    using var proc = Process.Start(psi);
                }
                else if (OperatingSystem.IsWindows())
                {
                    var safeTitle = EscapeXml(title);
                    var safeMsg = EscapeXml(message);
                    var script = $"""
                        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
                        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
                        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
                        $xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text>{safeTitle}</text><text>{safeMsg}</text></binding></visual></toast>')
                        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
                        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('ScholarFlow').Show($toast)
                        """;

                    var psi = new ProcessStartInfo("powershell.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-NonInteractive");
                    psi.ArgumentList.Add("-WindowStyle");
                    psi.ArgumentList.Add("Hidden");
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add(script);
                    using var proc = Process.Start(psi);
                }
            }
            catch
            {
                // Best-effort OS notification; never crash the background poller
            }
        });
    }

    private static string EscapeXml(string s) =>
        (s ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}
