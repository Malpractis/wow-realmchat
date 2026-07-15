using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;

namespace RealmChat
{
    public class HealthItem
    {
        public string Name;
        public bool Ok;
        public string Detail;
        public bool NeedsAdmin;     // whether fixing it requires elevation
        public string FixFlag;      // token passed to the elevated `--fix` run
    }

    // The continuously-enforced replacement for the one-shot setup script:
    // every check is cheap and unprivileged; every fix is either done in-place
    // or delegated to one elevated self-invocation (`--fix a,b,c`).
    public static class HealthCheck
    {
        public static List<HealthItem> RunAll(AppConfig cfg, OllamaController ollama)
        {
            var items = new List<HealthItem>();

            // 1. Ollama installed at the pinned version
            string ver = ollama.InstalledVersion();
            items.Add(new HealthItem
            {
                Name = "Ollama " + Constants.OllamaVersion,
                Ok = ver == Constants.OllamaVersion,
                Detail = ver == null ? "not installed" : ver == Constants.OllamaVersion ? "installed" : "version " + ver + " installed",
                NeedsAdmin = true,
                FixFlag = "install",
            });

            // 2. Machine-scope env vars (for interactive `ollama` use; the app
            //    passes env to its child explicitly regardless)
            bool envOk =
                Get("OLLAMA_HOST") == "0.0.0.0" &&
                Get("OLLAMA_KEEP_ALIVE") == Constants.KeepAlive &&
                string.Equals(Get("OLLAMA_MODELS"), cfg.GetModelsDir(), StringComparison.OrdinalIgnoreCase);
            items.Add(new HealthItem
            {
                Name = "System settings",
                Ok = envOk,
                Detail = envOk ? "set" : "need updating",
                NeedsAdmin = true,
                FixFlag = "env",
            });

            // 3. Firewall rule present + enabled with the expected subnets
            var expect = SubnetHelper.AllowedSubnets(cfg);
            string fwState;
            bool fwOk = FirewallRuleOk(expect, out fwState);
            items.Add(new HealthItem
            {
                Name = "Firewall (server access)",
                Ok = fwOk,
                Detail = fwState,
                NeedsAdmin = true,
                FixFlag = "firewall",
            });

            // 4. Optional: the configured DNS name should resolve to this PC
            if (!string.IsNullOrEmpty(cfg.dns_name))
            {
                string dnsDetail;
                bool dnsOk = DnsPointsHere(cfg.dns_name, out dnsDetail);
                items.Add(new HealthItem
                {
                    Name = "Address (" + cfg.dns_name + ")",
                    Ok = dnsOk,
                    Detail = dnsDetail,
                    NeedsAdmin = false,
                    FixFlag = null,     // not fixable from this PC - it's a server-side lease/record
                });
            }

            return items;
        }

        private static string Get(string name)
        {
            try { return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine); }
            catch { return null; }
        }

        // Query via PowerShell (readable unprivileged); the rule identity and
        // spec mirror what the original setup script created.
        private static bool FirewallRuleOk(List<string> expectSubnets, out string detail)
        {
            try
            {
                string script =
                    "$r = Get-NetFirewallRule -Name '" + Constants.FwRuleName + "' -ErrorAction SilentlyContinue; " +
                    "if (-not $r) { 'MISSING'; exit } " +
                    "if ($r.Enabled -ne 'True' -and $r.Enabled -ne $true) { 'DISABLED'; exit } " +
                    "$a = ($r | Get-NetFirewallAddressFilter).RemoteAddress; " +
                    "'OK ' + ($a -join ',')";
                string outp = RunPs(script);
                if (outp.StartsWith("MISSING")) { detail = "rule missing"; return false; }
                if (outp.StartsWith("DISABLED")) { detail = "rule disabled"; return false; }
                if (outp.StartsWith("OK"))
                {
                    var have = outp.Substring(2).Trim()
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).ToList();
                    var missing = expectSubnets.Where(s => !have.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (missing.Count == 0) { detail = "allows " + string.Join(", ", have.ToArray()); return true; }
                    detail = "missing subnet(s): " + string.Join(", ", missing.ToArray());
                    return false;
                }
                detail = "couldn't read rule";
                return false;
            }
            catch (Exception ex)
            {
                detail = "check failed: " + ex.Message;
                return false;
            }
        }

