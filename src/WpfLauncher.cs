using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Animation;

[assembly: AssemblyTitle("Rustorigin Launcher")]
[assembly: AssemblyProduct("Rustorigin Launcher")]
[assembly: AssemblyDescription("Rustorigin Launcher - downloads, installs and launches the client")]
[assembly: AssemblyCompany("Rustorigin")]
[assembly: AssemblyCopyright("Copyright (c) 2026 kaveOO")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]

// RUSTORIGIN launcher - WPF port of the Superdesign canvas composition:
// rounded dark card, full-bleed cross-fading screenshot slideshow, floating glass UI
// (left rail, top-right pills, hero block, server column, friends rail).
// Code-only WPF on .NET Framework 4.x: runs on any Windows 10/11, no runtime install.

public class App
{
    // Single-instance guard. The mutex is per-login-session (local namespace), so only one launcher
    // runs at a time per user; the event lets a second launch wake the first one to the foreground
    // (it may be hidden to the tray) instead of dying silently.
    const string MutexName = "RUSTORIGIN.Launcher.SingleInstance";
    const string ShowEventName = "RUSTORIGIN.Launcher.Show";
    static System.Threading.Mutex instanceMutex;

    [STAThread]
    static void Main()
    {
        bool createdNew;
        instanceMutex = new System.Threading.Mutex(true, MutexName, out createdNew);
        if (!createdNew)
        {
            // Already running: signal that instance to surface itself, then exit.
            try { System.Threading.EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            return;
        }
        var showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ShowEventName);

        Assets.Ensure();                 // unpack embedded screenshots/logo/fonts/config (single-exe distribution)
        LauncherWindow.ConfigureTls();
        var app = new Application();
        var win = new LauncherWindow();

        // Wake this window whenever another launch signals the event.
        var t = new System.Threading.Thread(delegate ()
        {
            while (true)
            {
                showEvent.WaitOne();
                try { win.Dispatcher.BeginInvoke((Action)(() => win.SurfaceFromAnywhere())); } catch { }
            }
        });
        t.IsBackground = true;
        t.Start();

        app.Run(win);
        GC.KeepAlive(instanceMutex);     // hold the handle for the whole process lifetime
    }
}

// Everything the launcher needs ships INSIDE the exe as manifest resources and is unpacked once
// per version to %LOCALAPPDATA%\Rustorigin\assets\<version>\ (loaded from real files for the
// background screenshots and private fonts). An optional launcher.cfg next to the exe overrides
// the embedded defaults.
static class Assets
{
    public static string Dir = "";
    static readonly string[] Files = {
        "1.jpg", "2.jpg", "3.jpg", "main.jpg", "train.jpg",
        "logo.png", "launcher.cfg",
        "fonts/Montserrat-Regular.ttf", "fonts/Montserrat-Medium.ttf",
        "fonts/Montserrat-SemiBold.ttf", "fonts/Montserrat-Bold.ttf", "fonts/OFL.txt",
        "fonts/Poppins-Regular.ttf", "fonts/Poppins-Medium.ttf",
        "fonts/Poppins-SemiBold.ttf", "fonts/Poppins-Bold.ttf", "fonts/Poppins-OFL.txt" };

    public static void Ensure()
    {
        try
        {
            var asm = typeof(Assets).Assembly;
            string ver = asm.GetName().Version.ToString();
            Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rustorigin", "assets", ver);
            foreach (string fname in Files)
            {
                string target = Path.Combine(Dir, fname.Replace('/', '\\'));
                using (Stream s = asm.GetManifestResourceStream("assets/" + fname))
                {
                    if (s == null) continue;
                    if (File.Exists(target) && new FileInfo(target).Length == s.Length) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var o = File.Create(target)) s.CopyTo(o);
                }
            }
        }
        catch { }
    }

    public static string[] ConfigLines()
    {
        try
        {
            using (Stream s = typeof(Assets).Assembly.GetManifestResourceStream("assets/launcher.cfg"))
            {
                if (s == null) return new string[0];
                using (var r = new StreamReader(s)) return r.ReadToEnd().Split('\n');
            }
        }
        catch { return new string[0]; }
    }
}

public class ServerEntry
{
    public string Tag, Name, Args, Players, Cover, StatusUrl;
    public ServerEntry(string tag, string name, string args, string players = "", string cover = "", string statusUrl = "")
    { Tag = tag; Name = name; Args = args; Players = players; Cover = cover; StatusUrl = statusUrl; }
}

public class SocialEntry
{
    public string Platform, Url;   // Platform is a lowercase key (discord/youtube/tiktok/...)
    public SocialEntry(string platform, string url) { Platform = platform; Url = url; }
}

public class LauncherWindow : Window
{
    // ---- config (launcher.cfg) ----
    string DownloadUrl = "https://REPLACE-ME.example.com/RustClient.zip";
    string ExpectedSha256 = "";   // hex SHA-256 of RustClient.zip. When set, a download whose hash does not match is rejected (never extracted or launched).
    string InstallDir  = "";
    string LaunchExe   = "RustClient.exe";
    string LaunchArgs  = "";
    int    DownloadConnections = 6;   // parallel HTTP Range connections for the client download (1 = single stream)
    string Version     = "";
    string UpdateRepo  = "RUSTORIGIN/launcher";   // owner/repo checked for launcher self-updates (GitHub Releases). Empty disables.
    string DiscordAppId = "";        // Discord application id for Rich Presence. Empty disables.
    string DiscordLargeImage = "";   // Rich Presence art-asset key uploaded in the Discord app.
    string DiscordButtonLabel = "";  // Rich Presence button label (e.g. "Play on Rustorigin").
    string DiscordButtonUrl = "";    // Rich Presence button link (e.g. https://rustorigin.com).
    DiscordRpc discord;
    long sessionStartUnix;
    string GameTitle   = "RUSTORIGIN";
    string Tagline     = "RUSTORIGIN is a private Rust world on the January 2021 build. Craft, raid and survive with a tight community - one click to jump in.";
    string PlayerName  = "White Pegasus";
    List<ServerEntry> Servers = new List<ServerEntry>();
    readonly List<Action> serverStatusRefreshers = new List<Action>();   // one live-status re-query per card
    bool serverStatusTimerStarted;
    List<SocialEntry> Socials = new List<SocialEntry>();   // bottom-left social links (Social= lines)

    // ---- state ----
    bool      busy;
    volatile bool cancelRequested;
    HttpWebRequest activeReq;
    Thread    dlThread;
    string    cacheDir, zipPath, partPath, metaPath, chunksPath;
    const string UA = "RUSTORIGIN-Launcher/1.0";

    // ---- ui refs ----
    Border           installFill;        // progress fill drawn INSIDE the install/pause button
    FrameworkElement installContent;     // the install button's icon+label content (measured for smooth width)
    string           dlLabel = "";       // live download status shown as the button's label while busy
    TextBlock    statusText;
    Border       playBtn, installBtn;
    Grid         mainGrid;
    Grid         bgHost;      // background slideshow container the glass panels sample (real acrylic)
    Image        slideBack, slideFront;   // two stacked images for cross-fading between screenshots
    BitmapImage[] slides = new BitmapImage[0];
    int          slideIndex;
    System.Windows.Threading.DispatcherTimer slideTimer;   // auto-advance timer (reset on manual pick)
    List<Border> slideDots;                                // carousel indicator dots at the bottom
    BitmapImage  logoBmp;
    Grid homeView;
    const double CornerR = 32;
    Border edgeBorder;
    Grid settingsOverlay;   // settings panel

    // ---- palette ----
    static Brush B(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
    static readonly Brush TextHi   = B("#FFFFFF");
    static readonly Brush TextDim  = B("#C9CCD6");
    static readonly Brush TextMute = B("#8A8E99");
    static readonly Brush Stroke   = B("#1FFFFFFF");   // rgba(255,255,255,.12) - hairline + resting glass-button fill
    static readonly Brush StrokeHi = B("#59FFFFFF");   // rgba(255,255,255,.35) outlined pills / inactive dots
    static readonly Brush GlassSoft  = B("#26FFFFFF");   // rgba(255,255,255,.15) - glass-button edge / toggle-off track
    static readonly Brush GlassHover = B("#33FFFFFF");   // rgba(255,255,255,.20) - glass-button hover fill
    static readonly Brush Ink        = B("#12141A");     // near-black glyph/text on the white PLAY/INSTALL pills
    static readonly Brush WindowBg   = B("#0B0B0C");     // app window / mainGrid background
    static readonly Brush StatChecking = B("#E0B341");   // status dot: querying (amber)
    // ---- website design tokens (rustorigin.com globals.css): flat solid gunmetal, no glass, violet brand.
    // Used by the settings panel so it matches the site's UI (surfaces are opaque).
    static readonly Brush Ink900   = B("#141416");   // panel surface
    static readonly Brush Ink850   = B("#17181A");   // raised / hover surface
    static readonly Brush Ink400   = B("#6B6D74");   // faint labels / descriptions
    static readonly Brush Ink200   = B("#A7A9B0");   // body copy
    static readonly Brush Ink100   = B("#ECECEE");   // headings
    static readonly Brush Brand600 = B("#7C3AED");   // brand accent fill
    static readonly Brush Brand500 = B("#8B5CF6");   // brand accent hover / input focus
    static readonly Brush Brand400 = B("#A78BFA");   // brand eyebrow text
    static readonly Brush Brand300 = B("#C4B5FD");   // brand hover / lighter accent
    static readonly Brush Live     = B("#4ADE80");   // toggle-on (the site's switch uses live green)
    static readonly Brush Danger   = B("#F87171");   // destructive / close hover
    static readonly FontFamily Icons = new FontFamily("Segoe MDL2 Assets");

    // Brand typeface: bundled Montserrat, falling back to Bahnschrift/Segoe UI.
    static readonly FontFamily Brand = MakeBrand();
    static FontFamily MakeBrand()
    {
        try
        {
            string fdir = Path.Combine(Assets.Dir, "fonts");
            if (!Directory.Exists(fdir)) fdir = Path.Combine(AppDir(), "fonts");
            if (File.Exists(Path.Combine(fdir, "Montserrat-SemiBold.ttf")))
            {
                var uri = new Uri(fdir.Replace('\\', '/') + "/", UriKind.Absolute);
                return new FontFamily(uri, "./#Montserrat");
            }
        }
        catch { }
        return new FontFamily("Bahnschrift, Segoe UI");
    }

    // Site typeface: bundled Poppins (the rustorigin.com font), used by the settings panel. Falls
    // back to any installed Poppins, then Segoe UI.
    static readonly FontFamily Site = MakeSite();
    static FontFamily MakeSite()
    {
        try
        {
            string fdir = Path.Combine(Assets.Dir, "fonts");
            if (!Directory.Exists(fdir)) fdir = Path.Combine(AppDir(), "fonts");
            if (File.Exists(Path.Combine(fdir, "Poppins-SemiBold.ttf")))
            {
                var uri = new Uri(fdir.Replace('\\', '/') + "/", UriKind.Absolute);
                return new FontFamily(uri, "./#Poppins");
            }
        }
        catch { }
        return new FontFamily("Poppins, Segoe UI");
    }

    static string Track(string s, int n)
    {
        var sp = new string(' ', n);
        return string.Join(sp, s.ToCharArray());
    }

    static string AppDir() { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }

    // True if the element (or an ancestor) is a clickable control (we mark those with a Hand cursor).
    static bool IsInteractive(DependencyObject d)
    {
        while (d != null)
        {
            var fe = d as FrameworkElement;
            if (fe != null && fe.Cursor == Cursors.Hand) return true;
            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    public LauncherWindow()
    {
        InstallDir = Path.Combine(AppDir(), "Rust");
        LoadConfig();
        if (Servers.Count == 0)
        {
            Servers.Add(new ServerEntry("Training", "Training Grounds", "-console +connect 51.195.60.227:28015", "", "train.jpg"));
            Servers.Add(new ServerEntry("Vanilla",  "Rustorigin Main",  "", "", "main.jpg"));
        }
        logoBmp = LoadBitmap(Path.Combine(Assets.Dir, "logo.png")) ?? LoadBitmap(Path.Combine(AppDir(), "logo.png"));

        // Download cache (survives launcher restarts so a partial download can resume).
        cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rustorigin");
        zipPath  = Path.Combine(cacheDir, "RustClient.zip");
        partPath = zipPath + ".part";
        metaPath = zipPath + ".part.meta";
        chunksPath = zipPath + ".part.chunks";   // which 32 MB ranges of the .part are complete (parallel download)
        try { string sfile = Path.Combine(cacheDir, "installdir.txt"); if (File.Exists(sfile)) { string sv = File.ReadAllText(sfile).Trim(); if (sv.Length > 0) InstallDir = sv; } } catch { }

        // ---- window chrome (native Windows title bar + standard window features) ----
        Title = "Rustorigin Launcher";
        try
        {
            var ico = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
            if (ico != null) base.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                ico.Handle, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch { }
        Width = 1100; Height = 700;
        MinWidth = 820; MinHeight = 520;
        // Frameless + rounded corners, but a REAL native window underneath: WindowChrome keeps
        // drag, resize, maximize, Aero Snap, taskbar and the system menu; custom caption buttons
        // (built in BuildCaption) provide min/max/close. WM_GETMINMAXINFO keeps a maximized window
        // inside the work area (taskbar stays visible).
        //
        // The window is deliberately NOT layered (AllowsTransparency = false): a layered window
        // loses the native minimize/maximize/restore animations. Instead the rounded corners come
        // from a rounded window region applied in ApplyWindowRegion() (SetWindowRgn), so we keep the
        // 32px radius AND the real Windows min/max animations.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = WindowBg;
        ResizeMode = ResizeMode.CanResize;               // resize + maximize + Aero Snap
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,                           // no native caption strip - we drag from anywhere (below)
            ResizeBorderThickness = new Thickness(6),    // still gives native resize grips at the edges
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });

        mainGrid = new Grid { Background = WindowBg };
        mainGrid.SizeChanged += (s, e) => ApplyRounding();
        Content = mainGrid;

        BuildBackground();
        BuildGradient();
        BuildContent();
        BuildSlideDots();   // carousel indicators on top of the content, bottom-center

        // 1px light edge to match the rounded card
        edgeBorder = new Border { CornerRadius = new CornerRadius(CornerR), BorderBrush = B("#1AFFFFFF"),
            BorderThickness = new Thickness(1), Background = Brushes.Transparent, IsHitTestVisible = false };
        mainGrid.Children.Add(edgeBorder);

        BuildCaption(mainGrid);
        BuildSocials(mainGrid);   // social icons, top-left corner (mirrors the caption buttons top-right)
        BuildSettingsOverlay();   // hidden glass settings panel, on top of everything (gear toggles it)
        ApplyRounding();

        // Esc closes the settings panel when it's open.
        PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && settingsOverlay != null && settingsOverlay.Visibility == Visibility.Visible) { ToggleSettings(false); e.Handled = true; } };

        // Drag the window from ANY empty area (not just the top). Interactive controls are marked
        // with a Hand cursor, so IsInteractive skips them and they still get their clicks. Using the
        // native NCLBUTTONDOWN/HTCAPTION move keeps Aero Snap (drag to an edge / the top to maximize).
        MouseLeftButtonDown += (s, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            if (e.ClickCount == 2) { ToggleMaximize(); return; }   // double-click empty area = maximize/restore
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                ReleaseCapture();
                SendMessage(hwnd, 0xA1, (IntPtr)0x2, IntPtr.Zero);  // WM_NCLBUTTONDOWN, HTCAPTION
            }
            catch { }
        };


