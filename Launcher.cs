using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Simple game launcher for the Rust client.
// - Reads launcher.cfg (next to the exe) for the download URL, install dir, and exe to launch.
// - "Install / Update" downloads a single .zip with a progress bar, then extracts it.
// - "Play" launches the client.
// Built against .NET Framework 4.x so it runs on any Windows 10/11 with no runtime install.

public class LauncherForm : Form
{
    // ----- Config (overridable via launcher.cfg) -----
    string DownloadUrl = "https://REPLACE-ME.example.com/RustClient.zip";
    string InstallDir  = "";          // default set in constructor
    string LaunchExe   = "RustClient.exe";
    string LaunchArgs  = "";
    string Version     = "";

    // ----- UI -----
    Label      lblTitle;
    Label      lblStatus;
    Label      lblPath;
    ProgressBar bar;
    Button     btnFolder;
    Button     btnInstall;
    Button     btnPlay;

    WebClient  client;
    string     zipPath;
    bool       busy = false;
    DateTime   dlStart;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new LauncherForm());
    }

    public LauncherForm()
    {
        // Default install dir = ".\Rust" next to the launcher.
        InstallDir = Path.Combine(AppDir(), "Rust");
        LoadConfig();

        // ---- Window ----
        Text = "Rust Launcher";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(540, 300);
        BackColor = Color.FromArgb(28, 28, 32);
        Font = new Font("Segoe UI", 9f);

        lblTitle = new Label();
        lblTitle.Text = "RUST";
        lblTitle.ForeColor = Color.FromArgb(205, 65, 45);
        lblTitle.Font = new Font("Segoe UI", 30f, FontStyle.Bold);
        lblTitle.AutoSize = true;
        lblTitle.Location = new Point(24, 18);
        Controls.Add(lblTitle);

        string sub = "January Update 2021 Client";
        if (Version.Length > 0) sub += "  •  v" + Version;
        Label lblSub = new Label();
        lblSub.Text = sub;
        lblSub.ForeColor = Color.FromArgb(150, 150, 155);
        lblSub.AutoSize = true;
        lblSub.Location = new Point(30, 82);
        Controls.Add(lblSub);

        lblPath = new Label();
        lblPath.ForeColor = Color.FromArgb(120, 120, 128);
        lblPath.AutoSize = false;
        lblPath.Size = new Size(430, 18);
        lblPath.Location = new Point(30, 118);
        lblPath.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(lblPath);

        btnFolder = new Button();
        btnFolder.Text = "...";
        btnFolder.Size = new Size(40, 22);
        btnFolder.Location = new Point(470, 116);
        FlatBtn(btnFolder, Color.FromArgb(55, 55, 60));
        btnFolder.Click += delegate { PickFolder(); };
        Controls.Add(btnFolder);

        bar = new ProgressBar();
        bar.Location = new Point(30, 158);
        bar.Size = new Size(480, 22);
        bar.Minimum = 0;
        bar.Maximum = 1000;
        Controls.Add(bar);

        lblStatus = new Label();
        lblStatus.ForeColor = Color.FromArgb(180, 180, 185);
        lblStatus.AutoSize = false;
        lblStatus.Size = new Size(480, 20);
        lblStatus.Location = new Point(30, 186);
        Controls.Add(lblStatus);

        btnInstall = new Button();
        btnInstall.Text = "Install / Update";
        btnInstall.Size = new Size(230, 46);
        btnInstall.Location = new Point(30, 224);
        FlatBtn(btnInstall, Color.FromArgb(55, 55, 60));
        btnInstall.Click += delegate { StartInstall(); };
        Controls.Add(btnInstall);

        btnPlay = new Button();
        btnPlay.Text = "PLAY";
        btnPlay.Size = new Size(230, 46);
        btnPlay.Location = new Point(280, 224);
        btnPlay.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        FlatBtn(btnPlay, Color.FromArgb(205, 65, 45));
        btnPlay.Click += delegate { Play(); };
        Controls.Add(btnPlay);

        RefreshState();
    }

    // ---------- helpers ----------
    static string AppDir()
    {
        return Path.GetDirectoryName(Application.ExecutablePath);
    }

    void FlatBtn(Button b, Color back)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = back;
        b.ForeColor = Color.White;
        b.Cursor = Cursors.Hand;
    }

    void LoadConfig()
    {
        try
        {
            string cfg = Path.Combine(AppDir(), "launcher.cfg");
            if (!File.Exists(cfg)) return;
            foreach (string raw in File.ReadAllLines(cfg))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "downloadurl": DownloadUrl = val; break;
                    case "installdir":  if (val.Length > 0) InstallDir = val; break;
                    case "launchexe":   LaunchExe = val; break;
                    case "launchargs":  LaunchArgs = val; break;
                    case "version":     Version = val; break;
                }
            }
        }
        catch { /* fall back to defaults */ }
    }

    // Find the client exe: prefer <InstallDir>\LaunchExe, else search recursively.
    string FindGameExe()
    {
        try
        {
            string direct = Path.Combine(InstallDir, LaunchExe);
            if (File.Exists(direct)) return direct;
            if (Directory.Exists(InstallDir))
            {
                string[] hits = Directory.GetFiles(InstallDir, LaunchExe, SearchOption.AllDirectories);
                if (hits.Length > 0) return hits[0];
            }
        }
        catch { }
        return null;
    }

    bool IsInstalled() { return FindGameExe() != null; }

    void RefreshState()
    {
        lblPath.Text = "Install: " + InstallDir;
        bool installed = IsInstalled();
        btnPlay.Enabled = installed && !busy;
        btnInstall.Enabled = !busy;
        btnFolder.Enabled = !busy;
        btnPlay.BackColor = btnPlay.Enabled ? Color.FromArgb(205, 65, 45) : Color.FromArgb(70, 50, 48);
        btnInstall.Text = installed ? "Update" : "Install";
        if (!busy)
            lblStatus.Text = installed ? "Ready to play." : "Client not installed yet - click Install.";
    }

    void PickFolder()
    {
        FolderBrowserDialog d = new FolderBrowserDialog();
        d.Description = "Choose where to install the Rust client";
        d.SelectedPath = Directory.Exists(InstallDir) ? InstallDir : AppDir();
        if (d.ShowDialog() == DialogResult.OK)
        {
            InstallDir = d.SelectedPath;
            RefreshState();
        }
    }

    // ---------- install flow ----------
    void StartInstall()
    {
        if (busy) return;
        if (string.IsNullOrEmpty(DownloadUrl) || DownloadUrl.Contains("REPLACE-ME"))
        {
            MessageBox.Show(this,
                "No valid download URL is set.\n\nEdit launcher.cfg next to this launcher and set:\n\n    DownloadUrl=https://your-host/RustClient.zip",
                "Configure download URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try { Directory.CreateDirectory(InstallDir); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Cannot create install folder:\n" + ex.Message, "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        busy = true;
        RefreshState();
        bar.Value = 0;
        lblStatus.Text = "Connecting...";

        zipPath = Path.Combine(Path.GetTempPath(), "RustClient_download.zip");
        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }

        dlStart = DateTime.UtcNow;
        client = new WebClient();
        client.DownloadProgressChanged += OnProgress;
        client.DownloadFileCompleted += OnDownloadDone;
        try
        {
            client.DownloadFileAsync(new Uri(DownloadUrl), zipPath);
        }
        catch (Exception ex)
        {
            busy = false;
            RefreshState();
            lblStatus.Text = "Download failed to start.";
            MessageBox.Show(this, ex.Message, "Download error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void OnProgress(object sender, DownloadProgressChangedEventArgs e)
    {
        long got = e.BytesReceived;
        long tot = e.TotalBytesToReceive;
        if (tot > 0)
        {
            bar.Style = ProgressBarStyle.Continuous;
            bar.Value = (int)Math.Min(1000, (got * 1000L) / tot);
        }
        else
        {
            bar.Style = ProgressBarStyle.Marquee;
        }
        double secs = (DateTime.UtcNow - dlStart).TotalSeconds;
        double mbps = secs > 0.5 ? (got / 1048576.0) / secs : 0;
        string totStr = tot > 0 ? Human(tot) : "?";
        lblStatus.Text = "Downloading  " + Human(got) + " / " + totStr +
                         (mbps > 0 ? "   (" + mbps.ToString("0.0") + " MB/s)" : "");
    }

    void OnDownloadDone(object sender, AsyncCompletedEventArgs e)
    {
        try { client.Dispose(); } catch { }
        client = null;

        if (e.Cancelled)
        {
            busy = false; RefreshState(); lblStatus.Text = "Cancelled."; return;
        }
        if (e.Error != null)
        {
            busy = false; RefreshState(); lblStatus.Text = "Download failed.";
            MessageBox.Show(this, e.Error.Message, "Download error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Extract on a background thread; keep UI responsive.
        bar.Style = ProgressBarStyle.Marquee;
        lblStatus.Text = "Extracting... this can take several minutes.";
        ThreadPool.QueueUserWorkItem(delegate { ExtractWorker(); });
    }

    void ExtractWorker()
    {
        string error = null;
        try
        {
            ExtractZip(zipPath, InstallDir);
            try { File.Delete(zipPath); } catch { }
        }
        catch (Exception ex) { error = ex.Message; }

        BeginInvoke((MethodInvoker)delegate
        {
            bar.Style = ProgressBarStyle.Continuous;
            bar.Value = bar.Maximum;
            busy = false;
            if (error != null)
            {
                lblStatus.Text = "Extract failed.";
                MessageBox.Show(this, error, "Extract error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                lblStatus.Text = "Install complete. Ready to play!";
            }
            RefreshState();
        });
    }

    // Extract with per-file progress reported to the status label.
    void ExtractZip(string zip, string dest)
    {
        Directory.CreateDirectory(dest);
        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            int total = archive.Entries.Count;
            int done = 0;
            string fullDest = Path.GetFullPath(dest);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
                // Guard against zip-slip.
                if (!target.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\") || entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
                done++;
                if ((done & 63) == 0 || done == total)
                {
                    int d = done, t = total;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        lblStatus.Text = "Extracting  " + d + " / " + t + " files...";
                        if (t > 0) { bar.Style = ProgressBarStyle.Continuous; bar.Value = (int)Math.Min(1000, (d * 1000L) / t); }
                    });
                }
            }
        }
    }

    // ---------- play ----------
    void Play()
    {
        string exe = FindGameExe();
        if (exe == null)
        {
            MessageBox.Show(this, "Client is not installed. Click Install first.", "Not installed",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshState();
            return;
        }
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            if (LaunchArgs.Length > 0) psi.Arguments = LaunchArgs;
            Process.Start(psi);
            lblStatus.Text = "Launching...";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Launch error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(i == 0 ? "0" : "0.0") + " " + u[i];
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && client != null)
        {
            if (MessageBox.Show(this, "A download is in progress. Cancel and quit?", "Quit",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true; return;
            }
            try { client.CancelAsync(); } catch { }
        }
        base.OnFormClosing(e);
    }
}
