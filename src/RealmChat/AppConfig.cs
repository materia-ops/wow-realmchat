using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace RealmChat
{
    // Persisted as JSON at <InstallDir>\config.json. Property names are
    // snake_case on purpose: they ARE the JSON schema.
    //
    // Everything environment-specific lives HERE (machine-local), never in the
    // published binary: extra firewall subnets and the optional expected DNS
    // name are entered once at first run.
    public class AppConfig
    {
        public const string DefaultRepo = "materia-ops/wow-realmchat";

        public bool setup_done { get; set; }
        public string theme { get; set; }             // "auto" | "light" | "dark" (null = auto)
        public string server_subnets { get; set; }    // comma-separated CIDRs allowed through the
                                                      // firewall IN ADDITION to the local subnet
        public string dns_name { get; set; }          // optional: hostname that should resolve to
                                                      // this PC (validated via system DNS)
        public bool tray_hint_shown { get; set; }
        public bool disable_firewall_watch { get; set; }   // watcher is ON by default
        public bool auto_resume { get; set; }              // opt-in: restart the chat after a reboot
        public bool chat_was_running { get; set; }         // persisted chat state driving auto-resume

        public string releases_repo { get; set; }     // overrides for dev/testing only
        public string base_url { get; set; }
        public string ollama_exe { get; set; }
        public int port { get; set; }
        public string models_dir { get; set; }

        public int consecutive_failures { get; set; }
        public string last_success_utc { get; set; }
        public string last_failure_toast_utc { get; set; }
        public string last_check_utc { get; set; }
        public string last_result { get; set; }

        // Previous model-store locations, recorded when the folder is changed
        // in Settings so Cleanup can offer to reclaim them later.
        public string old_models_dirs { get; set; }

        public List<string> GetOldModelsDirs()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(old_models_dirs)) return list;
            foreach (var raw in old_models_dirs.Split('|'))
            {
                var s = raw.Trim();
                if (s.Length > 0) list.Add(s);
            }
            return list;
        }

        public void AddOldModelsDir(string dir)
        {
            var list = GetOldModelsDirs();
            if (list.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(dir);
            old_models_dirs = string.Join("|", list.ToArray());
        }

        public void RemoveOldModelsDir(string dir)
        {
            var list = GetOldModelsDirs()
                .Where(s => !string.Equals(s, dir, StringComparison.OrdinalIgnoreCase)).ToList();
            old_models_dirs = list.Count == 0 ? null : string.Join("|", list.ToArray());
        }

        public int GetPort()
        {
            return port > 0 ? port : Constants.DefaultPort;
        }

        public string GetModelsDir()
        {
            return string.IsNullOrEmpty(models_dir) ? Constants.ModelsDir : models_dir;
        }

        public List<string> GetServerSubnets()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(server_subnets)) return list;
            foreach (var raw in server_subnets.Split(','))
            {
                var s = raw.Trim();
                if (s.Length > 0) list.Add(s);
            }
            return list;
        }

        // A method, not a property: JavaScriptSerializer would persist a
        // read-only property into config.json.
        public string GetBaseUrl()
        {
            if (!string.IsNullOrEmpty(base_url))
                return base_url.EndsWith("/") ? base_url : base_url + "/";
            string repo = string.IsNullOrEmpty(releases_repo) ? DefaultRepo : releases_repo;
            return "https://github.com/" + repo + "/releases/latest/download/";
        }

        public double DaysSinceFailureToast()
        {
            DateTime t;
            if (DateTime.TryParse(last_failure_toast_utc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out t))
                return (DateTime.UtcNow - t.ToUniversalTime()).TotalDays;
            return double.MaxValue;
        }

        private static string ConfigPath
        {
            get { return Path.Combine(Program.InstallDir, "config.json"); }
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                var json = File.ReadAllText(ConfigPath);
                return new JavaScriptSerializer().Deserialize<AppConfig>(json);
            }
            catch (Exception ex)
            {
                Logger.Log("couldn't read config (" + ex.Message + ") - treating as unconfigured");
                return null;
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Program.InstallDir);
            File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(this));
        }
    }
}
