using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;

namespace RealmChat
{
    public enum ChatState { Stopped, Starting, Warming, Ready, Crashed }

    // Owns the "ollama serve" process and everything HTTP about it: health,
    // model presence, warm-up, pull. The GUI drives this; nothing here touches
    // WinForms so it stays testable against the stub server.
    public class OllamaController
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private readonly AppConfig cfg;
        private readonly Action<string> log;
        private Process proc;                 // the serve process WE started (null if adopted/none)
        private bool stopping;                // suppresses crash reporting during our own Stop()

        // Fired from a worker thread when a server we started exits on its own.
        public event Action ServerExited;

        public OllamaController(AppConfig cfg, Action<string> log)
        {
            this.cfg = cfg;
            this.log = log ?? delegate { };
        }

        public int Port { get { return cfg.GetPort(); } }
        private string Api { get { return "http://127.0.0.1:" + Port; } }

        // --- discovery ---------------------------------------------------------

        public string ResolveExe()
        {
            return FindExe(cfg);
        }

        // Static so the firewall code (health check + elevated fix) can bind
        // rules to the exact exe path without owning a controller.
        public static string FindExe(AppConfig cfg)
        {
            if (cfg != null && !string.IsNullOrEmpty(cfg.ollama_exe) && File.Exists(cfg.ollama_exe))
                return cfg.ollama_exe;
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             @"Programs\Ollama\ollama.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             @"Ollama\ollama.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        public string InstalledVersion()
        {
            var exe = ResolveExe();
            if (exe == null) return null;
            try
            {
                var psi = new ProcessStartInfo(exe, "-v")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    // Read stderr concurrently: reading the pipes one after the
                    // other deadlocks if the child fills the second pipe's buffer.
                    var errTask = p.StandardError.ReadToEndAsync();
                    string outp = p.StandardOutput.ReadToEnd() + errTask.GetAwaiter().GetResult();
                    p.WaitForExit(15000);
                    var m = System.Text.RegularExpressions.Regex.Match(outp, @"(\d+\.\d+\.\d+)");
                    return m.Success ? m.Groups[1].Value : null;
                }
            }
            catch { return null; }
        }

        // --- health ------------------------------------------------------------

        public bool IsUp()
        {
            return TryGet("/api/version", 3) != null;
        }

        public bool ModelPresent()
        {
            return ListHasModel(TryGet("/api/tags", 10));
        }

        public bool ModelLoaded()
        {
            return ListHasModel(TryGet("/api/ps", 10));
        }

        private static bool ListHasModel(string json)
        {
            if (json == null) return false;
            try
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                object modelsObj;
                if (root == null || !root.TryGetValue("models", out modelsObj)) return false;
                var arr = modelsObj as System.Collections.ArrayList;
                if (arr == null) return false;
                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    object name;
                    if (d != null && d.TryGetValue("name", out name) &&
                        string.Equals(name as string, Constants.Model, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        private string TryGet(string path, int timeoutSec)
        {
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)))
                using (var resp = Http.GetAsync(Api + path, cts.Token).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
            catch { return null; }
        }

        // Loads the model onto the GPU; blocking (run it on a worker thread).
        public bool Warm()
        {
            try
            {
                var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "model", Constants.Model },
                    { "prompt", "Reply with the single word: ready" },
                    { "stream", false },
                });
                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (var resp = Http.PostAsync(Api + "/api/generate", content).GetAwaiter().GetResult())
                    return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // --- lifecycle -----------------------------------------------------------

        public bool WeStartedIt { get { return proc != null && !proc.HasExited; } }

        public void Start()
        {
            var exe = ResolveExe();
            if (exe == null) throw new Exception("Ollama is not installed (use Fix problems)");
            stopping = false;

            var psi = new ProcessStartInfo(exe, "serve")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // The bind + model-store env travels with the child; Machine-scope
            // vars exist for interactive `ollama` commands, but the app never
            // depends on them being picked up by an already-running shell.
            psi.EnvironmentVariables["OLLAMA_HOST"] = "0.0.0.0:" + Port;
            psi.EnvironmentVariables["OLLAMA_KEEP_ALIVE"] = Constants.KeepAlive;
            psi.EnvironmentVariables["OLLAMA_MODELS"] = cfg.GetModelsDir();

            proc = Process.Start(psi);
            proc.EnableRaisingEvents = true;
            proc.Exited += delegate
            {
                if (stopping) return;
                log("Ollama exited on its own (code " + SafeExitCode(proc) + ").");
                var h = ServerExited;
                if (h != null) h();
            };
            // Drain output so the pipe never blocks; surface lines to the log.
            DrainAsync(proc.StandardOutput);
            DrainAsync(proc.StandardError);
            log("Started ollama serve (pid " + proc.Id + ", port " + Port + ").");
        }

