using System.Windows.Forms;

namespace RealmChat
{
    // Balloon tips render as native toast notifications on Windows 10/11 and
    // need no WinRT interop. Used by silent runs (the GUI has its own tray
    // icon), so it pumps its own short-lived message loop.
    public static class Toast
    {
        // GUI path: the window already owns a pumped tray icon - just balloon it.
        public static void ShowFromTray(NotifyIcon tray, string title, string message, bool warning)
        {
            try
            {
                tray.ShowBalloonTip(6000, title, message,
                    warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            catch { }
        }

        public static void Show(string title, string message, bool warning)
        {
            try
            {
                using (var icon = new NotifyIcon())
                using (var timer = new Timer())
                {
                    icon.Icon = AppIcons.Neutral;
                    icon.Text = "Realm Chat";
                    icon.Visible = true;
                    icon.BalloonTipTitle = title;
                    icon.BalloonTipText = message;
                    icon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
                    icon.ShowBalloonTip(6000);

                    var ctx = new ApplicationContext();
                    timer.Interval = 7000;
                    timer.Tick += (s, e) => ctx.ExitThread();
                    timer.Start();
                    Application.Run(ctx);   // toast survives process exit only once shown
                }
            }
            catch
            {
                // notifications are best-effort; never fail anything over one
            }
        }
    }
}
