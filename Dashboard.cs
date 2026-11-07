using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace CyberMonitor;

/// <summary>
/// Renders the "cyberpunk retro" dashboard: dark background, grid, glowing neon,
/// chamfered corners, segmented bars and CRT scanlines.
/// </summary>
public sealed class Dashboard : IDisposable
{
    static readonly Color Bg = Color.FromArgb(6, 5, 16);
    static readonly Color Grid = Color.FromArgb(22, 18, 52);
    static readonly Color Cyan = Color.FromArgb(0, 240, 255);
    static readonly Color Magenta = Color.FromArgb(255, 43, 214);
    static readonly Color Yellow = Color.FromArgb(232, 255, 58);
    static readonly Color Violet = Color.FromArgb(150, 90, 255);
    static readonly Color Red = Color.FromArgb(255, 51, 85);
    static readonly Color Text = Color.FromArgb(232, 230, 255);
    static readonly Color Dim = Color.FromArgb(120, 116, 160);

    const int HistoryLength = 56;
    readonly Queue<float> _cpuHist = new(), _gpuHist = new();
    readonly Font _fTiny, _fSmall, _fLabel, _fValue, _fTab;
    readonly FontFamily _bigFamily;
    int _tick;

    public int Width { get; }
    public int Height { get; }

    public Dashboard(int width, int height)
    {
        Width = width;
        Height = height;
        _fTiny = MakeFont(9f, FontStyle.Regular, "Consolas", "Cascadia Mono", "Courier New");
        _fSmall = MakeFont(11f, FontStyle.Regular, "Consolas", "Cascadia Mono", "Courier New");
        _fLabel = MakeFont(12f, FontStyle.Bold, "Bahnschrift SemiBold SemiConden", "Bahnschrift", "Segoe UI");
        _fValue = MakeFont(17f, FontStyle.Regular, "Bahnschrift SemiBold Condensed", "Bahnschrift Condensed", "Bahnschrift", "Segoe UI");
        _fTab = MakeFont(13f, FontStyle.Bold, "Bahnschrift Bold Condensed", "Bahnschrift", "Segoe UI");
        _bigFamily = FindFamily("Bahnschrift Bold Condensed", "Bahnschrift SemiBold Condensed", "Bahnschrift", "Impact", "Segoe UI");
    }

    static FontFamily FindFamily(params string[] names)
    {
        using var installed = new InstalledFontCollection();
        foreach (var n in names)
        {
            var f = installed.Families.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (f != null) return new FontFamily(f.Name);
        }
        return FontFamily.GenericSansSerif;
    }

    static Font MakeFont(float px, FontStyle style, params string[] names)
    {
        var fam = FindFamily(names);
        if (!fam.IsStyleAvailable(style)) style = FontStyle.Regular;
        return new Font(fam, px, style, GraphicsUnit.Pixel);
    }

    public Bitmap Render(Snapshot s)
    {
        _tick++;
        Push(_cpuHist, s.CpuLoad ?? 0);
        Push(_gpuHist, s.GpuLoad ?? 0);

        var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Bg);

        DrawBackground(g);
        DrawHeader(g);

        int colW = (Width - 18) / 2;
        int x1 = 6, x2 = 12 + colW;

        DrawProcessorPanel(g, new Rectangle(x1, 32, colW, 146), Cyan, "CPU", s.CpuName,
            s.CpuLoad, s.CpuTemp, s.CpuClock, s.CpuPower, null, _cpuHist,
            s.CpuCores > 0 ? $"{s.CpuCores} THR" : null);
        DrawProcessorPanel(g, new Rectangle(x2, 32, colW, 146), Magenta, "GPU", s.GpuName,
            s.GpuLoad, s.GpuTemp, s.GpuClock, s.GpuPower, s.GpuFan, _gpuHist, null);

        DrawMemoryPanel(g, new Rectangle(x1, 184, colW, 70), Yellow, "RAM",
            s.RamUsedGb, s.RamTotalGb, s.RamLoad, "GB");
        float? vramPct = s.VramUsedMb != null && s.VramTotalMb > 0 ? 100f * s.VramUsedMb / s.VramTotalMb : null;
        DrawMemoryPanel(g, new Rectangle(x2, 184, colW, 70), Violet, "VRAM",
            s.VramUsedMb / 1024f, s.VramTotalMb / 1024f, vramPct, "GB");

