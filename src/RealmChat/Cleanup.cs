using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace RealmChat
{
    public enum CleanupKind { StaleDir, UnusedModel }

    public class CleanupItem
    {
        public CleanupKind Kind;
        public string Target;      // directory path, or model name:tag
        public string Why;
        public long Bytes;

        public string Describe()
        {
            return Target + "  (" + Cleanup.Gb(Bytes) + ")  — " + Why;
        }
    }

    // Finds and (on explicit confirmation only) removes leftover model data:
    // the old per-user store, previous custom store locations recorded when
    // the folder was changed in Settings, and models in the ACTIVE store that
    // aren't the pinned one. Never part of Fix, never run silently, and the
    // active store (or any parent of it) is untouchable by construction.
    public static class Cleanup
    {
        public static List<CleanupItem> Scan(AppConfig cfg, OllamaController ollama)
        {
            var items = new List<CleanupItem>();
            string active = Norm(cfg.GetModelsDir());

            // 1. Ollama's default per-user store, superseded by the shared one.
            string userStore = Norm(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".ollama\models"));
            AddStaleDir(items, userStore, active, "old per-user model store");

            // 2. Previous custom locations recorded at Settings changes.
            foreach (var old in cfg.GetOldModelsDirs())
                AddStaleDir(items, Norm(old), active, "previous model folder");

            // 3. Non-pinned models in the active store (server must be up:
            //    removal goes through `ollama rm`, which handles shared-blob
            //    refcounting - raw file deletion could corrupt the store).
            if (ollama.IsUp())
            {
                foreach (var m in ollama.ListModels())
                {
                    if (string.Equals(m.Key, Constants.Model, StringComparison.OrdinalIgnoreCase))
                        continue;
                    items.Add(new CleanupItem
                    {
                        Kind = CleanupKind.UnusedModel,
                        Target = m.Key,
                        Why = "model not used by the realm",
                        Bytes = m.Value,
                    });
                }
            }
            return items;
        }

        private static void AddStaleDir(List<CleanupItem> items, string dir, string active, string why)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            // Never the active store, never a parent or child of it.
            if (dir.Equals(active, StringComparison.OrdinalIgnoreCase)) return;
            if (active.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) return;
            if (dir.StartsWith(active + "\\", StringComparison.OrdinalIgnoreCase)) return;
            if (items.Any(i => i.Kind == CleanupKind.StaleDir &&
                               i.Target.Equals(dir, StringComparison.OrdinalIgnoreCase))) return;
            long size = DirSize(dir);
            if (size <= 0) return;   // empty leftovers aren't worth a prompt
            items.Add(new CleanupItem { Kind = CleanupKind.StaleDir, Target = dir, Why = why, Bytes = size });
        }

        // Returns the surviving items (empty = everything cleaned).
        public static List<CleanupItem> Delete(AppConfig cfg, OllamaController ollama,
            List<CleanupItem> items, Action<string> log)
        {
            var failed = new List<CleanupItem>();
            foreach (var item in items)
            {
                try
                {
                    if (item.Kind == CleanupKind.StaleDir)
                    {
                        DeleteDirWithRetry(item.Target);
                        cfg.RemoveOldModelsDir(item.Target);
                        log("Cleaned up " + item.Target + " (" + Gb(item.Bytes) + " freed).");
                    }
                    else
                    {
                        if (!ollama.RemoveModel(item.Target))
                            throw new Exception("ollama rm failed");
                        log("Removed unused model " + item.Target + " (" + Gb(item.Bytes) + " freed).");
                    }
                }
                catch (Exception ex)
                {
                    log("Couldn't clean " + item.Target + ": " + ex.Message);
                    failed.Add(item);
                }
            }
            cfg.Save();
            return failed;
        }

        public static string Gb(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB";
            return (bytes / (1024.0 * 1024)).ToString("0") + " MB";
        }

        private static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
            catch { return p; }
        }

        private static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        private static void DeleteDirWithRetry(string dir)
        {
            for (int i = 0; ; i++)
            {
                try { Directory.Delete(dir, true); return; }
                catch
                {
                    if (i >= 3) throw;
                    Thread.Sleep(400);
                }
            }
        }
    }
}