        public bool WaitUntilUp(int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (proc != null && proc.HasExited) return false;
                if (IsUp()) return true;
                System.Threading.Thread.Sleep(500);
            }
            return IsUp();
        }

        // Stops the serve process - ours if we own it, otherwise any ollama
        // process on this machine (the "adopted server" case).
        public void Stop()
        {
            stopping = true;
            if (proc != null && !proc.HasExited)
            {
                KillTree(proc.Id);
                proc.Dispose();
                proc = null;
                log("Chat stopped.");
                return;
            }
            if (proc != null) proc.Dispose();
            proc = null;
            bool any = false;
            foreach (var name in new[] { "ollama", "ollama app" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { KillTree(p.Id); any = true; } catch { }
                    finally { p.Dispose(); }
                }
            }
            log(any ? "Chat stopped (existing Ollama processes ended)." : "Nothing to stop.");
        }

        // `ollama serve` spawns runner children; net48 Process.Kill() has no
        // entire-tree overload, so lean on taskkill.
        private static void KillTree(int pid)
        {
            var psi = new ProcessStartInfo("taskkill", "/T /F /PID " + pid)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using (var p = Process.Start(psi)) p.WaitForExit(15000);
        }

        // All models the server knows, with sizes (server must be up).
        public List<KeyValuePair<string, long>> ListModels()
        {
            var result = new List<KeyValuePair<string, long>>();
            var json = TryGet("/api/tags", 10);
            if (json == null) return result;
            try
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                object modelsObj;
                if (root == null || !root.TryGetValue("models", out modelsObj)) return result;
                var arr = modelsObj as System.Collections.ArrayList;
                if (arr == null) return result;
                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    if (d == null) continue;
                    object name, size;
                    d.TryGetValue("name", out name);
                    d.TryGetValue("size", out size);
                    if (name is string)
                        result.Add(new KeyValuePair<string, long>((string)name,
                            size == null ? 0 : Convert.ToInt64(size)));
                }
            }
            catch { }
            return result;
        }

        // `ollama rm` handles shared-blob refcounting - never delete model
        // files by hand. Server must be up; no admin needed.
        public bool RemoveModel(string name)
        {
            var exe = ResolveExe();
            if (exe == null) return false;
            var psi = new ProcessStartInfo(exe, "rm " + name)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables["OLLAMA_HOST"] = "127.0.0.1:" + Port;
            psi.EnvironmentVariables["OLLAMA_MODELS"] = cfg.GetModelsDir();
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                errTask.GetAwaiter().GetResult();
                p.WaitForExit(60000);
                return p.HasExited && p.ExitCode == 0;
            }
        }

        // Downloads the pinned model via the CLI (server must be up; no admin).
        public bool Pull(Action<string> progressLine)
        {
            var exe = ResolveExe();
            if (exe == null) return false;
            var psi = new ProcessStartInfo(exe, "pull " + Constants.Model)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables["OLLAMA_HOST"] = "127.0.0.1:" + Port;   // client target
            psi.EnvironmentVariables["OLLAMA_MODELS"] = cfg.GetModelsDir();
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                string line;
                var seen = new HashSet<string>();
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    line = line.Trim();
                    // pull rewrites its progress line constantly; only surface changes
                    if (line.Length > 0 && seen.Add(line) && progressLine != null) progressLine(line);
                }
                errTask.GetAwaiter().GetResult();
                p.WaitForExit();
                return p.ExitCode == 0;
            }
        }

        private void DrainAsync(StreamReader reader)
        {
            var t = new System.Threading.Thread(delegate ()
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // Ollama's log is chatty; keep only lines that matter.
                        if (line.IndexOf("level=ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("panic", StringComparison.OrdinalIgnoreCase) >= 0)
                            log("ollama: " + line);
                    }
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static string SafeExitCode(Process p)
        {
            try { return p.ExitCode.ToString(); } catch { return "?"; }
        }
    }
}
