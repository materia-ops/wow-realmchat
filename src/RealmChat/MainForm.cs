using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RealmChat
{
    // The control surface: one big Start/Stop, a status pill, the health
    // checklist with a single Fix button, and an activity log. Closing while
    // the chat runs minimizes to the tray so an accidental click can't
    // silence the bots.
    public class MainForm : ThemedForm
    {
        private readonly AppConfig cfg;
        private readonly OllamaController ollama;

        private readonly Label lblTitle = new Label { AutoSize = true };
        private readonly MutedLabel lblSubtitle = new MutedLabel();
        private readonly ThemedButton btnTheme = new ThemedButton();
        private readonly StatusPill pill = new StatusPill();
        private readonly ThemedButton btnToggle = new ThemedButton { Primary = true };
        private readonly BusyBar bar = new BusyBar();
        private readonly MutedLabel capHealth = new MutedLabel { Text = "Health" };
        private readonly Label[] healthRows;
        private readonly ThemedButton btnFix = new ThemedButton();
        private readonly ThemedButton btnSettings = new ThemedButton();
        private readonly ThemedButton btnCleanup = new ThemedButton();
        private readonly CheckBox chkFwWatch = new CheckBox { AutoSize = true };
        private readonly MutedLabel capActivity = new MutedLabel { Text = "Activity" };
        private readonly LogBox log = new LogBox();
        private readonly MutedLabel lblFooter = new MutedLabel { AutoSize = false, AutoEllipsis = true };
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Timer poll = new Timer { Interval = 5000 };
        private readonly ToolTip tips = new ToolTip();

        private ChatState state = ChatState.Stopped;
        private List<HealthItem> health = new List<HealthItem>();
        private bool busy;
        private bool exiting;
        private List<CleanupItem> cleanupItems = new List<CleanupItem>();
        private int pollTicks;
        private bool fwAlerted;        // one toast per firewall incident
        private bool fwCheckRunning;
        private DateTime? fwPostStart; // schedules the just-after-start check

        public MainForm(AppConfig cfg)
        {
            this.cfg = cfg;
            ollama = new OllamaController(cfg, Say);
            ollama.ServerExited += OnServerExited;

            Text = "Realm Chat";
            Icon = AppIcons.Neutral;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(584, 520);
            MinimumSize = Size;   // grow-only

            lblTitle.Text = "Realm Chat";
            lblTitle.Font = new Font(Font.FontFamily, Font.Size * 1.35f, FontStyle.Bold);
            lblTitle.Location = new Point(16, 14);

            btnTheme.Size = new Size(116, 28);
            btnTheme.Location = new Point(ClientSize.Width - 16 - btnTheme.Width, 14);
            btnTheme.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnTheme.Click += OnThemeClick;
            tips.SetToolTip(btnTheme, "Cycle Auto / Light / Dark. Auto follows Windows.");

            lblSubtitle.Location = new Point(17, 42);
            lblSubtitle.Text = "The WoW bots' chat brain · " + Program.Version;

            pill.Location = new Point(16, 66);
            pill.SetStatus(StatusKind.Neutral, "Checking…");

            btnToggle.Text = "Start chat";
            btnToggle.Location = new Point(16, 98);
            btnToggle.Size = new Size(ClientSize.Width - 32, 40);
            btnToggle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnToggle.Click += OnToggleClick;
            AcceptButton = btnToggle;

            bar.Location = new Point(16, 146);
            bar.Size = new Size(ClientSize.Width - 32, 4);
            bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            capHealth.Location = new Point(17, 158);
            healthRows = new Label[4];
            for (int i = 0; i < healthRows.Length; i++)
            {
                healthRows[i] = new Label
                {
                    AutoSize = false,
                    AutoEllipsis = true,
                    Location = new Point(16, 178 + i * 19),
                    Size = new Size(ClientSize.Width - 240, 17),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                };
            }

            btnFix.Text = "Fix problems…";
            btnFix.Size = new Size(118, 28);
            btnFix.Location = new Point(ClientSize.Width - 16 - btnFix.Width, 172);
            btnFix.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFix.Visible = false;
            btnFix.Click += OnFixClick;

            btnSettings.Text = "Settings…";
            btnSettings.Size = new Size(118, 28);
            btnSettings.Location = new Point(ClientSize.Width - 16 - btnSettings.Width, 206);
            btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSettings.Click += OnSettingsClick;

            btnCleanup.Text = "Clean up…";
            btnCleanup.Size = new Size(118, 28);
            btnCleanup.Location = new Point(ClientSize.Width - 16 - btnCleanup.Width, 240);
            btnCleanup.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCleanup.Visible = false;
            btnCleanup.Click += OnCleanupClick;
            tips.SetToolTip(btnCleanup,
                "Frees disk space: old model folders and models the realm doesn't use.\n" +
                "Shows exactly what will be deleted and asks first.");

            // The Ollama app likes to (re)write firewall rules behind our back,
            // which silently cuts the game server off - so watch while running.
            chkFwWatch.Text = "Watch the firewall while the chat runs";
            chkFwWatch.Location = new Point(16, 256);
            chkFwWatch.Checked = !cfg.disable_firewall_watch;
            chkFwWatch.CheckedChanged += delegate
            {
                cfg.disable_firewall_watch = !chkFwWatch.Checked;
                cfg.Save();
                fwAlerted = false;
                Say(chkFwWatch.Checked ? "Firewall watch on." : "Firewall watch off.");
            };
            tips.SetToolTip(chkFwWatch,
                "Re-checks the firewall every minute while the chat runs and warns if an\n" +
                "Ollama-created rule breaks the game server's access to the model.");

            capActivity.Location = new Point(17, 278);
            log.Location = new Point(16, 298);
            log.Size = new Size(ClientSize.Width - 32, 182);
            log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            log.PlaceholderText = "No activity yet — starts, stops, and updates are logged here.";

            lblFooter.Location = new Point(16, ClientSize.Height - 28);
            lblFooter.Size = new Size(ClientSize.Width - 32, 16);
            lblFooter.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange(new Control[] { lblTitle, btnTheme, lblSubtitle, pill,
                btnToggle, bar, capHealth });
            Controls.AddRange(healthRows);
            Controls.AddRange(new Control[] { btnFix, btnSettings, btnCleanup, chkFwWatch, capActivity, log, lblFooter });

            // Tray: state at a glance, restore on double-click, control menu.
            tray.Icon = AppIcons.Neutral;
            tray.Text = "Realm Chat";
            tray.Visible = true;
            tray.DoubleClick += delegate { Restore(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Realm Chat", null, delegate { Restore(); });
            menu.Items.Add("Start / stop chat", null, delegate { Restore(); OnToggleClick(null, EventArgs.Empty); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit (stops the chat)", null, delegate { exiting = true; Close(); });
            tray.ContextMenuStrip = menu;

            poll.Tick += delegate
            {
                ReconcileState();
                pollTicks++;
                bool running = state == ChatState.Ready || state == ChatState.Warming;
                // Every minute while running - plus once shortly after start,
                // which is exactly when Windows/the Ollama app injects rules.
                if (running && (pollTicks % 12 == 0 ||
                    (fwPostStart.HasValue && (DateTime.UtcNow - fwPostStart.Value).TotalSeconds >= 10)))
                {
                    fwPostStart = null;
                    CheckFirewallAsync();
                }
            };
            poll.Start();

            Shown += delegate { OnOpened(); };
            FormClosing += OnClosingForm;

            RefreshFooter();
        }

        protected override void OnThemeApplied(Palette p)
        {
            btnTheme.Text = "Theme: " + Theme.Mode;
            PaintHealth();
        }

        private void OnThemeClick(object sender, EventArgs e)
        {
            var next = Theme.Mode == ThemeMode.Auto ? ThemeMode.Light
                     : Theme.Mode == ThemeMode.Light ? ThemeMode.Dark
                     : ThemeMode.Auto;
            cfg.theme = Theme.Serialize(next);
            cfg.Save();
            Theme.Set(next);
        }

        // --- logging / status -----------------------------------------------

        private void Say(string line)
        {
            Logger.Log(line);
            if (IsHandleCreated && InvokeRequired) BeginInvoke((Action)(() => log.Append(line)));
            else log.Append(line);
        }

        private void SetState(ChatState s)
        {
            state = s;
            switch (s)
            {
                case ChatState.Stopped:
                    pill.SetStatus(StatusKind.Neutral, "Chat stopped");
                    btnToggle.Text = "Start chat";
                    break;
                case ChatState.Starting:
                    pill.SetStatus(StatusKind.Busy, "Starting…");
                    break;
                case ChatState.Warming:
                    pill.SetStatus(StatusKind.Busy, "Warming the model…");
                    break;
                case ChatState.Ready:
                    pill.SetStatus(StatusKind.Success, "Chat ready — the bots can talk");
                    btnToggle.Text = "Stop chat";
                    break;
                case ChatState.Crashed:
                    pill.SetStatus(StatusKind.Error, "Chat stopped unexpectedly");
                    btnToggle.Text = "Start chat";
                    break;
            }
            bool running = s == ChatState.Ready || s == ChatState.Warming || s == ChatState.Starting;
            tray.Icon = running ? AppIcons.Running : AppIcons.Neutral;
            Icon = tray.Icon;
            tray.Text = running ? "Realm Chat — running" : "Realm Chat — stopped";
        }

        private void RefreshFooter()
        {
            if (string.IsNullOrEmpty(cfg.last_result)) { lblFooter.Text = ""; return; }
            string ago = Ago(cfg.last_check_utc);
            lblFooter.Text = "Last update check: " + cfg.last_result + (ago == null ? "" : " · " + ago);
        }

        private static string Ago(string isoUtc)
        {
            DateTime t;
            if (!DateTime.TryParse(isoUtc, null, DateTimeStyles.RoundtripKind, out t)) return null;
            var span = DateTime.UtcNow - t.ToUniversalTime();
            if (span.TotalMinutes < 2) return "just now";
            if (span.TotalHours < 2) return (int)span.TotalMinutes + " minutes ago";
            if (span.TotalDays < 2) return (int)span.TotalHours + " hours ago";
            return (int)span.TotalDays + " days ago";
        }

        // --- open sequence -----------------------------------------------------

        private void OnOpened()
        {
            busy = true;
            bar.Active = true;
            Task.Run(delegate
            {
                // 1. Self-update (GUI opens are a natural update point).
                bool relaunch = false;
                try { relaunch = new SelfUpdater(cfg, Say).Run(); }
                catch (Exception ex) { Say("Update check failed: " + ex.Message); }

                // 2. Health + current server state + reclaimable leftovers.
                var h = HealthCheck.RunAll(cfg, ollama);
                var c = Cleanup.Scan(cfg, ollama);
                bool up = ollama.IsUp();
                bool loaded = up && ollama.ModelLoaded();

                BeginInvoke((Action)(delegate
                {
                    if (relaunch)
                    {
                        Say("Restarting after update…");
                        Process.Start(Program.InstalledExe, "--postupdate");
                        exiting = true;
                        Close();
                        return;
                    }
                    health = h;
                    cleanupItems = c;
                    PaintHealth();
                    RefreshCleanupButton();
                    if (up)
                    {
                        Say("An Ollama server is already running — taking it over.");
                        SetState(loaded ? ChatState.Ready : ChatState.Warming);
                        if (!loaded) WarmAsync();
                    }
                    else
                    {
                        SetState(ChatState.Stopped);
                    }
                    busy = false;
                    bar.Active = false;
                }));
            });
        }

        // --- health ------------------------------------------------------------

        private void PaintHealth()
        {
            var p = Theme.Current;
            for (int i = 0; i < healthRows.Length; i++)
            {
                if (i < health.Count)
                {
                    var item = health[i];
                    healthRows[i].Text = (item.Ok ? "✓  " : "⚠  ") + item.Name + " — " + item.Detail;
                    healthRows[i].ForeColor = item.Ok ? p.Success : p.Warning;
                    healthRows[i].Visible = true;
                }
                else
                {
                    healthRows[i].Visible = false;
                }
            }
            btnFix.Visible = health.Any(x => !x.Ok && x.FixFlag != null);
        }

        // Firewall watcher: re-runs the deep firewall status while the chat is
        // running, updates the health row, and alerts once per incident. The
        // Ollama app rewriting rules is the #1 way the server silently loses
        // access to a perfectly healthy local model.
        private void CheckFirewallAsync()
        {
            if (fwCheckRunning || cfg.disable_firewall_watch) return;
            if (state != ChatState.Ready && state != ChatState.Warming) return;
            fwCheckRunning = true;
            Task.Run(delegate
            {
                string detail;
                bool ok;
                try { ok = HealthCheck.FirewallStatus(cfg, out detail); }
                catch (Exception ex) { ok = false; detail = "check failed: " + ex.Message; }
                try
                {
                    BeginInvoke((Action)(delegate
                    {
                        fwCheckRunning = false;
                        var row = health.FirstOrDefault(x => x.FixFlag == "firewall");
                        if (row != null) { row.Ok = ok; row.Detail = detail; PaintHealth(); }
                        if (!ok && !fwAlerted)
                        {
                            fwAlerted = true;
                            Say("FIREWALL: " + detail);
                            pill.SetStatus(StatusKind.Warning, "Chat running — firewall problem!");
                            Toast.ShowFromTray(tray, "Realm chat firewall problem",
                                detail + " Open Realm Chat and click Fix problems.", true);
                        }
                        else if (ok && fwAlerted)
                        {
                            fwAlerted = false;
                            Say("Firewall is healthy again.");
                            if (state == ChatState.Ready) SetState(ChatState.Ready);
                        }
                    }));
                }
                catch { fwCheckRunning = false; }
            });
        }

        // Loads the model in the background after adopting a cold server.
        private void WarmAsync()
        {
            Say("Loading the model onto the GPU…");
            Task.Run(delegate
            {
                bool ok = ollama.Warm();
                try
                {
                    BeginInvoke((Action)(delegate
                    {
                        if (ok) Say("Chat is ready — the bots can talk now.");
                        else Say("Server is up; the model will load on the bots' first message.");
                        SetState(ChatState.Ready);
                    }));
                }
                catch { }
            });
        }

        private void RefreshHealthAsync(Action then)
        {
            Task.Run(delegate
            {
                var h = HealthCheck.RunAll(cfg, ollama);
                var c = Cleanup.Scan(cfg, ollama);
                BeginInvoke((Action)(delegate
                {
                    health = h;
                    cleanupItems = c;
                    PaintHealth();
                    RefreshCleanupButton();
                    if (then != null) then();
                }));
            });
        }

        private void RefreshCleanupButton()
        {
            long total = cleanupItems.Sum(i => i.Bytes);
            btnCleanup.Visible = cleanupItems.Count > 0;
            btnCleanup.Text = cleanupItems.Count > 0 ? "Clean up " + Cleanup.Gb(total) : "Clean up…";
        }

        private void OnCleanupClick(object sender, EventArgs e)
        {
            if (busy || cleanupItems.Count == 0) return;
            var lines = cleanupItems.Select(i => "  •  " + i.Describe()).ToArray();
            var answer = MessageBox.Show(this,
                "This permanently deletes:\r\n\r\n" + string.Join("\r\n", lines) +
                "\r\n\r\nThe realm's own model and settings are not touched. Continue?",
                "Realm Chat — clean up disk space",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            busy = true;
            bar.Active = true;
            var doomed = cleanupItems;
            Task.Run(delegate
            {
                var left = Cleanup.Delete(cfg, ollama, doomed, Say);
                BeginInvoke((Action)(delegate
                {
                    cleanupItems = left;
                    RefreshCleanupButton();
                    busy = false;
                    bar.Active = false;
                }));
            });
        }

        private void OnFixClick(object sender, EventArgs e)
        {
            if (busy) return;
            busy = true;
            bar.Active = true;
            var broken = health.Where(x => !x.Ok && x.FixFlag != null).ToList();
            Task.Run(delegate
            {
                try { ElevatedFix.Run(cfg, broken, Say); }
                catch (Exception ex) { Say("Fix failed: " + ex.Message); }
                BeginInvoke((Action)(delegate
                {
                    RefreshHealthAsync(delegate { busy = false; bar.Active = false; });
                }));
            });
        }

        private void OnSettingsClick(object sender, EventArgs e)
        {
            if (busy) return;
            using (var setup = new SetupForm(cfg))
            {
                if (setup.ShowDialog(this) == DialogResult.OK)
                {
                    setup.Result.Save();
                    Say("Settings saved.");
                    RefreshHealthAsync(null);
                }
            }
        }

        // --- start / stop --------------------------------------------------------

        private void OnToggleClick(object sender, EventArgs e)
        {
            if (busy) return;
            if (state == ChatState.Ready || state == ChatState.Warming || state == ChatState.Starting)
                StopChat();
            else
                StartChat();
        }

        private void StartChat()
        {
            busy = true;
            bar.Active = true;
            SetState(ChatState.Starting);
            Say("Starting the chat brain…");
            Task.Run(delegate
            {
                try
                {
                    if (ollama.IsUp())
                    {
                        Say("Ollama was already running — using it.");
                    }
                    else
                    {
                        ollama.Start();
                        if (!ollama.WaitUntilUp(30))
                            throw new Exception("Ollama did not answer within 30 seconds");
                    }
                    bool present = ollama.ModelPresent();
                    if (!present)
                    {
                        Say("Chat model isn't downloaded yet — fetching " + Constants.Model);
                        Say("(about 5 GB, one-time; progress below)");
                        if (!ollama.Pull(Say))
                            throw new Exception("model download failed - see the log");
                    }
                    BeginInvoke((Action)(delegate { SetState(ChatState.Warming); }));
                    Say("Loading the model onto the GPU (first load ~30s)…");
                    bool warmed = ollama.Warm();
                    BeginInvoke((Action)(delegate
                    {
                        if (warmed) Say("Chat is ready — the bots can talk now.");
                        else Say("Server is up; the model will load on the bots' first message.");
                        SetState(ChatState.Ready);
                        fwPostStart = DateTime.UtcNow;   // firewall re-check shortly after start
                        busy = false;
                        bar.Active = false;
                    }));
                }
                catch (Exception ex)
                {
                    Say("FAILED to start: " + ex.Message);
                    try { ollama.Stop(); } catch { }
                    BeginInvoke((Action)(delegate
                    {
                        SetState(ChatState.Stopped);
                        busy = false;
                        bar.Active = false;
                    }));
                }
            });
        }

        private void StopChat()
        {
            busy = true;
            bar.Active = true;
            Task.Run(delegate
            {
                try { ollama.Stop(); }
                catch (Exception ex) { Say("Stop failed: " + ex.Message); }
                BeginInvoke((Action)(delegate
                {
                    SetState(ChatState.Stopped);
                    busy = false;
                    bar.Active = false;
                }));
            });
        }

        private void OnServerExited()
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((Action)(delegate
                {
                    SetState(ChatState.Crashed);
                    Toast.ShowFromTray(tray, "Realm chat stopped",
                        "Ollama exited unexpectedly. Open Realm Chat to start it again.", true);
                }));
            }
            catch { }
        }

        // Reconciles the pill with reality every few seconds (covers adopted
        // servers stopping, or someone starting Ollama outside the app).
        private void ReconcileState()
        {
            if (busy) return;
            Task.Run(delegate
            {
                bool up = ollama.IsUp();
                try
                {
                    BeginInvoke((Action)(delegate
                    {
                        if (busy) return;
                        if (up && state == ChatState.Stopped)
                        {
                            Say("Detected an Ollama server started outside this app.");
                            SetState(ChatState.Ready);
                        }
                        else if (!up && state == ChatState.Ready)
                        {
                            if (ollama.WeStartedIt) return;   // Exited event handles ours
                            SetState(ChatState.Stopped);
                            Say("The Ollama server has stopped.");
                        }
                    }));
                }
                catch { }
            });
        }

        // --- close / tray ---------------------------------------------------------

        private void Restore()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnClosingForm(object sender, FormClosingEventArgs e)
        {
            bool running = state == ChatState.Ready || state == ChatState.Warming || state == ChatState.Starting;
            if (!exiting && running && e.CloseReason == CloseReason.UserClosing)
            {
                // The whole point of the tray: closing must not silence the bots.
                e.Cancel = true;
                Hide();
                if (!cfg.tray_hint_shown)
                {
                    cfg.tray_hint_shown = true;
                    cfg.Save();
                    tray.ShowBalloonTip(8000, "Realm Chat is still running",
                        "The bots keep talking. Right-click the tray icon to stop the chat or exit.",
                        ToolTipIcon.Info);
                }
                return;
            }

            // Real exit: leaving Ollama running headless would be invisible state.
            if (running)
            {
                Say("Exiting — stopping the chat.");
                try { ollama.Stop(); } catch { }
            }
            poll.Stop();
            tray.Visible = false;
            tray.Dispose();
        }
    }
}
