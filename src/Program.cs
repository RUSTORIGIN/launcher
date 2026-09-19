using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RustLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
        }
    }

    internal sealed class LauncherForm : Form
    {
        private const string ClientProcessName = "RustClient";
        private const string ServerAddress = "127.0.0.1:28015";
        private const string ServerName = "Classic Server 2021";

        private static readonly Color Background = Color.FromArgb(30, 30, 30);
        private static readonly Color Accent = Color.FromArgb(205, 65, 43);
        private static readonly Color AccentDisabled = Color.FromArgb(80, 80, 80);
        private static readonly Color TextMuted = Color.FromArgb(170, 170, 170);

        private readonly string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RustLauncher.ini");
        private readonly Button playButton;
        private readonly CheckBox joinCheckBox;
        private readonly Label statusLabel;
        private readonly Timer stateTimer;
        private string clientPath;

        public LauncherForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Rust Launcher";
            ClientSize = new Size(420, 250);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Background;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

            var title = new Label
            {
                Text = "RUST",
                Font = new Font("Segoe UI", 30F, FontStyle.Bold),
                ForeColor = Accent,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(0, 14, 420, 60)
            };

            var subtitle = new Label
            {
                Text = "January 2021",
                ForeColor = TextMuted,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(0, 72, 420, 22)
            };

            joinCheckBox = new CheckBox
            {
                Text = "Join " + ServerName + " (" + ServerAddress + ")",
                Checked = true,
                AutoSize = true,
                ForeColor = Color.Gainsboro
            };
            joinCheckBox.Location = new Point((420 - joinCheckBox.PreferredSize.Width) / 2, 106);

            playButton = new Button
            {
                Text = "PLAY",
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Accent,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Bounds = new Rectangle(110, 138, 200, 56)
            };
            playButton.FlatAppearance.BorderSize = 0;
            playButton.Click += OnPlayClick;

            statusLabel = new Label
            {
                ForeColor = TextMuted,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(10, 208, 400, 30)
            };

            Controls.AddRange(new Control[] { title, subtitle, joinCheckBox, playButton, statusLabel });
            ResumeLayout(false);

            clientPath = FindClient();

            stateTimer = new Timer { Interval = 2000 };
            stateTimer.Tick += delegate { UpdateState(); };
            stateTimer.Start();
            UpdateState();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            stateTimer.Stop();
            stateTimer.Dispose();
            base.OnFormClosed(e);
        }

        private void OnPlayClick(object sender, EventArgs e)
        {
            if (clientPath == null || !File.Exists(clientPath))
            {
                clientPath = BrowseForClient();
                if (clientPath == null)
                {
                    UpdateState();
                    return;
                }
            }

            string arguments = "-console";
            if (joinCheckBox.Checked)
                arguments += " +connect " + ServerAddress;

            // CreateProcess directly (not the shell), same as the working directory the .bat used
            var startInfo = new ProcessStartInfo(clientPath, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(clientPath),
                UseShellExecute = false
            };

            try
            {
                Process.Start(startInfo)?.Dispose();
                statusLabel.Text = "Starting Rust...";
                playButton.Enabled = false;
                playButton.BackColor = AccentDisabled;
                joinCheckBox.Enabled = false;
            }
            catch (Win32Exception ex)
            {
                MessageBox.Show(this, "Could not start Rust:\n" + ex.Message, "Rust Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateState()
        {
            bool running = IsClientRunning();
            playButton.Enabled = !running;
            playButton.BackColor = running ? AccentDisabled : Accent;
            playButton.Text = running ? "RUNNING" : "PLAY";
            joinCheckBox.Enabled = !running;

            if (running)
                statusLabel.Text = "Rust is running.";
            else if (clientPath == null)
                statusLabel.Text = "RustClient.exe not found - click Play to locate it.";
            else
                statusLabel.Text = "Ready.";
        }

        private static bool IsClientRunning()
        {
            Process[] processes = Process.GetProcessesByName(ClientProcessName);
            foreach (Process process in processes)
                process.Dispose();
            return processes.Length > 0;
        }

        private string FindClient()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    string saved = File.ReadAllText(settingsPath).Trim();
                    if (File.Exists(saved))
                        return saved;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "RustClient.exe"),
                Path.Combine(baseDir, "RustClient", "RustClient.exe"),
                Path.Combine(baseDir, "..", "RustClient", "RustClient.exe")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }

            return null;
        }

        private string BrowseForClient()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Find RustClient.exe";
                dialog.Filter = "Rust client|RustClient.exe|Programs|*.exe";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return null;

                try { File.WriteAllText(settingsPath, dialog.FileName); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                return dialog.FileName;
            }
        }
    }
}