        DrawFooter(g, new Rectangle(6, 260, Width - 12, 54), s);
        DrawScanlines(g);
        return bmp;
    }

    // ---------- regions ----------

    void DrawBackground(Graphics g)
    {
        using var pen = new Pen(Grid);
        for (int x = 0; x < Width; x += 24) g.DrawLine(pen, x, 0, x, Height);
        for (int y = 0; y < Height; y += 24) g.DrawLine(pen, 0, y, Width, y);
    }

    void DrawHeader(Graphics g)
    {
        // brand block
        var brand = new[] { new Point(0, 0), new Point(150, 0), new Point(138, 24), new Point(0, 24) };
        using (var b = new SolidBrush(Magenta)) g.FillPolygon(b, brand);
        using (var b = new SolidBrush(Bg)) g.DrawString("// SYS.MONITOR", _fTab, b, 8, 4);

        // decorative diagonal stripes
        using (var p = new Pen(Color.FromArgb(120, Magenta), 2))
            for (int i = 0; i < 5; i++) g.DrawLine(p, 152 + i * 8, 22, 162 + i * 8, 2);

        var now = DateTime.Now;
        string clock = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string date = now.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();

        // Fixed-width cells so the clock and the date never shift as digits change
        const float clockPx = 22;
        float clockX = Width - ClockWidth(clockPx) - 8;
        DrawClock(g, clock, clockPx, clockX, 1, colonsOn: _tick % 2 == 0);
        using (var b = new SolidBrush(Dim))
        {
            var ds = g.MeasureString("WED 30 SEP 2026", _fSmall);
            g.DrawString(date, _fSmall, b, clockX - 8 - ds.Width, 6);
        }

        using var line = new Pen(Color.FromArgb(160, Cyan), 1);
        g.DrawLine(line, 0, 27, Width, 27);
    }

    void DrawProcessorPanel(Graphics g, Rectangle r, Color accent, string tag, string name,
        float? load, float? temp, float? clock, float? power, float? fan, Queue<float> hist, string? extra)
    {
        DrawFrame(g, r, accent, tag);

        // hardware name
        using (var b = new SolidBrush(Dim))
            g.DrawString(Ellipsize(g, name.ToUpperInvariant(), _fTiny, r.Width - 66), _fTiny, b, r.X + 58, r.Y + 4);

        // Right-aligned in three fixed digit cells so "%" never moves
        string pct = load != null ? $"{Math.Min(load.Value, 100):0}" : "--";
        float cell = DigitCell(50), numW = cell * 3;
        float nx = r.X + 8 + numW - cell * pct.Length;
        foreach (char c in pct)
        {
            float w = MeasurePath(c.ToString(), 50).Width;
            GlowText(g, c.ToString(), 50, accent, nx + (cell - w) / 2, r.Y + 20);
            nx += cell;
        }
        using (var b = new SolidBrush(accent))
            g.DrawString("%", _fValue, b, r.X + 10 + numW, r.Y + 46);
        using (var b = new SolidBrush(Dim))
            g.DrawString("LOAD", _fTiny, b, r.X + 12 + numW, r.Y + 34);

        // stats column
        int sx = r.X + r.Width - 92, sy = r.Y + 22;
        StatLine(g, sx, sy, "TEMP", temp != null ? $"{temp:0}°C" : "--", TempColor(temp));
        StatLine(g, sx, sy + 19, "CLK", clock != null ? (clock >= 1000 ? $"{clock / 1000:0.00}GHz" : $"{clock:0}MHz") : "--", Text);
        StatLine(g, sx, sy + 38, "PWR", power != null ? $"{power:0}W" : "--", Text);
        if (fan != null) StatLine(g, sx, sy + 57, "FAN", $"{fan:0}", Text);
        else if (extra != null) StatLine(g, sx, sy + 57, "CORE", extra, Text);

        // history
        var sparkRect = new Rectangle(r.X + 8, r.Y + 84, r.Width - 16, 34);
        DrawSparkline(g, sparkRect, hist, accent);

        // segmented bar
        DrawSegmentBar(g, new Rectangle(r.X + 8, r.Y + 124, r.Width - 16, 12), load ?? 0, accent);
    }

    void DrawMemoryPanel(Graphics g, Rectangle r, Color accent, string tag,
        float? used, float? total, float? pct, string unit)
    {
        DrawFrame(g, r, accent, tag);

        string val = used != null
            ? (total != null ? $"{used:0.0} / {total:0.0} {unit}" : $"{used:0.0} {unit}")
            : "--";
        using (var b = new SolidBrush(Text))
            g.DrawString(val, _fValue, b, r.X + 8, r.Y + 22);

        string p = pct != null ? $"{pct:0}%" : "--";
        var size = MeasurePath(p, 30);
        GlowText(g, p, 30, accent, r.Right - size.Width - 10, r.Y + 14);

        DrawSegmentBar(g, new Rectangle(r.X + 8, r.Y + 50, r.Width - 16, 12), pct ?? 0, accent);
    }

    void DrawFooter(Graphics g, Rectangle r, Snapshot s)
    {
        DrawChamfer(g, r, Color.FromArgb(90, Cyan), 8, fill: Color.FromArgb(14, 12, 34));

        int cw = r.Width / 4;
        FooterCell(g, r.X + 10, r.Y + 6, "NET ▲", FormatRate(s.NetUpBps), Cyan);
        FooterCell(g, r.X + 10 + cw, r.Y + 6, "NET ▼", FormatRate(s.NetDownBps), Magenta);
        FooterCell(g, r.X + 10 + cw * 2, r.Y + 6, $"DISK {s.DiskLabel}",
            s.DiskUsedPct != null ? $"{s.DiskUsedPct:0}%  {s.DiskFreeGb:0}G" : "--", Yellow);
        var up = s.Uptime;
        FooterCell(g, r.X + 10 + cw * 3, r.Y + 6, "UPTIME", $"{(int)up.TotalDays}d {up.Hours:00}:{up.Minutes:00}", Violet);

        using var sep = new Pen(Color.FromArgb(60, Cyan));
        for (int i = 1; i < 4; i++) g.DrawLine(sep, r.X + cw * i, r.Y + 8, r.X + cw * i, r.Bottom - 8);

        // status line with blinking cursor
        string status = (s.CpuDriverMissing
            ? "> CPU TEMP/PWR N/A :: INSTALL PAWNIO DRIVER"
            : "> LINK OK :: TURZX-3.5 :: 115200") + (_tick % 2 == 0 ? " _" : "");
        using var b = new SolidBrush(Color.FromArgb(150, s.CpuDriverMissing ? Yellow : Cyan));
        g.DrawString(status, _fTiny, b, r.X + 10, r.Bottom - 15);
    }

    // ---------- components ----------

    void DrawFrame(Graphics g, Rectangle r, Color accent, string tag)
    {
        DrawChamfer(g, r, accent, 10, fill: Color.FromArgb(12, 10, 28));

        // section tab
        var tab = new[] { new Point(r.X, r.Y), new Point(r.X + 50, r.Y), new Point(r.X + 42, r.Y + 16), new Point(r.X, r.Y + 16) };
        using (var b = new SolidBrush(accent)) g.FillPolygon(b, tab);
        using (var b = new SolidBrush(Bg)) g.DrawString(tag, _fTab, b, r.X + 5, r.Y);

        // corner markers
        using var p = new Pen(accent, 2);
        g.DrawLine(p, r.Right - 14, r.Bottom - 2, r.Right - 2, r.Bottom - 2);
        g.DrawLine(p, r.Right - 2, r.Bottom - 14, r.Right - 2, r.Bottom - 2);
    }

    static void DrawChamfer(Graphics g, Rectangle r, Color stroke, int c, Color fill)
    {
        var pts = new[]
        {
            new Point(r.X, r.Y), new Point(r.Right - c, r.Y), new Point(r.Right, r.Y + c),
            new Point(r.Right, r.Bottom), new Point(r.X + c, r.Bottom), new Point(r.X, r.Bottom - c),
        };
        using (var b = new SolidBrush(fill)) g.FillPolygon(b, pts);
        using (var glow = new Pen(Color.FromArgb(40, stroke), 4)) g.DrawPolygon(glow, pts);
        using (var p = new Pen(stroke, 1)) g.DrawPolygon(p, pts);
    }

    void StatLine(Graphics g, int x, int y, string label, string value, Color color)
    {
        using (var b = new SolidBrush(Dim)) g.DrawString(label, _fTiny, b, x, y + 5);
        using (var b = new SolidBrush(color)) g.DrawString(value, _fValue, b, x + 32, y);
    }

    void FooterCell(Graphics g, int x, int y, string label, string value, Color color)
    {
        using (var b = new SolidBrush(Dim)) g.DrawString(label, _fTiny, b, x, y);
        using (var b = new SolidBrush(color)) g.DrawString(value, _fLabel, b, x, y + 12);
    }

    static void DrawSegmentBar(Graphics g, Rectangle r, float pct, Color accent)
    {
        const int segs = 24;
        float segW = (r.Width - (segs - 1) * 2f) / segs;
        int lit = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100f * segs);
        for (int i = 0; i < segs; i++)
        {
            var rect = new RectangleF(r.X + i * (segW + 2), r.Y, segW, r.Height);
            Color c = i < lit
                ? (i >= segs * 0.85 ? Red : accent)
                : Color.FromArgb(32, accent);
            using var b = new SolidBrush(c);
            g.FillRectangle(b, rect);
        }
    }

    static void DrawSparkline(Graphics g, Rectangle r, Queue<float> hist, Color accent)
    {
        using (var p = new Pen(Color.FromArgb(40, accent)) { DashStyle = DashStyle.Dot })
        {
            g.DrawLine(p, r.X, r.Y, r.Right, r.Y);
            g.DrawLine(p, r.X, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);
        }
        var vals = hist.ToArray();
        if (vals.Length < 2) return;

        float step = (float)r.Width / (HistoryLength - 1);
        float x0 = r.Right - (vals.Length - 1) * step;
        var pts = vals.Select((v, i) => new PointF(x0 + i * step, r.Bottom - Math.Clamp(v, 0, 100) / 100f * r.Height)).ToArray();

        using (var path = new GraphicsPath())
        {
            path.AddLines(pts);
            path.AddLine(pts[^1], new PointF(pts[^1].X, r.Bottom));
            path.AddLine(new PointF(pts[^1].X, r.Bottom), new PointF(pts[0].X, r.Bottom));
            path.CloseFigure();
            using var fill = new LinearGradientBrush(r, Color.FromArgb(110, accent), Color.FromArgb(0, accent), LinearGradientMode.Vertical);
            g.FillPath(fill, path);
        }
        using (var glow = new Pen(Color.FromArgb(60, accent), 4)) g.DrawLines(glow, pts);
        using (var p = new Pen(accent, 1.5f)) g.DrawLines(p, pts);
    }

    void GlowText(Graphics g, string text, float px, Color color, float x, float y)
    {
        using var path = new GraphicsPath();
        path.AddString(text, _bigFamily, (int)FontStyle.Regular, px, new PointF(x, y), StringFormat.GenericTypographic);
        foreach (var (w, a) in new[] { (8f, 22), (5f, 40), (2.5f, 80) })
        {
            using var pen = new Pen(Color.FromArgb(a, color), w) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    float DigitCell(float px) => "0123456789".Max(c => MeasurePath(c.ToString(), px).Width) + 1;
    float ColonCell(float px) => MeasurePath(":", px).Width + 3;
    float ClockWidth(float px) => DigitCell(px) * 6 + ColonCell(px) * 2;

    /// <summary>Draws HH:mm:ss centring each glyph in a fixed cell; colons blink by fading, not by disappearing.</summary>
    void DrawClock(Graphics g, string text, float px, float x, float y, bool colonsOn)
    {
        float digit = DigitCell(px), colon = ColonCell(px);
        foreach (char c in text)
        {
            bool isColon = c == ':';
            float cell = isColon ? colon : digit;
            float w = MeasurePath(c.ToString(), px).Width;
            var color = isColon && !colonsOn ? Color.FromArgb(70, Yellow) : Yellow;
            GlowText(g, c.ToString(), px, color, x + (cell - w) / 2, y);
            x += cell;
        }
    }

    SizeF MeasurePath(string text, float px)
    {
        using var path = new GraphicsPath();
        path.AddString(text, _bigFamily, (int)FontStyle.Regular, px, PointF.Empty, StringFormat.GenericTypographic);
        var b = path.GetBounds();
        return new SizeF(b.Right, b.Bottom);
    }

    void DrawScanlines(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.None;
        using var p = new Pen(Color.FromArgb(55, 0, 0, 0));
        for (int y = 1; y < Height; y += 2) g.DrawLine(p, 0, y, Width, y);
    }

    // ---------- util ----------

    static Color TempColor(float? t) => t switch
    {
        null => Text,
        >= 85 => Red,
        >= 70 => Yellow,
        _ => Cyan,
    };

    static string FormatRate(float? bps)
    {
        if (bps == null) return "--";
        float v = bps.Value;
        if (v >= 1024 * 1024) return $"{v / 1048576:0.0} MB/s";
        if (v >= 1024) return $"{v / 1024:0} KB/s";
        return $"{v:0} B/s";
    }

    static string Ellipsize(Graphics g, string s, Font f, int maxWidth)
    {
        if (g.MeasureString(s, f).Width <= maxWidth) return s;
        while (s.Length > 1 && g.MeasureString(s + "…", f).Width > maxWidth) s = s[..^1];
        return s + "…";
    }

    static void Push(Queue<float> q, float v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }

    public void Dispose()
    {
        _fTiny.Dispose(); _fSmall.Dispose(); _fLabel.Dispose(); _fValue.Dispose(); _fTab.Dispose();
        _bigFamily.Dispose();
    }
}
