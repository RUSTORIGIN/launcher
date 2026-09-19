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

[assembly: AssemblyTitle("RustOrigin Launcher")]
[assembly: AssemblyProduct("RustOrigin")]
[assembly: AssemblyDescription("RustOrigin game launcher - downloads, installs and launches the client")]
[assembly: AssemblyCompany("RustOrigin")]
[assembly: AssemblyCopyright("RustOrigin 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// RUSTORIGIN launcher — WPF port of the Superdesign canvas composition:
// rounded dark card, full-bleed looping video hero, floating glass UI
// (left rail, top-right pills, hero block, server column, friends rail).
// Code-only WPF on .NET Framework 4.x: runs on any Windows 10/11, no runtime install.

public class App
{
    [STAThread]
    static void Main()
    {
        Assets.Ensure();                 // unpack embedded video/logo/fonts/config (single-exe distribution)
        LauncherWindow.ConfigureTls();
        var app = new Application();
        app.Run(new LauncherWindow());
    }
}

// Everything the launcher needs ships INSIDE the exe as manifest resources and is unpacked once
// per version to %LOCALAPPDATA%\RUSTORIGIN\assets\<version>\ (WPF needs real files for the
// MediaElement and for private fonts). An optional launcher.cfg next to the exe overrides the
// embedded defaults.
static class Assets
{
    public static string Dir = "";
    static readonly string[] Files = {
        "background.mp4", "logo.png", "server-cover.png", "launcher.cfg",
        "fonts/Montserrat-Regular.ttf", "fonts/Montserrat-Medium.ttf",
        "fonts/Montserrat-SemiBold.ttf", "fonts/Montserrat-Bold.ttf", "fonts/OFL.txt" };

    public static void Ensure()
    {
        try
        {
            var asm = typeof(Assets).Assembly;
            string ver = asm.GetName().Version.ToString();
            Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustOrigin", "assets", ver);
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
    public string Tag, Name, Args, Players;
    public ServerEntry(string tag, string name, string args, string players = "") { Tag = tag; Name = name; Args = args; Players = players; }
}

public class LauncherWindow : Window
{
    // ---- config (launcher.cfg) ----
    string DownloadUrl = "https://REPLACE-ME.example.com/RustClient.zip";
    string ExpectedSha256 = "";   // hex SHA-256 of RustClient.zip. When set, a download whose hash does not match is rejected (never extracted or launched).
    string InstallDir  = "";
    string LaunchExe   = "RustClient.exe";
    string LaunchArgs  = "";
    string Version     = "";
    string GameTitle   = "RUSTORIGIN";
    string Tagline     = "RUSTORIGIN is a private Rust world on the January 2021 build. Craft, raid and survive with a tight community — one click to jump in.";
    string PlayerName  = "White Pegasus";
    List<ServerEntry> Servers = new List<ServerEntry>();

    // ---- state ----
    bool      busy;
    volatile bool cancelRequested;
    HttpWebRequest activeReq;
    Thread    dlThread;
    string    cacheDir, zipPath, partPath, metaPath;
    const string UA = "RUSTORIGIN-Launcher/1.0";

    // ---- ui refs ----
    Border       progTrack, progFill;
    TextBlock    statusText;
    Border       playBtn, installBtn;
    Grid         mainGrid;
    MediaElement video;
    Grid         blurLayer;   // blurred+tinted copy of the video that glass panels sample (real acrylic)
    BitmapImage  logoBmp;
    BitmapImage  coverBmp;
    Border settingsPanel; TextBlock settingsPathLabel; TextBlock settingsStatus; TextBlock settingsCacheLabel, settingsDiskLabel; ColumnDefinition settingsDiskFillCol, settingsDiskRestCol; Grid homeView; bool settingsOpen;
    readonly List<Border> navBorders = new List<Border>(); readonly List<TextBlock> navIcons = new List<TextBlock>(); readonly List<TextBlock> navLabels = new List<TextBlock>(); readonly List<FrameworkElement> settingsPages = new List<FrameworkElement>(); int settingsPage; TextBox launchArgsBox;

    // ---- palette ----
    static Brush B(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
    static readonly Brush Accent   = B("#D14431");
    static readonly Brush AccentHi = B("#E9583F");
    static readonly Brush TextHi   = B("#FFFFFF");
    static readonly Brush TextDim  = B("#C9CCD6");
    static readonly Brush TextMute = B("#8A8E99");
    static readonly Brush Glass    = B("#8C0E1016");   // rgba(14,16,22,.55)
    static readonly Brush GlassHi  = B("#A61E222C");
    static readonly Brush Stroke   = B("#1FFFFFFF");   // rgba(255,255,255,.12)
    static readonly Brush StrokeHi = B("#59FFFFFF");   // rgba(255,255,255,.35) outlined pills
    static readonly Brush Online   = B("#3BD16F");
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

    static string Track(string s, int n)
    {
        var sp = new string(' ', n);
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
            Servers.Add(new ServerEntry("Most Played", "RustOrigin Main", "", "187/250"));
            Servers.Add(new ServerEntry("Most Recent", "Merged Map Test", "-console +connect 127.0.0.1:28025", "64/150"));
            Servers.Add(new ServerEntry("Promoted",    "Localhost Dev",   "-console +connect 127.0.0.1:28015", "12/100"));
            Servers.Add(new ServerEntry("Promoted",    "Deadman Desert Arena", "", "96/200"));
            Servers.Add(new ServerEntry("Trending",    "Training Grounds", "", "41/100"));
            Servers.Add(new ServerEntry("Trending",    "Skinbox Sandbox", "", "8/50"));
        }
        logoBmp = LoadBitmap(Path.Combine(Assets.Dir, "logo.png")) ?? LoadBitmap(Path.Combine(AppDir(), "logo.png"));
        coverBmp = LoadBitmap(Path.Combine(Assets.Dir, "server-cover.png")) ?? LoadBitmap(Path.Combine(AppDir(), "server-cover.png"));

        // Download cache (survives launcher restarts so a partial download can resume).
        cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustOrigin");
        zipPath  = Path.Combine(cacheDir, "RustClient.zip");
        partPath = zipPath + ".part";
        metaPath = zipPath + ".part.meta";
        try { string sfile = Path.Combine(cacheDir, "installdir.txt"); if (File.Exists(sfile)) { string sv = File.ReadAllText(sfile).Trim(); if (sv.Length > 0) InstallDir = sv; } } catch { }
        try { string la = Prefs.Get("LaunchArgs", null); if (la != null) LaunchArgs = la; } catch { }

        // ---- window chrome ----
        Title = "RustOrigin Launcher";
        Width = 1440; Height = 860;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        const double R = 32;
        mainGrid = new Grid { Background = B("#0B0D12") };
        mainGrid.SizeChanged += (s, e) =>
            mainGrid.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), R, R);
        Content = mainGrid;

        BuildBackground();
        BuildGradient();
        BuildContent();

        mainGrid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(R), BorderBrush = B("#1AFFFFFF"),
            BorderThickness = new Thickness(1), Background = Brushes.Transparent, IsHitTestVisible = false
        });

        Loaded += (s, e) => { try { if (Prefs.GetBool("BgVideo", true)) video.Play(); } catch { } };
        // Drag the window from empty areas only — never from buttons/cards, or DragMove
        // would swallow the MouseLeftButtonUp those controls need.
        MouseLeftButtonDown += (s, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || IsInteractive(e.OriginalSource as DependencyObject)) return;
            try { DragMove(); } catch { }
        };

        SetupTray();
        StartGameTimer();
        RefreshState();
    }

    // ---------- system tray icon (notification area) ----------
    System.Windows.Forms.NotifyIcon tray;
    void SetupTray()
    {
        try
        {
            tray = new System.Windows.Forms.NotifyIcon();
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); } catch { }
            tray.Text = "RustOrigin Launcher";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var open = new System.Windows.Forms.ToolStripMenuItem("Open RustOrigin");
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
            WindowState = WindowState.Normal;   // fade-in via OnStateChanged
            Activate();
            Topmost = true; Topmost = false;
        }
        catch { }
    }

    // ---------- minimize support ----------
    // A borderless (WindowStyle=None) window needs WS_MINIMIZEBOX or the taskbar cannot
    // minimize/restore it and the minimize animation is missing.
    const int GWL_STYLE = -16, WS_MINIMIZEBOX = 0x20000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) | WS_MINIMIZEBOX);
        }
        catch { }
    }

    // ---------- background video ----------
    void BuildBackground()
    {
        string vid = Path.Combine(Assets.Dir, "background.mp4");
        if (!File.Exists(vid)) vid = Path.Combine(AppDir(), "background.mp4");
        video = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.UniformToFill, IsMuted = true, Volume = 0, ScrubbingEnabled = false
        };
        video.MediaEnded += (s, e) => { try { video.Position = TimeSpan.Zero; video.Play(); } catch { } };
        video.MediaFailed += (s, e) => ShowFallbackBackdrop();
        if (File.Exists(vid)) { try { video.Source = new Uri(vid, UriKind.Absolute); } catch { ShowFallbackBackdrop(); } }
        else ShowFallbackBackdrop();

        // Frosted-glass source: a blurred + tinted copy of the video. Glass panels sample the
        // region of this layer directly behind them, giving real backdrop blur. It sits under
        // the sharp video (never shown directly) but still renders so VisualBrush can read it.
        var blurRect = new Rectangle
        {
            Fill = new VisualBrush(video) { Stretch = Stretch.UniformToFill },
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 26, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance }
        };
        RenderOptions.SetBitmapScalingMode(blurRect, BitmapScalingMode.LowQuality);
        blurLayer = new Grid();
        blurLayer.Children.Add(blurRect);
        blurLayer.Children.Add(new Rectangle { Fill = B("#8A0E1016") });   // frosted tint baked in (keeps text legible over bright frames)
        // Rasterize the blurred video ONCE per frame at half resolution — blur is low-frequency,
        // so half-res is invisible, and every glass panel then samples this cheap cache instead
        // of each re-blurring the video. Major GPU saving; keeps animations fluid.
        blurLayer.CacheMode = new BitmapCache { RenderAtScale = 0.5, SnapsToDevicePixels = false };
        mainGrid.Children.Add(blurLayer);
        mainGrid.Children.Add(video);

        // Loop only the first 0:16 of the intro clip (the good part) and restart from the top.
        var loopAt = TimeSpan.FromSeconds(16);
        var loopTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        loopTimer.Tick += (s, e) => { try { if (Prefs.GetBool("ShortLoop", true) && video.Source != null && video.Position >= loopAt) video.Position = TimeSpan.Zero; } catch { } };
        loopTimer.Start();
    }

    void ShowFallbackBackdrop()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#2B1C13"), 0));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#1C1A1F"), 0.42));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#0E1420"), 1));
        mainGrid.Children.Add(new Rectangle { Fill = g });
    }

    // ---------- legibility gradients (left 45%, bottom 40%, vignette) ----------
    void BuildGradient()
    {
        mainGrid.Children.Add(new Rectangle { Fill = B("#400A0C11"), IsHitTestVisible = false });

        var left = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        left.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#DB0A0C11"), 0));
        left.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#8C0A0C11"), 0.24));
        left.GradientStops.Add(new GradientStop(Colors.Transparent, 0.45));
        mainGrid.Children.Add(new Rectangle { Fill = left, IsHitTestVisible = false });

        var bottom = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
        bottom.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#E60A0C11"), 0));
        bottom.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#590A0C11"), 0.22));
        bottom.GradientStops.Add(new GradientStop(Colors.Transparent, 0.40));
        mainGrid.Children.Add(new Rectangle { Fill = bottom, IsHitTestVisible = false });

        var vig = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.85, RadiusY = 0.85 };
        vig.GradientStops.Add(new GradientStop(Colors.Transparent, 0.55));
        vig.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#B305060A"), 1));
        mainGrid.Children.Add(new Rectangle { Fill = vig, IsHitTestVisible = false });
    }

    // ---------- content ----------
    void BuildContent()
    {
        var content = new Grid();
        mainGrid.Children.Add(content);

        // Home widgets live in their own layer so the Settings tab can fully replace them.
        homeView = new Grid();
        content.Children.Add(homeView);

        BuildTopRight(homeView);
        BuildHero(homeView);
        BuildServerGrid(homeView);
        BuildSettingsPanel(content);
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

    Border IconBtn(string glyph, double size, Action onClick, bool outlined)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            BorderBrush = outlined ? StrokeHi : Brushes.Transparent, BorderThickness = new Thickness(1),
            Child = Icon(glyph, size * 0.42, B("#E6E8EE"))
        };
        b.MouseEnter += (s, e) => { b.Background = B("#1AFFFFFF"); };
        b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; };
        if (onClick != null) b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return b;
    }

    Border GlassPanel(double radius)
    {
        var panel = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderBrush = GlassEdge(), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 34, ShadowDepth = 9, Direction = 270, Opacity = 0.5, Color = (Color)ColorConverter.ConvertFromString("#000000") }
        };
        if (blurLayer != null)
        {
            // Real backdrop blur: paint the slice of the blurred layer that sits behind this panel.
            var vb = new VisualBrush(blurLayer) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            panel.Background = vb;
            panel.LayoutUpdated += delegate
            {
                try
                {
                    GeneralTransform t = panel.TransformToVisual(blurLayer);
                    Rect r = t.TransformBounds(new Rect(new Point(0, 0), panel.RenderSize));
                    if (r.Width > 1 && r.Height > 1 && r != vb.Viewbox) vb.Viewbox = r;
                }
                catch { }
            };
        }
        else panel.Background = Glass;
        return panel;
    }

    // Light-catching edge for glass panels: bright at the top, fading down.
    static Brush GlassEdge()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#42FFFFFF"), 0));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#12FFFFFF"), 0.5));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#10000000"), 1));
        return g;
    }

    static Border Tag(string text, double size)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(999), Background = Brushes.Transparent,
            BorderBrush = StrokeHi, BorderThickness = new Thickness(1),
            Padding = new Thickness(size < 12 ? 9 : 12, size < 12 ? 3 : 4, size < 12 ? 9 : 12, size < 12 ? 3.5 : 4.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.Medium, FontSize = size }
        };
    }

    static LinearGradientBrush Grad(string c1, string c2)
    {
        var lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c1), 0));
        lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c2), 1));
        return lg;
    }

    // ---- top-right pills + window controls ----
    void BuildTopRight(Grid content)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 32, 64, 0) };

        // window controls (recent-games pill removed)
        var gear = IconBtn("\uE713", 36, delegate { OpenSettings(); }, false); gear.Background = Glass; gear.BorderBrush = Stroke;
        row.Children.Add(gear);
        var min = IconBtn("", 36, () => MinimizeWithFade(), false); min.Margin = new Thickness(16, 0, 0, 0); min.Background = Glass; min.BorderBrush = Stroke;
        var cls = IconBtn("", 36, () => Close(), false); cls.Margin = new Thickness(8, 0, 0, 0); cls.Background = Glass; cls.BorderBrush = Stroke;
        cls.MouseEnter += (s, e) => { cls.Background = Accent; };
        cls.MouseLeave += (s, e) => { cls.Background = Glass; };
        row.Children.Add(min); row.Children.Add(cls);

        content.Children.Add(row);
    }

    // ---- hero block ----
    void BuildHero(Grid content)
    {
        var hero = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(64, 0, 0, 0), Width = 470
        };

        if (logoBmp != null)
        {
            var big = new Image { Source = logoBmp, Width = 250, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 10, Opacity = 0.6, Color = Colors.Black } };
            RenderOptions.SetBitmapScalingMode(big, BitmapScalingMode.HighQuality);
            hero.Children.Add(big);
        }

        hero.Children.Add(new TextBlock
        {
            Text = GameTitle, Foreground = TextHi, FontSize = 54, FontFamily = Brand, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(-2, 14, 0, 0), LineHeight = 54, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        });
        hero.Children.Add(new TextBlock
        {
            Text = Track("JANUARY UPDATE 2021", 1),
            Foreground = Accent, FontSize = 13, FontFamily = Brand, FontWeight = FontWeights.SemiBold, Margin = new Thickness(1, 10, 0, 0)
        });
        hero.Children.Add(new TextBlock
        {
            Text = Tagline, Foreground = TextDim, FontSize = 16.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, 20, 0, 0), MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left,
            LineHeight = 25, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 30, 0, 0) };
        playBtn = PlayButton();
        installBtn = LinkButton("", "INSTALL", StartInstall);
        row.Children.Add(playBtn);
        row.Children.Add(installBtn);
        hero.Children.Add(row);

        progTrack = new Border
        {
            Height = 5, Width = 380, CornerRadius = new CornerRadius(3), Background = B("#33202531"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 20, 0, 0),
            Visibility = Visibility.Collapsed, ClipToBounds = true
        };
        progFill = new Border { Height = 5, Width = 0, CornerRadius = new CornerRadius(3), Background = Accent, HorizontalAlignment = HorizontalAlignment.Left };
        progTrack.Child = progFill;
        hero.Children.Add(progTrack);

        statusText = new TextBlock { Text = "", Foreground = TextMute, FontSize = 12.5, Margin = new Thickness(1, 10, 0, 0) };
        hero.Children.Add(statusText);

        content.Children.Add(hero);
    }

    Border PlayButton()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ic = Icon("", 12, B("#12141A")); ic.Margin = new Thickness(0, 1, 9, 0);
        var tb = new TextBlock { Text = Track("PLAY", 1), FontSize = 14, FontFamily = Brand, FontWeight = FontWeights.SemiBold,
            Foreground = B("#12141A"), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(ic); sp.Children.Add(tb);
        var b = new Border
        {
            Height = 46, MinWidth = 130, CornerRadius = new CornerRadius(23), Cursor = Cursors.Hand,
            Background = TextHi, Padding = new Thickness(26, 0, 28, 0), Child = sp
        };
        b.MouseEnter += (s, e) => { if (b.IsEnabled) b.Background = B("#F0F1F4"); };
        b.MouseLeave += (s, e) => { if (b.IsEnabled) b.Background = TextHi; };
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (b.IsEnabled && !busy) Play(LaunchArgs); };
        b.Tag = new object[] { true, tb, ic };
        return b;
    }

    Border LinkButton(string glyph, string text, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var ic = Icon(glyph, 15, TextHi); ic.Margin = new Thickness(0, 0, 9, 0);
        var tb = new TextBlock { Text = Track(text, 1), FontSize = 13.5, FontFamily = Brand, FontWeight = FontWeights.SemiBold,
            Foreground = TextHi, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(ic); sp.Children.Add(tb);
        var b = new Border { Height = 46, Margin = new Thickness(30, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = sp };
        b.MouseEnter += (s, e) => { if (b.IsEnabled) tb.Foreground = AccentHi; };
        b.MouseLeave += (s, e) => { if (b.IsEnabled) tb.Foreground = TextHi; };
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (b.IsEnabled && !busy) onClick(); };
        b.Tag = new object[] { false, tb, ic };
        return b;
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
        var wrap = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 64, 0) };
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        string[][] pal = {
            new[]{"#3A2E5A","#141020"}, new[]{"#2C4A3A","#121C17"}, new[]{"#2F3D5A","#12161F"},
            new[]{"#5A3A26","#1C1512"}, new[]{"#4A2F2F","#1C1212"}, new[]{"#3A4A5A","#141A20"} };
        for (int i = 0; i < Servers.Count && i < 6; i++)
            grid.Children.Add(ServerTile(Servers[i], pal[i % pal.Length][0], pal[i % pal.Length][1]));
        wrap.Children.Add(grid);

        var more = new Border
        {
            Height = 40, CornerRadius = new CornerRadius(20), Background = Brushes.Transparent,
            BorderBrush = StrokeHi, BorderThickness = new Thickness(1), Padding = new Thickness(20, 0, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0), Cursor = Cursors.Hand
        };
        var ms = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        ms.Children.Add(new TextBlock { Text = Track("DISCOVER MORE", 1), Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        var ch = Icon("\uE76C", 10, TextHi); ch.Margin = new Thickness(8, 1, 0, 0); ms.Children.Add(ch);
        more.Child = ms;
        more.MouseEnter += (s, e) => { more.Background = B("#1AFFFFFF"); };
        more.MouseLeave += (s, e) => { more.Background = Brushes.Transparent; };
        wrap.Children.Add(more);

        content.Children.Add(wrap);
    }

    // A server "pill": cover image background + name + player-count badge (server-browser style).
    Grid ServerTile(ServerEntry srv, string c1, string c2)
    {
        double W = 200, H = 116;
        var tile = new Grid { Width = W, Height = H, Margin = new Thickness(7), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        tile.Clip = new RectangleGeometry(new Rect(0, 0, W, H), 14, 14);

        if (coverBmp != null)
        {
            var img = new Image { Source = coverBmp, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            tile.Children.Add(img);
        }
        else tile.Children.Add(new Rectangle { Fill = Grad(c1, c2) });

        var ov = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        ov.GradientStops.Add(new GradientStop(Colors.Transparent, 0.22));
        ov.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#F00A0C11"), 1));
        tile.Children.Add(new Rectangle { Fill = ov, IsHitTestVisible = false });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(12, 0, 12, 11) };
        texts.Children.Add(new TextBlock
        {
            Text = srv.Name.ToUpperInvariant(), Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.Bold,
            FontSize = 15, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap, MaxWidth = W - 24,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.85, Color = Colors.Black }
        });
        if (srv.Tag.Length > 0)
            texts.Children.Add(new TextBlock { Text = srv.Tag.ToUpperInvariant(), Foreground = TextDim, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 9.5, Margin = new Thickness(0, 3, 0, 0), Opacity = 0.9 });
        tile.Children.Add(texts);

        if (srv.Players.Length > 0)
        {
            var badge = new Border
            {
                Background = B("#B3000000"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0),
                Child = new TextBlock { Text = srv.Players, Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 11 }
            };
            tile.Children.Add(badge);
        }

        var edge = new Border { CornerRadius = new CornerRadius(14), BorderBrush = B("#1FFFFFFF"), BorderThickness = new Thickness(1), Background = Brushes.Transparent, IsHitTestVisible = false };
        tile.Children.Add(edge);

        tile.MouseEnter += (s, e) => { edge.BorderBrush = StrokeHi; };
        tile.MouseLeave += (s, e) => { edge.BorderBrush = B("#1FFFFFFF"); };
        tile.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (!busy) Play(srv.Args.Length > 0 ? srv.Args : LaunchArgs); };
        return tile;
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
                    case "sha256":
                    case "clientsha256":
                    case "expectedsha256": ExpectedSha256 = v; break;
                    case "installdir":  if (v.Length > 0) InstallDir = v; break;
                    case "launchexe":   LaunchExe = v; break;
                    case "launchargs":  LaunchArgs = v; break;
                    case "version":     Version = v; break;
                    case "title":       if (v.Length > 0) GameTitle = v; break;
                    case "tagline":     if (v.Length > 0) Tagline = v; break;
                    case "player":      if (v.Length > 0) PlayerName = v; break;
                    case "server":
                        // Server=Tag|Name|launch args   (args optional). A source that defines
                        // servers replaces the list from the previous source instead of appending.
                        if (!clearedServers) { Servers.Clear(); clearedServers = true; }
                        var parts = v.Split(new[] { '|' }, 4);
                        if (parts.Length >= 2)
                            Servers.Add(new ServerEntry(parts[0].Trim(), parts[1].Trim(), parts.Length > 2 ? parts[2].Trim() : "", parts.Length > 3 ? parts[3].Trim() : ""));
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

    bool IsInstalled() { return FindGameExe() != null; }

    bool HasPartial()
    {
        try { return File.Exists(partPath) && new FileInfo(partPath).Length > 0; } catch { return false; }
    }

    bool GameRunning()
    {
        try { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LaunchExe)).Length > 0; }
        catch { return false; }
    }

    void StartGameTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.Tick += delegate { try { RefreshState(); } catch { } };
        t.Start();
    }

    void RefreshState()
    {
        bool installed = IsInstalled();
        bool partial   = HasPartial();
        bool game      = GameRunning();

        // PLAY -> IN-GAME while the game runs (clicking it focuses the running game)
        SetButtonEnabled(playBtn, (installed || game) && !busy);
        object[] pmeta = (object[])playBtn.Tag;
        ((TextBlock)pmeta[1]).Text = Track(game ? "IN-GAME" : "PLAY", 1);

        // Install/Resume button: hidden once installed (no UPDATE); shown to install, resume, or while downloading
        object[] imeta = (object[])installBtn.Tag;
        if (installed && !partial && !busy)
        {
            installBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            installBtn.Visibility = Visibility.Visible;
            SetButtonEnabled(installBtn, !busy);
            ((TextBlock)imeta[1]).Text = Track(partial ? "RESUME" : "INSTALL", 1);
            ((TextBlock)imeta[2]).Text = partial ? "\uE768" : "\uE896";
        }

        if (!busy)
        {
            if (partial) statusText.Text = "Partial download saved (" + Human(new FileInfo(partPath).Length) + ") \u2014 click Resume to continue.";
            else if (game) statusText.Text = "In game.";
            else statusText.Text = installed ? "Installed \u2014 ready to play." : "Not installed yet \u2014 click Install to download.";
        }
    }

    // ---------- install (resumable, TLS 1.2+) ----------
    // Force TLS 1.2 (and 1.3 where the OS supports it): Cloudflare/R2 and most hosts refuse older protocols,
    // and .NET Framework may otherwise negotiate TLS 1.0/1.1 on some machines and fail.
    public static void ConfigureTls()
    {
        try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }  // Tls12 | Tls13
        catch { try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } }            // Tls12 only
        ServicePointManager.DefaultConnectionLimit = 8;
        ServicePointManager.Expect100Continue = false;
    }

    void StartInstall()
    {
        if (busy) return;
        if (string.IsNullOrEmpty(DownloadUrl) || DownloadUrl.Contains("REPLACE-ME"))
        { statusText.Foreground = AccentHi; statusText.Text = "Set DownloadUrl in launcher.cfg first."; return; }
        // Verification is mandatory: refuse to download/install anything we can't check.
        if (NormalizedExpectedHash().Length == 0)
        { statusText.Foreground = AccentHi; statusText.Text = "Set Sha256 in launcher.cfg first — downloads must be verified before install."; return; }
        try { Directory.CreateDirectory(InstallDir); Directory.CreateDirectory(cacheDir); }
        catch (Exception ex) { statusText.Foreground = AccentHi; statusText.Text = "Folder error: " + ex.Message; return; }

        busy = true; cancelRequested = false; RefreshState();
        statusText.Foreground = TextMute;
        progTrack.Visibility = Visibility.Visible; progFill.Width = 0;
        statusText.Text = "Connecting…";

        Log("StartInstall: url=" + DownloadUrl + "  installDir=" + InstallDir);
        dlThread = new Thread(DownloadWorker) { IsBackground = true, Name = "download" };
        dlThread.Start();
    }

    void CancelDownload()
    {
        cancelRequested = true;
        try { var r = activeReq; if (r != null) r.Abort(); } catch { }
    }

    void SetStatus(string text)
    {
        Dispatcher.BeginInvoke((Action)(() => { statusText.Text = text; }));
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
            if (total > 0) progFill.Width = progTrack.ActualWidth * Math.Min(1.0, (double)have / total);
            statusText.Text = (resumed ? "Resuming  " : "Downloading  ") + Human(have) + " / " + (total > 0 ? Human(total) : "?") +
                              (mbps > 0 ? "    " + mbps.ToString("0.0") + " MB/s" : "");
        }));
    }

    // Background thread. Downloads to <cache>\RustClient.zip.part using HTTP Range, so an interrupted
    // transfer continues where it stopped — across automatic retries AND across launcher restarts.
    void DownloadWorker()
    {
        string error = null; bool cancelled = false; bool verifyFailed = false; string verifyMsg = null;
        try
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
            long have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            string want  = DownloadUrl + "|" + etag + "|" + total;
            string saved = File.Exists(metaPath) ? File.ReadAllText(metaPath) : "";
            if (have > 0 && (saved != want || (total > 0 && have > total)))
            { Log("discarding stale .part (" + have + " bytes): meta mismatch"); try { File.Delete(partPath); } catch { } have = 0; }
            File.WriteAllText(metaPath, want);
            bool resumed = have > 0;
            Log("start: have=" + have + (resumed ? " (resuming)" : ""));

            // 3) download, auto-resuming on any network error
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
                        SetStatus("Connection lost — resuming in 5s (attempt " + attempt + "/30), " + Human(have) + " saved");
                        for (int i = 0; i < 50 && !cancelRequested; i++) Thread.Sleep(100);
                    }
                }
            }

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
                        verifyMsg = "Integrity check failed — the download did not match the expected SHA-256 and was rejected. Nothing was installed.";
                        Log("INTEGRITY FAIL (mismatch): expected=" + ExpectedSha256 + " actual=" + actualHash + " — discarding download");
                        try { File.Delete(partPath); } catch { }
                        try { File.Delete(metaPath); } catch { }
                    }
                    else
                    {
                        // No hash configured (StartInstall normally blocks this; defensive). Keep the
                        // .part so that adding Sha256 and clicking Resume verifies it without re-downloading.
                        verifyMsg = "Install blocked — no expected SHA-256 is configured, so the download can't be verified. Set Sha256 in launcher.cfg, then click Resume. Nothing was installed.";
                        Log("INTEGRITY FAIL (no hash configured): actual=" + actualHash + " — keeping .part for Resume once a hash is set");
                    }
                }
                else
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    File.Move(partPath, zipPath);
                    try { File.Delete(metaPath); } catch { }
                    Log("finalized+verified zip (" + new FileInfo(zipPath).Length + " bytes), extracting to " + InstallDir);
                    SetStatus("Extracting… this can take several minutes.");
                    ExtractZip(zipPath, InstallDir);
                    Log("extract done");
                    try { File.Delete(zipPath); } catch { }
                    InstallLauncherAndShortcut();
                }
            }
        }
        catch (Exception ex) { error = ex.Message; Log("FAILED: " + ex.GetType().Name + ": " + ex.Message); }

        Dispatcher.BeginInvoke((Action)(() =>
        {
            busy = false; activeReq = null;
            if (cancelled) statusText.Text = "Paused — progress saved. Click Resume to continue.";
            else if (verifyFailed) { statusText.Foreground = AccentHi; statusText.Text = verifyMsg ?? "Integrity check failed — the download was rejected. Nothing was installed."; }
            else if (error != null) { statusText.Foreground = AccentHi; statusText.Text = "Download failed: " + error; }
            else { progFill.Width = progTrack.ActualWidth; statusText.Text = "Install complete — ready to play!"; }
            RefreshState();   // the .part is kept on cancel/error so Resume can continue
        }));
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
            int total = archive.Entries.Count, done = 0;
            string fullDest = Path.GetFullPath(dest);
            foreach (var entry in archive.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
                if (!target.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\") || entry.Name.Length == 0)
                    Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
                done++;
                if ((done & 63) == 0 || done == total)
                {
                    int d = done, t = total;
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        statusText.Text = "Extracting  " + d + " / " + t + " files…";
                        if (t > 0) progFill.Width = progTrack.ActualWidth * ((double)d / t);
                    }));
                }
            }
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
            Log("integrity: no ExpectedSha256 configured — refusing to install unverified download (sha256=" + actualHex + ")");
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
            statusText.Text = "Verifying download…";
            progFill.Width = 0;
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
                        statusText.Text = "Verifying  " + Human(d) + (t > 0 ? " / " + Human(t) : "");
                        if (t > 0) progFill.Width = progTrack.ActualWidth * Math.Min(1.0, (double)d / t);
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
    void MinimizeWithFade()
    {
        try
        {
            var anim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(140)));
            anim.Completed += (s, e) => { try { WindowState = WindowState.Minimized; } catch { } };
            BeginAnimation(OpacityProperty, anim);
        }
        catch { try { WindowState = WindowState.Minimized; } catch { } }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Normal)   // fade back in on restore
        {
            try { BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)))); }
            catch { Opacity = 1; }
        }
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

    void RestoreFromGame()
    {
        try
        {
            try { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; Activate(); } } catch { }
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
            statusText.Foreground = AccentHi; statusText.Text = "Client not installed — click Install first.";
            RefreshState(); return;
        }
        // single instance: never launch a second RustClient — focus the running one instead
        try
        {
            var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LaunchExe));
            if (running.Length > 0)
            {
                statusText.Foreground = TextMute; statusText.Text = "RustOrigin is already running.";
                try { if (running[0].MainWindowHandle != IntPtr.Zero) SetForegroundWindow(running[0].MainWindowHandle); } catch { }
                return;
            }
        }
        catch { }
        try
        {
            var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe) };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            var proc = Process.Start(psi);
            if (proc != null) { WatchGame(proc); if (Prefs.GetBool("MinimizeInGame", false)) { try { WindowState = WindowState.Minimized; } catch { } } }
            statusText.Foreground = TextMute; statusText.Text = "Launching…";
        }
        catch (Exception ex) { statusText.Foreground = AccentHi; statusText.Text = "Launch error: " + ex.Message; }
    }

    // ---------- settings panel ----------
    void OpenSettings()
    {
        try
        {
            if (settingsOpen) return;
            settingsOpen = true;
            RefreshSettingsInfo();
            SetSettingsStatus("");

            // Bring the Settings tab in front and slide it up while it fades in.
            settingsPanel.Visibility = Visibility.Visible;
            settingsPanel.IsHitTestVisible = true;
            var tt = settingsPanel.RenderTransform as TranslateTransform;
            if (tt == null) { tt = new TranslateTransform(); settingsPanel.RenderTransform = tt; }
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(34, 0, new Duration(TimeSpan.FromMilliseconds(300))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            settingsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(230))));

            // Erase the home view: fade + settle it out, then take it off the tree.
            if (homeView != null)
            {
                homeView.IsHitTestVisible = false;
                var ht = homeView.RenderTransform as TranslateTransform;
                if (ht == null) { ht = new TranslateTransform(); homeView.RenderTransform = ht; }
                ht.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -18, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
                var fo = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(180)));
                fo.Completed += delegate { if (settingsOpen) homeView.Visibility = Visibility.Collapsed; };
                homeView.BeginAnimation(OpacityProperty, fo);
            }
        }
        catch { }
    }

    void CloseSettings()
    {
        try
        {
            settingsOpen = false;

            // Restore the home view underneath and fade + settle it back in.
            if (homeView != null)
            {
                homeView.Visibility = Visibility.Visible;
                homeView.IsHitTestVisible = true;
                var ht = homeView.RenderTransform as TranslateTransform;
                if (ht == null) { ht = new TranslateTransform(); homeView.RenderTransform = ht; }
                ht.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-18, 0, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                homeView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(240))));
            }

            // Slide the Settings tab down while it fades out, then take it off the tree.
            settingsPanel.IsHitTestVisible = false;
            var tt = settingsPanel.RenderTransform as TranslateTransform;
            if (tt == null) { tt = new TranslateTransform(); settingsPanel.RenderTransform = tt; }
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 34, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            var a = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(180)));
            a.Completed += delegate { if (!settingsOpen) settingsPanel.Visibility = Visibility.Collapsed; };
            settingsPanel.BeginAnimation(OpacityProperty, a);
        }
        catch { try { settingsPanel.Visibility = Visibility.Collapsed; if (homeView != null) homeView.Visibility = Visibility.Visible; } catch { } }
    }

    void SetSettingsStatus(string t) { try { if (settingsStatus != null) settingsStatus.Text = t; } catch { } }

    static string AppVer()
    {
        try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); } catch { return "1.0"; }
    }

    TextBlock SettingsLabel(string text)
    {
        return new TextBlock { Text = Track(text, 1), Foreground = TextMute, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 20, 0, 0) };
    }

    Border SettingsButton(string glyph, string text, Brush bg, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var ic = Icon(glyph, 15, TextHi); ic.Margin = new Thickness(0, 0, 8, 0); sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = text, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = TextHi, VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Height = 44, MinWidth = 150, CornerRadius = new CornerRadius(12), Background = bg, BorderBrush = Stroke, BorderThickness = new Thickness(1), Padding = new Thickness(16, 0, 16, 0), Cursor = Cursors.Hand, Child = sp };
        b.MouseEnter += (s, e) => { b.Background = GlassHi; };
        b.MouseLeave += (s, e) => { b.Background = bg; };
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return b;
    }

    void BuildSettingsPanel(Grid content)
    {
        // A real Settings tab: it replaces the whole home view (hero, grid, rail) but keeps the
        // launcher's video background showing through a dark scrim, in the home design language.
        var panel = new Border { Background = B("#CC0A0C11"), Visibility = Visibility.Collapsed, Opacity = 0 };
        panel.RenderTransform = new TranslateTransform();
        var rootg = new Grid();

        var top = new Grid { VerticalAlignment = VerticalAlignment.Top, Height = 92, Margin = new Thickness(40, 0, 40, 0) };
        var tl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var back = IconBtn("\uE72B", 44, delegate { CloseSettings(); }, false); back.Background = Glass; back.BorderBrush = Stroke;
        tl.Children.Add(back);
        tl.Children.Add(new TextBlock { Text = Track("SETTINGS", 2), Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.Bold, FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) });
        top.Children.Add(tl);
        var trr = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        trr.Children.Add(Tag("RustOrigin  \u2022  v" + AppVer(), 12.5));
        var cls = IconBtn("\uE8BB", 44, delegate { CloseSettings(); }, false); cls.Background = Glass; cls.BorderBrush = Stroke; cls.Margin = new Thickness(12, 0, 0, 0);
        trr.Children.Add(cls);
        top.Children.Add(trr);
        rootg.Children.Add(top);

        var nav = GlassPanel(22); nav.Width = 236; nav.HorizontalAlignment = HorizontalAlignment.Left; nav.VerticalAlignment = VerticalAlignment.Top; nav.Margin = new Thickness(40, 120, 0, 40); nav.Padding = new Thickness(12);
        var navsp = new StackPanel();
        navsp.Children.Add(NavItem("\uEDA2", "Installation", 0));
        navsp.Children.Add(NavItem("\uE7FC", "Game", 1));
        navsp.Children.Add(NavItem("\uE896", "Downloads", 2));
        navsp.Children.Add(NavItem("\uE790", "Appearance", 3));
        navsp.Children.Add(NavItem("\uE946", "About", 4));
        var stc = new Border { CornerRadius = new CornerRadius(14), Background = B("#0AFFFFFF"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 8, 0, 0) };
        var stsp = new StackPanel();
        stsp.Children.Add(new TextBlock { Text = Track("STATUS", 1), Foreground = TextMute, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 11 });
        var stline = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        stline.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Online, VerticalAlignment = VerticalAlignment.Center });
        stline.Children.Add(new TextBlock { Text = "Installed \u00B7 ready", Foreground = TextDim, FontSize = 13, Margin = new Thickness(8, 0, 0, 0) });
        stsp.Children.Add(stline); stc.Child = stsp; navsp.Children.Add(stc);
        nav.Child = navsp; rootg.Children.Add(nav);

        var scroll = new ScrollViewer { Margin = new Thickness(300, 120, 40, 36), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Brushes.Transparent };
        var pages = new Grid();
        settingsPages.Clear();
        settingsPages.Add(BuildInstallPage());
        settingsPages.Add(BuildGamePage());
        settingsPages.Add(BuildDownloadsPage());
        settingsPages.Add(BuildAppearancePage());
        settingsPages.Add(BuildAboutPage());
        foreach (FrameworkElement pg in settingsPages) { pg.Visibility = Visibility.Collapsed; pages.Children.Add(pg); }
        scroll.Content = pages; rootg.Children.Add(scroll);

        panel.Child = rootg;
        panel.MouseLeftButtonDown += (s, e) => { e.Handled = true; };
        settingsPanel = panel;
        content.Children.Add(panel);
        SelectSettingsPage(0);
    }

    Border NavItem(string glyph, string text, int index)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var ic = Icon(glyph, 17, TextDim); ic.Margin = new Thickness(4, 0, 12, 0); sp.Children.Add(ic);
        var lbl = new TextBlock { Text = text, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 14.5, Foreground = TextDim, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(lbl);
        var b = new Border { Height = 50, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = sp, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
        b.MouseEnter += (s, e) => { if (settingsPage != index) b.Background = B("#0FFFFFFF"); };
        b.MouseLeave += (s, e) => { if (settingsPage != index) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; SelectSettingsPage(index); };
        navBorders.Add(b); navIcons.Add(ic); navLabels.Add(lbl);
        return b;
    }

    void SelectSettingsPage(int i)
    {
        try
        {
            settingsPage = i;
            for (int k = 0; k < navBorders.Count; k++)
            {
                bool a = (k == i);
                navBorders[k].Background = a ? B("#29D14431") : (Brush)Brushes.Transparent;
                navBorders[k].BorderBrush = a ? B("#66D14431") : (Brush)Brushes.Transparent;
                navIcons[k].Foreground = a ? TextHi : TextDim;
                navLabels[k].Foreground = a ? TextHi : TextDim;
            }
            for (int k = 0; k < settingsPages.Count; k++) settingsPages[k].Visibility = (k == i) ? Visibility.Visible : Visibility.Collapsed;
            if (i >= 0 && i < settingsPages.Count)
                settingsPages[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        catch { }
    }

    void PageHeader(StackPanel col, string tag, string title)
    {
        var t = Tag(tag, 12.5); t.HorizontalAlignment = HorizontalAlignment.Left; col.Children.Add(t);
        col.Children.Add(new TextBlock { Text = title, Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 30, Margin = new Thickness(0, 14, 0, 18) });
    }

    void OpenInstallFolder() { try { if (Directory.Exists(InstallDir)) Process.Start("explorer.exe", "\"" + InstallDir + "\""); } catch { } }

    void SaveLaunchArgs()
    {
        try { if (launchArgsBox != null) { LaunchArgs = launchArgsBox.Text == null ? "" : launchArgsBox.Text.Trim(); Prefs.Set("LaunchArgs", LaunchArgs); SetSettingsStatus("Launch options saved."); } } catch { }
    }

    Border ToggleSwitch(bool on, Action<bool> changed)
    {
        var thumb = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(3, 0, 3, 0) };
        var track = new Border { Width = 48, Height = 26, CornerRadius = new CornerRadius(13), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Child = thumb, Background = on ? B("#D14431") : B("#26FFFFFF") };
        bool[] st = new bool[] { on };
        track.MouseLeftButtonUp += (s, e) => { e.Handled = true; st[0] = !st[0]; thumb.HorizontalAlignment = st[0] ? HorizontalAlignment.Right : HorizontalAlignment.Left; track.Background = st[0] ? B("#D14431") : B("#26FFFFFF"); if (changed != null) changed(st[0]); };
        return track;
    }

    Border ToggleRow(string title, string sub, bool on, Action<bool> changed)
    {
        var card = GlassPanel(16); card.Padding = new Thickness(22, 16, 22, 16); card.Margin = new Thickness(0, 12, 0, 0);
        var g = new Grid();
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 70, 0) };
        left.Children.Add(new TextBlock { Text = title, Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 15 });
        if (sub != null) left.Children.Add(new TextBlock { Text = sub, Foreground = TextDim, FontSize = 13, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
        g.Children.Add(left);
        var sw = ToggleSwitch(on, changed); sw.HorizontalAlignment = HorizontalAlignment.Right; sw.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(sw);
        card.Child = g; return card;
    }

    FrameworkElement BuildInstallPage()
    {
        var col = new StackPanel();
        PageHeader(col, "INSTALLATION", "Where RustOrigin lives");

        var loc = GlassPanel(16); loc.Padding = new Thickness(24);
        var locg = new StackPanel();
        var locrow = new Grid();
        var locleft = new StackPanel();
        locleft.Children.Add(new TextBlock { Text = Track("INSTALL LOCATION", 1), Foreground = TextMute, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        var locpath = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        locpath.Children.Add(Icon("\uE8B7", 16, AccentHi));
        settingsPathLabel = new TextBlock { Text = InstallDir, Foreground = TextHi, FontSize = 16, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        locpath.Children.Add(settingsPathLabel); locleft.Children.Add(locpath); locrow.Children.Add(locleft);
        var locbtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        locbtns.Children.Add(SettingsButton("\uE8DA", "CHANGE", Glass, delegate { PickInstallFolder(); }));
        var ob = SettingsButton("\uE8A7", "OPEN", Glass, delegate { OpenInstallFolder(); }); ob.Margin = new Thickness(10, 0, 0, 0);
        locbtns.Children.Add(ob); locrow.Children.Add(locbtns); locg.Children.Add(locrow);
        var dtop = new Grid { Margin = new Thickness(0, 22, 0, 6) };
        dtop.Children.Add(new TextBlock { Text = "Disk usage", Foreground = TextDim, FontSize = 13 });
        settingsDiskLabel = new TextBlock { Text = "", Foreground = TextDim, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right };
        dtop.Children.Add(settingsDiskLabel); locg.Children.Add(dtop);
        var track = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = B("#1AFFFFFF"), ClipToBounds = true };
        var bargrid = new Grid();
        settingsDiskFillCol = new ColumnDefinition { Width = new GridLength(0.3, GridUnitType.Star) };
        settingsDiskRestCol = new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) };
        bargrid.ColumnDefinitions.Add(settingsDiskFillCol); bargrid.ColumnDefinitions.Add(settingsDiskRestCol);
        var fillb = new Border { CornerRadius = new CornerRadius(5), Background = Grad("#D14431", "#E9583F") };
        Grid.SetColumn(fillb, 0); bargrid.Children.Add(fillb); track.Child = bargrid; locg.Children.Add(track);
        loc.Child = locg; col.Children.Add(loc);

        var statsg = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        statsg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statsg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        statsg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cc1 = GlassPanel(16); cc1.Padding = new Thickness(24);
        var cc1s = new StackPanel();
        cc1s.Children.Add(new TextBlock { Text = Track("CLIENT", 1), Foreground = TextMute, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        cc1s.Children.Add(new TextBlock { Text = "Installed", Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.Bold, FontSize = 26, Margin = new Thickness(0, 6, 0, 0) });
        cc1s.Children.Add(new TextBlock { Text = "January 2021 build", Foreground = TextMute, FontSize = 13, Margin = new Thickness(0, 2, 0, 0) });
        cc1.Child = cc1s; Grid.SetColumn(cc1, 0); statsg.Children.Add(cc1);
        var cc2 = GlassPanel(16); cc2.Padding = new Thickness(24);
        var cc2g = new Grid();
        var cc2s = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        cc2s.Children.Add(new TextBlock { Text = Track("DOWNLOAD CACHE", 1), Foreground = TextMute, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        settingsCacheLabel = new TextBlock { Text = "0 MB", Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.Bold, FontSize = 26, Margin = new Thickness(0, 6, 0, 0) };
        cc2s.Children.Add(settingsCacheLabel); cc2g.Children.Add(cc2s);
        var clrb = SettingsButton("\uE894", "CLEAR", Glass, delegate { ClearCache(); RefreshSettingsInfo(); }); clrb.HorizontalAlignment = HorizontalAlignment.Right; clrb.VerticalAlignment = VerticalAlignment.Center;
        cc2g.Children.Add(clrb); cc2.Child = cc2g; Grid.SetColumn(cc2, 2); statsg.Children.Add(cc2);
        col.Children.Add(statsg);

        settingsStatus = new TextBlock { Text = "", Foreground = TextMute, FontSize = 13, Margin = new Thickness(2, 16, 0, 0), TextWrapping = TextWrapping.Wrap };
        col.Children.Add(settingsStatus);

        var dz = new Border { CornerRadius = new CornerRadius(16), Background = B("#14D14431"), BorderBrush = B("#59D14431"), BorderThickness = new Thickness(1), Padding = new Thickness(24), Margin = new Thickness(0, 16, 0, 0) };
        var dzg = new Grid();
        var dzl = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var dzt = new StackPanel { Orientation = Orientation.Horizontal };
        dzt.Children.Add(Icon("\uE7BA", 18, AccentHi));
        dzt.Children.Add(new TextBlock { Text = "Uninstall client", Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.Bold, FontSize = 16, Margin = new Thickness(8, 0, 0, 0) });
        dzl.Children.Add(dzt);
        dzl.Children.Add(new TextBlock { Text = "Removes the game files. The launcher stays installed.", Foreground = TextDim, FontSize = 13.5, Margin = new Thickness(0, 4, 0, 0) });
        dzg.Children.Add(dzl);
        var ub = SettingsButton("\uE74D", "UNINSTALL", B("#38D14431"), delegate { Uninstall(); }); ub.HorizontalAlignment = HorizontalAlignment.Right; ub.VerticalAlignment = VerticalAlignment.Center;
        dzg.Children.Add(ub); dz.Child = dzg; col.Children.Add(dz);
        return col;
    }

    FrameworkElement BuildGamePage()
    {
        var col = new StackPanel();
        PageHeader(col, "GAME", "Launch & runtime");

        var lo = GlassPanel(16); lo.Padding = new Thickness(24);
        var los = new StackPanel();
        los.Children.Add(new TextBlock { Text = Track("LAUNCH OPTIONS", 1), Foreground = TextMute, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        los.Children.Add(new TextBlock { Text = "Extra arguments passed to the client, e.g. -console +connect ip:port", Foreground = TextDim, FontSize = 13, Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap });
        var lorow = new Grid();
        lorow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lorow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        lorow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        launchArgsBox = new TextBox { Text = LaunchArgs, Foreground = TextHi, CaretBrush = Brushes.White, Background = B("#14FFFFFF"), BorderBrush = Stroke, BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 14, 10), FontSize = 14, Height = 44, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
        Grid.SetColumn(launchArgsBox, 0); lorow.Children.Add(launchArgsBox);
        var save = SettingsButton("\uE74E", "SAVE", Glass, delegate { SaveLaunchArgs(); }); save.MinWidth = 110; Grid.SetColumn(save, 2); lorow.Children.Add(save);
        los.Children.Add(lorow);
        lo.Child = los; col.Children.Add(lo);

        var exec = GlassPanel(16); exec.Padding = new Thickness(24); exec.Margin = new Thickness(0, 16, 0, 0);
        var exes = new StackPanel();
        exes.Children.Add(new TextBlock { Text = Track("GAME EXECUTABLE", 1), Foreground = TextMute, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        var exerow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        exerow.Children.Add(Icon("\uE7FC", 16, AccentHi));
        exerow.Children.Add(new TextBlock { Text = LaunchExe, Foreground = TextHi, FontSize = 16, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        exes.Children.Add(exerow);
        exec.Child = exes; col.Children.Add(exec);

        col.Children.Add(ToggleRow("Minimize launcher while in-game", "Hide the launcher to the taskbar when the game starts; it returns when you quit.", Prefs.GetBool("MinimizeInGame", false), delegate(bool v) { Prefs.Set("MinimizeInGame", v); }));
        return col;
    }

    FrameworkElement BuildDownloadsPage()
    {
        var col = new StackPanel();
        PageHeader(col, "DOWNLOADS", "Client files");

        var src = GlassPanel(16); src.Padding = new Thickness(24);
        var srcs = new StackPanel();
        srcs.Children.Add(new TextBlock { Text = Track("DOWNLOAD SOURCE", 1), Foreground = TextMute, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        srcs.Children.Add(new TextBlock { Text = DownloadUrl, Foreground = TextHi, FontSize = 14.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        src.Child = srcs; col.Children.Add(src);

        var cac = GlassPanel(16); cac.Padding = new Thickness(24); cac.Margin = new Thickness(0, 16, 0, 0);
        var cag = new Grid();
        var cas = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        cas.Children.Add(new TextBlock { Text = Track("DOWNLOAD CACHE", 1), Foreground = TextMute, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        cas.Children.Add(new TextBlock { Text = "Leftover .zip / .part files from downloads.", Foreground = TextDim, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        cag.Children.Add(cas);
        var cclr = SettingsButton("\uE894", "CLEAR CACHE", Glass, delegate { ClearCache(); RefreshSettingsInfo(); }); cclr.HorizontalAlignment = HorizontalAlignment.Right; cclr.VerticalAlignment = VerticalAlignment.Center;
        cag.Children.Add(cclr); cac.Child = cag; col.Children.Add(cac);

        var rep = GlassPanel(16); rep.Padding = new Thickness(24); rep.Margin = new Thickness(0, 16, 0, 0);
        var repg = new Grid();
        var reps = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        reps.Children.Add(new TextBlock { Text = "Re-download client", Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.SemiBold, FontSize = 15 });
        reps.Children.Add(new TextBlock { Text = "Fetch the client again and reinstall over the current files.", Foreground = TextDim, FontSize = 13, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
        repg.Children.Add(reps);
        var repb = SettingsButton("\uE896", "RE-DOWNLOAD", Glass, delegate { CloseSettings(); StartInstall(); }); repb.HorizontalAlignment = HorizontalAlignment.Right; repb.VerticalAlignment = VerticalAlignment.Center;
        repg.Children.Add(repb); rep.Child = repg; col.Children.Add(rep);
        return col;
    }

    FrameworkElement BuildAppearancePage()
    {
        var col = new StackPanel();
        PageHeader(col, "APPEARANCE", "Look & motion");
        col.Children.Add(ToggleRow("Background video", "Play the animated intro behind the launcher.", Prefs.GetBool("BgVideo", true), delegate(bool v) { Prefs.Set("BgVideo", v); try { if (video != null) { if (v) video.Play(); else video.Pause(); } } catch { } }));
        col.Children.Add(ToggleRow("Loop intro only (0:16)", "Restart the clip at the 16-second mark instead of playing it in full.", Prefs.GetBool("ShortLoop", true), delegate(bool v) { Prefs.Set("ShortLoop", v); }));
        return col;
    }

    FrameworkElement BuildAboutPage()
    {
        var col = new StackPanel();
        PageHeader(col, "ABOUT", "RustOrigin");

        var card = GlassPanel(16); card.Padding = new Thickness(28);
        var cs = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        if (logoBmp != null) head.Children.Add(new Image { Source = logoBmp, Width = 44, Height = 44, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
        var htxt = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(logoBmp != null ? 16 : 0, 0, 0, 0) };
        htxt.Children.Add(new TextBlock { Text = "RustOrigin", Foreground = TextHi, FontFamily = Brand, FontWeight = FontWeights.Bold, FontSize = 24 });
        htxt.Children.Add(new TextBlock { Text = "Private Rust world \u2014 January 2021 build", Foreground = TextDim, FontSize = 14, Margin = new Thickness(0, 2, 0, 0) });
        head.Children.Add(htxt);
        cs.Children.Add(head);
        cs.Children.Add(AboutRow("Launcher version", "v" + AppVer()));
        cs.Children.Add(AboutRow("Client build", "January 2021"));
        cs.Children.Add(AboutRow("Install folder", InstallDir));
        card.Child = cs; col.Children.Add(card);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        btns.Children.Add(SettingsButton("\uE8A7", "OPEN INSTALL FOLDER", Glass, delegate { OpenInstallFolder(); }));
        var logb = SettingsButton("\uE7C3", "VIEW LOG", Glass, delegate { try { string lp = Path.Combine(cacheDir, "launcher.log"); if (File.Exists(lp)) Process.Start("notepad.exe", "\"" + lp + "\""); else SetSettingsStatus("No log yet."); } catch { } }); logb.Margin = new Thickness(12, 0, 0, 0);
        btns.Children.Add(logb);
        col.Children.Add(btns);
        return col;
    }

    Border AboutRow(string k, string v)
    {
        var g = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        g.Children.Add(new TextBlock { Text = k, Foreground = TextDim, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(new TextBlock { Text = v, Foreground = TextHi, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(120, 0, 0, 0) });
        return new Border { Child = g, BorderBrush = B("#12FFFFFF"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 4) };
    }

    static class Prefs
    {
        static Dictionary<string, string> map;
        static string PrefsPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustOrigin", "prefs.cfg"); } }
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

    void RefreshSettingsInfo()
    {
        try { if (settingsPathLabel != null) settingsPathLabel.Text = InstallDir; } catch { }
        try
        {
            var di = new System.IO.DriveInfo(Path.GetPathRoot(Path.GetFullPath(InstallDir)));
            long free = di.AvailableFreeSpace, total = di.TotalSize;
            double frac = total > 0 ? (double)(total - free) / total : 0.3;
            if (frac < 0.02) frac = 0.02; if (frac > 1) frac = 1;
            if (settingsDiskFillCol != null) { settingsDiskFillCol.Width = new GridLength(frac, GridUnitType.Star); settingsDiskRestCol.Width = new GridLength(1 - frac, GridUnitType.Star); }
            if (settingsDiskLabel != null) settingsDiskLabel.Text = Human(free) + " free of " + Human(total);
        }
        catch { }
        try
        {
            long cache = 0;
            foreach (string p in new string[] { zipPath, partPath }) { try { if (File.Exists(p)) cache += new FileInfo(p).Length; } catch { } }
            if (settingsCacheLabel != null) settingsCacheLabel.Text = cache > 0 ? Human(cache) : "0 MB";
        }
        catch { }
    }

    void PickInstallFolder()
    {
        try
        {
            var d = new System.Windows.Forms.FolderBrowserDialog();
            d.Description = "Choose where RustOrigin installs the client";
            try { if (Directory.Exists(InstallDir)) d.SelectedPath = InstallDir; } catch { }
            if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK && d.SelectedPath.Length > 0)
            {
                InstallDir = d.SelectedPath;
                try { Directory.CreateDirectory(cacheDir); File.WriteAllText(Path.Combine(cacheDir, "installdir.txt"), InstallDir); } catch { }
                if (settingsPathLabel != null) settingsPathLabel.Text = InstallDir;
                RefreshState();
                SetSettingsStatus("Install location set. New downloads go here.");
            }
        }
        catch (Exception ex) { SetSettingsStatus("Folder error: " + ex.Message); }
    }

    void ClearCache()
    {
        int n = 0;
        foreach (string p in new string[] { zipPath, partPath, metaPath }) { try { if (File.Exists(p)) { File.Delete(p); n++; } } catch { } }
        SetSettingsStatus(n > 0 ? "Download cache cleared." : "Cache already empty.");
        RefreshState();
    }

    void Uninstall()
    {
        if (busy) { SetSettingsStatus("Busy \u2014 wait for the current operation to finish."); return; }
        if (GameRunning()) { SetSettingsStatus("Close the game before uninstalling."); return; }
        if (MessageBox.Show(this, "Delete the installed RustOrigin client from:\n\n" + InstallDir + "\n\nThe launcher itself stays. Continue?", "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        busy = true; RefreshState(); SetSettingsStatus("Uninstalling\u2026 this can take a minute.");
        ThreadPool.QueueUserWorkItem(delegate { UninstallWorker(); });
    }

    void UninstallWorker()
    {
        string err = null; int removed = 0;
        try
        {
            string[] items = { "Bundles", "RustClient_Data", "cfg", "temp", "maps", "RustClient.exe", "GameAssembly.dll", "UnityPlayer.dll", "UnityCrashHandler64.exe", "EasyAntiCheat", "Rust.exe" };
            foreach (string it in items)
            {
                try
                {
                    string p = Path.Combine(InstallDir, it);
                    if (Directory.Exists(p)) { Directory.Delete(p, true); removed++; }
                    else if (File.Exists(p)) { File.Delete(p); removed++; }
                }
                catch { }
            }
            foreach (string p in new string[] { zipPath, partPath, metaPath }) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
        }
        catch (Exception ex) { err = ex.Message; }
        Dispatcher.BeginInvoke((Action)delegate
        {
            busy = false;
            SetSettingsStatus(err != null ? "Uninstall error: " + err : "Uninstalled \u2014 removed " + removed + " item(s).");
            RefreshState();
        });
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
            string installed = Path.Combine(InstallDir, "RustOrigin.exe");
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
            Dispatcher.Invoke((Action)(() => CreateDesktopShortcut(target, "RustOrigin")));   // STA thread for COM
            Log("shortcut -> " + target);
        }
        catch (Exception ex) { Log("shortcut step failed: " + ex.Message); }
    }

    static void CreateDesktopShortcut(string targetExe, string name)
    {
        try
        {
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
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "RustOrigin Launcher" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
        catch { }
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
            if (MessageBox.Show(this, "A download is in progress. Quit now?\n\nProgress is saved — click Resume next time to continue where it left off.",
                "Quit", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
            CancelDownload();
        }
        try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
        try { video.Stop(); video.Close(); } catch { }
        base.OnClosing(e);
    }
}