        // (background slideshow starts its own timer in BuildBackground)

        SetupTray();
        StartGameTimer();
        RefreshState();
        StartUpdateCheck();
        StartDiscord();
    }

    // ---------- Discord Rich Presence ----------
    void StartDiscord()
    {
        try
        {
            if (string.IsNullOrEmpty(DiscordAppId) || !Prefs.GetBool("DiscordRpc", true)) return;
            sessionStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            discord = new DiscordRpc();
            discord.OnLog = m => Log("discord: " + m);
            discord.Start(DiscordAppId);
            SetDiscord("In the launcher");
        }
        catch { }
    }

    void SetDiscord(string state)
    {
        // The Discord app name is already the top line, so use details for the build and state for
        // the activity: "RUSTORIGIN" / "January Update 2021" / "In the launcher".
        try { if (discord != null) discord.SetPresence("January Update 2021", state, sessionStartUnix, DiscordLargeImage, GameTitle + " - January Update 2021", DiscordButtonLabel, DiscordButtonUrl); }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { if (discord != null) discord.Stop(); } catch { }
        base.OnClosed(e);
    }

    // ---------- system tray icon (notification area) ----------
    System.Windows.Forms.NotifyIcon tray;
    void SetupTray()
    {
        try
        {
            tray = new System.Windows.Forms.NotifyIcon();
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); } catch { }
            tray.Text = "Rustorigin Launcher";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var open = new System.Windows.Forms.ToolStripMenuItem("Open Rustorigin");
            open.Click += delegate { ShowFromTray(); };
            var quit = new System.Windows.Forms.ToolStripMenuItem("Quit");
            quit.Click += delegate { try { tray.Visible = false; } catch { } Close(); };
            menu.Items.Add(open);
            menu.Items.Add(quit);
            tray.ContextMenuStrip = menu;
        }
        catch { }
    }

    void ShowFromTray()
    {
        try
        {
            Show();
            WindowState = WindowState.Normal;   // native restore animation (non-layered window)
            Activate();
            Topmost = true; Topmost = false;
        }
        catch { }
    }

    // Bring the window to the foreground from any state (used by the single-instance guard when a
    // second launch is attempted - the running instance may be hidden to the tray or minimized).
    public void SurfaceFromAnywhere() { ShowFromTray(); }

    // ---------- rounded frameless chrome + caption buttons ----------
    void ApplyRounding()
    {
        double r = WindowState == WindowState.Maximized ? 0 : CornerR;   // square when maximized
        try { mainGrid.Clip = new RectangleGeometry(new Rect(0, 0, mainGrid.ActualWidth, mainGrid.ActualHeight), r, r); } catch { }
        if (edgeBorder != null) edgeBorder.CornerRadius = new CornerRadius(r);
    }

    void BuildCaption(Grid host)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 14, 0) };
        row.Children.Add(CaptionBtn("", delegate { ToggleSettings(true); }, false));            // settings (gear) - leftmost
        row.Children.Add(CaptionBtn("", delegate { WindowState = WindowState.Minimized; }, false));   // minimize
        row.Children.Add(CaptionBtn("", delegate { Close(); }, true));                                 // close (quits the launcher)
        host.Children.Add(row);
    }

    // Circular glass caption button, matching the social icon buttons. Close hovers red (Danger),
    // the others brighten the glass; the glyph lifts to white on hover.
    Border CaptionBtn(string glyph, Action onClick, bool closeBtn)
    {
        var tb = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 11, Foreground = TextDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = Stroke, BorderBrush = GlassSoft, BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand, Child = tb, Margin = new Thickness(10, 0, 0, 0)
        };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(b, true);   // clickable inside the caption drag area
        b.MouseEnter += (s, e) => { b.Background = closeBtn ? Danger : GlassHover; tb.Foreground = TextHi; };
        b.MouseLeave += (s, e) => { b.Background = Stroke; tb.Foreground = TextDim; };
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return b;
    }

    void ToggleMaximize() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }

    // ---------- settings panel (gear button) ----------
    // A frosted-glass modal over the (blurred) launcher, toggled by the caption gear. Exposes the
    // per-user Prefs, the PLAY launch args, and a couple of utility actions - so nothing needs editing
    // in prefs.cfg. Toggles persist immediately; launch args apply when the panel closes. Styled to the
    // rustorigin.com theme, laid out like a site page: a dark ink-950 scrim, an ink-900 sheet, a
    // SectionHeading-style header (violet eyebrow + gradient rule + blurb), and settings grouped into
    // raised ink-850 cards whose rows are split by hairlines. Violet brand accent, live-green switches. No glass.
    void BuildSettingsOverlay()
    {
        // Solid dark scrim (ink-950 @ ~80%) - a flat modal dim, not a frosted-glass wash.
        settingsOverlay = new Grid { Visibility = Visibility.Collapsed, Background = B("#CC0B0B0C") };
        settingsOverlay.MouseLeftButtonDown += (s, e) => e.Handled = true;
        settingsOverlay.MouseLeftButtonUp += (s, e) => { if (e.OriginalSource == settingsOverlay) ToggleSettings(false); };

        var card = new Border
        {
            Width = 480, MaxHeight = 760,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = Ink900, CornerRadius = new CornerRadius(17), Cursor = Cursors.Arrow, Padding = new Thickness(26, 22, 26, 22),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 44, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black }
        };
        card.MouseLeftButtonDown += (s, e) => e.Handled = true;
        settingsOverlay.Children.Add(card);

        var col = new StackPanel();
        card.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = col };

        // ---- header: eyebrow + title + accent rule + blurb (SectionHeading), flat close on the right ----
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var htxt = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        htxt.Children.Add(new TextBlock { Text = Track("RUSTORIGIN", 3), Foreground = Brand400, FontFamily = Site, FontWeight = FontWeights.SemiBold, FontSize = 11 });
        htxt.Children.Add(new TextBlock { Text = "SETTINGS", Foreground = Ink100, FontFamily = Site, FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 3, 0, 0) });
        htxt.Children.Add(new Border { Height = 2, Width = 46, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Background = AccentRule() });
        htxt.Children.Add(new TextBlock { Text = "Preferences apply instantly.", Foreground = Ink200, FontFamily = Site, FontSize = 12, Margin = new Thickness(0, 12, 0, 0) });
        Grid.SetColumn(htxt, 0); head.Children.Add(htxt);
        var xTb = new TextBlock { Text = "\uE711", FontFamily = Icons, FontSize = 12, Foreground = Ink400, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var x = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = xTb, VerticalAlignment = VerticalAlignment.Top };
        x.MouseEnter += (s, e) => { x.Background = B("#0DFFFFFF"); xTb.Foreground = Danger; };
        x.MouseLeave += (s, e) => { x.Background = Brushes.Transparent; xTb.Foreground = Ink400; };
        x.MouseLeftButtonUp += (s, e) => { e.Handled = true; ToggleSettings(false); };
        Grid.SetColumn(x, 1); head.Children.Add(x);
        col.Children.Add(head);

        // ---- GENERAL ---- (background slideshow is always on; updates are mandatory - no toggles for either)
        col.Children.Add(GroupLabel("GENERAL"));
        col.Children.Add(GroupCard(
            SettingRow("Discord Rich Presence", "Show your In the launcher / In game status on Discord.",
                Prefs.GetBool("DiscordRpc", true), v => { Prefs.Set("DiscordRpc", v); ApplyDiscordPref(v); })));
        col.Children.Add(new TextBlock { Text = "Updates are required - when a newer version is available the launcher prompts you to update before continuing.", Foreground = Ink400, FontFamily = Site, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0) });

        // ---- GAME ----
        col.Children.Add(GroupLabel("GAME"));
        col.Children.Add(GroupCard(
            SettingRow("Minimize while in game", "Hide the launcher to the tray while playing.",
                Prefs.GetBool("MinimizeInGame", false), v => Prefs.Set("MinimizeInGame", v))));

        // ---- utility actions (compact rounded-md buttons, like the site's copy button) ----
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(UtilityBtn("\uE838", "Open data folder", delegate { try { Process.Start("explorer.exe", cacheDir); } catch { } }));
        col.Children.Add(actions);

        // ---- uninstall (danger) ----
        var danger = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        danger.Children.Add(DangerBtn("\uE74D", "Uninstall client", delegate { UninstallClient(); }));
        col.Children.Add(danger);

        // ---- footer: version (left) + Done pill (right) ----
        col.Children.Add(new Border { Height = 1, Background = B("#0DFFFFFF"), Margin = new Thickness(0, 18, 0, 14) });
        var foot = new Grid();
        string vs = "1.0.0";
        try { var vv = Assembly.GetExecutingAssembly().GetName().Version; vs = vv.Major + "." + vv.Minor + "." + vv.Build; } catch { }
        foot.Children.Add(new TextBlock { Text = "Rustorigin Launcher  v" + vs, Foreground = Ink400, FontFamily = Site, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left });
        foot.Children.Add(PrimaryPill("Done", delegate { ToggleSettings(false); }));
        col.Children.Add(foot);

        mainGrid.Children.Add(settingsOverlay);
    }

    // Left-aligned accent rule under the header (violet -> transparent), like the site's SectionHeading divider.
    static LinearGradientBrush AccentRule()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#8B5CF6"), 0));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#008B5CF6"), 1));
        return g;
    }

    // Group eyebrow above a card of rows (violet, wide-tracked) - the site's section eyebrow.
    TextBlock GroupLabel(string t)
    {
        return new TextBlock { Text = Track(t, 3), Foreground = Brand400, FontFamily = Site, FontWeight = FontWeights.SemiBold, FontSize = 10.5, Margin = new Thickness(2, 18, 0, 8) };
    }

    // Raised ink-850 card holding rows split by hairlines - the site's "divide-y rounded bg-ink" list.
    Border GroupCard(params UIElement[] rows)
    {
        var sp = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) sp.Children.Add(new Border { Height = 1, Background = B("#0DFFFFFF") });   // white/5 divider
            sp.Children.Add(rows[i]);
        }
        var b = new Border { Background = Ink850, CornerRadius = new CornerRadius(17), Child = sp };
        b.SizeChanged += (s, e) => { try { b.Clip = new RectangleGeometry(new Rect(0, 0, b.ActualWidth, b.ActualHeight), 17, 17); } catch { } };
        return b;
    }

    // One settings row: label + description on the left, switch on the right; faint hover fill.
    Border SettingRow(string title, string desc, bool on, Action<bool> onChange)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        txt.Children.Add(new TextBlock { Text = title, Foreground = Ink100, FontFamily = Site, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        if (desc != null) txt.Children.Add(new TextBlock { Text = desc, Foreground = Ink400, FontFamily = Site, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(txt, 0); g.Children.Add(txt);
        var tg = MakeToggle(on, onChange);
        tg.VerticalAlignment = VerticalAlignment.Center; tg.Margin = new Thickness(18, 0, 0, 0);
        Grid.SetColumn(tg, 1); g.Children.Add(tg);
        var row = new Border { Padding = new Thickness(16, 13, 16, 13), Background = Brushes.Transparent, Child = g };
        row.MouseEnter += (s, e) => row.Background = B("#0AFFFFFF");   // white/[0.03] hover
        row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
        return row;
    }

    // Switch: live green when on, white/15 when off (the site's toggle); white knob slides with a soft ease.
    Border MakeToggle(bool on, Action<bool> onChange)
    {
        var knob = new System.Windows.Shapes.Ellipse
        {
            Width = 20, Height = 20, Fill = TextHi, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black }
        };
        var track = new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, Child = knob, Padding = new Thickness(2, 0, 2, 0) };
        bool[] state = { on };
        track.Background = state[0] ? Live : GlassSoft;
        knob.Margin = new Thickness(state[0] ? 20 : 0, 0, 0, 0);
        track.MouseLeftButtonUp += (s, e) =>
        {
            e.Handled = true; state[0] = !state[0];
            track.Background = state[0] ? Live : GlassSoft;
            var to = new Thickness(state[0] ? 20 : 0, 0, 0, 0);
            knob.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(to, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            onChange(state[0]);
        };
        return track;
    }

    // Compact rounded-md utility button (the site's copy-button style): faint fill; glyph + label lift to brand on hover.
    Border UtilityBtn(string glyph, string label, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var ic = Icon(glyph, 12, Ink200); ic.Margin = new Thickness(0, 1, 8, 0); sp.Children.Add(ic);
        var tb = new TextBlock { Text = Track(label.ToUpperInvariant(), 1), Foreground = Ink200, FontFamily = Site, FontWeight = FontWeights.SemiBold, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(tb);
        var b = new Border { Height = 34, CornerRadius = new CornerRadius(6), Background = B("#0DFFFFFF"), Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand, Child = sp };
        b.MouseEnter += (s, e) => { b.Background = B("#14FFFFFF"); tb.Foreground = Brand300; ic.Foreground = Brand300; };
        b.MouseLeave += (s, e) => { b.Background = B("#0DFFFFFF"); tb.Foreground = Ink200; ic.Foreground = Ink200; };
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return b;
    }

    // Compact rounded-md danger button (uninstall): faint red fill, red glyph+label, deeper red on hover.
    Border DangerBtn(string glyph, string label, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var ic = Icon(glyph, 12, Danger); ic.Margin = new Thickness(0, 1, 8, 0); sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = Track(label.ToUpperInvariant(), 1), Foreground = Danger, FontFamily = Site, FontWeight = FontWeights.SemiBold, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Height = 34, CornerRadius = new CornerRadius(6), Background = B("#14F87171"), Padding = new Thickness(14, 0, 14, 0), Cursor = Cursors.Hand, Child = sp };
        b.MouseEnter += (s, e) => b.Background = B("#26F87171");
        b.MouseLeave += (s, e) => b.Background = B("#14F87171");
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return b;
    }

    // White primary pill (the site's primary button, like "PLAY NOW"): white fill, near-black label,
    // subtle hover. Uppercase tracked Poppins. Used for the settings "Done".
    Border PrimaryPill(string label, Action onClick)
    {
        var tb = new TextBlock { Text = Track(label.ToUpperInvariant(), 1), Foreground = Ink, FontFamily = Site, FontWeight = FontWeights.SemiBold, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Height = 40, MinWidth = 108, CornerRadius = new CornerRadius(20), Background = TextHi, Padding = new Thickness(24, 0, 24, 0), Cursor = Cursors.Hand, Child = tb, HorizontalAlignment = HorizontalAlignment.Right };
        b.MouseEnter += (s, e) => b.Background = B("#F0F1F4");
        b.MouseLeave += (s, e) => b.Background = TextHi;
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return b;
    }
    void ToggleSettings(bool show)
    {
        if (settingsOverlay == null) return;
        // Website style is flat (no glass): the solid scrim dims the launcher; keep the backdrop crisp.
        try { if (homeView != null) homeView.Effect = null; } catch { }
        settingsOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // Start/stop Discord Rich Presence live when its toggle changes (pref is already saved by the caller).
    void ApplyDiscordPref(bool on)
    {
        try
        {
            if (on) { if (discord == null) StartDiscord(); }
            else if (discord != null) { discord.Stop(); discord = null; }
        }
        catch { }
    }

    // ---------- branded modal dialogs (replace native MessageBox) ----------
    // Shared dialog header: RUSTORIGIN eyebrow + bold title in the site font (Poppins), so every branded
    // dialog (this modal + the update gate) reads the same as the settings panel.
    StackPanel DialogHeader(string title, bool centered)
    {
        var h = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        var a = centered ? TextAlignment.Center : TextAlignment.Left;
        var sp = new StackPanel { HorizontalAlignment = h };
        sp.Children.Add(new TextBlock { Text = Track("RUSTORIGIN", 3), Foreground = Brand400, FontFamily = Site, FontWeight = FontWeights.SemiBold, FontSize = 10, HorizontalAlignment = h, TextAlignment = a });
        sp.Children.Add(new TextBlock { Text = title, Foreground = Ink100, FontFamily = Site, FontWeight = FontWeights.Bold, FontSize = 18, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = h, TextAlignment = a });
        return sp;
    }

    // Frameless card shown over the launcher: header + message, a primary (violet) button and an
    // optional secondary. Returns true for primary / Enter, false for secondary / Esc.
    bool ModalDialog(string heading, string message, string okText, string cancelText)
    {
        bool result = false;
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, FontFamily = Site
        };
        try { win.Owner = this; } catch { }
        var card = new Border
        {
            Background = Ink900, CornerRadius = new CornerRadius(17),   // borderless (like the site cards); the shadow lifts it
            Padding = new Thickness(22, 18, 22, 20), Margin = new Thickness(28),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black }
        };
        var col = new StackPanel { MaxWidth = 300 };
        card.Child = col;
        col.Children.Add(DialogHeader(heading, false));
        col.Children.Add(new TextBlock { Text = message, Foreground = Ink200, FontFamily = Site, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (cancelText != null) row.Children.Add(DialogBtn(cancelText, false, delegate { result = false; win.Close(); }));
        row.Children.Add(DialogBtn(okText, true, delegate { result = true; win.Close(); }));
        col.Children.Add(row);
        win.Content = card;
        win.KeyDown += (s, e) => { if (e.Key == Key.Escape) { result = (cancelText == null); win.Close(); } else if (e.Key == Key.Enter) { result = true; win.Close(); } };
        try { win.ShowDialog(); } catch { }
        return result;
    }

    Border DialogBtn(string label, bool primary, Action onClick)
    {
        var tb = new TextBlock { Text = Track(label.ToUpperInvariant(), 1), Foreground = primary ? TextHi : Ink100, FontFamily = Site, FontWeight = FontWeights.SemiBold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Height = 32, MinWidth = 80, CornerRadius = new CornerRadius(16), Background = primary ? Brand600 : B("#12FFFFFF"), Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, Child = tb };
        b.MouseEnter += (s, e) => b.Background = primary ? Brand500 : Stroke;
        b.MouseLeave += (s, e) => b.Background = primary ? Brand600 : B("#12FFFFFF");
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return b;
    }

    void Alert(string heading, string message) { ModalDialog(heading, message, "OK", null); }
    bool Confirm(string heading, string message, string okText, string cancelText) { return ModalDialog(heading, message, okText, cancelText); }

    // "Uninstall client" action: delete the installed game files (frees the multi-GB client) and the
    // download cache, after a confirm. The launcher itself is kept - even its self-copied exe under
    // InstallDir is skipped if it's the running one - so the user can reinstall from the same window.
    void UninstallClient()
    {
        if (busy) { statusText.Foreground = Danger; statusText.Text = "Finish or pause the download before uninstalling."; return; }
        if (GameRunning()) { Alert("Game is running", "Close the game first."); return; }
        bool anything = IsInstalled() || IsBrokenInstall() || HasPartial();
        try { anything = anything || File.Exists(zipPath); } catch { }
        if (!anything) { Alert("Nothing to uninstall", "Nothing is installed."); return; }
        if (!Confirm("Uninstall client",
                "Delete the downloaded game files (several GB)? The launcher is kept.",
                "Uninstall", "Cancel")) return;

        ToggleSettings(false);
        busy = true; SetDlLabel("REMOVING"); statusText.Text = ""; RefreshState();
        var t = new Thread(delegate ()
        {
            string err = null;
            try
            {
                // The launcher can live in the same folder as the client (the installer defaults both to
                // C:\Rustorigin), so never delete the launcher's own files: the running exe, the installed
                // copy the shortcuts point at, and the installer's Uninstall.exe (Windows "Apps" needs it).
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string self = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
                    keep.Add(self);
                    keep.Add(Path.Combine(Path.GetDirectoryName(self), "Uninstall.exe"));
                }
                catch { }
                try
                {
                    keep.Add(Path.GetFullPath(Path.Combine(InstallDir, "RustoriginLauncher.exe")));
                    keep.Add(Path.GetFullPath(Path.Combine(InstallDir, "Uninstall.exe")));
                }
                catch { }
                DeleteDirContents(InstallDir, keep);
                try { if (Directory.Exists(InstallDir) && Directory.GetFileSystemEntries(InstallDir).Length == 0) Directory.Delete(InstallDir, false); } catch { }
                try { File.Delete(zipPath); } catch { }
                try { File.Delete(partPath); } catch { }
                try { File.Delete(metaPath); } catch { }
                try { File.Delete(chunksPath); } catch { }
                Log("uninstalled client from " + InstallDir);
            }
            catch (Exception ex) { err = ex.Message; Log("uninstall failed: " + ex.Message); }
            string e2 = err;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                busy = false;
                if (e2 != null) { statusText.Foreground = Danger; statusText.Text = "Uninstall error: " + e2; }
                else { statusText.Foreground = TextMute; statusText.Text = ""; }
                RefreshState();
            }));
        });
        t.IsBackground = true; t.Start();
    }

    // Recursively delete everything under dir, skipping the given full file paths (the launcher's own
    // files, so the client can be removed from a shared folder without touching the launcher). Best-effort per entry.
    static void DeleteDirContents(string dir, ICollection<string> keep)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.GetFiles(dir))
        {
            try
            {
                if (keep != null && keep.Contains(Path.GetFullPath(f))) continue;
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                File.Delete(f);
            }
            catch { }
        }
        foreach (string d in Directory.GetDirectories(dir))
        {
            try
            {
                DeleteDirContents(d, keep);
                if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d, false);
            }
            catch { }
        }
    }

    // ---------- native window plumbing ----------
    const int GWL_STYLE = -16, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            SizeChanged += (s, ev) => ApplyWindowRegion();   // keep the rounded region matched to the size
            ApplyWindowRegion();
        }
        catch { }
    }

    // Clip the (non-layered) window to a rounded rectangle so we keep the 32px corners without a
    // layered window. Cleared when maximized (square, fills the work area). The OS owns the region
    // once handed over, so it must not be deleted here.
    void ApplyWindowRegion()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (WindowState == WindowState.Maximized) { SetWindowRgn(hwnd, IntPtr.Zero, true); return; }
            RECT rc;
            if (!GetWindowRect(hwnd, out rc)) return;
            int w = rc.right - rc.left, h = rc.bottom - rc.top;
            if (w <= 0 || h <= 0) return;
            double scale = 1.0;
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) scale = src.CompositionTarget.TransformToDevice.M11;
            int d = (int)Math.Round(CornerR * 2 * scale);   // diameter for CreateRoundRectRgn
            SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d), true);
        }
        catch { }
    }

    protected override void OnDpiChanged(System.Windows.DpiScale oldDpi, System.Windows.DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyWindowRegion();   // region is in device pixels, so re-scale it on a DPI change
    }

    // Keep a maximized frameless window inside the monitor work area (don't cover the taskbar).
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024)   // WM_GETMINMAXINFO
        {
            try
            {
                var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                IntPtr mon = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
                if (mon != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    GetMonitorInfo(mon, ref mi);
                    RECT wa = mi.rcWork, mrc = mi.rcMonitor;
                    mmi.ptMaxPosition.x = wa.left - mrc.left;
                    mmi.ptMaxPosition.y = wa.top  - mrc.top;
                    mmi.ptMaxSize.x = wa.right  - wa.left;
                    mmi.ptMaxSize.y = wa.bottom - wa.top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    // ---------- background slideshow (cross-fading screenshots) ----------
    void BuildBackground()
    {
        // Load the embedded night screenshots (unpacked to Assets.Dir; fall back to a copy next to the exe).
        var list = new List<BitmapImage>();
        foreach (string n in new[] { "1.jpg", "2.jpg", "3.jpg" })
        {
            BitmapImage bmp = LoadBitmap(Path.Combine(Assets.Dir, n)) ?? LoadBitmap(Path.Combine(AppDir(), n));
            if (bmp != null) list.Add(bmp);
        }
        slides = list.ToArray();

        if (slides.Length == 0) { ShowFallbackBackdrop(); return; }   // no images - plain gradient fallback

        // Two stacked images: slideBack shows the current screenshot, slideFront fades the next one in.
        slideBack  = new Image { Stretch = Stretch.UniformToFill, Source = slides[0] };
        slideFront = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
        RenderOptions.SetBitmapScalingMode(slideBack, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(slideFront, BitmapScalingMode.HighQuality);
        bgHost = new Grid();
        bgHost.Children.Add(slideBack);
        bgHost.Children.Add(slideFront);
        // Soft blur on the whole background so the foreground UI reads cleanly. Rendered at half
        // resolution (blur is low-frequency, so this is invisible) with a performance bias to stay
        // cheap during the cross-fade.
        bgHost.Effect = new System.Windows.Media.Effects.BlurEffect
        {
            Radius = 14,
            KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
        };
        bgHost.CacheMode = new BitmapCache { RenderAtScale = 0.5, SnapsToDevicePixels = false };
        mainGrid.Children.Add(bgHost);

        // Auto-switch every 7s with a ~0.9s cross-fade. Always on - the slideshow is a core part of
        // the look, so it can't be disabled (no pref gate).
        slideIndex = 0;
        slideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        slideTimer.Tick += (s, e) => { try { if (slides.Length > 1) NextSlide(); } catch { } };
        slideTimer.Start();
    }

    void NextSlide() { ShowSlide((slideIndex + 1) % slides.Length); }

    // Cross-fade to a specific slide and sync the carousel dots.
    void ShowSlide(int next)
    {
        if (slides.Length == 0) next = 0; else next %= slides.Length;
        if (next == slideIndex) return;
        slideFront.Source = slides[next];
        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(900)));
        fade.Completed += (s, e) => { try { slideBack.Source = slides[next]; } catch { } };   // settle the fade onto the back layer
        slideFront.BeginAnimation(UIElement.OpacityProperty, fade);
        slideIndex = next;
        UpdateSlideDots();
    }

    // Carousel indicator dots (bottom-center): one per screenshot; active is a wide accent pill.
    // Clicking a dot jumps to that slide and resets the auto-advance timer. Hidden when <2 slides.
    void BuildSlideDots()
    {
        if (slides == null || slides.Length < 2) return;
        slideDots = new List<Border>();
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 20)
        };
        for (int i = 0; i < slides.Length; i++)
        {
            int idx = i;
            var dot = new Border
            {
                Height = 6, Width = 6, CornerRadius = new CornerRadius(3),
                Background = StrokeHi, Margin = new Thickness(5, 0, 5, 0),
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center
            };
            dot.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowSlide(idx);
                if (slideTimer != null) { slideTimer.Stop(); slideTimer.Start(); }   // full interval after a manual pick
            };
            slideDots.Add(dot);
            bar.Children.Add(dot);
        }
        mainGrid.Children.Add(bar);
        UpdateSlideDots();
    }

    void UpdateSlideDots()
    {
        if (slideDots == null) return;
        for (int i = 0; i < slideDots.Count; i++)
        {
            bool active = (i == slideIndex);
            slideDots[i].Width = active ? 20 : 6;
            slideDots[i].Background = active ? TextHi : StrokeHi;   // active = solid white, inactive = translucent white
        }
    }

    void ShowFallbackBackdrop()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#2B1C13"), 0));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#1C1A1F"), 0.42));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#0E1420"), 1));
        mainGrid.Children.Add(new Rectangle { Fill = g });
    }

    // ---------- legibility scrim ----------
    // A single subtle uniform darken so the hero text and cards stay readable over bright
    // screenshots. The heavy left/bottom gradients and the vignette were removed by request.
    void BuildGradient()
    {
        mainGrid.Children.Add(new Rectangle { Fill = B("#400A0C11"), IsHitTestVisible = false });
    }

    // ---------- content ----------
    void BuildContent()
    {
        // The UI is designed on a fixed 1440x860 canvas; a Viewbox scales it uniformly to the
        // window so it looks right when resized/maximized (the background fills behind it).
        homeView = new Grid { Width = 960, Height = 680 };
        BuildHero(homeView);
        BuildServerGrid(homeView);
        mainGrid.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = homeView });
    }

    // Social links pinned to the top-left of the launcher (aligned to the hero's left gutter).
    void BuildSocials(Grid content)
    {
        var social = SocialRow(new Thickness(14, 12, 0, 0));
        if (social == null) return;
        social.HorizontalAlignment = HorizontalAlignment.Left;
        social.VerticalAlignment = VerticalAlignment.Top;
        content.Children.Add(social);
    }

    // ---- social links (a horizontal row of round icon buttons) ----
    StackPanel SocialRow(Thickness margin)
    {
        if (Socials == null || Socials.Count == 0) return null;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = margin };
        foreach (var s in Socials)
        {
            var btn = SocialButton(s);
            if (btn != null) bar.Children.Add(btn);
        }
        return bar.Children.Count > 0 ? bar : null;
    }

    Border SocialButton(SocialEntry s)
    {
        var glyph = SocialGlyph(s.Platform);
        if (glyph == null) return null;   // unknown platform -> skip rather than draw a blank pill
        var box = new Viewbox { Width = 16, Height = 16, Child = glyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = Stroke, BorderBrush = GlassSoft, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0), Cursor = Cursors.Hand, Child = box, ToolTip = s.Url
        };
        b.MouseEnter += (o, e) => { b.Background = GlassHover; glyph.Fill = TextHi; };
        b.MouseLeave += (o, e) => { b.Background = Stroke; glyph.Fill = TextDim; };
        b.MouseLeftButtonUp += (o, e) => { e.Handled = true; OpenUrl(s.Url); };
        return b;
    }

    // Brand marks as inline vector paths (24x24 viewBox, Simple Icons / CC0), so no extra asset files
    // ship and the glyph recolors on hover. Returns null for an unrecognised platform key.
    System.Windows.Shapes.Path SocialGlyph(string key)
    {
        string d;
        switch (key)
        {
            case "discord":
                d = "M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9459 2.4189-2.1568 2.4189Z";
                break;
            case "youtube":
                d = "M23.498 6.186a3.016 3.016 0 0 0-2.122-2.136C19.505 3.545 12 3.545 12 3.545s-7.505 0-9.377.505A3.017 3.017 0 0 0 .502 6.186C0 8.07 0 12 0 12s0 3.93.502 5.814a3.016 3.016 0 0 0 2.122 2.136c1.871.505 9.376.505 9.376.505s7.505 0 9.377-.505a3.015 3.015 0 0 0 2.122-2.136C24 15.93 24 12 24 12s0-3.93-.502-5.814zM9.545 15.568V8.432L15.818 12l-6.273 3.568z";
                break;
            case "tiktok":
                d = "M12.525.02c1.31-.02 2.61-.01 3.91-.02.08 1.53.63 3.09 1.75 4.17 1.12 1.11 2.7 1.62 4.24 1.79v4.03c-1.44-.05-2.89-.35-4.2-.97-.57-.26-1.1-.59-1.62-.93-.01 2.92.01 5.84-.02 8.75-.08 1.4-.54 2.79-1.35 3.94-1.31 1.92-3.58 3.17-5.91 3.21-1.43.08-2.86-.31-4.08-1.03-2.02-1.19-3.44-3.37-3.65-5.71-.02-.5-.03-1-.01-1.49.18-1.9 1.12-3.72 2.58-4.96 1.66-1.44 3.98-2.13 6.15-1.72.02 1.48-.04 2.96-.04 4.44-.99-.32-2.15-.23-3.02.37-.63.41-1.11 1.04-1.36 1.75-.21.51-.15 1.08-.14 1.62.24 1.64 1.82 3.02 3.5 2.87 1.12-.01 2.19-.66 2.77-1.61.19-.33.4-.67.41-1.06.1-1.79.06-3.57.07-5.36.01-4.03-.01-8.05.02-12.07z";
                break;
            default:
                return null;
        }
        return new System.Windows.Shapes.Path { Data = Geometry.Parse(d), Fill = TextDim, Stretch = Stretch.Uniform };
    }

    static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ---- small helpers ----
    static TextBlock Icon(string glyph, double size, Brush brush)
    {
        return new TextBlock
        {
            Text = glyph, FontFamily = Icons, FontSize = size, Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    static LinearGradientBrush Grad(string c1, string c2)
    {
        var lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c1), 0));
        lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c2), 1));
        return lg;
    }

    // ---- hero block ----
    void BuildHero(Grid content)
    {
        var hero = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(44, 0, 0, 0), Width = 400
        };

        if (logoBmp != null)
        {
            var big = new Image { Source = logoBmp, Width = 200, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 10, Opacity = 0.6, Color = Colors.Black } };
            RenderOptions.SetBitmapScalingMode(big, BitmapScalingMode.HighQuality);
            hero.Children.Add(big);
        }

        hero.Children.Add(new TextBlock
        {
            Text = GameTitle, Foreground = TextHi, FontSize = 44, FontFamily = Brand, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(-2, 12, 0, 0), LineHeight = 44, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        });
        hero.Children.Add(new TextBlock
        {
            Text = Track("JANUARY UPDATE 2021", 1),
            Foreground = Brand400, FontSize = 13, FontFamily = Brand, FontWeight = FontWeights.SemiBold, Margin = new Thickness(1, 8, 0, 0)   // violet brand eyebrow, matching the site
        });
        hero.Children.Add(new TextBlock
        {
            Text = Tagline, Foreground = TextDim, FontSize = 14.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, 14, 0, 0), MaxWidth = 370, HorizontalAlignment = HorizontalAlignment.Left,
            LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        playBtn = PlayButton();
        installBtn = LinkButton("", "INSTALL", OnDownloadButton);
        row.Children.Add(playBtn);
        row.Children.Add(installBtn);
        hero.Children.Add(row);

        statusText = new TextBlock { Text = "", Foreground = TextMute, FontSize = 12.5, Margin = new Thickness(1, 14, 0, 0) };
        hero.Children.Add(statusText);

        content.Children.Add(hero);
    }

    Border PlayButton()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ic = new System.Windows.Shapes.Polygon { Points = new PointCollection { new Point(0, 0), new Point(0, 11), new Point(10, 5.5) }, Fill = Ink, Width = 10, Height = 11, Stretch = Stretch.Fill, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };   // proper filled play triangle
        var tb = new TextBlock { Text = Track("PLAY", 1), FontSize = 13, FontFamily = Site, FontWeight = FontWeights.SemiBold,
            Foreground = Ink, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(ic); sp.Children.Add(tb);
        var b = new Border
        {
            Height = 40, MinWidth = 112, CornerRadius = new CornerRadius(20), Cursor = Cursors.Hand,
            Background = TextHi, Padding = new Thickness(20, 0, 22, 0), Child = sp
        };
        b.MouseEnter += (s, e) => { if (b.IsEnabled) b.Background = B("#F0F1F4"); };   // subtle hover (site primary)
        b.MouseLeave += (s, e) => b.Background = TextHi;
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (b.IsEnabled && !busy) Play(LaunchArgs); };
        b.Tag = new object[] { true, tb, ic };
        return b;
    }

    Border LinkButton(string glyph, string text, Action onClick)
    {
        // Content (icon + label) carries the horizontal breathing room the Border padding used to give,
        // so the progress fill can span the full pill edge-to-edge behind it.
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, 0, 22, 0) };
        var ic = Icon(glyph, 13, Ink); ic.Margin = new Thickness(0, 1, 9, 0);
        var tb = new TextBlock { Text = Track(text, 1), FontSize = 13, FontFamily = Site, FontWeight = FontWeights.SemiBold,
            Foreground = Ink, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(ic); sp.Children.Add(tb);

        // Download progress + status live INSIDE this button: a left-anchored fill grows behind the
        // label while a download runs, and the label itself shows the live status. The grid is clipped
        // to the pill so the fill keeps the rounded ends.
        installFill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = B("#598B5CF6") };   // translucent violet brand fill
        installContent = sp;
        var g = new Grid();
        g.Children.Add(installFill);
        g.Children.Add(sp);
        g.SizeChanged += (s, e) => { try { g.Clip = new RectangleGeometry(new Rect(0, 0, g.ActualWidth, g.ActualHeight), 20, 20); } catch { } };

        // Full white pill, matching PLAY. Left margin is set in RefreshState so it aligns to the
        // hero's left edge when it is the leading button (PLAY hidden).
        var b = new Border
        {
            Height = 40, MinWidth = 112, CornerRadius = new CornerRadius(20), Cursor = Cursors.Hand,
            Background = TextHi, Child = g,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)   // for the pause/resume press pop
        };
        b.MouseEnter += (s, e) => { if (b.IsEnabled) b.Background = B("#F0F1F4"); };   // subtle hover (site primary)
        b.MouseLeave += (s, e) => b.Background = TextHi;
        // No !busy guard here: this button doubles as PAUSE while a download runs.
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (b.IsEnabled) onClick(); };
        b.Tag = new object[] { false, tb, ic };
        return b;
    }

    // Set the in-button download progress (0..1): grows the fill behind the install/pause label.
    void SetInstallProgress(double frac)
    {
        if (installFill == null || installBtn == null) return;
        if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
        installFill.Width = installBtn.ActualWidth * frac;
    }

    // Set the live download status shown as the button's own label while busy. Stored in dlLabel so the
    // periodic RefreshState re-applies it instead of reverting the button to a generic caption.
    void SetDlLabel(string s)
    {
        dlLabel = s ?? "";
        if (installBtn != null) ((TextBlock)((object[])installBtn.Tag)[1]).Text = Track(dlLabel, 1);
        AnimateInstallWidth();
    }

    // Smoothly animate the install/pause button's width to fit its current label (it changes as the
    // caption cycles INSTALL -> CONNECTING -> DOWNLOADING xx% -> VERIFYING -> ...), instead of snapping.
    void AnimateInstallWidth()
    {
        if (installBtn == null || installContent == null || installBtn.Visibility != Visibility.Visible) return;
        installContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double to = Math.Max(installBtn.MinWidth, Math.Ceiling(installContent.DesiredSize.Width));
        double from = installBtn.ActualWidth;
        if (from <= 0) { installBtn.Width = to; return; }        // first layout: no animation
        if (Math.Abs(from - to) < 0.5) return;                   // label width unchanged (e.g. 42% -> 43%)
        installBtn.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    static string Pct(double frac) { if (frac < 0) frac = 0; else if (frac > 1) frac = 1; return ((int)Math.Round(frac * 100)) + "%"; }

    // Same as SetDlLabel but safe to call from the download worker thread.
    void SetDlLabelAsync(string s) { Dispatcher.BeginInvoke((Action)(() => SetDlLabel(s))); }

    // A quick press-and-release "pop" on a pill button (used for the pause/resume tap feedback).
    static void PulseButton(Border b)
    {
        var st = b == null ? null : b.RenderTransform as ScaleTransform;
        if (st == null) return;
        var pop = new DoubleAnimationUsingKeyFrames();
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.93, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),  new CubicEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(210)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    void SetButtonEnabled(Border b, bool enabled)
    {
        b.IsEnabled = enabled;
        b.Opacity = enabled ? 1.0 : 0.45;
        b.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
    }

    // ---- right column: server cards ----
    void BuildServerGrid(Grid content)
    {
        serverStatusRefreshers.Clear();
        var wrap = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 44, 0) };
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 1 };   // vertical list
        string[][] pal = {
            new[]{"#3A2E5A","#141020"}, new[]{"#2C4A3A","#121C17"}, new[]{"#2F3D5A","#12161F"},
            new[]{"#5A3A26","#1C1512"}, new[]{"#4A2F2F","#1C1212"}, new[]{"#3A4A5A","#141A20"} };
        for (int i = 0; i < Servers.Count && i < 6; i++)
            grid.Children.Add(ServerTile(Servers[i], pal[i % pal.Length][0], pal[i % pal.Length][1]));
        wrap.Children.Add(grid);
        StartServerStatusTimer();

        var more = new Border
        {
            Height = 40, CornerRadius = new CornerRadius(20), Background = B("#0FFFFFFF"),   // site glass secondary (white/6)
            BorderThickness = new Thickness(0), Padding = new Thickness(22, 0, 18, 0),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0), Cursor = Cursors.Hand
        };
        var ms = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        ms.Children.Add(new TextBlock { Text = Track("DISCOVER MORE", 1), Foreground = Ink100, FontFamily = Site, FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var ch = Icon("\uE76C", 10, Ink100); ch.Margin = new Thickness(8, 1, 0, 0); ms.Children.Add(ch);
        more.Child = ms;
        more.MouseEnter += (s, e) => more.Background = B("#1FFFFFFF");   // site glass hover (white/12)
        more.MouseLeave += (s, e) => more.Background = B("#0FFFFFFF");
        more.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenUrl("https://rustorigin.com"); };   // open the site
        wrap.Children.Add(more);

        content.Children.Add(wrap);
    }

    // Server card: cover image with a compact bottom overlay - status dot + name + live player count,
    // and the online-players bar (violet fill). Clickable to launch/join; hover shows a play glyph.
    Grid ServerTile(ServerEntry srv, string c1, string c2)
    {
        const double W = 300, H = 150, PAD = 12;
        double barW = W - PAD * 2;
        string eff = srv.Args.Length > 0 ? srv.Args : LaunchArgs;
        string qHost; int qPort;
        bool canQuery = TryParseConnect(eff, out qHost, out qPort);
        bool hasWeb = !string.IsNullOrEmpty(srv.StatusUrl);
        bool live = canQuery || hasWeb;
        bool joinable = srv.Args.Length > 0 || canQuery;

        var wrap = new Grid { Width = W, Height = H, Margin = new Thickness(0, 0, 0, 12), Cursor = joinable ? Cursors.Hand : Cursors.Arrow };
        var card = new Border { Background = Ink900, CornerRadius = new CornerRadius(17) };
        card.SizeChanged += (s, e) => { try { card.Clip = new RectangleGeometry(new Rect(0, 0, card.ActualWidth, card.ActualHeight), 17, 17); } catch { } };
        wrap.Children.Add(card);
        var inner = new Grid();
        card.Child = inner;

        // cover image (or gradient fallback)
        BitmapImage cov = null;
        if (!string.IsNullOrEmpty(srv.Cover))
            cov = LoadBitmap(Path.Combine(Assets.Dir, srv.Cover)) ?? LoadBitmap(Path.Combine(AppDir(), srv.Cover));
        if (cov != null)
        {
            var img = new Image { Source = cov, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            img.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 3, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };   // subtle blur so the overlays read
            inner.Children.Add(img);
        }
        else inner.Children.Add(new Rectangle { Fill = Grad(c1, c2) });

        // bottom scrim so the overlay reads on any screenshot
        var scrim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        scrim.GradientStops.Add(new GradientStop(Colors.Transparent, 0.35));
        scrim.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#CC000000"), 1));
        inner.Children.Add(new Rectangle { Fill = scrim, IsHitTestVisible = false });

        // hover: dim + centred play glyph
        if (joinable)
        {
            var hover = new Grid { Opacity = 0 };
            hover.Children.Add(new Rectangle { Fill = B("#40000000") });
            hover.Children.Add(Icon("\uE768", 30, TextHi));
            inner.Children.Add(hover);
            wrap.MouseEnter += (s, e) => hover.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(120))));
            wrap.MouseLeave += (s, e) => hover.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(120))));
            wrap.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (!busy) Play(eff); };
        }

        // ---- bottom info: [dot + name] .... [count], then the players bar ----
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(PAD, 0, PAD, PAD), IsHitTestVisible = false };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse { Width = 8, Height = 8, Fill = B("#6B7280"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) };
        nameSp.Children.Add(dot);
        nameSp.Children.Add(new TextBlock { Text = srv.Name.ToUpperInvariant(), Foreground = TextHi, FontFamily = Site, FontWeight = FontWeights.Bold, FontSize = 14, MaxWidth = 175, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(nameSp, 0); top.Children.Add(nameSp);
        var count = new TextBlock { Text = live ? "--" : (srv.Players.Length > 0 ? srv.Players : "SOON"), Foreground = TextHi, FontFamily = Site, FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(count, 1); top.Children.Add(count);
        info.Children.Add(top);

        // online-players bar (track white/15, fill violet)
        var barFill = new Border { Height = 5, CornerRadius = new CornerRadius(3), Background = Brand500, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        info.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(3), Background = GlassSoft, Margin = new Thickness(0, 8, 0, 0), Child = barFill });
        inner.Children.Add(info);

        // edge hairline
        inner.Children.Add(new Border { CornerRadius = new CornerRadius(17), BorderBrush = Stroke, BorderThickness = new Thickness(1), IsHitTestVisible = false });

        // live status wiring: dot colour, count, and bar fill (refreshes via the shared ~60s timer)
        if (live)
        {
            Action<int, int, int> apply = delegate (int st, int pcur, int pmax)
            {
                if (st == 0) { dot.Fill = StatChecking; count.Text = "\u2022 \u2022 \u2022"; }
                else if (st == 1)
                {
                    dot.Fill = Live; count.Text = pmax > 0 ? (pcur + " / " + pmax) : pcur.ToString();
                    double ratio = pmax > 0 ? Math.Max(0.0, Math.Min(1.0, (double)pcur / pmax)) : 0;
                    barFill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(barW * ratio, new Duration(TimeSpan.FromMilliseconds(400))));
                }
                else { dot.Fill = B("#6B7280"); count.Text = "Offline"; barFill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(200)))); }
            };
            Action refresh = hasWeb ? (Action)delegate { QueryServerStatusWeb(srv.StatusUrl, apply); }
                                    : delegate { QueryServerStatus(qHost, qPort, apply); };
            serverStatusRefreshers.Add(refresh);
            refresh();
        }

        return wrap;
    }

    // Pull "host:port" out of a "+connect host:port" launch-args string (the A2S query endpoint).
    static bool TryParseConnect(string args, out string host, out int port)
    {
        host = null; port = 0;
        if (string.IsNullOrEmpty(args)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(args, @"\+connect\s+([^\s:]+):(\d{1,5})");
        if (!m.Success) return false;
        host = m.Groups[1].Value;
        return int.TryParse(m.Groups[2].Value, out port) && port > 0 && port <= 65535;
    }

    // Query one server's live player count over A2S on a background thread. onResult runs on the UI
    // thread with (state, players, max): state 0 = checking, 1 = online, 2 = unreachable.
    void QueryServerStatus(string host, int port, Action<int, int, int> onResult)
    {
        onResult(0, 0, 0);
        var t = new Thread(delegate ()
        {
            int players, max;
            bool ok = A2S.TryQueryInfo(host, port, 2500, out players, out max);
            Dispatcher.BeginInvoke((Action)delegate { onResult(ok ? 1 : 2, players, max); });
        }) { IsBackground = true, Name = "a2s" };
        t.Start();
    }

    // Read a server's live player count from the website stats feed instead of A2S. The feed is the
    // OriginStatsPublisher payload for ONE server: its top-level "server" block carries
    // "players"/"maxPlayers"; a slim {"players":N,"maxPlayers":N} works too. onResult runs on the UI
    // thread with (state, players, max): 0 = checking, 1 = online, 2 = unreachable.
    void QueryServerStatusWeb(string url, Action<int, int, int> onResult)
    {
        onResult(0, 0, 0);
        var t = new Thread(delegate ()
        {
            int players = 0, max = 0;
            bool ok = false;
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                    string bust = url + (url.IndexOf('?') >= 0 ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
                    string json = wc.DownloadString(bust);
                    var mp = System.Text.RegularExpressions.Regex.Match(json, "\"players\"\\s*:\\s*(\\d+)");
                    var mm = System.Text.RegularExpressions.Regex.Match(json, "\"maxPlayers\"\\s*:\\s*(\\d+)");
                    if (mp.Success && int.TryParse(mp.Groups[1].Value, out players))
                    {
                        ok = true;
                        if (mm.Success) int.TryParse(mm.Groups[1].Value, out max);
                    }
                }
            }
            catch { ok = false; }
            Dispatcher.BeginInvoke((Action)delegate { onResult(ok ? 1 : 2, players, max); });
        }) { IsBackground = true, Name = "status-web" };
        t.Start();
    }
    // Re-query every card's live status once a minute (started once, after the grid is built).
    void StartServerStatusTimer()
    {
        if (serverStatusTimerStarted) return;
        serverStatusTimerStarted = true;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        t.Tick += delegate { foreach (var r in serverStatusRefreshers.ToArray()) { try { r(); } catch { } } };
        t.Start();
    }

    // ---------- config ----------
    void LoadConfig()
    {
        // embedded defaults first, then an optional launcher.cfg next to the exe overrides them
        ApplyConfig(Assets.ConfigLines());
        try
        {
            string cfg = Path.Combine(AppDir(), "launcher.cfg");
            if (File.Exists(cfg)) ApplyConfig(File.ReadAllLines(cfg));
        }
        catch { }
    }

    void ApplyConfig(string[] lines)
    {
        bool clearedServers = false;
        bool clearedSocial = false;
        try
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "downloadurl": DownloadUrl = v; break;
                    case "updaterepo":  UpdateRepo = v; break;
                    case "discordappid":     DiscordAppId = v; break;
                    case "discordlargeimage": DiscordLargeImage = v; break;
                    case "discordbuttonlabel": DiscordButtonLabel = v; break;
                    case "discordbuttonurl":   DiscordButtonUrl = v; break;
                    case "sha256":
                    case "clientsha256":
                    case "expectedsha256": ExpectedSha256 = v; break;
                    case "installdir":  if (v.Length > 0) InstallDir = v; break;
                    case "launchexe":   LaunchExe = v; break;
                    case "launchargs":  LaunchArgs = v; break;
                    case "downloadconnections": { int dc; if (int.TryParse(v, out dc)) DownloadConnections = Math.Max(1, Math.Min(16, dc)); break; }
                    case "version":     Version = v; break;
                    case "title":       if (v.Length > 0) GameTitle = v; break;
                    case "tagline":     if (v.Length > 0) Tagline = v; break;
                    case "player":      if (v.Length > 0) PlayerName = v; break;
                    case "server":
                        // Server=Tag|Name|launch args|players|cover|statusUrl   (all but Name optional).
                        // statusUrl is a website stats-feed URL for live player count (preferred over
                        // A2S). A source that defines servers replaces the list from the previous source.
                        if (!clearedServers) { Servers.Clear(); clearedServers = true; }
                        var parts = v.Split(new[] { '|' }, 6);
                        if (parts.Length >= 2)
                            Servers.Add(new ServerEntry(parts[0].Trim(), parts[1].Trim(),
                                parts.Length > 2 ? parts[2].Trim() : "", parts.Length > 3 ? parts[3].Trim() : "",
                                parts.Length > 4 ? parts[4].Trim() : "", parts.Length > 5 ? parts[5].Trim() : ""));
                        break;
                    case "social":
                        // Social=platform|url  -> bottom-left social icon linking out. A source that
                        // defines Social= lines replaces the list from the previous source.
                        if (!clearedSocial) { Socials.Clear(); clearedSocial = true; }
                        var sp = v.Split(new[] { '|' }, 2);
                        if (sp.Length == 2 && sp[0].Trim().Length > 0 && sp[1].Trim().Length > 0)
                            Socials.Add(new SocialEntry(sp[0].Trim().ToLowerInvariant(), sp[1].Trim()));
                        break;
                }
            }
        }
        catch { }
    }

    string FindGameExe()
    {
        try
        {
            string direct = Path.Combine(InstallDir, LaunchExe);
            if (File.Exists(direct)) return direct;
            if (Directory.Exists(InstallDir))
            {
                var hits = Directory.GetFiles(InstallDir, LaunchExe, SearchOption.AllDirectories);
                if (hits.Length > 0) return hits[0];
            }
            string here = Path.Combine(AppDir(), LaunchExe);
            if (File.Exists(here)) return here;
        }
        catch { }
        return null;
    }

    string InstallMarkerPath() { return Path.Combine(InstallDir, ".rustorigin-installed"); }

    // Structural completeness: the game exe AND a real Unity data folder next to it. A finished
    // install is either marked by our completion marker (written after a full extract) OR is
    // structurally sound - the `<exe>_Data` folder exists and is non-empty AND `UnityPlayer.dll`
    // sits next to the exe. A half-extracted client (RustClient.exe present but RustClient_Data
    // missing/empty, or UnityPlayer.dll gone) does NOT count as installed, so the launcher offers a
    // repair instead of launching a broken game. A valid pre-existing install (no marker) still
    // passes on the structural check. This is a cheap sanity check, not cryptographic verification.
    bool InstallComplete(string exe)
    {
        if (exe == null) return false;
        try { if (File.Exists(InstallMarkerPath())) return true; } catch { }
        try
        {
            string dir  = Path.GetDirectoryName(exe);
            string data = Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + "_Data");
            bool dataOk = Directory.Exists(data) && DirHasEntries(data);
            bool unityOk = File.Exists(Path.Combine(dir, "UnityPlayer.dll"));
            return dataOk && unityOk;
        }
        catch { return false; }
    }

    static bool DirHasEntries(string dir)
    {
        try { var e = Directory.EnumerateFileSystemEntries(dir).GetEnumerator(); return e.MoveNext(); }
        catch { return false; }
    }

    bool IsInstalled() { return InstallComplete(FindGameExe()); }

    // The game exe is present but the install is structurally incomplete (needs a reinstall/repair).
    bool IsBrokenInstall() { string exe = FindGameExe(); return exe != null && !InstallComplete(exe); }

    bool HasPartial() { return PartialBytes() > 0; }

    // Bytes already downloaded into the saved .part: from its chunk map when the parallel downloader
    // wrote it (the file is preallocated to full size, so its length says nothing), else the file
    // length (a single-stream partial is one contiguous block from the start).
    long PartialBytes()
    {
        try
        {
            if (!File.Exists(partPath)) return 0;
            if (!File.Exists(chunksPath)) return new FileInfo(partPath).Length;
            long total;
            string[] h = File.ReadAllLines(chunksPath)[0].Split(' ');
            if (h.Length != 3 || !long.TryParse(h[2], out total)) return 0;
            bool[] done = ReadChunkMap(total);
            return done == null ? 0 : DoneBytes(done, total);
        }
        catch { return 0; }
    }

    Process gameProc;   // the client this launcher started (if any)
    bool hiddenForGame;                          // true while hidden to the tray for a running game
    WindowState preGameState = WindowState.Normal;  // window state to restore to when the game exits

    // True only when OUR client is running: the one we launched this session, or a process whose exe
    // is the installed client under InstallDir. A same-named process elsewhere on the PC (e.g. another
    // Rust client) must NOT count - otherwise the button shows IN-GAME while nothing is installed here.
    bool GameRunning()
    {
        try { if (gameProc != null && !gameProc.HasExited) return true; } catch { }
        try
        {
            string exe = FindGameExe();
            if (exe == null) return false;                 // nothing installed here -> our game can't be running
            string full = Path.GetFullPath(exe);
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LaunchExe)))
            {
                try { if (string.Equals(Path.GetFullPath(p.MainModule.FileName), full, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }                                   // access denied / bitness mismatch -> skip this one
            }
        }
        catch { }
        return false;
    }

    void StartGameTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.Tick += delegate { try { RefreshState(); if (hiddenForGame && !GameRunning()) RestoreFromGame(); } catch { } };
        t.Start();
    }

    void RefreshState()
    {
        bool installed = IsInstalled();
        bool partial   = HasPartial();
        bool game      = GameRunning();
        bool broken    = !installed && !game && IsBrokenInstall();   // exe present but files missing

        // The in-button progress fill is only for an active download; clear it once idle.
        if (!busy) SetInstallProgress(0);   // clear the in-button fill when idle (finished, paused, errored)

        // PLAY: shown only when the client is installed (or our game is running) - hidden otherwise.
        // While the game runs it becomes IN-GAME (clicking it focuses the running game).
        object[] pmeta = (object[])playBtn.Tag;
        bool showPlay = installed || game;
        playBtn.Visibility = showPlay ? Visibility.Visible : Visibility.Collapsed;
        if (showPlay)
        {
            SetButtonEnabled(playBtn, !busy || game);
            ((TextBlock)pmeta[1]).Text = Track(game ? "IN-GAME" : "PLAY", 1);
        }

        // Download button: PAUSE while a download runs, else RESUME (a partial exists) or INSTALL;
        // hidden once installed with nothing to resume. When it leads (PLAY hidden) it sits flush to
        // the hero's left edge; when PLAY is shown it gets a small gap after it.
        object[] imeta = (object[])installBtn.Tag;
        installBtn.Margin = new Thickness(showPlay ? 14 : 0, 0, 0, 0);
        if (busy)
        {
            installBtn.Visibility = Visibility.Visible;
            SetButtonEnabled(installBtn, true);                 // clickable to pause
            ((TextBlock)imeta[1]).Text = Track(dlLabel.Length > 0 ? dlLabel : "WORKING", 1);   // live status inside the button
            ((TextBlock)imeta[2]).Text = "";                    // hide the glyph; the fill + status tell the story
        }
        else if (installed && !partial)
        {
            installBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            installBtn.Visibility = Visibility.Visible;
            SetButtonEnabled(installBtn, true);
            ((TextBlock)imeta[1]).Text = Track(partial ? "RESUME" : (broken ? "REPAIR" : "INSTALL"), 1);
            ((TextBlock)imeta[2]).Text = partial ? "\uE768" : "\uE896";
        }
        AnimateInstallWidth();   // smoothly grow/shrink the pill to fit its new caption

        if (!busy)
        {
            if (broken) { statusText.Foreground = Danger; statusText.Text = "Install looks incomplete or corrupted - click Repair to reinstall."; }
            else { statusText.Foreground = TextMute; statusText.Text = ""; }
        }
    }

    // ---------- install (resumable, TLS 1.2+) ----------
    // Force TLS 1.2 (and 1.3 where the OS supports it): Cloudflare/R2 and most hosts refuse older protocols,
    // and .NET Framework may otherwise negotiate TLS 1.0/1.1 on some machines and fail.
    public static void ConfigureTls()
    {
        try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }  // Tls12 | Tls13
        catch { try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } }            // Tls12 only
        ServicePointManager.DefaultConnectionLimit = 16;   // room for the parallel download (DownloadConnections max) + other requests
        ServicePointManager.Expect100Continue = false;
    }

    void StartInstall()
    {
        if (busy) return;
        if (string.IsNullOrEmpty(DownloadUrl) || DownloadUrl.Contains("REPLACE-ME"))
        { statusText.Foreground = Danger; statusText.Text = "Set DownloadUrl in launcher.cfg first."; return; }
        // Verification is mandatory: refuse to download/install anything we can't check.
        if (NormalizedExpectedHash().Length == 0)
        { statusText.Foreground = Danger; statusText.Text = "Set Sha256 in launcher.cfg first - downloads must be verified before install."; return; }
        try { Directory.CreateDirectory(InstallDir); Directory.CreateDirectory(cacheDir); }
        catch (Exception ex) { statusText.Foreground = Danger; statusText.Text = "Folder error: " + ex.Message; return; }

        busy = true; cancelRequested = false;
        statusText.Foreground = TextMute; statusText.Text = "";
        SetInstallProgress(0);
        SetDlLabel("CONNECTING");
        RefreshState();

        Log("StartInstall: url=" + DownloadUrl + "  installDir=" + InstallDir);
        dlThread = new Thread(DownloadWorker) { IsBackground = true, Name = "download" };
        dlThread.Start();
    }

    // The hero download button: PAUSE while a download runs, otherwise INSTALL/RESUME.
    void OnDownloadButton()
    {
        PulseButton(installBtn);   // small tactile pop on pause/resume
        if (busy) { CancelDownload(); SetDlLabel("PAUSING"); statusText.Text = ""; }
        else StartInstall();
    }

    void CancelDownload()
    {
        cancelRequested = true;
        try { var r = activeReq; if (r != null) r.Abort(); } catch { }
    }

    // Timestamped diagnostics in <cache>\launcher.log (players can send this when a download misbehaves).
    static readonly object logLock = new object();
    void Log(string msg)
    {
        try
        {
            lock (logLock)
            {
                Directory.CreateDirectory(cacheDir);
                File.AppendAllText(Path.Combine(cacheDir, "launcher.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
        }
        catch { }
    }

    void ReportProgress(long have, long total, double mbps, bool resumed)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (total > 0) SetInstallProgress((double)have / total);
            SetDlLabel((resumed ? "INSTALLING" : "DOWNLOADING") + (total > 0 ? "  " + Pct((double)have / total) : ""));
        }));
    }

    // Background thread. Downloads to <cache>\RustClient.zip.part using HTTP Range, so an interrupted
    // transfer continues where it stopped - across automatic retries AND across launcher restarts.
    void DownloadWorker()
    {
        string error = null; bool cancelled = false; bool verifyFailed = false; string verifyMsg = null;
        try
        {
            // 0) Reuse a cached RustClient.zip (e.g. left by an interrupted extract) rather than
            //    re-downloading the whole client. It was already SHA-256-verified when it was written,
            //    so it is extracted directly - no re-hash. If extraction fails (the zip is corrupt),
            //    it's discarded and a fresh, verified download runs. (The mandatory verification on a
            //    fresh download - the trust anchor - is unchanged; only the redundant re-hash is gone.)
            bool usedCache = false;
            if (!cancelRequested && File.Exists(zipPath))
            {
                Log("cached zip present (" + new FileInfo(zipPath).Length + " bytes); reusing without re-download");
                SetDlLabelAsync("EXTRACTING");
                try
                {
                    Directory.CreateDirectory(InstallDir);
                    try { File.Delete(InstallMarkerPath()); } catch { }
                    ExtractZip(zipPath, InstallDir);
                    Log("extract done (from cached zip)");
                    try { File.WriteAllText(InstallMarkerPath(), DateTime.Now.ToString("o")); } catch { }
                    try { File.Delete(zipPath); } catch { }
                    InstallLauncherAndShortcut();
                    usedCache = true;
                }
                catch (Exception cex)
                {
                    Log("cached zip extract failed (" + cex.Message + "); discarding and downloading fresh");
                    try { File.Delete(zipPath); } catch { }   // corrupt cached zip -> re-download
                }
            }

            if (!usedCache && !cancelled)
            {
            // 1) size + identity of the remote file (HEAD; optional)
            long total = -1; string etag = "";
            try
            {
                var h = (HttpWebRequest)WebRequest.Create(DownloadUrl);
                h.Method = "HEAD"; h.Timeout = 30000; h.UserAgent = UA; h.AllowAutoRedirect = true;
                using (var r = (HttpWebResponse)h.GetResponse())
                { total = r.ContentLength; etag = (r.Headers["ETag"] ?? r.Headers["Last-Modified"] ?? "").Trim(); }
            }
            catch (Exception hx) { Log("HEAD failed (ok, will learn size from GET): " + hx.Message); }
            Log("HEAD: total=" + total + " etag=" + etag);

            // 2) reuse a saved .part only if it belongs to this exact remote file
            long fileLen = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            string want  = DownloadUrl + "|" + etag + "|" + total;
            string saved = File.Exists(metaPath) ? File.ReadAllText(metaPath) : "";
            if (File.Exists(partPath) && (saved != want || (total > 0 && fileLen > total)))
            {
                Log("discarding stale .part (" + fileLen + " bytes): meta mismatch");
                try { File.Delete(partPath); } catch { }
                try { File.Delete(chunksPath); } catch { }
                fileLen = 0;
            }
            File.WriteAllText(metaPath, want);

            // 3a) parallel: several HTTP Range connections at once (needs a known size)
            bool parallel = total > 0 && DownloadConnections > 1;
            if (parallel)
            {
                // a parallel partial is always preallocated to full size; otherwise its map can't be trusted
                bool[] done = fileLen == total ? ReadChunkMap(total) : null;
                if (done == null)
                {
                    done = new bool[ChunkCount(total)];
                    // a partial from the single-stream downloader is one contiguous block from the start
                    if (fileLen > 0 && !File.Exists(chunksPath))
                        for (int i = 0; i < done.Length; i++) done[i] = (long)i * ChunkSize + ChunkLen(i, total) <= fileLen;
                    try { File.Delete(chunksPath); } catch { }   // unreadable/foreign map: start that part clean
                }
                long have = DoneBytes(done, total);
                Log("start (parallel x" + DownloadConnections + "): have=" + have + (have > 0 ? " (resuming)" : ""));
                try { DownloadParallel(total, done, have > 0); Log("download complete: " + total + " bytes"); }
                catch (OperationCanceledException) { cancelled = true; Log("cancelled by user"); }
                catch (RangeNotSupportedException)
                {
                    // the server stopped honoring Range: fall back to one stream from the start
                    Log("server ignored Range; falling back to a single stream");
                    try { File.Delete(partPath); } catch { }
                    try { File.Delete(chunksPath); } catch { }
                    parallel = false;
                }
            }

            // 3b) single stream: unknown size, DownloadConnections=1, or Range not honored
            if (!parallel && !cancelled)
            {
            // a parallel .part is preallocated; keep only its complete prefix so Range-append resume is valid
            if (File.Exists(chunksPath))
            {
                bool[] pdone = total > 0 ? ReadChunkMap(total) : null;
                long prefix = 0;
                if (pdone != null) for (int i = 0; i < pdone.Length && pdone[i]; i++) prefix += ChunkLen(i, total);
                try { using (var pfs = new FileStream(partPath, FileMode.Open, FileAccess.Write)) pfs.SetLength(prefix); } catch { }
                try { File.Delete(chunksPath); } catch { }
                Log("kept " + prefix + " contiguous bytes of the parallel partial for single-stream resume");
            }
            long have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            bool resumed = have > 0;
            Log("start: have=" + have + (resumed ? " (resuming)" : ""));

            // download, auto-resuming on any network error
            if (!(total > 0 && have == total))
            {
                int attempt = 0;
                while (true)
                {
                    if (cancelRequested) { cancelled = true; break; }
                    try { DownloadRange(ref have, ref total, resumed); Log("download complete: " + have + " bytes"); break; }
                    catch (Exception ex)
                    {
                        if (cancelRequested) { cancelled = true; Log("cancelled by user"); break; }
                        attempt++;
                        if (attempt > 30) throw new Exception("Gave up after 30 retries: " + ex.Message);
                        have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                        resumed = have > 0;
                        Log("attempt " + attempt + " failed: " + ex.GetType().Name + ": " + ex.Message + "  -> have=" + have + ", retry in 5s");
                        SetDlLabelAsync("RECONNECTING");
                        for (int i = 0; i < 50 && !cancelRequested; i++) Thread.Sleep(100);
                    }
                }
            }
            }   // end single stream

            // 4) verify integrity, then finalize + extract
            if (!cancelled)
            {
                // Verify the completed .part BEFORE promoting it to the real zip or extracting anything,
                // so a corrupted or tampered download is never written into the install folder or launched.
                // On cancel the .part + meta are kept, so Resume re-checks it; on mismatch they are deleted
                // so the next attempt re-downloads cleanly (a bad file is never reused).
                bool hadHash = NormalizedExpectedHash().Length > 0;
                string actualHash = "";
                bool integrityOk;
                try { integrityOk = VerifyDownload(partPath, out actualHash); }
                catch (OperationCanceledException) { cancelled = true; integrityOk = false; }

                if (cancelled) { /* keep .part + meta for Resume */ }
                else if (!integrityOk)
                {
                    verifyFailed = true;
                    if (hadHash)
                    {
                        // A hash was configured and the file did not match it: reject and discard,
                        // so the next attempt re-downloads cleanly instead of reusing a bad file.
                        verifyMsg = "Integrity check failed - the download did not match the expected SHA-256 and was rejected. Nothing was installed.";
                        Log("INTEGRITY FAIL (mismatch): expected=" + ExpectedSha256 + " actual=" + actualHash + " - discarding download");
                        try { File.Delete(partPath); } catch { }
                        try { File.Delete(metaPath); } catch { }
                        try { File.Delete(chunksPath); } catch { }
                    }
                    else
                    {
                        // No hash configured (StartInstall normally blocks this; defensive). Keep the
                        // .part so that adding Sha256 and clicking Resume verifies it without re-downloading.
                        verifyMsg = "Install blocked - no expected SHA-256 is configured, so the download can't be verified. Set Sha256 in launcher.cfg, then click Resume. Nothing was installed.";
                        Log("INTEGRITY FAIL (no hash configured): actual=" + actualHash + " - keeping .part for Resume once a hash is set");
                    }
                }
                else
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    File.Move(partPath, zipPath);
                    try { File.Delete(metaPath); } catch { }
                    try { File.Delete(chunksPath); } catch { }
                    Log("finalized+verified zip (" + new FileInfo(zipPath).Length + " bytes), extracting to " + InstallDir);
                    SetDlLabelAsync("EXTRACTING");
                    try { Directory.CreateDirectory(InstallDir); File.Delete(InstallMarkerPath()); } catch { }  // clear any old marker: not "installed" until extract finishes
                    ExtractZip(zipPath, InstallDir);
                    Log("extract done");
                    try { File.WriteAllText(InstallMarkerPath(), DateTime.Now.ToString("o")); } catch { }        // mark the install complete only after a full extract
                    try { File.Delete(zipPath); } catch { }
                    InstallLauncherAndShortcut();
                }
            }
            }   // end if (!usedCache && !cancelled)
        }
        catch (Exception ex) { error = ex.Message; Log("FAILED: " + ex.GetType().Name + ": " + ex.Message); }

        Dispatcher.BeginInvoke((Action)(() =>
        {
            busy = false; activeReq = null;
            if (cancelled) statusText.Text = "";
            else if (verifyFailed) { statusText.Foreground = Danger; statusText.Text = verifyMsg ?? "Integrity check failed - the download was rejected. Nothing was installed."; }
            else if (error != null) { statusText.Foreground = Danger; statusText.Text = "Download failed: " + error; }
            else { statusText.Text = ""; }   // clean: status lived in the button; nothing below
            RefreshState();   // the .part is kept on cancel/error so Resume can continue
        }));
    }

    // ---------- parallel download ----------
    // The client is fetched as 32 MB HTTP Range requests over several connections at once, each writing
    // its own region of a preallocated .part. A single TCP connection is capped by latency and by the
    // per-connection throttling some ISP routes apply; several in parallel add up. Completed chunks are
    // recorded in .part.chunks, so a pause, restart or dropped connection resumes where it stopped
    // (an unfinished chunk is simply fetched again). The whole file is still SHA-256 verified afterwards.
    const long ChunkSize = 32L << 20;

    sealed class RangeNotSupportedException : Exception
    {
        public RangeNotSupportedException() : base("server ignored the Range header") { }
    }

    static int  ChunkCount(long total)      { return (int)((total + ChunkSize - 1) / ChunkSize); }
    static long ChunkLen(int i, long total) { return Math.Min(ChunkSize, total - (long)i * ChunkSize); }
    static long DoneBytes(bool[] done, long total)
    {
        long b = 0;
        for (int i = 0; i < done.Length; i++) if (done[i]) b += ChunkLen(i, total);
        return b;
    }

    // The saved chunk map for a partial of `total` bytes, or null if missing, unreadable or for another file.
    bool[] ReadChunkMap(long total)
    {
        try
        {
            if (!File.Exists(chunksPath)) return null;
            string[] l = File.ReadAllLines(chunksPath);
            if (l.Length < 2) return null;
            string[] h = l[0].Split(' ');
            if (h.Length != 3 || h[0] != "chunks-v1" || h[1] != ChunkSize.ToString() || h[2] != total.ToString()) return null;
            string bits = l[1].Trim();
            if (bits.Length != ChunkCount(total)) return null;
            var done = new bool[bits.Length];
            for (int i = 0; i < bits.Length; i++) done[i] = bits[i] == '1';
            return done;
        }
        catch { return null; }
    }

    void WriteChunkMap(bool[] done, long total)
    {
        var sb = new System.Text.StringBuilder(done.Length);
        foreach (bool d in done) sb.Append(d ? '1' : '0');
        File.WriteAllText(chunksPath, "chunks-v1 " + ChunkSize + " " + total + "\n" + sb + "\n");
    }

    static bool ContentRangeStartsAt(HttpWebResponse r, long start)
    {
        string cr = r.Headers["Content-Range"] ?? "";   // "bytes <from>-<to>/<total>"
        int sp = cr.IndexOf(' '), dash = cr.IndexOf('-');
        long a;
        return sp >= 0 && dash > sp && long.TryParse(cr.Substring(sp + 1, dash - sp - 1), out a) && a == start;
    }

    // Downloads every chunk not yet marked done. Returns when all are done; throws
    // OperationCanceledException on pause, RangeNotSupportedException if the server answers a Range
    // request with the whole file, or a plain Exception after 3 minutes without receiving any data.
    void DownloadParallel(long total, bool[] done, bool resumed)
    {
        var queue = new Queue<int>();
        for (int i = 0; i < done.Length; i++) if (!done[i]) queue.Enqueue(i);
        if (queue.Count == 0) return;

        using (var pfs = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            if (pfs.Length != total) pfs.SetLength(total);   // preallocate: each chunk writes at its own offset
        WriteChunkMap(done, total);

        object gate = new object();
        int workers = Math.Min(DownloadConnections, queue.Count);
        long doneBytes = DoneBytes(done, total);
        long[] inFlight = new long[workers];                 // bytes of the chunk each worker is fetching
        var active = new List<HttpWebRequest>();
        int failures = 0;                                    // failed chunk attempts since the last success
        Exception lastError = null;
        bool stop = false, rangeIgnored = false;

        var threads = new List<Thread>();
        for (int w = 0; w < workers; w++)
        {
            int id = w;
            threads.Add(new Thread(() =>
            {
                var buf = new byte[1 << 20];
                while (true)
                {
                    int ci;
                    lock (gate) { if (cancelRequested || stop || queue.Count == 0) return; ci = queue.Dequeue(); }
                    long start = (long)ci * ChunkSize, len = ChunkLen(ci, total), got = 0;
                    HttpWebRequest req = null;
                    try
                    {
                        req = (HttpWebRequest)WebRequest.Create(DownloadUrl);
                        req.Method = "GET"; req.Timeout = 30000; req.ReadWriteTimeout = 60000;
                        req.UserAgent = UA; req.AllowAutoRedirect = true;
                        req.AddRange(start, start + len - 1);
                        lock (gate) active.Add(req);
                        if (cancelRequested) throw new OperationCanceledException();
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        {
                            if (resp.StatusCode != HttpStatusCode.PartialContent || !ContentRangeStartsAt(resp, start))
                                throw new RangeNotSupportedException();
                            using (var s = resp.GetResponseStream())
                            using (var fs = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 16))
                            {
                                fs.Seek(start, SeekOrigin.Begin);
                                int n;
                                while (got < len && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, len - got))) > 0)
                                {
                                    if (cancelRequested) throw new OperationCanceledException();
                                    fs.Write(buf, 0, n); got += n;
                                    Interlocked.Exchange(ref inFlight[id], got);
                                }
                            }
                        }
                        if (got != len) throw new IOException("connection closed mid-chunk (" + got + "/" + len + " bytes)");
                        lock (gate)
                        {
                            done[ci] = true; doneBytes += len; failures = 0;
                            Interlocked.Exchange(ref inFlight[id], 0);
                            try { WriteChunkMap(done, total); } catch (Exception wx) { Log("chunk map write failed: " + wx.Message); }
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Exchange(ref inFlight[id], 0);
                        lock (gate)
                        {
                            if (cancelRequested) return;
                            if (ex is RangeNotSupportedException) { rangeIgnored = true; stop = true; return; }
                            queue.Enqueue(ci);                   // fetch this chunk again later
                            failures++; lastError = ex;
                            if (failures <= 3 || failures % 10 == 0)
                                Log("chunk " + ci + " failed (#" + failures + "): " + ex.GetType().Name + ": " + ex.Message);
                        }
                        for (int i = 0; i < 30 && !cancelRequested && !stop; i++) Thread.Sleep(100);   // back off 3 s
                    }
                    finally { if (req != null) lock (gate) active.Remove(req); }
                }
            }) { IsBackground = true, Name = "download-" + id });
        }
        foreach (var t in threads) t.Start();

        // this thread: progress, pause and stall detection
        var sw = Stopwatch.StartNew();
        long sessionStart = doneBytes, lastBytes = -1, lastChangeMs = 0;
        while (true)
        {
            bool alive = false;
            foreach (var t in threads) if (t.IsAlive) { alive = true; break; }
            long now; int fails;
            lock (gate) { now = doneBytes; fails = failures; }
            for (int i = 0; i < inFlight.Length; i++) now += Interlocked.Read(ref inFlight[i]);
            if (now != lastBytes) { lastBytes = now; lastChangeMs = sw.ElapsedMilliseconds; }
            long idleMs = sw.ElapsedMilliseconds - lastChangeMs;

            if (cancelRequested || idleMs > 180000)          // paused, or no data at all for 3 minutes
                lock (gate) { stop = true; foreach (var r in active) { try { r.Abort(); } catch { } } }
            if (!alive) break;
            if (!cancelRequested)
            {
                if (fails > 0 && idleMs > 5000) SetDlLabelAsync("RECONNECTING");
                else
                {
                    double secs = sw.Elapsed.TotalSeconds;
                    ReportProgress(now, total, secs > 0.5 ? ((now - sessionStart) / 1048576.0) / secs : 0, resumed);
                }
            }
            Thread.Sleep(250);
        }
        foreach (var t in threads) t.Join();

        if (cancelRequested) throw new OperationCanceledException();
        if (rangeIgnored) throw new RangeNotSupportedException();
        foreach (bool d in done)
            if (!d) throw new Exception("Download stalled (no data for 3 minutes)" + (lastError != null ? ": " + lastError.Message : ""));
        ReportProgress(total, total, 0, resumed);
    }

    // One GET (with a Range header when resuming). Returns normally only when the file is complete.
    void DownloadRange(ref long have, ref long total, bool resumed)
    {
        var req = (HttpWebRequest)WebRequest.Create(DownloadUrl);
        req.Method = "GET"; req.Timeout = 30000; req.ReadWriteTimeout = 60000;
        req.UserAgent = UA; req.AllowAutoRedirect = true;
        if (have > 0) req.AddRange(have);
        activeReq = req;

        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException wex)
        {
            var r = wex.Response as HttpWebResponse;
            if (r != null && (int)r.StatusCode == 416 && total > 0 && have >= total) { r.Close(); activeReq = null; return; }  // already complete
            throw;
        }

        using (resp)
        {
            bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
            if (have > 0 && !partial) { have = 0; resumed = false; }   // server ignored Range: start over
            if (partial)
            {
                string cr = resp.Headers["Content-Range"] ?? "";        // "bytes <from>-<to>/<total>"
                int slash = cr.LastIndexOf('/');
                long t; if (slash > 0 && long.TryParse(cr.Substring(slash + 1), out t)) total = t;
            }
            else if (resp.ContentLength > 0) total = resp.ContentLength;
            Log("GET " + (int)resp.StatusCode + (partial ? " partial" : "") + ": from=" + have + " total=" + total);

            using (var fs = new FileStream(partPath, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
            using (var s = resp.GetResponseStream())
            {
                var buf = new byte[1 << 20]; int n;
                long sessionStart = have; var sw = Stopwatch.StartNew(); long lastUi = -1000;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelRequested) throw new OperationCanceledException();
                    fs.Write(buf, 0, n); have += n;
                    if (sw.ElapsedMilliseconds - lastUi >= 250)
                    {
                        lastUi = sw.ElapsedMilliseconds;
                        double mbps = sw.Elapsed.TotalSeconds > 0.5 ? ((have - sessionStart) / 1048576.0) / sw.Elapsed.TotalSeconds : 0;
                        ReportProgress(have, total, mbps, resumed);
                    }
                }
            }
        }
        activeReq = null;
        if (total > 0 && have < total) throw new IOException("Connection closed before the file was complete");
    }

    void ExtractZip(string zip, string dest)
    {
        Directory.CreateDirectory(dest);
        using (var archive = ZipFile.OpenRead(zip))
        {
            string fullDest = Path.GetFullPath(dest);

            // Refuse to start extracting if the target drive can't hold the uncompressed client
            // (avoids failing halfway with a confusing error). +512 MB headroom.
            long needed = 0;
            foreach (var en in archive.Entries) needed += en.Length;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(fullDest));
                if (drive.AvailableFreeSpace < needed + (512L << 20))
                    throw new IOException("Not enough free space to install: need " + Human(needed) + " on drive "
                        + drive.Name + ", but only " + Human(drive.AvailableFreeSpace) + " is free. Free up space and try again.");
            }
            catch (IOException) { throw; }
            catch { }   // if the drive can't be queried, don't block the install
            long written = 0;                                   // uncompressed bytes written so far
            var sw = Stopwatch.StartNew(); long lastUi = -1000;
            foreach (var entry in archive.Entries)
            {
                if (cancelRequested) throw new OperationCanceledException();
                string target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
                if (!target.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\") || entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                // Stream each entry in chunks and report by uncompressed BYTES, so the bar keeps
                // advancing even through one large file. (File-count progress parks on big files and
                // looks frozen, e.g. stuck at 85% while a multi-GB bundle unpacks.)
                using (var src = entry.Open())
                using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buf = new byte[1 << 20]; int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelRequested) throw new OperationCanceledException();
                        dst.Write(buf, 0, n); written += n;
                        if (sw.ElapsedMilliseconds - lastUi >= 150)
                        {
                            lastUi = sw.ElapsedMilliseconds;
                            long w = written, tot = needed;
                            Dispatcher.BeginInvoke((Action)(() =>
                            {
                                if (tot > 0) { SetInstallProgress((double)w / tot); SetDlLabel("EXTRACTING  " + Pct((double)w / tot)); }
                                else SetDlLabel("EXTRACTING");
                            }));
                        }
                    }
                }
                try { File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime); } catch { }
            }
            Dispatcher.BeginInvoke((Action)(() => { SetInstallProgress(1.0); SetDlLabel("EXTRACTING  100%"); }));
        }
    }

    // ---------- integrity verification (SHA-256) ----------
    // The configured expected hash, normalized: an optional "sha256:" prefix, spaces and dashes
    // stripped. Empty string means no usable hash is configured.
    string NormalizedExpectedHash()
    {
        string expected = (ExpectedSha256 ?? "").Trim();
        int colon = expected.IndexOf(':');
        if (colon >= 0) expected = expected.Substring(colon + 1);
        return expected.Replace(" ", "").Replace("-", "").Trim();
    }

    // Returns true only when the file's SHA-256 matches the configured ExpectedSha256.
    // Returns false on mismatch OR when no hash is configured (verification is mandatory).
    // Throws OperationCanceledException if the user cancels while hashing.
    bool VerifyDownload(string path, out string actualHex)
    {
        actualHex = ComputeSha256(path);
        string expected = NormalizedExpectedHash();
        if (expected.Length == 0)
        {
            Log("integrity: no ExpectedSha256 configured - refusing to install unverified download (sha256=" + actualHex + ")");
            return false;
        }

        bool match = string.Equals(expected, actualHex, StringComparison.OrdinalIgnoreCase);
        Log("integrity " + (match ? "OK" : "MISMATCH") + ": expected=" + expected + " actual=" + actualHex);
        return match;
    }

    // Streams the file through SHA-256 (1 MB buffer), reporting progress and honoring cancellation.
    // Returns the lowercase hex digest.
    string ComputeSha256(string path)
    {
        long total = 0; try { total = new FileInfo(path).Length; } catch { }
        Dispatcher.BeginInvoke((Action)(() =>
        {
            statusText.Foreground = TextMute;
            statusText.Text = "";
            SetInstallProgress(0);
            SetDlLabel("VERIFYING");
        }));

        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            var buf = new byte[1 << 20];
            int n; long done = 0; var sw = Stopwatch.StartNew(); long lastUi = -1000;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                if (cancelRequested) throw new OperationCanceledException();
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                if (sw.ElapsedMilliseconds - lastUi >= 200)
                {
                    lastUi = sw.ElapsedMilliseconds;
                    long d = done, t = total;
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (t > 0) SetInstallProgress((double)d / t);
                        SetDlLabel("VERIFYING" + (t > 0 ? "  " + Pct((double)d / t) : ""));
                    }));
                }
            }
            sha.TransformFinalBlock(buf, 0, 0);
            var sb = new System.Text.StringBuilder(sha.Hash.Length * 2);
            foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // ---------- play ----------
    // ---- minimize/restore effects + game watching ----
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        ApplyRounding();        // square content corners when maximized, rounded otherwise
        ApplyWindowRegion();    // rounded window region when normal, cleared (square) when maximized
        // No manual restore fade: the window is non-layered, so Windows plays its own native
        // minimize/maximize/restore animation.
    }

    // Tuck the launcher away while the game runs, then restore + focus it when the game exits.
    void WatchGame(Process proc)
    {
        // launcher stays visible while the game runs; just watch for exit
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (s, e) => Dispatcher.BeginInvoke((Action)RestoreFromGame);
        }
        catch
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { proc.WaitForExit(); } catch { }
                Dispatcher.BeginInvoke((Action)RestoreFromGame);
            });
        }
    }

    // Hide the launcher to the tray while the game runs, remembering the window state to restore to.
    void HideForGame()
    {
        try
        {
            preGameState = (WindowState == WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal;
            hiddenForGame = true;
            Hide();   // disappears from the taskbar; the tray icon stays, and it's restored when the game exits
        }
        catch { }
    }

    void RestoreFromGame()
    {
        try
        {
            gameProc = null;                 // the process we launched has exited
            if (GameRunning()) return;       // a same-named client is still up (bootstrapper launch) - stay hidden; the game timer retries
            SetDiscord("In the launcher");
            if (hiddenForGame)
            {
                hiddenForGame = false;
                try { Show(); WindowState = preGameState; Activate(); Topmost = true; Topmost = false; } catch { }
            }
            else { try { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; Activate(); } } catch { } }
            RefreshState();
            statusText.Foreground = TextMute; statusText.Text = "Ready to play.";
        }
        catch { }
    }

    void Play(string args)
    {
        string exe = FindGameExe();
        if (exe == null)
        {
            statusText.Foreground = Danger; statusText.Text = "Client not installed - click Install first.";
            RefreshState(); return;
        }
        // Don't launch a structurally-incomplete install (a server-card click also lands here).
        if (!GameRunning() && !InstallComplete(exe))
        {
            statusText.Foreground = Danger; statusText.Text = "Install looks incomplete or corrupted - click Repair to reinstall.";
            RefreshState(); return;
        }
        // single instance: never launch a second copy of OUR client - focus the running one instead
        if (GameRunning())
        {
            statusText.Foreground = TextMute; statusText.Text = "Rustorigin is already running.";
            try
            {
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LaunchExe)))
                { if (p.MainWindowHandle != IntPtr.Zero) { SetForegroundWindow(p.MainWindowHandle); break; } }
            }
            catch { }
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe) };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            var proc = Process.Start(psi);
            if (proc != null) { gameProc = proc; WatchGame(proc); SetDiscord("In game"); if (Prefs.GetBool("MinimizeInGame", false)) HideForGame(); }
            statusText.Foreground = TextMute; statusText.Text = "Launching...";
        }
        catch (Exception ex) { statusText.Foreground = Danger; statusText.Text = "Launch error: " + ex.Message; }
    }

    // ---------- settings panel ----------
    static string AppVer()
    {
        try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); } catch { return "1.0"; }
    }

    static class Prefs
    {
        static Dictionary<string, string> map;
        static string PrefsPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rustorigin", "prefs.cfg"); } }
        static void Load()
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(PrefsPath)) foreach (string line in File.ReadAllLines(PrefsPath)) { int i = line.IndexOf('='); if (i > 0) map[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim(); } } catch { }
        }
        public static string Get(string k, string d) { if (map == null) Load(); string v; return map.TryGetValue(k, out v) ? v : d; }
        public static bool GetBool(string k, bool d) { return Get(k, d ? "1" : "0") == "1"; }
        public static void Set(string k, string v) { if (map == null) Load(); map[k] = v; Save(); }
        public static void Set(string k, bool v) { Set(k, v ? "1" : "0"); }
        static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(PrefsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in map) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(PrefsPath, lines.ToArray());
            }
            catch { }
        }
    }

    static BitmapImage LoadBitmap(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    // On successful install: copy this launcher into the install folder (stable location)
    // and drop a Desktop shortcut to it so players can relaunch/update easily.
    void InstallLauncherAndShortcut()
    {
        try
        {
            string self = Process.GetCurrentProcess().MainModule.FileName;
            string installed = Path.Combine(InstallDir, "RustoriginLauncher.exe");
            try
            {
                if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(InstallDir);
                    File.Copy(self, installed, true);
                }
            }
            catch { installed = self; }   // fall back to the running exe if the copy is blocked
            string target = installed;
            Dispatcher.Invoke((Action)(() => CreateDesktopShortcut(target, "Rustorigin Launcher")));   // STA thread for COM
            Log("shortcut -> " + target);
        }
        catch (Exception ex) { Log("shortcut step failed: " + ex.Message); }
    }

    static void CreateDesktopShortcut(string targetExe, string name)
    {
        try
        {
            // The installer already put this shortcut on the all-users Desktop: a second copy on the
            // user's Desktop would show two identical icons. Only the portable exe needs its own.
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrEmpty(common) && File.Exists(Path.Combine(common, name + ".lnk"))) return;

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnk = Path.Combine(desktop, name + ".lnk");
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type t = sc.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { targetExe });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(targetExe) });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { targetExe + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Rustorigin Launcher" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
        catch { }
    }

    // ---------- launcher self-update ----------
    // Checks the configured GitHub repo's latest release, and if it is newer than this build,
    // downloads the new RustoriginLauncher.exe, VERIFIES its SHA-256 against the release's SHA256SUMS.txt,
    // and swaps itself out (rename-running-exe trick) before relaunching. A failed hash check
    // rejects the update - the launcher never runs an unverified replacement, same as the client.
    void StartUpdateCheck()
    {
        try
        {
            string repo = (UpdateRepo ?? "").Trim();
            if (repo.Length == 0) return;
            // clean up a leftover .old from a previous self-update
            try { string old = SelfPath() + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
            var t = new Thread(delegate () { try { UpdateCheckWorker(repo); } catch (Exception ex) { Log("update check failed: " + ex.Message); } })
            { IsBackground = true, Name = "update-check" };
            t.Start();
        }
        catch { }
    }

    static string SelfPath() { return Process.GetCurrentProcess().MainModule.FileName; }

    void UpdateCheckWorker(string repo)
    {
        System.Version current;
        if (!System.Version.TryParse(AppVer(), out current) || current.Major == 0) return;   // skip dev/0.0.0 builds

        string json = HttpGetString("https://api.github.com/repos/" + repo + "/releases/latest");
        if (json == null) return;   // private/unreleased/offline - silently skip

        string tag = UpdateParsing.JsonStr(json, "tag_name");
        if (!UpdateParsing.IsNewer(AppVer(), tag)) { Log("update check: up to date (v" + current + " vs tag " + (tag ?? "?") + ")"); return; }

        string exeUrl  = UpdateParsing.AssetUrl(json, "RustoriginLauncher.exe");
        string sumsUrl = UpdateParsing.AssetUrl(json, "SHA256SUMS.txt");
        if (exeUrl == null || sumsUrl == null) { Log("update: release " + tag + " missing RustoriginLauncher.exe or SHA256SUMS.txt asset"); return; }
        Log("update available: v" + current + " -> " + tag);

        // Update available: show the prompt. It does NOT auto-download - the player presses Update.
        pendingRepo = repo; pendingExeUrl = exeUrl; pendingSumsUrl = sumsUrl; pendingTag = tag;
        ShowUpdateGate("Version " + tag + " is available (you have v" + current + "). Press Update to install it - the launcher will verify and restart.");
    }

    Grid updateGate; TextBlock updateGateMsg; Border updateGateFill, updateGateBar, updateGateUpdateBtn; const double UpdateBarW = 260;
    string pendingRepo, pendingExeUrl, pendingSumsUrl, pendingTag;

    // Blocking update prompt shown when a newer release is available. It does NOT auto-download - the
    // player presses UPDATE to install (or QUIT). Sits on top of everything (incl. the caption buttons).
    void ShowUpdateGate(string message)
    {
        Dispatcher.Invoke((Action)(() =>
        {
            if (updateGate == null)
            {
                updateGate = new Grid { Background = B("#F20B0B0C") };
                updateGate.MouseLeftButtonDown += (s, e) => e.Handled = true;   // swallow clicks + window drag
                var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 440, Margin = new Thickness(24) };
                box.Children.Add(DialogHeader("Update required", true));
                updateGateMsg = new TextBlock { Text = message, Foreground = Ink200, FontFamily = Site, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
                box.Children.Add(updateGateMsg);
                updateGateFill = new Border { Height = 6, Width = 0, CornerRadius = new CornerRadius(3), Background = Brand500, HorizontalAlignment = HorizontalAlignment.Left };
                updateGateBar = new Border { Width = UpdateBarW, Height = 6, CornerRadius = new CornerRadius(3), Background = GlassSoft, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0), Child = updateGateFill, Visibility = Visibility.Collapsed };
                box.Children.Add(updateGateBar);
                var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
                updateGateUpdateBtn = DialogBtn("Update", true, delegate { StartPendingUpdate(); });
                var quit = DialogBtn("Quit", false, delegate { try { if (tray != null) tray.Visible = false; } catch { } Application.Current.Shutdown(); });
                btns.Children.Add(updateGateUpdateBtn); btns.Children.Add(quit);
                box.Children.Add(btns);
                updateGate.Children.Add(box);
                mainGrid.Children.Add(updateGate);
            }
            else { updateGateMsg.Text = message; updateGate.Visibility = Visibility.Visible; }
        }));
    }

    // UPDATE pressed: switch the prompt to downloading mode and run the update on a background thread.
    void StartPendingUpdate()
    {
        if (pendingExeUrl == null) return;
        if (updateGateUpdateBtn != null) updateGateUpdateBtn.Visibility = Visibility.Collapsed;
        if (updateGateBar != null) updateGateBar.Visibility = Visibility.Visible;
        if (updateGateFill != null) updateGateFill.Width = 0;
        if (updateGateMsg != null) updateGateMsg.Text = "Downloading " + pendingTag + "...";
        var t = new Thread(delegate () { try { PerformUpdate(); } catch (Exception ex) { GateFail(pendingRepo, "Update error: " + ex.Message); } }) { IsBackground = true, Name = "self-update" };
        t.Start();
    }

    // Download + SHA-256-verify + swap the new build, then restart. Runs on a background thread.
    void PerformUpdate()
    {
        string repo = pendingRepo, exeUrl = pendingExeUrl, sumsUrl = pendingSumsUrl, tag = pendingTag;
        string expected = UpdateParsing.HashFromSums(HttpGetString(sumsUrl), "RustoriginLauncher.exe");
        if (expected == null) { GateFail(repo, "Could not read the update checksum."); return; }

        string self = SelfPath();
        string newPath = self + ".new";
        try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
        if (!HttpDownload(exeUrl, newPath, SetUpdateProgress)) { GateFail(repo, "The update download failed."); return; }

        string actual = Sha256File(newPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(newPath); } catch { }
            Log("update REJECTED (hash mismatch): expected " + expected + " got " + actual);
            GateFail(repo, "The downloaded update failed its integrity check and was discarded.");
            return;
        }
        Log("update verified (" + actual + "); swapping in " + tag);

        try
        {
            string old = self + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(self, old);       // a running exe can be renamed on Windows
            File.Move(newPath, self);   // put the new build in its place
            Process.Start(new ProcessStartInfo(self) { UseShellExecute = true });
            Dispatcher.Invoke((Action)(() => { try { if (tray != null) tray.Visible = false; } catch { } Application.Current.Shutdown(); }));
        }
        catch (Exception ex)
        {
            Log("self-replace failed: " + ex.Message);
            try { if (!File.Exists(self) && File.Exists(self + ".old")) File.Move(self + ".old", self); } catch { }   // roll back
            GateFail(repo, "Could not apply the update (the install folder may be read-only).");
        }
    }

    // Update the gate's download progress bar + byte count (called from the download thread).
    void SetUpdateProgress(long have, long total)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            try
            {
                if (updateGateFill != null) updateGateFill.Width = total > 0 ? UpdateBarW * Math.Max(0.0, Math.Min(1.0, (double)have / total)) : 0;
                if (updateGateMsg != null && total > 0) updateGateMsg.Text = "Downloading update  " + Human(have) + " / " + Human(total);
            }
            catch { }
        }));
    }

    // Update failed: open the releases page and let the player retry (Update) or Quit - the gate stays up.
    void GateFail(string repo, string msg)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/" + repo + "/releases/latest") { UseShellExecute = true }); } catch { }
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (updateGateBar != null) updateGateBar.Visibility = Visibility.Collapsed;
            if (updateGateUpdateBtn != null) updateGateUpdateBtn.Visibility = Visibility.Visible;
            if (updateGateMsg != null) updateGateMsg.Text = msg + "  The releases page has opened - or press Update to retry.";
        }));
    }

    // --- small HTTP + parsing helpers for the updater (no external dependency) ---
    string HttpGetString(string url)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA; req.Timeout = 20000; req.AllowAutoRedirect = true;
            req.Accept = "application/vnd.github+json";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }
        catch (Exception ex) { Log("GET " + url + " failed: " + ex.Message); return null; }
    }

    bool HttpDownload(string url, string dest, Action<long, long> onProgress = null)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA; req.Timeout = 30000; req.ReadWriteTimeout = 60000; req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                long total = resp.ContentLength, have = 0;
                if (onProgress != null) onProgress(0, total);
                byte[] buf = new byte[1 << 16];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n);
                    have += n;
                    if (onProgress != null) onProgress(have, total);
                }
            }
            return true;
        }
        catch (Exception ex) { Log("download " + url + " failed: " + ex.Message); return false; }
    }

    static string Sha256File(string path)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = File.OpenRead(path))
        {
            byte[] h = sha.ComputeHash(fs);
            var sb = new System.Text.StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }


    static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(i == 0 ? "0" : "0.0") + " " + u[i];
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (busy && dlThread != null && dlThread.IsAlive)
        {
            if (!Confirm("Quit", "A download is running. Quit now? Progress is saved.", "Quit", "Keep downloading"))
            { e.Cancel = true; return; }
            CancelDownload();
        }
        try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
        base.OnClosing(e);
    }
}

