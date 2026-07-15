using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RealmChat
{
    // First-run wizard. The published app is environment-blank on purpose;
    // the two site-specific values are entered here once and stored only in
    // the machine-local config.
    public class SetupForm : ThemedForm
    {
        public AppConfig Result { get; private set; }

        private readonly TextBox subnetBox = new TextBox();
        private readonly TextBox dnsBox = new TextBox();
        private readonly TextBox modelsBox = new TextBox();

        public SetupForm(AppConfig existing)
        {
            Result = existing ?? new AppConfig();

            Text = "Realm Chat - setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 372);

            var title = new Label { AutoSize = true, Location = new Point(16, 14), Text = "Realm Chat setup" };
            title.Font = new Font(Font.FontFamily, Font.Size * 1.35f, FontStyle.Bold);

            var intro = new MutedLabel
            {
                AutoSize = false,
                Location = new Point(16, 44),
                Size = new Size(528, 34),
                Text = "One-time setup. These values stay on this PC only - the game server " +
                       "admin will tell you what to enter (or has filled them in already).",
            };

            var detected = SubnetHelper.LocalSubnets();
            var capLocal = new MutedLabel { Location = new Point(16, 88), Text = "This PC's network (detected automatically)" };
            var valLocal = new Label
            {
                AutoSize = true,
                Location = new Point(16, 106),
                Text = detected.Count > 0 ? string.Join(", ", detected.ToArray()) : "none detected",
                Font = new Font(Font, FontStyle.Bold),
            };

            var capSubnet = new MutedLabel { Location = new Point(16, 138), Text = "Game server network (e.g. 10.0.0.0/24) - from the admin" };
            subnetBox.Location = new Point(16, 156);
            subnetBox.Size = new Size(528, 24);
            subnetBox.Text = Result.server_subnets ?? "";

            var capDns = new MutedLabel { Location = new Point(16, 190), Text = "This PC's DNS name (optional) - lets the app check the server can find it" };
            dnsBox.Location = new Point(16, 208);
            dnsBox.Size = new Size(528, 24);
            dnsBox.Text = Result.dns_name ?? "";

            var capModels = new MutedLabel
            {
                Location = new Point(16, 242),
                Text = "Model storage folder (about 5 GB; move it if another drive has more room)",
            };
            modelsBox.Location = new Point(16, 260);
            modelsBox.Size = new Size(432, 24);
            modelsBox.Text = Result.GetModelsDir();
            var browseModels = new ThemedButton { Text = "Browse…", Location = new Point(456, 258), Size = new Size(88, 27) };
            browseModels.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Pick the folder Ollama stores its models in";
                    if (dlg.ShowDialog(this) == DialogResult.OK) modelsBox.Text = dlg.SelectedPath;
                }
            };

            var ok = new ThemedButton { Text = "Save", Primary = true, Location = new Point(348, 322), Size = new Size(96, 30) };
            ok.Click += OnOk;
            var cancel = new ThemedButton
            {
                Text = "Cancel",
                Location = new Point(452, 322),
                Size = new Size(92, 30),
                DialogResult = DialogResult.Cancel,
            };

            Controls.AddRange(new Control[] { title, intro, capLocal, valLocal,
                capSubnet, subnetBox, capDns, dnsBox,
                capModels, modelsBox, browseModels, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void OnOk(object sender, EventArgs e)
        {
            var subnets = (subnetBox.Text ?? "").Split(',')
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var bad = subnets.FirstOrDefault(s => !SubnetHelper.LooksLikeCidr(s));
            if (bad != null)
            {
                MessageBox.Show(this,
                    "'" + bad + "' doesn't look like a network in a.b.c.d/nn form.\r\n" +
                    "Check the value the admin gave you (leave it empty to set later).",
                    "Realm Chat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string models = (modelsBox.Text ?? "").Trim();
            if (models.Length > 0 && models.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
            {
                MessageBox.Show(this, "That model folder path contains invalid characters.",
                    "Realm Chat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string oldModels = Result.GetModelsDir();
            // Store the override only when it differs from the default.
            Result.models_dir = (models.Length == 0 ||
                string.Equals(models, Constants.ModelsDir, StringComparison.OrdinalIgnoreCase))
                ? null : models;
            if (!string.Equals(oldModels, Result.GetModelsDir(), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "Model folder changed. Click 'Fix problems' afterwards so the system\r\n" +
                    "setting follows, and note the model re-downloads into the new folder\r\n" +
                    "on the next start (the old folder can then be deleted).",
                    "Realm Chat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            Result.server_subnets = string.Join(",", subnets.ToArray());
            Result.dns_name = (dnsBox.Text ?? "").Trim();
            Result.setup_done = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
