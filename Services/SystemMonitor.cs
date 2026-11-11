using System;
using System.Collections.Generic;
using System.Linq;

using System.Runtime.InteropServices;

namespace Island.Services;

/// <summary>
/// Нагрузка на ПК раз в секунду: процессор (GetSystemTimes), память (GlobalMemoryStatusEx),
/// видеокарта и диск (счётчики PDH — как в диспетчере задач), сеть (байты по сетевым картам).
/// Опрашивается только пока открыта вкладка «Система».
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    public const int History = 60;
    public readonly Queue<double> Cpu = new(), Ram = new(), Gpu = new(), Disk = new(), NetDown = new(), NetUp = new();
    public double CpuNow, RamNow, GpuNow = -1, DiskNow = -1, DownNow, UpNow;
    public double RamUsedGb, RamTotalGb;

    // ---------- процессор ----------
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    long _idle, _kernel, _user;

    // ---------- память ----------
    [StructLayout(LayoutKind.Sequential)]
    struct MEMSTATUS { public uint len, load; public ulong total, avail, pageTotal, pageAvail, virtTotal, virtAvail, extAvail; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTATUS m);

    // ---------- PDH ----------
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr buf);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr c, uint fmt, out uint type, out PDH_VALUE v);
    [StructLayout(LayoutKind.Explicit)] struct PDH_VALUE { [FieldOffset(0)] public uint status; [FieldOffset(8)] public double value; }
    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    IntPtr _q, _gpu, _disk;
    bool _pdhTried;

    IntPtr _rxC, _txC;
    // VPN-туннели и виртуальные адаптеры повторяют трафик настоящей сетевой карты — не считаем их
    static readonly string[] SkipNet = { "tun", "tap", "wintun", "wireguard", "vpn", "virtual", "hyper-v", "loopback", "isatap", "teredo", "miniport", "happ", "xray", "bluetooth" };

    volatile bool _pdhReady;

    /// <summary>Открыть счётчики PDH в фоне: это ~200 мс, в потоке интерфейса было бы заметное подвисание.</summary>
    public void BeginInit()
    {
        if (_pdhTried) return;
        _pdhTried = true;
        System.Threading.Tasks.Task.Run(() => { InitPdh(); _pdhReady = true; });
    }

    void InitPdh()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _q) != 0) { _q = IntPtr.Zero; return; }
            if (PdhAddEnglishCounter(_q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu) != 0) _gpu = IntPtr.Zero;
            if (PdhAddEnglishCounter(_q, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _disk) != 0) _disk = IntPtr.Zero;
            if (PdhAddEnglishCounter(_q, @"\Network Interface(*)\Bytes Received/sec", IntPtr.Zero, out _rxC) != 0) _rxC = IntPtr.Zero;
            if (PdhAddEnglishCounter(_q, @"\Network Interface(*)\Bytes Sent/sec", IntPtr.Zero, out _txC) != 0) _txC = IntPtr.Zero;
            PdhCollectQueryData(_q);
        }
        catch { _q = IntPtr.Zero; }
    }

    /// <summary>Снять показания (раз в секунду).</summary>
    public double TPdh, TNet;
    public void Sample()
    {
        BeginInit();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // процессор
        if (GetSystemTimes(out var i, out var k, out var u))
        {
            long di = i - _idle, dk = k - _kernel, du = u - _user;
            if (_kernel != 0 && dk + du > 0) CpuNow = Math.Clamp(100.0 * (dk + du - di) / (dk + du), 0, 100);
            _idle = i; _kernel = k; _user = u;
        }

        // память
        var m = new MEMSTATUS { len = (uint)Marshal.SizeOf<MEMSTATUS>() };
        if (GlobalMemoryStatusEx(ref m))
        {
            RamTotalGb = m.total / 1073741824.0;
            RamUsedGb = (m.total - m.avail) / 1073741824.0;
            RamNow = m.total > 0 ? 100.0 * (m.total - m.avail) / m.total : 0;
        }

        // видеокарта и диск
        if (_pdhReady && _q != IntPtr.Zero && PdhCollectQueryData(_q) == 0)
        {
            if (_gpu != IntPtr.Zero) GpuNow = ReadGpu();
            if (_disk != IntPtr.Zero && PdhGetFormattedCounterValue(_disk, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0 && v.status <= 1)
                DiskNow = Math.Clamp(100 - v.value, 0, 100);
        }

        TPdh = sw.Elapsed.TotalMilliseconds; sw.Restart();
        // сеть: счётчики PDH по сетевым картам (NetworkInterface из .NET стоил ~20 мс на замер)
        if (_pdhReady && _q != IntPtr.Zero)
        {
            if (_rxC != IntPtr.Zero) DownNow = SumNet(_rxC);
            if (_txC != IntPtr.Zero) UpNow = SumNet(_txC);
        }

        TNet = sw.Elapsed.TotalMilliseconds;
        Push(Cpu, CpuNow); Push(Ram, RamNow); Push(Gpu, Math.Max(0, GpuNow)); Push(Disk, Math.Max(0, DiskNow));
        Push(NetDown, DownNow); Push(NetUp, UpNow);
    }

    static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > History) q.Dequeue();
    }

    static double SumNet(IntPtr c)
    {
        double sum = 0;
        ForEachItem(c, (name, v) =>
        {
            var n = name.ToLowerInvariant();
            foreach (var x in SkipNet) if (n.Contains(x)) return;
            sum += v;
        });
        return sum;
    }

    /// <summary>Обход значений счётчика с подстановкой «*».</summary>
    static void ForEachItem(IntPtr c, Action<string, double> f)
    {
        uint size = 0;
        PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (size == 0) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buf) != 0) return;
            int stride = IntPtr.Size + 16; // PDH_FMT_COUNTERVALUE_ITEM: LPWSTR + PDH_FMT_COUNTERVALUE(16)
            for (int n = 0; n < count; n++)
            {
                var item = buf + n * stride;
                var v = Marshal.PtrToStructure<PDH_VALUE>(item + IntPtr.Size);
                if (v.status > 1 || double.IsNaN(v.value)) continue;
                f(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "", v.value);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Как в диспетчере задач: загрузка каждого движка видеокарты = сумма по процессам, итог — самый загруженный движок.</summary>
    double ReadGpu()
    {
        uint size = 0;
        int rc = PdhGetFormattedCounterArray(_gpu, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (size == 0) return 0;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_gpu, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buf) != 0) return 0;
            var engines = new Dictionary<string, double>();
            int stride = IntPtr.Size + 16; // PDH_FMT_COUNTERVALUE_ITEM: LPWSTR + PDH_FMT_COUNTERVALUE(16)
            for (int n = 0; n < count; n++)
            {
                var item = buf + n * stride;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                double val = Marshal.PtrToStructure<PDH_VALUE>(item + IntPtr.Size).value;
                if (double.IsNaN(val) || val <= 0) continue;
                // «pid_123_luid_0x.._phys_0_eng_3_engtype_3D» → ключ движка «luid_.._phys_0_eng_3»
                int a = name.IndexOf("luid_", StringComparison.Ordinal), b = name.IndexOf("_engtype", StringComparison.Ordinal);
                var key = a >= 0 && b > a ? name[a..b] : name;
                engines[key] = engines.GetValueOrDefault(key) + val;
            }
            return engines.Count == 0 ? 0 : Math.Clamp(engines.Values.Max(), 0, 100);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) { PdhCloseQuery(_q); _q = IntPtr.Zero; }
    }
}
