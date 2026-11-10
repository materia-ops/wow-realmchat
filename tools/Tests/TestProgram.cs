// Test runner for RealmChat (see Tests.csproj: the app sources compile in, so
// internals are directly reachable). Three layers:
//   unit      - pure logic: sums parsing/verification, the pinned release key
//               (against fixtures signed by the real private key), subnet and
//               config helpers, the Scheduled Task XML, the pinned-Ollama
//               mismatch notice decision.
//   updater   - SelfUpdater.Fetch() against a local HttpListener serving the
//               fixtures, including the tampered-manifest refusal.
//   e2e       - OllamaController against tools/OllamaStub (start, pull, warm,
//               model list/rm, crash detection, stop).
// Env: REALMCHAT_TEST_STUB   = path to ollama-stub.exe   (default out\ollama-stub.exe)
//      REALMCHAT_TEST_FIXTURES = fixtures dir            (default tools\Tests\fixtures)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace RealmChat.Tests
{
    internal static class TestProgram
    {
        private static int failed;
        private static int passed;

        private static int Main()
        {
            // Before ANY app type is touched: point the app at a throwaway
            // home so nothing reads or writes %LOCALAPPDATA%\RealmChat.
            string home = Path.Combine(Path.GetTempPath(), "realmchat-tests-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("REALMCHAT_HOME", home);
            Directory.CreateDirectory(home);

            string fixtures = Environment.GetEnvironmentVariable("REALMCHAT_TEST_FIXTURES");
            if (string.IsNullOrEmpty(fixtures)) fixtures = Path.Combine("tools", "Tests", "fixtures");
            string stub = Environment.GetEnvironmentVariable("REALMCHAT_TEST_STUB");
            if (string.IsNullOrEmpty(stub)) stub = Path.Combine("out", "ollama-stub.exe");

            try
            {
                ParseSumsTests();
                VerifyBytesTests();
                ReleaseKeyTests(fixtures);
                SubnetTests();
                AppConfigTests();
                AutoResumeTests();
                OllamaPinNoticeTests();
                ScheduledTaskXmlTests();
                UpdaterFetchTests(fixtures);
                StubE2ETests(stub);
            }
            catch (Exception ex)
            {
                Fail("unhandled: " + ex);
            }
            finally
            {
                try { Directory.Delete(home, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? "ALL TESTS PASSED (" + passed + ")"
                : "FAILED: " + failed + " of " + (passed + failed));
            return failed == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string name)
        {
            if (ok) { passed++; Console.WriteLine("  ok  " + name); }
            else Fail(name);
        }

        private static void Fail(string name)
        {
            failed++;
            Console.WriteLine("FAIL  " + name);
        }

        private static void Throws(Action a, string fragment, string name)
        {
            try { a(); Fail(name + " (no exception)"); }
            catch (Exception ex)
            {
                Check(ex.Message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0,
                      name + " [" + ex.Message + "]");
            }
        }

        // --- unit: SHA256SUMS parsing + hash checks ---------------------------

        private static void ParseSumsTests()
        {
            Console.WriteLine("ParseSums:");
            string hexA = new string('a', 64), hexB = new string('b', 64);
            var sums = SelfUpdater.ParseSums(
                hexA + "  RealmChat.exe\n" +
                hexB + " *manifest.json\r\n" +          // binary-mode marker
                "not a sums line\n" +
                new string('c', 63) + "  short-hash-ignored\n" +
                "\n");
            Check(sums.Count == 2, "parses exactly the two valid lines");
            Check(sums["RealmChat.exe"] == hexA, "plain entry");
            Check(sums["manifest.json"] == hexB, "binary-mode '*' is stripped");
            Check(sums["REALMCHAT.EXE"] == hexA, "name lookup is case-insensitive");
        }

        private static void VerifyBytesTests()
        {
            Console.WriteLine("VerifyBytes:");
            byte[] data = Encoding.UTF8.GetBytes("payload");
            string hex;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hex = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();

            var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { { "payload.bin", hex } };
            SelfUpdater.VerifyBytes(data, "payload.bin", sums);
            Check(true, "matching hash passes");
            Throws(() => SelfUpdater.VerifyBytes(Encoding.UTF8.GetBytes("tampered"), "payload.bin", sums),
                   "checksum mismatch", "tampered bytes are refused");
            Throws(() => SelfUpdater.VerifyBytes(data, "missing.bin", sums),
                   "no entry", "unlisted asset is refused");
        }

        // --- unit: the pinned release key vs real signed fixtures --------------

        private static void ReleaseKeyTests(string fixtures)
        {
            Console.WriteLine("ReleaseKey (fixtures: " + fixtures + "):");
            byte[] sums = File.ReadAllBytes(Path.Combine(fixtures, "SHA256SUMS"));
            byte[] sig = File.ReadAllBytes(Path.Combine(fixtures, "SHA256SUMS.sig"));

            Check(ReleaseKey.Verify(sums, sig), "real signature verifies against the pinned key");

            byte[] tampered = (byte[])sums.Clone();
            tampered[0] ^= 0xff;
            Check(!ReleaseKey.Verify(tampered, sig), "one flipped byte fails verification");

            Check(!ReleaseKey.Verify(sums, new byte[] { 1, 2, 3 }), "garbage signature fails, no throw");
        }

        // --- unit: helpers ------------------------------------------------------

        private static void SubnetTests()
        {
            Console.WriteLine("SubnetHelper:");
            Check(SubnetHelper.LooksLikeCidr("10.0.0.0/24"), "plain CIDR accepted");
            Check(SubnetHelper.LooksLikeCidr("192.168.1.0/32"), "/32 accepted");
            Check(!SubnetHelper.LooksLikeCidr("10.0.0.0"), "bare IP rejected");
            Check(!SubnetHelper.LooksLikeCidr("10.0.0.0/33"), "prefix > 32 rejected");
            Check(!SubnetHelper.LooksLikeCidr("10.0.0.0/7"), "prefix < 8 rejected");
            Check(!SubnetHelper.LooksLikeCidr("fd00::/64"), "IPv6 rejected");
            Check(!SubnetHelper.LooksLikeCidr("wat/24"), "junk rejected");
        }

        private static void AppConfigTests()
        {
            Console.WriteLine("AppConfig:");
            var cfg = new AppConfig();
            Check(cfg.GetBaseUrl() == "https://github.com/materia-ops/wow-realmchat/releases/latest/download/",
                  "default base url points at the releases repo");
            cfg.base_url = "http://127.0.0.1:9/custom";
            Check(cfg.GetBaseUrl() == "http://127.0.0.1:9/custom/", "override gains a trailing slash");
            Check(cfg.GetPort() == Constants.DefaultPort, "default port");
            cfg.port = 12345;
            Check(cfg.GetPort() == 12345, "port override");
            cfg.server_subnets = " 10.0.0.0/24 ,, 192.168.1.0/24 ";
            var subnets = cfg.GetServerSubnets();
            Check(subnets.Count == 2 && subnets[0] == "10.0.0.0/24" && subnets[1] == "192.168.1.0/24",
                  "subnet list parsing trims and skips empties");
            cfg.AddOldModelsDir(@"C:\a");
            cfg.AddOldModelsDir(@"c:\A");
            Check(cfg.GetOldModelsDirs().Count == 1, "old model dirs dedupe case-insensitively");
            cfg.RemoveOldModelsDir(@"C:\a");
            Check(cfg.GetOldModelsDirs().Count == 0, "old model dir removal");
        }

        private static void AutoResumeTests()
        {
            Console.WriteLine("Program.ShouldAutoResume:");
            const uint min = 60 * 1000;
            var cfg = new AppConfig { auto_resume = true, chat_was_running = true };
            Check(Program.ShouldAutoResume(cfg, 2 * min), "resumes shortly after boot");
            Check(!Program.ShouldAutoResume(cfg, 16 * min), "daily check outside the boot window never resumes");
            Check(!Program.ShouldAutoResume(new AppConfig { chat_was_running = true }, 2 * min),
                  "opt-out (default) never resumes");
            Check(!Program.ShouldAutoResume(new AppConfig { auto_resume = true }, 2 * min),
                  "chat that was stopped stays stopped");
        }

        private static void OllamaPinNoticeTests()
        {
            Console.WriteLine("OllamaPinNotice:");
            var noon = new DateTime(2026, 3, 10, 12, 0, 0);
            string today = OllamaPinNotice.Day(noon);
            Check(today == "2026-03-10", "day key is the local calendar date");

            Check(!OllamaPinNotice.ShouldNotify("1.2.3", "1.2.3", null, null, noon),
                  "installed == pinned never notifies");
            Check(OllamaPinNotice.ShouldNotify("1.2.2", "1.2.3", null, null, noon),
                  "first mismatch notifies");
            Check(OllamaPinNotice.ShouldNotify("1.2.4", "1.2.3", null, null, noon),
                  "a NEWER install is a mismatch too (exact pin)");
            Check(!OllamaPinNotice.ShouldNotify("1.2.2", "1.2.3", "1.2.3", today, noon),
                  "same pin already notified today stays quiet");
            Check(!OllamaPinNotice.ShouldNotify("1.2.2", "1.2.3", "1.2.3", today, noon.AddHours(11)),
                  "still quiet later the same day");
            Check(OllamaPinNotice.ShouldNotify("1.2.2", "1.2.3", "1.2.3", today, noon.AddDays(1)),
                  "same pin notifies again the next day");
            Check(OllamaPinNotice.ShouldNotify("1.2.2", "1.2.4", "1.2.3", today, noon),
                  "a NEW pin notifies even though the old one was notified today");
            Check(!OllamaPinNotice.ShouldNotify(null, "1.2.3", null, null, noon),
                  "unknown installed version (not installed / unreadable) never notifies");
            Check(!HealthCheck.IsPinnedVersion(null, "1.2.3"), "no version is not the pinned version");

            string msg = OllamaPinNotice.Message("1.2.2", "1.2.3");
            Check(msg.Contains("1.2.2") && msg.Contains("1.2.3") && msg.Contains("Fix problems"),
                  "message names installed, pinned and the fix");
            // Balloon limits: 63 chars of title, 255 of text - including the
            // "Now running version <12 hex>. " prefix of the post-update toast.
            Check(OllamaPinNotice.Title.Length <= 63 &&
                  ("Now running version 0123456789ab. " +
                   OllamaPinNotice.Message("100.200.300", "100.200.301")).Length <= 255,
                  "toast fits the balloon limits");

            // Evaluate: logs EVERY mismatch, toasts once, records the state.
            var cfg = new AppConfig();
            var lines = new List<string>();
            string stale = "0.0.1";     // never the pin
            string note = OllamaPinNotice.Evaluate(cfg, stale, noon, lines.Add);
            Check(note != null && note.Contains(stale) && note.Contains(Constants.OllamaVersion),
                  "Evaluate returns the toast text on a first mismatch");
            Check(cfg.ollama_notice_pin == Constants.OllamaVersion && cfg.ollama_notice_day == today,
                  "Evaluate records pin + day");
            Check(OllamaPinNotice.Evaluate(cfg, stale, noon.AddHours(1), lines.Add) == null,
                  "Evaluate stays quiet the second time that day");
            Check(lines.Count == 2 && lines[1].Contains("mismatch") && lines[1].Contains(stale),
                  "the mismatch is logged on every check, toast or not");
            Check(OllamaPinNotice.Evaluate(cfg, stale, noon.AddDays(1), lines.Add) != null &&
                  cfg.ollama_notice_day == OllamaPinNotice.Day(noon.AddDays(1)),
                  "Evaluate notifies again the next day");

            var clean = new AppConfig();
            lines.Clear();
            Check(OllamaPinNotice.Evaluate(clean, Constants.OllamaVersion, noon, lines.Add) == null &&
                  OllamaPinNotice.Evaluate(clean, null, noon, lines.Add) == null &&
                  lines.Count == 0 && clean.ollama_notice_pin == null,
                  "match / unknown: no toast, no log line, no state");
            Check(OllamaPinNotice.Evaluate(null, stale, noon, delegate { throw new Exception("boom"); }) == null,
                  "Evaluate swallows its own failures (null config, throwing logger)");
        }

        private static void ScheduledTaskXmlTests()
        {
            Console.WriteLine("ScheduledTask.BuildXml:");
            string xml = ScheduledTask.BuildXml(@"PC\user & co", "2026-01-01T12:00:00",
                                                @"C:\Tools & Games\RealmChat.exe");
            Check(xml.Contains("PC\\user &amp; co"), "user is XML-escaped");
            Check(xml.Contains(@"C:\Tools &amp; Games\RealmChat.exe"), "exe path is XML-escaped");
            Check(xml.Contains("<LogonTrigger>") && xml.Contains("<CalendarTrigger>"),
                  "logon + daily triggers present");
            Check(xml.Contains("<Arguments>--silent</Arguments>"), "task runs --silent");
        }

        // --- updater: Fetch() against a local server ---------------------------

        private static void UpdaterFetchTests(string fixtures)
        {
            Console.WriteLine("SelfUpdater.Fetch:");
            byte[] sums = File.ReadAllBytes(Path.Combine(fixtures, "SHA256SUMS"));
            byte[] sig = File.ReadAllBytes(Path.Combine(fixtures, "SHA256SUMS.sig"));
            byte[] manifest = File.ReadAllBytes(Path.Combine(fixtures, "manifest.json"));

            using (var server = new FixtureServer(sums, sig, manifest))
            {
                var cfg = new AppConfig { base_url = server.BaseUrl };
                var release = new SelfUpdater(cfg, null).Fetch();
                Check(release.Manifest.tag == "v9.9.9", "manifest tag round-trips");
                Check(release.Manifest.updaterVersion == "fixture0000", "updaterVersion round-trips");
                Check(release.Sums.ContainsKey("RealmChat.exe"), "exe entry present in verified sums");

                server.TamperManifest = true;
                Throws(() => new SelfUpdater(cfg, null).Fetch(),
                       "checksum mismatch", "tampered manifest is refused");
                server.TamperManifest = false;

                server.TamperSums = true;
                Throws(() => new SelfUpdater(cfg, null).Fetch(),
                       "signature is invalid", "tampered SHA256SUMS is refused outright");
            }
        }

        private sealed class FixtureServer : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            private readonly byte[] sums, sig, manifest;
            public volatile bool TamperManifest;
            public volatile bool TamperSums;
            public string BaseUrl { get; private set; }

            public FixtureServer(byte[] sums, byte[] sig, byte[] manifest)
            {
                this.sums = sums; this.sig = sig; this.manifest = manifest;
                int port = FreePort();
                // localhost, not 127.0.0.1: non-admin HttpListener may listen
                // on the former without a URL ACL.
                BaseUrl = "http://localhost:" + port + "/";
                listener.Prefixes.Add(BaseUrl);
                listener.Start();
                var t = new Thread(Serve) { IsBackground = true };
                t.Start();
            }

            private void Serve()
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var ctx = listener.GetContext();
                        string name = ctx.Request.Url.AbsolutePath.TrimStart('/');
                        byte[] body =
                            name == "SHA256SUMS" ? (TamperSums ? Tamper(sums) : sums) :
                            name == "SHA256SUMS.sig" ? sig :
                            name == "manifest.json" ? (TamperManifest ? Tamper(manifest) : manifest) :
                            null;
                        if (body == null) ctx.Response.StatusCode = 404;
                        else ctx.Response.OutputStream.Write(body, 0, body.Length);
                        ctx.Response.Close();
                    }
                }
                catch { }
            }

            private static byte[] Tamper(byte[] b)
            {
                var t = (byte[])b.Clone();
                t[t.Length - 2] ^= 0xff;
                return t;
            }

            public void Dispose()
            {
                try { listener.Stop(); listener.Close(); } catch { }
            }
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // --- e2e: OllamaController against the stub -----------------------------

        private static bool StubProcessesGone()
        {
            for (int i = 0; i < 50; i++)
            {
                var alive = System.Diagnostics.Process.GetProcessesByName("ollama-stub");
                foreach (var p in alive) p.Dispose();
                if (alive.Length == 0) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private static void StubE2ETests(string stub)
        {
            Console.WriteLine("OllamaController vs stub (" + stub + "):");
            if (!File.Exists(stub))
            {
                Fail("stub not found - compile tools/OllamaStub first (see ci.yml)");
                return;
            }

            string models = Path.Combine(Path.GetTempPath(), "realmchat-stub-models-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(models);
            Environment.SetEnvironmentVariable("STUB_VERSION", Constants.OllamaVersion);

            var cfg = new AppConfig
            {
                ollama_exe = Path.GetFullPath(stub),
                port = FreePort(),
                models_dir = models,
            };
            var log = new List<string>();
            var ollama = new OllamaController(cfg, log.Add);

            try
            {
                Check(ollama.InstalledVersion() == Constants.OllamaVersion,
                      "InstalledVersion parses the stub's -v output");

                // The mismatch notice end to end, as the silent run calls it.
                try
                {
                    int before = log.Count;
                    Check(OllamaPinNotice.Check(cfg, log.Add) == null && log.Count == before &&
                          cfg.ollama_notice_pin == null,
                          "pin notice: pinned version installed - silent");

                    Environment.SetEnvironmentVariable("STUB_VERSION", "0.0.1");
                    string note = OllamaPinNotice.Check(cfg, log.Add);
                    Check(note != null && note.Contains("0.0.1") && note.Contains(Constants.OllamaVersion),
                          "pin notice: other version installed - toast text names both");
                    Check(cfg.ollama_notice_pin == Constants.OllamaVersion,
                          "pin notice: state recorded for the caller to save");
                    before = log.Count;
                    Check(OllamaPinNotice.Check(cfg, log.Add) == null && log.Count == before + 1,
                          "pin notice: second check the same day only logs");

                    Environment.SetEnvironmentVariable("STUB_VERSION", "unknown build");
                    var fresh = new AppConfig { ollama_exe = cfg.ollama_exe };
                    before = log.Count;
                    Check(OllamaPinNotice.Check(fresh, log.Add) == null && fresh.ollama_notice_pin == null &&
                          log.Count == before + 1 && log[before].Contains("couldn't read"),
                          "pin notice: unparseable -v output - logged, no toast, no state");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("STUB_VERSION", Constants.OllamaVersion);
                }

                // A hung `ollama -v` (no server is running yet, so every stub
                // process alive here is a probe child).
                try
                {
                    bool timedOut;
                    Check(ollama.InstalledVersion(20000, out timedOut) == Constants.OllamaVersion && !timedOut,
                          "timeout probe: same answer as InstalledVersion when the child exits");

                    Environment.SetEnvironmentVariable("STUB_HANG", "1");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    Check(ollama.InstalledVersion(1500, out timedOut) == null && timedOut,
                          "timeout probe: a hung child reports a timeout, not a version");
                    Check(clock.ElapsedMilliseconds < 15000, "timeout probe: returns near the timeout");
                    Check(StubProcessesGone(), "timeout probe: the hung child was killed");

                    var hung = new AppConfig { ollama_exe = cfg.ollama_exe };
                    int before = log.Count;
                    Check(OllamaPinNotice.Evaluate(hung, OllamaPinNotice.Probe(ollama, log.Add, 1500),
                                                   DateTime.Now, log.Add) == null &&
                          hung.ollama_notice_pin == null &&
                          log.Count == before + 1 && log[before].Contains("did not answer"),
                          "pin notice: hung -v - logged, no toast, no state");
                    Check(StubProcessesGone(), "pin notice: no child process left behind");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("STUB_HANG", null);
                }

                ollama.Start();
                Check(ollama.WaitUntilUp(20), "server answers /api/version after Start");
                Check(!ollama.ModelPresent(), "model absent before pull");
                Check(ollama.Pull(null), "pull succeeds");
                Check(ollama.ModelPresent(), "model present after pull");
                Check(!ollama.ModelLoaded(), "model not loaded before warm");
                Check(ollama.Warm(), "warm-up generate succeeds");
                Check(ollama.ModelLoaded(), "model loaded after warm");

                File.WriteAllText(Path.Combine(models, "extra.present"), "junk:latest");
                Check(ollama.ListModels().Count == 2, "extra model shows in the list");
                Check(ollama.RemoveModel("junk:latest"), "ollama rm removes the extra model");
                Check(ollama.ListModels().Count == 1, "only the realm's model remains");

                // Crash detection: kill the stub out from under the controller.
                var exited = new ManualResetEvent(false);
                ollama.ServerExited += () => exited.Set();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("ollama-stub"))
                    using (p) p.Kill();
                Check(exited.WaitOne(10000), "ServerExited fires when the server dies");

                ollama.Start();
                Check(ollama.WaitUntilUp(20), "restart after crash works");
                ollama.Stop();
                Thread.Sleep(500);
                Check(!ollama.IsUp(), "server is down after Stop");
            }
            finally
            {
                try { ollama.Stop(); } catch { }
                try { Directory.Delete(models, true); } catch { }
            }
        }
    }
}
