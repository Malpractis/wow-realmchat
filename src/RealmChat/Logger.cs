using System;
using System.IO;

namespace RealmChat
{
    // Line-per-event log in the install dir so silent runs and start/stop
    // history are diagnosable after the fact. Self-trims; never throws.
    public static class Logger
    {
        private static readonly object Gate = new object();

        public static string LogPath
        {
            get { return Path.Combine(Program.InstallDir, "realmchat.log"); }
        }

        public static void Log(string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Program.InstallDir);
                    var f = new FileInfo(LogPath);
                    if (f.Exists && f.Length > 256 * 1024)
                    {
                        var lines = File.ReadAllLines(LogPath);
                        var keep = new string[Math.Min(lines.Length, 500)];
                        Array.Copy(lines, lines.Length - keep.Length, keep, 0, keep.Length);
                        File.WriteAllLines(LogPath, keep);
                    }
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
