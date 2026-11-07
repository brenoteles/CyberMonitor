using System.Management;
using LibreHardwareMonitor.Hardware;

namespace CyberMonitor;

public sealed class Snapshot
{
    public string CpuName = "CPU";
    public float? CpuLoad, CpuTemp, CpuClock, CpuPower;
    public int CpuCores;

    public string GpuName = "GPU";
    public float? GpuLoad, GpuTemp, GpuClock, GpuPower, GpuFan;
    public float? VramUsedMb, VramTotalMb;

    public float? RamUsedGb, RamTotalGb, RamLoad;

    public float? NetUpBps, NetDownBps;
    public string DiskLabel = "C:";
    public float? DiskUsedPct;
    public float? DiskFreeGb;

    public TimeSpan Uptime;

    /// <summary>True when CPU temperature/power are unreadable (no PawnIO driver).</summary>
    public bool CpuDriverMissing;
}

/// <summary>Auto-detects CPU, GPU, RAM, network and disk.</summary>
public sealed class HardwareReader : IDisposable
{
    readonly Computer _computer;
    readonly UpdateVisitor _visitor = new();
    readonly ManagementObjectSearcher _perfSearcher = new(
        "SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
    readonly float? _baseClockMhz = ReadBaseClock();

    public HardwareReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsNetworkEnabled = true,
            IsMotherboardEnabled = false,
            IsStorageEnabled = false,
        };
        _computer.Open();
    }

    public Snapshot Read()
    {
        _computer.Accept(_visitor);
        var s = new Snapshot();
        // Without a low-level driver some sensors read zero: treat them as unavailable
        static float? Valid(float? v) => v is > 0 ? v : null;

        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu != null)
        {
            s.CpuName = CleanName(cpu.Name);
            var sensors = cpu.Sensors;
            s.CpuLoad = Find(sensors, SensorType.Load, "CPU Total");
            s.CpuTemp = Find(sensors, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core Average", "CCD1 (Tdie)")
                        ?? sensors.Where(x => x.SensorType == SensorType.Temperature).Select(x => x.Value).FirstOrDefault(v => v > 0);
            s.CpuTemp = Valid(s.CpuTemp);
            s.CpuPower = Valid(Find(sensors, SensorType.Power, "Package", "CPU Package"));
            var clocks = sensors.Where(x => x.SensorType == SensorType.Clock && x.Name.StartsWith("Core #") && x.Value > 0)
                                .Select(x => x.Value!.Value).ToList();
            s.CpuClock = clocks.Count > 0 ? clocks.Max() : WindowsEffectiveClock();
            s.CpuCores = sensors.Count(x => x.SensorType == SensorType.Load && x.Name.StartsWith("CPU Core #"));
            s.CpuDriverMissing = s.CpuTemp == null;
        }

        var gpu = PickGpu();
        if (gpu != null)
        {
            s.GpuName = CleanName(gpu.Name);
            var sensors = gpu.Sensors;
            s.GpuLoad = Find(sensors, SensorType.Load, "GPU Core", "D3D 3D");
            s.GpuTemp = Find(sensors, SensorType.Temperature, "GPU Core", "GPU Hot Spot");
            s.GpuClock = Find(sensors, SensorType.Clock, "GPU Core");
            s.GpuPower = Find(sensors, SensorType.Power, "GPU Package", "GPU Core", "GPU Power")
                         ?? sensors.Where(x => x.SensorType == SensorType.Power).Select(x => x.Value).FirstOrDefault(v => v > 0);
            s.GpuFan = sensors.Where(x => x.SensorType == SensorType.Fan).Select(x => x.Value).FirstOrDefault();
            s.VramUsedMb = Find(sensors, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            s.VramTotalMb = Find(sensors, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
            if (s.VramTotalMb == null && s.VramUsedMb != null)
            {
                var free = Find(sensors, SensorType.SmallData, "GPU Memory Free", "D3D Dedicated Memory Free");
                if (free != null) s.VramTotalMb = s.VramUsedMb + free;
            }
        }

        var mem = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual"));
        if (mem != null)
        {
            s.RamLoad = Find(mem.Sensors, SensorType.Load, "Memory");
            s.RamUsedGb = Find(mem.Sensors, SensorType.Data, "Memory Used");
            var avail = Find(mem.Sensors, SensorType.Data, "Memory Available");
            if (s.RamUsedGb != null && avail != null) s.RamTotalGb = s.RamUsedGb + avail;
        }

        // Network: the adapter with the most traffic right now
        var nics = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Network)
            .Select(h => (up: Find(h.Sensors, SensorType.Throughput, "Upload Speed") ?? 0,
                          down: Find(h.Sensors, SensorType.Throughput, "Download Speed") ?? 0))
            .ToList();
        if (nics.Count > 0)
        {
            var best = nics.OrderByDescending(n => n.up + n.down).First();
            s.NetUpBps = best.up;
            s.NetDownBps = best.down;
        }

        try
        {
            var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            if (sys.IsReady)
            {
                s.DiskLabel = sys.Name.TrimEnd('\\');
                s.DiskUsedPct = 100f * (sys.TotalSize - sys.TotalFreeSpace) / sys.TotalSize;
                s.DiskFreeGb = sys.TotalFreeSpace / 1073741824f;
            }
        }
        catch (IOException) { }

        s.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return s;
    }

    /// <summary>
    /// Driverless fallback: Windows' "% Processor Performance" counter × base clock
    /// gives the average effective clock (the same figure Task Manager shows as "Speed").
    /// </summary>
    float? WindowsEffectiveClock()
    {
        if (_baseClockMhz == null) return null;
        try
        {
            foreach (var o in _perfSearcher.Get())
                using (o)
                    return _baseClockMhz * Convert.ToSingle(o["PercentProcessorPerformance"]) / 100f;
        }
        catch (ManagementException) { }
        return null;
    }

    static float? ReadBaseClock()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (var o in searcher.Get())
                using (o)
                    return Convert.ToSingle(o["MaxClockSpeed"]);
        }
        catch (ManagementException) { }
        return null;
    }

    /// <summary>Prefers the discrete GPU (largest VRAM) when both iGPU and dGPU exist.</summary>
    IHardware? PickGpu()
    {
        var gpus = _computer.Hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList();
        if (gpus.Count <= 1) return gpus.FirstOrDefault();
        return gpus.OrderByDescending(g => g.HardwareType == HardwareType.GpuIntel ? 0 : 1)
                   .ThenByDescending(g => Find(g.Sensors, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total") ?? 0)
                   .First();
    }

    static float? Find(IEnumerable<ISensor> sensors, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            var v = sensors.FirstOrDefault(x => x.SensorType == type && x.Name == n)?.Value;
            if (v != null && !float.IsNaN(v.Value)) return v;
        }
        return null;
    }

    static string CleanName(string n) => n
        .Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "")
        .Replace(" CPU", "").Replace("Processor", "")
        .Replace("with Radeon Graphics", "").Replace("w/ Radeon Graphics", "")
        .Replace("  ", " ").Trim();

    public void Dispose()
    {
        _perfSearcher.Dispose();
        _computer.Close();
    }

    sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
