using System.Drawing;
using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal static class NotificationHelper
{
    public static void ShowTransientInfo(string title, string message)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Information;
            using var notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Visible = true,
                BalloonTipTitle = title,
                BalloonTipText = message,
                BalloonTipIcon = ToolTipIcon.Info
            };

            notifyIcon.ShowBalloonTip(1500);
            Application.DoEvents();
            Thread.Sleep(1200);
            notifyIcon.Visible = false;
        }
        catch
        {
            // Notification failures should not block interception.
        }
    }
}
