using System;
using System.Globalization;

namespace RealmChat
{
    // Tells an unattended host that the installed Ollama is not the pinned
    // one. Self-update moves the pin but never the Ollama install (that needs
    // an admin prompt), and the health card is only seen when someone opens
    // the window - so the daily silent run and the tray app toast it instead.
    //
    // Read-only by design: one `ollama -v`, a log line and at most one toast.
    // It never elevates, downloads or installs, and never throws - a failed
    // check must not fail the update run it rides on.
    public static class OllamaPinNotice
    {
        public const string Title = "Realm Chat: Ollama needs updating";

        // A hung `ollama -v` must not hold the single-instance lock - it is
        // killed after this long.
        private const int ProbeTimeoutMs = 20000;

        // Pure decision. At most one notice per pinned version per calendar
        // day; a new pin notifies again at once. An unknown installed version
        // (not installed, or unreadable `ollama -v` output) never notifies:
        // there is nothing truthful to say about "installed vs pinned".
        internal static bool ShouldNotify(string installed, string pinned,
                                          string lastPin, string lastDay, DateTime now)
        {
            if (installed == null) return false;
            if (HealthCheck.IsPinnedVersion(installed, pinned)) return false;
            return !(lastPin == pinned && lastDay == Day(now));
        }

        internal static string Day(DateTime now)
        {
            return now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        internal static string Message(string installed, string pinned)
        {
            return "Ollama " + installed + " is installed, but " + pinned + " is pinned. " +
                   "Open Realm Chat and click Fix problems (one admin prompt, about a 1 GB download). " +
                   "The chat keeps working meanwhile.";
        }

        // Worker-thread half: the installed version, or null when it can't be
        // determined (logged here).
        public static string Probe(OllamaController ollama, Action<string> log)
        {
            return Probe(ollama, log, ProbeTimeoutMs);
        }

        internal static string Probe(OllamaController ollama, Action<string> log, int timeoutMs)
        {
            try
            {
                bool timedOut;
                string installed = ollama.InstalledVersion(timeoutMs, out timedOut);
                if (timedOut)
                {
                    SafeLog(log, "Ollama version check: 'ollama -v' did not answer - stopped it, skipped");
                    return null;
                }
                if (installed == null)
                    SafeLog(log, ollama.ResolveExe() == null
                        ? "Ollama version check: Ollama is not installed (pinned " + Constants.OllamaVersion + ")"
                        : "Ollama version check: couldn't read the installed version (pinned " +
                          Constants.OllamaVersion + ")");
                return installed;
            }
            catch (Exception ex)
            {
                SafeLog(log, "Ollama version check failed: " + ex.Message);
                return null;
            }
        }

        // Decision half: logs every mismatch, and returns the toast text when
        // one is due - after recording it in cfg, which the CALLER saves.
        public static string Evaluate(AppConfig cfg, string installed, DateTime now, Action<string> log)
        {
            try
            {
                string pinned = Constants.OllamaVersion;
                if (installed == null || HealthCheck.IsPinnedVersion(installed, pinned)) return null;
                SafeLog(log, "Ollama version mismatch: " + installed + " installed, " + pinned +
                             " pinned - open Realm Chat and click Fix problems");
                if (!ShouldNotify(installed, pinned, cfg.ollama_notice_pin, cfg.ollama_notice_day, now))
                    return null;
                cfg.ollama_notice_pin = pinned;
                cfg.ollama_notice_day = Day(now);
                return Message(installed, pinned);
            }
            catch (Exception ex)
            {
                SafeLog(log, "Ollama version check failed: " + ex.Message);
                return null;
            }
        }

        // Both halves in one call, for the silent run.
        public static string Check(AppConfig cfg, Action<string> log)
        {
            try
            {
                var ollama = new OllamaController(cfg, log);
                return Evaluate(cfg, Probe(ollama, log), DateTime.Now, log);
            }
            catch (Exception ex)
            {
                SafeLog(log, "Ollama version check failed: " + ex.Message);
                return null;
            }
        }

        private static void SafeLog(Action<string> log, string line)
        {
            try { if (log != null) log(line); } catch { }
        }
    }
}
