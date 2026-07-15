using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace RealmChat
{
    // Registers the silent self-update check via schtasks /XML (two triggers
    // need XML, not flags). The task only updates the app; starting the chat
    // stays a human decision, except the opt-in auto-resume after a reboot.
    public static class ScheduledTask
    {
        public static void EnsureRegistered()
        {
            try
            {
                if (Exists()) return;
                Register();
                Logger.Log("update task registered (at logon + daily at noon)");
            }
            catch (Exception ex)
            {
                // Not fatal: updates still apply whenever the GUI is opened.
                Logger.Log("couldn't register the update task: " + ex.Message);
            }
        }

        private static bool Exists()
        {
            return Exec("schtasks", "/Query /TN \"" + Program.TaskName + "\"") == 0;
        }

        public static void Register()
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string start = DateTime.Now.Date.AddHours(12).ToString("yyyy-MM-dd'T'HH:mm:ss");
            string xml = BuildXml(user, start, Program.InstalledExe);

            string tmp = Path.Combine(Path.GetTempPath(), "realmchat-task-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                if (Exec("schtasks", "/Create /F /TN \"" + Program.TaskName + "\" /XML \"" + tmp + "\"") != 0)
                    throw new Exception("schtasks /Create failed");
            }
            finally
            {
                Program.TryDelete(tmp);
            }
        }

        public static string BuildXml(string user, string startBoundary, string exePath)
        {
            return
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo>\r\n" +
"    <Description>Keeps the WoW Realm Chat app up to date (starts the chat only for the opt-in auto-resume after a reboot).</Description>\r\n" +
"  </RegistrationInfo>\r\n" +
"  <Triggers>\r\n" +
"    <LogonTrigger>\r\n" +
"      <Enabled>true</Enabled>\r\n" +
"      <UserId>" + SecurityElement.Escape(user) + "</UserId>\r\n" +
"      <Delay>PT1M</Delay>\r\n" +
"    </LogonTrigger>\r\n" +
"    <CalendarTrigger>\r\n" +
"      <StartBoundary>" + startBoundary + "</StartBoundary>\r\n" +
"      <Enabled>true</Enabled>\r\n" +
"      <ScheduleByDay>\r\n" +
"        <DaysInterval>1</DaysInterval>\r\n" +
"      </ScheduleByDay>\r\n" +
"    </CalendarTrigger>\r\n" +
"  </Triggers>\r\n" +
"  <Principals>\r\n" +
"    <Principal id=\"Author\">\r\n" +
"      <UserId>" + SecurityElement.Escape(user) + "</UserId>\r\n" +
"      <LogonType>InteractiveToken</LogonType>\r\n" +
"      <RunLevel>LeastPrivilege</RunLevel>\r\n" +
"    </Principal>\r\n" +
"  </Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
"    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\">\r\n" +
"    <Exec>\r\n" +
"      <Command>" + SecurityElement.Escape(exePath) + "</Command>\r\n" +
"      <Arguments>--silent</Arguments>\r\n" +
"    </Exec>\r\n" +
"  </Actions>\r\n" +
"</Task>\r\n";
        }

        private static int Exec(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(30000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }
    }

    // Best-effort Start Menu entry so "open Realm Chat" is findable.
    public static class StartMenu
    {
        public static void EnsureShortcut()
        {
            try
            {
                string lnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    "Realm Chat.lnk");
                if (File.Exists(lnk)) return;
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                var sc = shell.CreateShortcut(lnk);
                sc.TargetPath = Program.InstalledExe;
                sc.WorkingDirectory = Program.InstallDir;
                sc.Description = "Start and stop the WoW realm's chat brain";
                sc.Save();
            }
            catch { }
        }
    }
}
