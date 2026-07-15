// RealmChat - start/stop the WoW realm's chat brain (Ollama) with a GUI, keep
// the pinned Ollama + model + firewall state healthy, and keep ITSELF up to
// date from the public releases repo. Modes:
//   (no args)     GUI: first run walks through setup, self-installs to
//                 %LOCALAPPDATA%\RealmChat and registers a Scheduled Task
//                 (login + daily) for silent self-updates; then the window.
//   --silent      what the Scheduled Task runs: self-update quietly, toast on
//                 change or persistent failure, exit. Never touches the chat.
//   --fix <a,b>   internal: elevated self-invocation applying admin fixes.
//   --configure   re-run setup; --postupdate internal flag after self-update.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace RealmChat
{
    internal static class Program
    {
        public const string TaskName = "Realm Chat Updater";
        public const string ExeName = "RealmChat.exe";
        public const string MutexName = "WoW-RealmChat";

        // REALMCHAT_HOME overrides the install/config dir (portable/dev mode;
        // also skips Scheduled Task + Start Menu registration).
        public static readonly bool Portable;
        public static readonly string InstallDir;

        static Program()
        {
            var home = Environment.GetEnvironmentVariable("REALMCHAT_HOME");
            Portable = !string.IsNullOrEmpty(home);
            InstallDir = Portable
                ? home
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "RealmChat");
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, ExeName); } }

        public static string CurrentExePath()
        {
            return Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        }

        public static string Version
        {
            get
            {
                var a = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));
                return (a == null || string.IsNullOrEmpty(a.InformationalVersion)) ? "dev" : a.InformationalVersion;
            }
        }

        private static Action pendingToast;

        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = args.Contains("--silent");
            bool configure = args.Contains("--configure");
            bool postUpdate = args.Contains("--postupdate");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // Elevated fix runs are fire-and-return workers; they skip the
            // single-instance mutex (the parent GUI holds it while waiting).
            int fixIdx = Array.IndexOf(args, "--fix");
            if (fixIdx >= 0 && fixIdx + 1 < args.Length)
            {
                var cfgForFix = AppConfig.Load() ?? new AppConfig();
                return ElevatedFix.Apply(cfgForFix, args[fixIdx + 1]);
            }

            // After a self-update the old process may still be exiting; wait
            // for its mutex instead of bailing.
            var mutex = new Mutex(false, MutexName);
            bool got = false;
            try { got = mutex.WaitOne(postUpdate ? 15000 : 0); }
            catch (AbandonedMutexException) { got = true; }
            if (!got)
            {
                if (silent) Logger.Log("another instance is already running - skipping this check");
                else MessageBox.Show("Realm Chat is already running - look for its tray icon.", "Realm Chat");
                return 0;
            }
            try { return Run(silent, configure, postUpdate); }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
                // Toast AFTER releasing the single-instance lock: the balloon
                // pumps a message loop for several seconds and must not block
                // the next scheduled run meanwhile.
                var t = pendingToast;
                if (t != null) t();
            }
        }

        private static int Run(bool silent, bool configure, bool postUpdate)
        {
            Directory.CreateDirectory(InstallDir);
            if (postUpdate) CleanupOldExe();

            var cfg = AppConfig.Load();

            if (silent)
            {
                if (cfg == null)
                {
                    Logger.Log("silent run: not configured - open Realm Chat once first");
                    return 1;
                }
                return SilentRun(cfg, postUpdate);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Init(cfg == null ? null : cfg.theme);

            if (cfg == null || configure || !cfg.setup_done)
            {
                using (var setup = new SetupForm(cfg))
                {
                    if (setup.ShowDialog() != DialogResult.OK) return 0;
                    cfg = setup.Result;
                    cfg.Save();
                }
            }

            InstallSelf();
            Application.Run(new MainForm(cfg));
            return 0;
        }

        private static int SilentRun(AppConfig cfg, bool postUpdate)
        {
            Logger.Log("--- silent update check (" + Version + ") ---");
            try
            {
                // The scheduled task ONLY updates the app; starting/stopping
                // the chat stays a human decision in the GUI.
                bool relaunch = !postUpdate && new SelfUpdater(cfg, Logger.Log).Run();
                if (relaunch)
                {
                    Process.Start(InstalledExe, "--silent --postupdate");
                    return 0;
                }
                cfg.consecutive_failures = 0;
                cfg.last_success_utc = DateTime.UtcNow.ToString("o");
                cfg.last_check_utc = cfg.last_success_utc;
                cfg.last_result = postUpdate ? "updated to " + Version : "up to date (" + Version + ")";
                cfg.Save();
                if (postUpdate)
                    Toast.Show("Realm Chat updated",
                        "Now running version " + Version + ".", false);
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Log("FAILED: " + ex.Message);
                cfg.consecutive_failures++;
                cfg.last_check_utc = DateTime.UtcNow.ToString("o");
                cfg.last_result = "failed: " + ex.Message;
                if (cfg.consecutive_failures >= 3 && cfg.DaysSinceFailureToast() >= 3.0)
                {
                    cfg.last_failure_toast_utc = DateTime.UtcNow.ToString("o");
                    int n = cfg.consecutive_failures;
                    pendingToast = () => Toast.Show("Realm Chat updater needs attention",
                        "Update checks have failed " + n + " times in a row. " +
                        "Open 'Realm Chat' from the Start Menu for details.", true);
                }
                cfg.Save();
                return 1;
            }
        }

        // Copy self to the stable location and register the update task +
        // Start Menu shortcut. Interactive runs only.
        private static void InstallSelf()
        {
            string self = CurrentExePath();
            if (!string.Equals(self, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(self, InstalledExe, true);
                TryUnblock(InstalledExe);
                Logger.Log("installed to " + InstalledExe);
            }
            if (!Portable)
            {
                ScheduledTask.EnsureRegistered();
                StartMenu.EnsureShortcut();
            }
        }

        private static void CleanupOldExe()
        {
            string old = InstalledExe + ".old";
            for (int i = 0; i < 6 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { Thread.Sleep(500); }
            }
        }

        // Strip the mark-of-the-web so the installed copy doesn't re-trigger
        // SmartScreen. Best-effort.
        private static void TryUnblock(string path)
        {
            try { File.Delete(path + ":Zone.Identifier"); } catch { }
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
