using System.Text.Json;
using Microsoft.Win32;

namespace CyberMonitor;

public sealed class Settings
{
    public string Port { get; set; } = "auto";
    public int Brightness { get; set; } = 80;
    public bool Flip { get; set; }
    public int RefreshMs { get; set; } = 1000;

    static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (JsonException) { }
        return new Settings();
    }

    public void Save() =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

        // --snapshot <file.png> [frames]: render without a display (previews/tests).
        // With frames > 1, writes file-0.png, file-1.png, ... one second apart.
        if (args.Length >= 2 && args[0] == "--snapshot")
        {
            int frames = args.Length > 2 ? int.Parse(args[2]) : 1;
            using var hw = new HardwareReader();
            using var dash = new Dashboard(TuringLcd.NativeHeight, TuringLcd.NativeWidth);
            hw.Read();
            for (int i = 0; i < 40; i++) // warm up the history graphs with real readings
            {
                Thread.Sleep(250);
                dash.Render(hw.Read()).Dispose();
            }
            for (int i = 0; i < frames; i++)
            {
                if (i > 0) Thread.Sleep(1000);
                using var frame = dash.Render(hw.Read());
                var path = frames == 1 ? args[1]
                    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, $"{Path.GetFileNameWithoutExtension(args[1])}-{i}.png");
                frame.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return;
        }

        using var mutex = new Mutex(true, "CyberMonitor.Turzx35", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(Settings.Load()));
    }
}

sealed class TrayContext : ApplicationContext
{
    readonly Settings _settings;
    readonly NotifyIcon _tray;
    readonly CancellationTokenSource _cts = new();
    readonly Thread _worker;
    PreviewForm? _preview;
    Bitmap? _lastFrame;
    readonly object _frameLock = new();
    volatile bool _reconfigure = true;
    volatile string _status = "Starting…";
    volatile bool _asleep;
    readonly ManualResetEventSlim _screenOff = new(false);

    public TrayContext(Settings settings)
    {
        _settings = settings;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show preview", null, (_, _) => ShowPreview());
        var bright = new ToolStripMenuItem("Brightness");
        foreach (var level in new[] { 10, 25, 50, 75, 100 })
            bright.DropDownItems.Add($"{level}%", null, (_, _) => { _settings.Brightness = level; Persist(); });
        menu.Items.Add(bright);
        menu.Items.Add("Rotate 180°", null, (_, _) => { _settings.Flip = !_settings.Flip; Persist(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "CyberMonitor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowPreview();

        // The display is powered by USB standby power, so it stays lit after shutdown
        // unless we switch it off ourselves.
        SystemEvents.SessionEnded += OnSessionEnded;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _worker = new Thread(Loop) { IsBackground = true, Name = "lcd" };
        _worker.Start();
    }

    void OnSessionEnded(object? sender, SessionEndedEventArgs e) => TurnScreenOff();

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) TurnScreenOff();
        else if (e.Mode == PowerModes.Resume)
        {
            _asleep = false;
            _reconfigure = true;
        }
    }

    /// <summary>Asks the worker to blank the display and waits briefly for it to happen.</summary>
    void TurnScreenOff()
    {
        _screenOff.Reset();
        _asleep = true;
        _screenOff.Wait(3000);
    }

    void Persist()
    {
        _settings.Save();
        _reconfigure = true;
    }

    void ShowPreview()
    {
        if (_preview == null || _preview.IsDisposed)
        {
            _preview = new PreviewForm(() =>
            {
                lock (_frameLock) return (_lastFrame?.Clone() as Bitmap, _status);
            });
        }
        _preview.Show();
        _preview.Activate();
    }

    void Loop()
    {
        using var hw = new HardwareReader();
        Dashboard? dash = null;
        TuringLcd? lcd = null;
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            if (_asleep)
            {
                if (!_screenOff.IsSet)
                {
                    BlankScreen(lcd);
                    _screenOff.Set();
                }
                token.WaitHandle.WaitOne(500);
                continue;
            }
            try
            {
                if (lcd == null)
                {
                    lcd = TuringLcd.Open(_settings.Port);
                    _reconfigure = true;
                }
                if (_reconfigure)
                {
                    _reconfigure = false;
                    lcd.ScreenOn();
                    lcd.SetBrightness(_settings.Brightness);
                    // On this unit the default mounting corresponds to "reverse landscape"
                    lcd.SetOrientation(_settings.Flip ? TuringLcd.Orientation.Landscape : TuringLcd.Orientation.ReverseLandscape);
                    if (dash == null || dash.Width != lcd.Width || dash.Height != lcd.Height)
                    {
                        dash?.Dispose();
                        dash = new Dashboard(lcd.Width, lcd.Height);
                    }
                    _status = $"Connected on {lcd.PortName}";
                }

                var frame = dash!.Render(hw.Read());
                lcd.Display(frame);
                lock (_frameLock)
                {
                    _lastFrame?.Dispose();
                    _lastFrame = frame;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
            {
                _status = ex is UnauthorizedAccessException
                    ? "Port in use — close the vendor UsbMonitor app"
                    : $"No display: {ex.Message}";
                lcd?.Dispose();
                lcd = null;
                token.WaitHandle.WaitOne(3000);
                continue;
            }

            var elapsed = Environment.TickCount64 - started;
            token.WaitHandle.WaitOne((int)Math.Max(50, _settings.RefreshMs - elapsed));
        }

        BlankScreen(lcd);
        lcd?.Dispose();
        dash?.Dispose();
    }

    static void BlankScreen(TuringLcd? lcd)
    {
        if (lcd == null) return;
        try
        {
            lcd.SetBrightness(0);
            lcd.ScreenOff();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionEnded -= OnSessionEnded;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _cts.Cancel();
        _worker.Join(5000);
        _tray.Visible = false;
        _tray.Dispose();
        _preview?.Dispose();
        base.ExitThreadCore();
    }
}

sealed class PreviewForm : Form
{
    readonly Func<(Bitmap? frame, string status)> _source;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    Bitmap? _frame;
    string _status = "";

    public PreviewForm(Func<(Bitmap?, string)> source)
    {
        _source = source;
        Text = "CyberMonitor — preview";
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(480, 344);
        DoubleBuffered = true;
        _timer.Tick += (_, _) =>
        {
            var (f, s) = _source();
            _frame?.Dispose();
            _frame = f;
            _status = s;
            Invalidate();
        };
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_frame != null) e.Graphics.DrawImage(_frame, 0, 0, 480, 320);
        TextRenderer.DrawText(e.Graphics, _status, Font, new Point(6, 324), Color.FromArgb(0, 240, 255));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _frame?.Dispose();
        }
        base.Dispose(disposing);
    }
}
