using System.Drawing.Imaging;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CyberMonitor;

/// <summary>
/// Turing/Turzx 3.5" (rev. A, USB CDC VID 1A86 / PID 5722).
/// Protocol (as observed on the vendor software, for interoperability):
///  - port: 115200 8N1, DTR+RTS enabled
///  - every command starts with 6 bytes: x,y,ex,ey packed as 10-bit values + command byte
///  - 197 = draw bitmap (RGB565 little-endian, row by row) into (x,y)-(ex,ey)
/// </summary>
public sealed class TuringLcd : IDisposable
{
    public const int NativeWidth = 320;
    public const int NativeHeight = 480;

    const byte CmdReset = 101;
    const byte CmdScreenOff = 108;
    const byte CmdScreenOn = 109;
    const byte CmdBrightness = 110;
    const byte CmdOrientation = 121;
    const byte CmdDisplayBitmap = 197;

    public enum Orientation { Portrait = 0, ReversePortrait = 1, Landscape = 2, ReverseLandscape = 3 }

    readonly SerialPort _port;
    readonly object _lock = new();
    ushort[]? _lastFrame;

    public string PortName => _port.PortName;
    public int Width { get; private set; } = NativeWidth;
    public int Height { get; private set; } = NativeHeight;

    TuringLcd(string portName)
    {
        _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 1000,
            WriteTimeout = 5000,
        };
        _port.Open();
    }

    public static TuringLcd Open(string? portName)
    {
        var name = string.IsNullOrWhiteSpace(portName) || portName.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? FindPort() : portName;
        if (name == null)
            throw new IOException("Display Turing 3.5\" not found (USB VID_1A86&PID_5722).");
        return new TuringLcd(name);
    }

    /// <summary>Finds the display COM port by its USB VID/PID.</summary>
    public static string? FindPort()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_1A86&PID_5722%'");
            foreach (var obj in searcher.Get())
            {
                var m = Regex.Match(obj["Name"]?.ToString() ?? "", @"\((COM\d+)\)");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (ManagementException) { }
        return null;
    }

    static byte[] Header(byte cmd, int x, int y, int ex, int ey) =>
    [
        (byte)(x >> 2),
        (byte)(((x & 3) << 6) + (y >> 4)),
        (byte)(((y & 15) << 4) + (ex >> 6)),
        (byte)(((ex & 63) << 2) + (ey >> 8)),
        (byte)(ey & 255),
        cmd,
    ];

    void Write(byte[] data, int offset, int count)
    {
        lock (_lock) _port.Write(data, offset, count);
    }

    void Send(byte cmd, int x = 0, int y = 0, int ex = 0, int ey = 0)
    {
        var h = Header(cmd, x, y, ex, ey);
        Write(h, 0, h.Length);
    }

    public void Reset() => Send(CmdReset);
    public void ScreenOn() => Send(CmdScreenOn);
    public void ScreenOff() => Send(CmdScreenOff);

    /// <summary>0..100 (%). The hardware uses 0 = brightest, 255 = off.</summary>
    public void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        Send(CmdBrightness, 255 - percent * 255 / 100);
    }

    public void SetOrientation(Orientation o)
    {
        bool landscape = o is Orientation.Landscape or Orientation.ReverseLandscape;
        Width = landscape ? NativeHeight : NativeWidth;
        Height = landscape ? NativeWidth : NativeHeight;

        var buf = new byte[16];
        Header(CmdOrientation, 0, 0, 0, 0).CopyTo(buf, 0);
        buf[6] = (byte)((int)o + 100);
        buf[7] = (byte)(Width >> 8);
        buf[8] = (byte)(Width & 255);
        buf[9] = (byte)(Height >> 8);
        buf[10] = (byte)(Height & 255);
        Write(buf, 0, buf.Length);
        _lastFrame = null;
    }

    /// <summary>Forces the next frame to be sent in full.</summary>
    public void Invalidate() => _lastFrame = null;

    /// <summary>
    /// Sends a frame. Only the bands that changed are transmitted (the serial link is the bottleneck).
    /// </summary>
    public void Display(Bitmap frame)
    {
        if (frame.Width != Width || frame.Height != Height)
            throw new ArgumentException($"Frame must be {Width}x{Height}");

        var pixels = ToRgb565(frame);
        var prev = _lastFrame;
        _lastFrame = pixels;

        if (prev == null)
        {
            SendRect(pixels, 0, 0, Width, Height);
            return;
        }

        const int band = 16;
        for (int by = 0; by < Height; by += band)
        {
            int h = Math.Min(band, Height - by);
            int minX = int.MaxValue, maxX = -1;
            for (int y = by; y < by + h; y++)
            {
                int row = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    if (pixels[row + x] != prev[row + x])
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }
                }
            }
            if (maxX >= 0)
                SendRect(pixels, minX, by, maxX - minX + 1, h);
        }
    }

    void SendRect(ushort[] pixels, int x, int y, int w, int h)
    {
        var data = new byte[w * h * 2];
        int i = 0;
        for (int row = y; row < y + h; row++)
        {
            int p = row * Width + x;
            for (int col = 0; col < w; col++, p++)
            {
                data[i++] = (byte)(pixels[p] & 0xFF);
                data[i++] = (byte)(pixels[p] >> 8);
            }
        }

        lock (_lock)
        {
            var hdr = Header(CmdDisplayBitmap, x, y, x + w - 1, y + h - 1);
            _port.Write(hdr, 0, hdr.Length);
            int chunk = Width * 8 * 2;
            for (int off = 0; off < data.Length; off += chunk)
                _port.Write(data, off, Math.Min(chunk, data.Length - off));
        }
    }

    static ushort[] ToRgb565(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var raw = new byte[bd.Stride * bmp.Height];
            Marshal.Copy(bd.Scan0, raw, 0, raw.Length);
            var result = new ushort[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
            {
                int s = y * bd.Stride;
                int d = y * bmp.Width;
                for (int x = 0; x < bmp.Width; x++, s += 3)
                {
                    int b = raw[s], g = raw[s + 1], r = raw[s + 2];
                    result[d + x] = (ushort)(((r << 8) & 0xF800) | ((g << 3) & 0x07E0) | (b >> 3));
                }
            }
            return result;
        }
        finally { bmp.UnlockBits(bd); }
    }

    public void Dispose()
    {
        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
    }
}
