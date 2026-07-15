using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace RealmChat
{
    // Shape of the manifest.json asset published with every release.
    public class Manifest
    {
        public string tag { get; set; }
        public string updaterVersion { get; set; }
    }

    // A release whose SHA256SUMS carried a valid signature from the pinned
    // release key, and whose manifest.json matched its entry in those sums.
    public class VerifiedRelease
    {
        public Manifest Manifest;
        public Dictionary<string, string> Sums;
    }

    // Self-update engine, same contract as the ATT updater it was vendored
    // from: poll releases/latest/download/manifest.json (no API, no auth),
    // verify the new exe against the signed SHA256SUMS, rename the running
    // exe aside, move the new one in, and have the caller relaunch.
    public class SelfUpdater
    {
        public static readonly HttpClient Http = CreateClient();

        private readonly AppConfig cfg;
        private readonly Action<string> log;

        public SelfUpdater(AppConfig cfg, Action<string> log)
        {
            this.cfg = cfg;
            this.log = log ?? delegate { };
        }

        private static HttpClient CreateClient()
        {
            var c = new HttpClient();
            c.Timeout = TimeSpan.FromMinutes(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WoW-RealmChat");
            return c;
        }

        // The only way release metadata enters the process: SHA256SUMS must
        // carry a valid signature from the pinned release key (SHA256SUMS.sig)
        // and manifest.json must hash-match its entry in those sums. Nothing
        // downstream (version compare, exe download) sees unverified data.
        public VerifiedRelease Fetch()
        {
            byte[] sumsBytes = GetBytes(cfg.GetBaseUrl() + "SHA256SUMS");
            byte[] sig = GetBytes(cfg.GetBaseUrl() + "SHA256SUMS.sig");
            if (Program.Version == "dev")
                log("dev build - SKIPPING release signature verification");
            else if (!ReleaseKey.Verify(sumsBytes, sig))
                throw new Exception("SHA256SUMS signature is invalid - refusing to proceed");

            var sums = ParseSums(System.Text.Encoding.UTF8.GetString(sumsBytes));
            byte[] manifestBytes = GetBytes(cfg.GetBaseUrl() + "manifest.json");
            VerifyBytes(manifestBytes, "manifest.json", sums);

            var m = new JavaScriptSerializer()
                .Deserialize<Manifest>(System.Text.Encoding.UTF8.GetString(manifestBytes));
            if (m == null || string.IsNullOrEmpty(m.tag))
                throw new Exception("release manifest is malformed");
            return new VerifiedRelease { Manifest = m, Sums = sums };
        }

        // Returns true when the installed exe now carries a newer version and
        // the caller must relaunch Program.InstalledExe and exit - whether the
        // running process WAS the installed copy (rename-swap) or a stray one
        // (refresh + hand off).
        public bool Run()
        {
            string mine = Program.Version;
            if (mine == "dev")
            {
                log("dev build - skipping self-update");
                return false;
            }

            var release = Fetch();
            var manifest = release.Manifest;
            if (string.IsNullOrEmpty(manifest.updaterVersion) || manifest.updaterVersion == mine)
            {
                log("Up to date (" + manifest.tag + ").");
                return false;
            }

            log("Version " + manifest.updaterVersion + " available (running " + mine + ") - updating...");
            var sums = release.Sums;

            var tmp = Path.Combine(Path.GetTempPath(), "realmchat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string fresh = Path.Combine(tmp, Program.ExeName);
                Download(cfg.GetBaseUrl() + Program.ExeName, fresh, null);
                Verify(fresh, Program.ExeName, sums);

                string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                string installed = Path.GetFullPath(Program.InstalledExe);

                if (string.Equals(self, installed, StringComparison.OrdinalIgnoreCase))
                {
                    // A running exe can't be overwritten, but it CAN be renamed.
                    string old = installed + ".old";
                    Program.TryDelete(old);
                    File.Move(installed, old);
                    try { File.Move(fresh, installed); }
                    catch { File.Move(old, installed); throw; }
                    log("Updated - restarting.");
                    return true;
                }

                // Running from a stray copy (Desktop/Downloads): refresh the
                // installed exe and hand off to it - continuing this run would
                // leave the user watching OLD code that just claimed to update.
                Program.TryDelete(installed);
                File.Move(fresh, installed);
                log("Updated the installed copy - switching to it.");
                return true;
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        // --- HTTP + integrity (shared with the Ollama installer download) -----

        public byte[] GetBytes(string url)
        {
            using (var resp = Http.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)resp.StatusCode + " fetching " + url);
                return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
        }

        public static void Download(string url, string dest, Action<int> progressPercent)
        {
            using (var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                                  .GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)resp.StatusCode + " fetching " + url);
                long total = resp.Content.Headers.ContentLength ?? -1;
                using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var f = File.Create(dest))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int lastPct = -1, n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        f.Write(buf, 0, n);
                        done += n;
                        if (progressPercent != null && total > 0)
                        {
                            int pct = (int)(done * 100 / total);
                            if (pct >= lastPct + 10) { lastPct = pct; progressPercent(pct); }
                        }
                    }
                }
            }
        }

        // "sha256sum" output: "<64 hex>  <name>" per line ("*name" = binary mode).
        public static Dictionary<string, string> ParseSums(string text)
        {
            var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0].Length != 64) continue;
                sums[parts[1].TrimStart('*')] = parts[0].ToLowerInvariant();
            }
            return sums;
        }

        public static void Verify(string file, string name, Dictionary<string, string> sums)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(file))
                CheckHash(sha.ComputeHash(f), name, sums);
        }

        public static void VerifyBytes(byte[] data, string name, Dictionary<string, string> sums)
        {
            using (var sha = SHA256.Create())
                CheckHash(sha.ComputeHash(data), name, sums);
        }

        private static void CheckHash(byte[] hash, string name, Dictionary<string, string> sums)
        {
            string want;
            if (!sums.TryGetValue(name, out want))
                throw new Exception("SHA256SUMS has no entry for " + name);
            string have = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            if (have != want)
                throw new Exception("checksum mismatch for " + name + " - refusing to install");
        }
    }
}
