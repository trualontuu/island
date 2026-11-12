using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Island.Services;

/// <summary>
/// Микрофон: кто им сейчас пользуется и выключен ли он.
/// «Кто пользуется» — из реестра Windows (CapabilityAccessManager): там же берёт данные значок микрофона в трее.
/// Ждём изменений через RegNotifyChangeKeyValue в отдельном потоке — без опроса, ~0% CPU.
/// </summary>
public sealed class MicService : IDisposable
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    [DllImport("advapi32.dll")] static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool subtree, uint filter, IntPtr hEvent, bool async);
    const uint REG_NOTIFY_CHANGE_NAME = 1, REG_NOTIFY_CHANGE_LAST_SET = 4;

    readonly Dispatcher _ui;
    readonly MMDeviceEnumerator _en = new();
    MMDevice? _dev;
    object? _notify;
    readonly Thread _thread;
    readonly ManualResetEvent _stop = new(false);
    readonly Dictionary<string, string> _names = new();
    static readonly string OwnExe = (Environment.ProcessPath ?? "").Replace('\\', '#');

    public IReadOnlyList<string> Users { get; private set; } = Array.Empty<string>();
    public bool Muted { get; private set; }
    public bool HasDevice => _dev != null;
    public bool InUse => Users.Count > 0;
    public event Action? Changed;

    public MicService(Dispatcher ui)
    {
        _ui = ui;
        try
        {
            var nc = _en.CreateNotificationClient();
            _notify = nc;
            nc.DefaultDeviceChanged += (_, e) => { if (e.Flow == DataFlow.Capture && e.Role == Role.Communications) _ui.BeginInvoke(() => { Bind(); Changed?.Invoke(); }); };
        }
        catch { }
        Bind();
        Users = ReadUsers();
        _thread = new Thread(Watch) { IsBackground = true, Name = "MicWatch", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    void Bind()
    {
        try
        {
            if (_dev != null) _dev.AudioEndpointVolume.OnVolumeNotification -= OnVol;
            _dev?.Dispose();
            // «Связь» — то устройство, которое берут Discord/TeamSpeak/браузер по умолчанию
            _dev = _en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _dev.AudioEndpointVolume.OnVolumeNotification += OnVol;
            Muted = _dev.AudioEndpointVolume.Mute;
        }
        catch { _dev = null; Muted = false; }
    }

    void OnVol(AudioVolumeNotificationData d) => _ui.BeginInvoke(() => { if (Muted != d.Muted) { Muted = d.Muted; Changed?.Invoke(); } });

    /// <summary>Пиковый уровень микрофона 0–1 (для подсказки «говоришь, а микрофон выключен»).</summary>
    public float Peak { get { try { return _dev?.AudioMeterInformation.MasterPeakValue ?? 0; } catch { return 0; } } }

    /// <summary>Выключить/включить все микрофоны сразу (а не только основной) — «везде».</summary>
    public bool ToggleMuteAll()
    {
        bool to = !Muted;
        try
        {
            foreach (var d in _en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try { d.AudioEndpointVolume.Mute = to; } catch { }
                d.Dispose();
            }
        }
        catch { }
        Muted = to;
        Changed?.Invoke();
        return to;
    }

    void Watch()
    {
        using var ev = new AutoResetEvent(false);
        while (true)
        {
            RegistryKey? k = null;
            try
            {
                k = Registry.CurrentUser.OpenSubKey(KeyPath);
                if (k == null) { if (_stop.WaitOne(30000)) return; continue; }
                while (true)
                {
                    if (RegNotifyChangeKeyValue(k.Handle.DangerousGetHandle(), true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, ev.SafeWaitHandle.DangerousGetHandle(), true) != 0)
                        throw new IOException("RegNotifyChangeKeyValue");
                    if (WaitHandle.WaitAny(new WaitHandle[] { _stop, ev }) == 0) return;
                    Thread.Sleep(120); // программа пишет Start и Stop по очереди — дождёмся обоих
                    var u = ReadUsers();
                    if (!u.SequenceEqual(Users)) _ui.BeginInvoke(() => { Users = u; Changed?.Invoke(); });
                }
            }
            catch { if (_stop.WaitOne(5000)) return; }
            finally { k?.Dispose(); }
        }
    }

    List<string> ReadUsers()
    {
        var res = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (root == null) return res;
            foreach (var name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var np = root.OpenSubKey(name);
                    if (np == null) continue;
                    foreach (var exe in np.GetSubKeyNames())
                    {
                        if (string.Equals(exe, OwnExe, StringComparison.OrdinalIgnoreCase)) continue;
                        using var sk = np.OpenSubKey(exe);
                        if (Active(sk)) res.Add(Pretty(exe, packaged: false));
                    }
                }
                else
                {
                    using var sk = root.OpenSubKey(name);
                    if (Active(sk)) res.Add(Pretty(name, packaged: true));
                }
            }
        }
        catch { }
        return res.Distinct().OrderBy(x => x).ToList();
    }

    static bool Active(RegistryKey? k)
    {
        if (k == null) return false;
        return k.GetValue("LastUsedTimeStop") is long stop && stop == 0 && k.GetValue("LastUsedTimeStart") is long start && start > 0;
    }

    string Pretty(string id, bool packaged)
    {
        lock (_names) if (_names.TryGetValue(id, out var n)) return n;
        string name;
        if (packaged)
        {
            // TelegramMessengerLLP.TelegramDesktop_t4vj0pshhgkwm → TelegramDesktop
            var core = id.Split('_')[0];
            name = core.Contains('.') ? core[(core.LastIndexOf('.') + 1)..] : core;
        }
        else
        {
            var path = id.Replace('#', '\\');
            name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var fi = FileVersionInfo.GetVersionInfo(path);
                var d = (fi.FileDescription ?? "").Trim();
                if (d.Length is > 1 and < 40) name = d;
            }
            catch { }
        }
        lock (_names) _names[id] = name;
        return name;
    }

    public void Dispose()
    {
        _stop.Set();
        try { if (_dev != null) _dev.AudioEndpointVolume.OnVolumeNotification -= OnVol; _dev?.Dispose(); } catch { }
    }
}
