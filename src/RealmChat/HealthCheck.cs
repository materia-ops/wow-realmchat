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

            // 3. Firewall rule present + enabled with the expected subnets,
            //    and no foreign Ollama rules (the Ollama app / Windows likes
            //    to add its own, which can block the game server's access)
            string fwState;
            bool fwOk = FirewallStatus(cfg, out fwState);
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

        // Verifies OUR rule (present, enabled, bound to ollama.exe, expected
        // subnets) AND hunts for foreign Ollama rules: anything named *ollama*
        // or program-bound to ollama.exe that isn't ours. The Ollama app /
        // Windows' first-listen prompt creates those, and a Block rule among
        // them cuts the game server off while everything still works locally.
        //
        // Reads the firewall through the COM policy API (HNetCfg.FwPolicy2)
        // in-process: the old PowerShell + Get-NetFirewallRule pipeline took
        // seconds and ~50 MB per run, and this re-runs every minute while the
        // chat is up. COM identifies rules by display name (the PS `Name` id
        // isn't exposed there), which is why ours is matched on FwDisplay.
        // Public because the GUI's firewall watcher re-runs it while running.
        public static bool FirewallStatus(AppConfig cfg, out string detail)
        {
            var expectSubnets = SubnetHelper.AllowedSubnets(cfg);
            string exe = OllamaController.FindExe(cfg);
            try
            {
                dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                bool oursFound = false, oursEnabled = false;
                string oursRemote = "", oursApp = null;
                int foreign = 0, blocks = 0;

                foreach (dynamic r in policy.Rules)
                {
                    string display = "", app = "";
                    try { display = (string)r.Name ?? ""; } catch { }
                    try { app = (string)r.ApplicationName ?? ""; } catch { }

                    if (!oursFound &&
                        string.Equals(display, Constants.FwDisplay, StringComparison.OrdinalIgnoreCase))
                    {
                        oursFound = true;
                        try { oursEnabled = (bool)r.Enabled; } catch { }
                        try { oursRemote = (string)r.RemoteAddresses ?? ""; } catch { }
                        oursApp = app;
                        continue;
                    }
                    if (display.IndexOf("ollama", StringComparison.OrdinalIgnoreCase) < 0 &&
                        app.IndexOf("ollama", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    foreign++;
                    bool enabled = false; int action = 1;
                    try { enabled = (bool)r.Enabled; } catch { }
                    try { action = (int)r.Action; } catch { }
                    if (enabled && action == 0) blocks++;   // NET_FW_ACTION_BLOCK
                }

                // Block rules win over allows in Windows Firewall: critical.
                if (blocks > 0)
                {
                    detail = blocks + " BLOCKING Ollama rule(s) found (the Ollama app adds these) - Fix removes them";
                    return false;
                }
                if (!oursFound) { detail = "rule missing"; return false; }
                if (!oursEnabled) { detail = "rule disabled"; return false; }

                // Program binding is what suppresses the Windows "allow access"
                // popup when ollama starts listening - the prompt fires for any
                // exe that listens with no rule naming that exe.
                if (exe != null &&
                    !string.Equals(oursApp ?? "", exe, StringComparison.OrdinalIgnoreCase))
                {
                    detail = "rule isn't bound to ollama.exe - Fix rebinds it (stops the Windows firewall popup)";
                    return false;
                }

                // Windows often reports subnet entries in dotted-mask form
                // (192.168.1.0/255.255.255.0) even when set as /24 - normalize
                // both sides or the check can never match what it just set.
                var have = oursRemote.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => NormalizeCidr(s)).ToList();
                bool any = have.Any(s => s == "*");
                var missing = any ? new List<string>() : expectSubnets.Select(NormalizeCidr)
                    .Where(s => !have.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count > 0)
                {
                    detail = "missing subnet(s): " + string.Join(", ", missing.ToArray()) +
                             " (rule has: " + (have.Count == 0 ? "none" : string.Join(", ", have.ToArray())) + ")";
                    return false;
                }
                if (foreign > 0)
                {
                    detail = foreign + " extra Ollama rule(s) found - Fix keeps exactly one";
                    return false;
                }
                detail = "allows " + string.Join(", ", have.ToArray());
                return true;
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

        // "192.168.1.0/255.255.255.0" -> "192.168.1.0/24"; prefix/bare forms
        // pass through unchanged.
        internal static string NormalizeCidr(string s)
        {
            s = s.Trim();
            int slash = s.IndexOf('/');
            if (slash < 0) return s;
            string ip = s.Substring(0, slash);
            string right = s.Substring(slash + 1);
            if (right.IndexOf('.') < 0) return ip + "/" + right;
            System.Net.IPAddress mask;
            if (!System.Net.IPAddress.TryParse(right, out mask)) return s;
            int prefix = 0;
            foreach (var b in mask.GetAddressBytes())
                for (int bit = 7; bit >= 0; bit--)
                    if ((b & (1 << bit)) != 0) prefix++;
            return ip + "/" + prefix;
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
                // Read both pipes concurrently: sequential ReadToEnd deadlocks
                // if the child fills the not-yet-read pipe's buffer.
                var errTask = p.StandardError.ReadToEndAsync();
                string outp = p.StandardOutput.ReadToEnd().Trim();
                errTask.GetAwaiter().GetResult();
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
                        // Bind the rule to the exe: Windows' "allow access"
                        // popup fires whenever a program starts listening and
                        // NO rule names that program - a port-only rule never
                        // suppresses it. (When install runs in the same fix
                        // pass it is ordered before this, so the exe exists.)
                        string exePath = OllamaController.FindExe(cfg);
                        string progArg = exePath == null ? "" :
                            "-Program '" + exePath.Replace("'", "''") + "' ";
                        // Keep exactly ONE Ollama rule: delete every foreign one
                        // (named *ollama* or program-bound to ollama.exe - the
                        // Ollama app and Windows' first-listen prompt create
                        // those, including Block rules that cut the server off),
                        // then recreate ours from the full spec so stale fields
                        // (missing program binding, old port) can't survive.
                        string script =
                            "$ErrorActionPreference = 'Stop'; " +
                            "try { " +
                            "$ours = '" + Constants.FwRuleName + "'; " +
                            "$subs = @(" + subnets + "); " +
                            "$named = @(Get-NetFirewallRule -DisplayName '*ollama*' -ErrorAction SilentlyContinue | " +
                            "  Where-Object { $_.Name -ne $ours }); " +
                            "$prog = @(); " +
                            "try { $prog = @(Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | " +
                            "  Where-Object { $_.Program -like '*ollama*' } | Get-NetFirewallRule -ErrorAction SilentlyContinue | " +
                            "  Where-Object { $_.Name -ne $ours }) } catch {} " +
                            "@($named + $prog) | Sort-Object Name -Unique | Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
                            "Get-NetFirewallRule -Name $ours -ErrorAction SilentlyContinue | " +
                            "  Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
                            "New-NetFirewallRule -Name $ours -DisplayName '" + Constants.FwDisplay + "' " +
                            "-Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol TCP " +
                            "-LocalPort " + cfg.GetPort() + " -RemoteAddress $subs " + progArg + "| Out-Null; " +
                            "$now = (Get-NetFirewallRule -Name $ours | Get-NetFirewallAddressFilter).RemoteAddress -join ','; " +
                            "'DONE ' + $now " +
                            "} catch { 'ERR ' + $_.Exception.Message }";
                        var outp = HealthCheck.RunPs(script);
                        if (!outp.StartsWith("DONE"))
                            throw new Exception("firewall fix failed: " +
                                (outp.StartsWith("ERR") ? outp.Substring(4) : outp));
                        Logger.Log("fix: firewall rule now allows " + outp.Substring(5) +
                            (exePath == null ? " (ollama.exe not found - rule not program-bound yet)" :
                             " (bound to " + exePath + ")"));
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
