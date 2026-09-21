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

// RUSTORIGIN launcher - WPF port of the Superdesign canvas composition:
// rounded dark card, full-bleed cross-fading screenshot slideshow, floating glass UI
// (left rail, top-right pills, hero block, server column, friends rail).
// Code-only WPF on .NET Framework 4.x: runs on any Windows 10/11, no runtime install.

public class App
{
    [STAThread]
    static void Main()
    {
        Assets.Ensure();                 // unpack embedded screenshots/logo/fonts/config (single-exe distribution)
        LauncherWindow.ConfigureTls();
        var app = new Application();
        app.Run(new LauncherWindow());
    }
}

// Everything the launcher needs ships INSIDE the exe as manifest resources and is unpacked once
// per version to %LOCALAPPDATA%\RUSTORIGIN\assets\<version>\ (loaded from real files for the
// background screenshots and private fonts). An optional launcher.cfg next to the exe overrides
// the embedded defaults.
static class Assets
{
    public static string Dir = "";
    static readonly string[] Files = {
        "1.jpg", "2.jpg", "3.jpg", "4.jpg", "main.jpg", "train.jpg",
        "logo.png", "server-cover.png", "launcher.cfg",
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
    public string Tag, Name, Args, Players, Cover;
    public ServerEntry(string tag, string name, string args, string players = "", string cover = "")
    { Tag = tag; Name = name; Args = args; Players = players; Cover = cover; }
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
    string UpdateRepo  = "RUSTORIGIN/RustOriginLauncher";   // owner/repo checked for launcher self-updates (GitHub Releases). Empty disables.
    string GameTitle   = "RUSTORIGIN";
    string Tagline     = "RUSTORIGIN is a private Rust world on the January 2021 build. Craft, raid and survive with a tight community - one click to jump in.";
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
    Grid         bgHost;      // background slideshow container the glass panels sample (real acrylic)
    Image        slideBack, slideFront;   // two stacked images for cross-fading between screenshots
    BitmapImage[] slides = new BitmapImage[0];
    int          slideIndex;
    BitmapImage  logoBmp;
    BitmapImage  coverBmp;
    Grid homeView;
    const double CornerR = 32;
    Border edgeBorder;
    Border maxBtn; TextBlock maxGlyph;

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
            Servers.Add(new ServerEntry("Vanilla",  "RustOrigin Main", "", "", "main.jpg"));
            Servers.Add(new ServerEntry("Training", "Aim Train",       "", "", "train.jpg"));
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

        // ---- window chrome (native Windows title bar + standard window features) ----
        Title = "RustOrigin Launcher";
        try
        {
            var ico = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
            if (ico != null) base.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                ico.Handle, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch { }
        Width = 1440; Height = 860;
        MinWidth = 960; MinHeight = 600;
        // Frameless + rounded corners, but a REAL native window underneath: WindowChrome keeps
        // drag, resize, maximize, Aero Snap, taskbar and the system menu; custom caption buttons
        // (built in BuildCaption) provide min/max/close. WM_GETMINMAXINFO keeps a maximized window
        // inside the work area (taskbar stays visible).
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;                       // needed for the rounded corners to show through
        Background = Brushes.Transparent;
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

        mainGrid = new Grid { Background = B("#0B0D12") };
        mainGrid.SizeChanged += (s, e) => ApplyRounding();
        Content = mainGrid;

        BuildBackground();
        BuildGradient();
        BuildContent();

        // 1px light edge to match the rounded card
        edgeBorder = new Border { CornerRadius = new CornerRadius(CornerR), BorderBrush = B("#1AFFFFFF"),
            BorderThickness = new Thickness(1), Background = Brushes.Transparent, IsHitTestVisible = false };
        mainGrid.Children.Add(edgeBorder);

        BuildCaption(mainGrid);
        ApplyRounding();

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

    // ---------- rounded frameless chrome + caption buttons ----------
    void ApplyRounding()
    {
        double r = WindowState == WindowState.Maximized ? 0 : CornerR;   // square when maximized
        try { mainGrid.Clip = new RectangleGeometry(new Rect(0, 0, mainGrid.ActualWidth, mainGrid.ActualHeight), r, r); } catch { }
        if (edgeBorder != null) edgeBorder.CornerRadius = new CornerRadius(r);
        if (maxGlyph != null) maxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";   // restore : maximize
    }

    void BuildCaption(Grid host)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 12, 0) };
        row.Children.Add(CaptionBtn("", delegate { WindowState = WindowState.Minimized; }, false));   // minimize
        maxBtn = CaptionBtn("", delegate { ToggleMaximize(); }, false);                                // maximize/restore
        maxGlyph = (TextBlock)maxBtn.Child;
        row.Children.Add(maxBtn);
        row.Children.Add(CaptionBtn("", delegate { Close(); }, true));                                 // close
        host.Children.Add(row);
    }

    Border CaptionBtn(string glyph, Action onClick, bool closeBtn)
    {
        var tb = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 11, Foreground = B("#E6E8EE"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Width = 42, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = tb, Margin = new Thickness(4, 0, 0, 0) };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(b, true);   // clickable inside the caption drag area
        b.MouseEnter += (s, e) => { b.Background = closeBtn ? Accent : B("#1AFFFFFF"); };
        b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return b;
    }

    void ToggleMaximize() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }

    // ---------- native window plumbing ----------
    const int GWL_STYLE = -16, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        }
        catch { }
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
        // Load the embedded screenshots (unpacked to Assets.Dir; fall back to a copy next to the exe).
        var list = new List<BitmapImage>();
        foreach (string n in new[] { "1.jpg", "2.jpg", "3.jpg", "4.jpg" })
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

        // Auto-switch every 7s with a ~0.9s cross-fade (gated by the BgSlideshow pref).
        slideIndex = 0;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        t.Tick += (s, e) => { try { if (slides.Length > 1 && Prefs.GetBool("BgSlideshow", true)) NextSlide(); } catch { } };
        t.Start();
    }

    void NextSlide()
    {
        int next = (slideIndex + 1) % slides.Length;
        slideFront.Source = slides[next];
        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(900)));
        fade.Completed += (s, e) => { try { slideBack.Source = slides[next]; } catch { } };   // settle the fade onto the back layer
        slideFront.BeginAnimation(UIElement.OpacityProperty, fade);
        slideIndex = next;
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
        homeView = new Grid { Width = 1440, Height = 860 };
        BuildHero(homeView);
        BuildServerGrid(homeView);
        mainGrid.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = homeView });
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
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 1 };   // vertical list
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
        double W = 340, H = 150;
        var tile = new Grid { Width = W, Height = H, Margin = new Thickness(7), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        tile.Clip = new RectangleGeometry(new Rect(0, 0, W, H), 16, 16);

        // Per-server cover (the Server= cover field), else the shared cover, else a gradient.
        BitmapImage cov = null;
        if (!string.IsNullOrEmpty(srv.Cover))
            cov = LoadBitmap(Path.Combine(Assets.Dir, srv.Cover)) ?? LoadBitmap(Path.Combine(AppDir(), srv.Cover));
        if (cov == null) cov = coverBmp;
        if (cov != null)
        {
            var img = new Image { Source = cov, Stretch = Stretch.UniformToFill };
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
            FontSize = 19, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
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
                    case "updaterepo":  UpdateRepo = v; break;
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
                        // Server=Tag|Name|launch args|players|cover   (all but Name optional). A source
                        // that defines servers replaces the list from the previous source, not appends.
                        if (!clearedServers) { Servers.Clear(); clearedServers = true; }
                        var parts = v.Split(new[] { '|' }, 5);
                        if (parts.Length >= 2)
                            Servers.Add(new ServerEntry(parts[0].Trim(), parts[1].Trim(),
                                parts.Length > 2 ? parts[2].Trim() : "", parts.Length > 3 ? parts[3].Trim() : "",
                                parts.Length > 4 ? parts[4].Trim() : ""));
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
            if (partial) statusText.Text = "Partial download saved (" + Human(new FileInfo(partPath).Length) + ") - click Resume to continue.";
            else if (game) statusText.Text = "In game.";
            else statusText.Text = installed ? "Installed - ready to play." : "Not installed yet - click Install to download.";
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
        { statusText.Foreground = AccentHi; statusText.Text = "Set Sha256 in launcher.cfg first - downloads must be verified before install."; return; }
        try { Directory.CreateDirectory(InstallDir); Directory.CreateDirectory(cacheDir); }
        catch (Exception ex) { statusText.Foreground = AccentHi; statusText.Text = "Folder error: " + ex.Message; return; }

        busy = true; cancelRequested = false; RefreshState();
        statusText.Foreground = TextMute;
        progTrack.Visibility = Visibility.Visible; progFill.Width = 0;
        statusText.Text = "Connecting...";

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
    // transfer continues where it stopped - across automatic retries AND across launcher restarts.
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
                        SetStatus("Connection lost - resuming in 5s (attempt " + attempt + "/30), " + Human(have) + " saved");
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
                        verifyMsg = "Integrity check failed - the download did not match the expected SHA-256 and was rejected. Nothing was installed.";
                        Log("INTEGRITY FAIL (mismatch): expected=" + ExpectedSha256 + " actual=" + actualHash + " - discarding download");
                        try { File.Delete(partPath); } catch { }
                        try { File.Delete(metaPath); } catch { }
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
                    Log("finalized+verified zip (" + new FileInfo(zipPath).Length + " bytes), extracting to " + InstallDir);
                    SetStatus("Extracting... this can take several minutes.");
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
            if (cancelled) statusText.Text = "Paused - progress saved. Click Resume to continue.";
            else if (verifyFailed) { statusText.Foreground = AccentHi; statusText.Text = verifyMsg ?? "Integrity check failed - the download was rejected. Nothing was installed."; }
            else if (error != null) { statusText.Foreground = AccentHi; statusText.Text = "Download failed: " + error; }
            else { progFill.Width = progTrack.ActualWidth; statusText.Text = "Install complete - ready to play!"; }
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
                        statusText.Text = "Extracting  " + d + " / " + t + " files...";
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
            statusText.Text = "Verifying download...";
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
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        ApplyRounding();                         // square corners when maximized, rounded otherwise
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
            statusText.Foreground = AccentHi; statusText.Text = "Client not installed - click Install first.";
            RefreshState(); return;
        }
        // single instance: never launch a second RustClient - focus the running one instead
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
            statusText.Foreground = TextMute; statusText.Text = "Launching...";
        }
        catch (Exception ex) { statusText.Foreground = AccentHi; statusText.Text = "Launch error: " + ex.Message; }
    }

    // ---------- settings panel ----------
    static string AppVer()
    {
        try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); } catch { return "1.0"; }
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

    // ---------- launcher self-update ----------
    // Checks the configured GitHub repo's latest release, and if it is newer than this build,
    // downloads the new RustOrigin.exe, VERIFIES its SHA-256 against the release's SHA256SUMS.txt,
    // and swaps itself out (rename-running-exe trick) before relaunching. A failed hash check
    // rejects the update - the launcher never runs an unverified replacement, same as the client.
    void StartUpdateCheck()
    {
        try
        {
            if (!Prefs.GetBool("AutoUpdate", true)) return;
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

        string exeUrl  = UpdateParsing.AssetUrl(json, "RustOrigin.exe");
        string sumsUrl = UpdateParsing.AssetUrl(json, "SHA256SUMS.txt");
        if (exeUrl == null || sumsUrl == null) { Log("update: release " + tag + " missing RustOrigin.exe or SHA256SUMS.txt asset"); return; }
        Log("update available: v" + current + " -> " + tag);

        bool go = false;
        Dispatcher.Invoke((Action)(() =>
        {
            go = MessageBox.Show(this,
                "A new version of RustOrigin Launcher is available.\n\nInstalled:  v" + current + "\nLatest:     " + tag + "\n\nDownload and update now?",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
        }));
        if (!go) return;

        string expected = UpdateParsing.HashFromSums(HttpGetString(sumsUrl), "RustOrigin.exe");
        if (expected == null) { UpdateFail("Could not read the update checksum."); return; }

        string self = SelfPath();
        string newPath = self + ".new";
        try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
        if (!HttpDownload(exeUrl, newPath)) { UpdateFail("The update download failed."); return; }

        string actual = Sha256File(newPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(newPath); } catch { }
            Log("update REJECTED (hash mismatch): expected " + expected + " got " + actual);
            UpdateFail("The downloaded update failed its integrity check and was discarded. Nothing was changed.");
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
            UpdateFail("Could not apply the update (the install folder may be read-only). Opening the releases page so you can update manually.");
            try { Process.Start(new ProcessStartInfo("https://github.com/" + repo + "/releases/latest") { UseShellExecute = true }); } catch { }
        }
    }

    void UpdateFail(string msg)
    {
        Dispatcher.Invoke((Action)(() => MessageBox.Show(this, msg, "Update", MessageBoxButton.OK, MessageBoxImage.Warning)));
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

    bool HttpDownload(string url, string dest)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA; req.Timeout = 30000; req.ReadWriteTimeout = 60000; req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                s.CopyTo(fs);
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
            if (MessageBox.Show(this, "A download is in progress. Quit now?\n\nProgress is saved - click Resume next time to continue where it left off.",
                "Quit", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
            CancelDownload();
        }
        try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
        base.OnClosing(e);
    }
}

