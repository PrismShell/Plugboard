using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Plugboard.Host;

// System-tray presence for the host. The glyph is a toggle switch — on-brand for
// "switch"board and legible at 16px: a green switch flipped ON when the host is
// running, a red switch flipped OFF when it's stopped. A balloon fires on startup
// so you actually see it come up, and the menu can open the live /catalog in a
// browser to show which plugins loaded. Runs on its own STA thread; the ASP.NET
// host keeps running on the main thread.
public static class TrayIcon
{
    private static NotifyIcon?        _tray;
    private static Form?              _dispatcher;
    private static ToolStripMenuItem? _statusItem;
    private static ToolStripMenuItem? _startItem;
    private static ToolStripMenuItem? _stopItem;
    private static ToolStripMenuItem? _restartItem;
    private static string             _baseUrl = "http://localhost:9195";

    public static void Start(string baseUrl, Action start, Action restart, Action stop)
    {
        _baseUrl = baseUrl;
        var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();

            _dispatcher = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized, Opacity = 0 };
            _dispatcher.Show();
            _dispatcher.Hide();

            _tray = new NotifyIcon { Visible = true, Text = "Plugboard" };

            _statusItem  = new ToolStripMenuItem("● Running")  { Enabled = false };
            _startItem   = new ToolStripMenuItem("Start",   null, (_, _) => start())   { Visible = false };
            _restartItem = new ToolStripMenuItem("Restart", null, (_, _) => restart());
            _stopItem    = new ToolStripMenuItem("Stop",    null, (_, _) => stop());

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            // One entry to the admin page; its nav bar reaches catalog / tools /
            // health / info, so a submenu of endpoints is redundant now.
            menu.Items.Add(new ToolStripMenuItem("Admin Page", null, (_, _) => OpenUrl(_baseUrl + "/console")));
            menu.Items.Add(new ToolStripMenuItem("Copy Base URL", null, (_, _) => TrySetClipboard(_baseUrl)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_startItem);
            menu.Items.Add(_restartItem);
            menu.Items.Add(_stopItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => { _tray.Visible = false; Application.Exit(); Environment.Exit(0); }));

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => OpenUrl(_baseUrl + "/console");

            SetRunning(null);
            ready.Set();
            Application.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait(2000);   // let the tray exist before the caller fires a balloon
    }

    // subtitle e.g. "localhost:9195 · 6 plugins"
    public static void SetRunning(string? subtitle)
    {
        Dispatch(() =>
        {
            _tray!.Icon  = CreateIcon(on: true);
            _tray.Text   = Truncate("Plugboard — Running" + (subtitle is null ? "" : $"\n{subtitle}"));
            if (_statusItem  != null) _statusItem.Text     = "● Running" + (subtitle is null ? "" : $"  ({subtitle})");
            if (_startItem   != null) _startItem.Visible   = false;
            if (_restartItem != null) _restartItem.Visible = true;
            if (_stopItem    != null) _stopItem.Visible    = true;
        });
    }

    public static void SetStopped()
    {
        Dispatch(() =>
        {
            _tray!.Icon  = CreateIcon(on: false);
            _tray.Text   = "Plugboard — Stopped";
            if (_statusItem  != null) _statusItem.Text     = "● Stopped";
            if (_startItem   != null) _startItem.Visible   = true;
            if (_restartItem != null) _restartItem.Visible = false;
            if (_stopItem    != null) _stopItem.Visible    = false;
        });
    }

    // Balloon tip — the "you can see it started" signal.
    public static void Notify(string title, string text)
        => Dispatch(() =>
        {
            if (_tray == null) return;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText  = text;
            _tray.BalloonTipIcon  = ToolTipIcon.Info;
            _tray.ShowBalloonTip(4000);
        });

    private static void Dispatch(Action a)
    {
        if (_dispatcher == null || !_dispatcher.IsHandleCreated) { a(); return; }
        if (_dispatcher.InvokeRequired) _dispatcher.Invoke(a);
        else a();
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser — ignore */ }
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { /* clipboard busy — ignore */ }
    }

    private static string Truncate(string s) => s.Length <= 63 ? s : s[..63];   // NotifyIcon.Text cap

    // A green plug glyph - the SAME design as plugboard.ico (the right-click menu icon
    // and the app icon), so the tray and the shell verb read as one brand. Green plug
    // when ON (running), slate/dimmed when OFF (stopped). Drawn at 64px, then
    // antialiased-downscaled by the tray.
    private static Icon CreateIcon(bool on)
    {
        const int S = 64;
        var bmp = new Bitmap(S, S);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Near-black rounded tile so the plug pops (matches plugboard.ico).
            using (var tile = RoundedRect(new Rectangle(2, 2, S - 4, S - 4), 12))
            {
                using var tb = new SolidBrush(Color.FromArgb(15, 18, 22));
                g.FillPath(tb, tile);
                using var te = new Pen(Color.FromArgb(40, 50, 66), 1.5f);
                g.DrawPath(te, tile);
            }

            var body    = on ? Color.FromArgb(34, 197, 94)   : Color.FromArgb(100, 116, 139); // green-500 / slate-500
            var bodyDk  = on ? Color.FromArgb(21, 128, 61)    : Color.FromArgb(71, 85, 105);
            var prong   = Color.FromArgb(203, 213, 225);      // silver
            var prongDk = Color.FromArgb(148, 163, 184);

            // Two prongs (top).
            using (var pb = new SolidBrush(prong))
            using (var pp = new Pen(prongDk, 1f))
                foreach (int cx in new[] { 25, 39 })
                {
                    using var pr = RoundedRect(new Rectangle(cx - 3, 8, 6, 16), 2);
                    g.FillPath(pb, pr); g.DrawPath(pp, pr);
                }

            // Plug body (rounded, outlined).
            var b = new Rectangle(15, 20, 34, 26);
            using (var path = RoundedRect(b, 7))
            using (var br = new SolidBrush(body))
            using (var pen = new Pen(bodyDk, 2f))
            { g.FillPath(br, path); g.DrawPath(pen, path); }

            // Faceplate highlight (same cue as the .ico).
            using (var hp = new Pen(Color.FromArgb(90, 255, 255, 255), 2f))
                g.DrawLine(hp, b.Left + 5, b.Top + 6, b.Right - 5, b.Top + 6);

            // Cord stub.
            using (var cb = new SolidBrush(bodyDk))
            using (var path = RoundedRect(new Rectangle(29, 44, 6, 9), 3))
                g.FillPath(cb, path);
        }
        var icon = Icon.FromHandle(bmp.GetHicon());
        bmp.Dispose();
        return icon;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);                    // top-left
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);               // top-right
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);          // bottom-right
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);              // bottom-left
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundedPill(Rectangle r)
    {
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, r.Height, r.Height, 90, 180);                     // left cap
        path.AddArc(r.Right - r.Height, r.Top, r.Height, r.Height, 270, 180);        // right cap
        path.CloseFigure();
        return path;
    }
}