        private static bool DnsPointsHere(string name, out string detail)
        {
            try
            {
                var resolved = Dns.GetHostAddresses(name)
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString()).ToList();
                if (resolved.Count == 0) { detail = "does not resolve"; return false; }
                var mine = Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString()).ToList();
                if (resolved.Any(r => mine.Contains(r))) { detail = "resolves to this PC"; return true; }
                detail = "resolves to " + resolved[0] + " which is not this PC";
                return false;
            }
            catch (Exception ex)
            {
                detail = "lookup failed: " + ex.Message;
                return false;
            }
        }

        internal static string RunPs(string script)
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd().Trim();
                p.StandardError.ReadToEnd();
                p.WaitForExit(60000);
                return outp;
            }
        }
    }

    // The elevated half: `RealmChat.exe --fix env,firewall,install=<path>`
    // runs these under UAC, then exits; the parent re-runs the health checks
    // for the truth. Also the parent-side orchestration (download-as-user,
    // then elevate).
    public static class ElevatedFix
    {
        // Parent side: prepares anything that shouldn't run elevated (the
        // installer download), then launches the elevated self-invocation.
        public static bool Run(AppConfig cfg, List<HealthItem> broken, Action<string> log)
        {
            var flags = new List<string>();
            foreach (var item in broken.Where(b => !b.Ok && b.FixFlag != null))
            {
                if (item.FixFlag == "install")
                {
                    string installer = Path.Combine(Path.GetTempPath(),
                        "OllamaSetup-" + Constants.OllamaVersion + ".exe");
                    if (!File.Exists(installer))
                    {
                        log("Downloading Ollama v" + Constants.OllamaVersion + " (about 1 GB)...");
                        SelfUpdater.Download(Constants.InstallerUrl, installer,
                            pct => log("  " + pct + "%"));
                    }
                    flags.Add("install=" + installer);
                }
                else if (!flags.Contains(item.FixFlag))
                {
                    flags.Add(item.FixFlag);
                }
            }
            if (flags.Count == 0) return true;

            log("Applying fixes (a Windows permission prompt will appear)...");
            try
            {
                var psi = new ProcessStartInfo(Program.CurrentExePath(),
                    "--fix " + string.Join(",", flags.ToArray()))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode == 0) { log("Fixes applied."); return true; }
                    log("Fix run reported a problem (exit " + p.ExitCode + ") - see the log.");
                    return false;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                log("Permission prompt was cancelled - nothing changed.");
                return false;
            }
        }

        // Elevated side. Returns the process exit code.
        public static int Apply(AppConfig cfg, string flagsArg)
        {
            int failures = 0;
            foreach (var flag in flagsArg.Split(','))
            {
                try
                {
                    if (flag == "env")
                    {
                        Environment.SetEnvironmentVariable("OLLAMA_HOST", "0.0.0.0", EnvironmentVariableTarget.Machine);
                        Environment.SetEnvironmentVariable("OLLAMA_KEEP_ALIVE", Constants.KeepAlive, EnvironmentVariableTarget.Machine);
                        Environment.SetEnvironmentVariable("OLLAMA_MODELS", cfg.GetModelsDir(), EnvironmentVariableTarget.Machine);
                        Directory.CreateDirectory(cfg.GetModelsDir());
                        Logger.Log("fix: machine env vars set");
                    }
                    else if (flag == "firewall")
                    {
                        var subnets = string.Join(",",
                            SubnetHelper.AllowedSubnets(cfg).Select(s => "'" + s + "'").ToArray());
                        string script =
                            "$subs = @(" + subnets + "); " +
                            "$r = Get-NetFirewallRule -Name '" + Constants.FwRuleName + "' -ErrorAction SilentlyContinue; " +
                            "if ($r) { Set-NetFirewallRule -Name '" + Constants.FwRuleName + "' -DisplayName '" + Constants.FwDisplay + "' " +
                            "-Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol TCP " +
                            "-LocalPort " + Constants.DefaultPort + " -RemoteAddress $subs } " +
                            "else { New-NetFirewallRule -Name '" + Constants.FwRuleName + "' -DisplayName '" + Constants.FwDisplay + "' " +
                            "-Direction Inbound -Action Allow -Profile Any -Protocol TCP " +
                            "-LocalPort " + Constants.DefaultPort + " -RemoteAddress $subs | Out-Null }; 'DONE'";
                        var outp = HealthCheck.RunPs(script);
                        if (!outp.Contains("DONE")) throw new Exception("firewall script did not confirm");
                        Logger.Log("fix: firewall rule set for " + subnets);
                    }
                    else if (flag.StartsWith("install="))
                    {
                        string installer = flag.Substring("install=".Length);
                        if (!File.Exists(installer)) throw new Exception("installer not found: " + installer);
                        // Stop anything Ollama-related so files can be replaced.
                        foreach (var name in new[] { "ollama", "ollama app" })
                            foreach (var p in Process.GetProcessesByName(name))
                            {
                                try { p.Kill(); } catch { }
                                finally { p.Dispose(); }
                            }
                        var psi = new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        using (var p = Process.Start(psi))
                        {
                            p.WaitForExit();
                            if (p.ExitCode != 0) throw new Exception("installer exit code " + p.ExitCode);
                        }
                        // The installer autostarts the tray app; keep on-demand semantics.
                        foreach (var name in new[] { "ollama", "ollama app" })
                            foreach (var p in Process.GetProcessesByName(name))
                            {
                                try { p.Kill(); } catch { }
                                finally { p.Dispose(); }
                            }
                        HealthCheck.RunPs(
                            "Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' " +
                            "-Name 'Ollama' -ErrorAction SilentlyContinue");
                        Logger.Log("fix: Ollama " + Constants.OllamaVersion + " installed");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("fix '" + flag + "' FAILED: " + ex.Message);
                    failures++;
                }
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
