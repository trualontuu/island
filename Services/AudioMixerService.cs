using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Island.Services;

/// <summary>Одно приложение в микшере (у Chrome и т.п. бывает много сессий — объединяем по процессу).</summary>
public sealed class AppAudio
{
    public required string Key;          // pid или "system"
    public required string Name;
    public string? ExePath;
    public bool IsSystem;
    public readonly List<AudioSessionControl> Sessions = new();

    public float Volume
    {
        get { try { return Sessions.Count > 0 ? Sessions[0].SimpleAudioVolume.Volume : 0; } catch { return 0; } }
        set { foreach (var s in Sessions) try { s.SimpleAudioVolume.Volume = Math.Clamp(value, 0, 1); } catch { } }
    }
    public bool Mute
    {
        get { try { return Sessions.Count > 0 && Sessions[0].SimpleAudioVolume.Mute; } catch { return false; } }
        set { foreach (var s in Sessions) try { s.SimpleAudioVolume.Mute = value; } catch { } }
    }
    /// <summary>Пиковый уровень сейчас (0..1) — дешёвый вызов, можно на каждом кадре.</summary>
    public float Peak
    {
        get
        {
            float p = 0;
            foreach (var s in Sessions) try { p = Math.Max(p, s.AudioMeterInformation.MasterPeakValue); } catch { }
            return p;
        }
    }

    /// <summary>Уровень для индикатора без «зеркальных» сессий (см. <see cref="AudioMixerService.SamplePeaks"/>).</summary>
    public float ShownPeak;
}

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Микшер на Core Audio (WASAPI) через NAudio. Списки обновляются по событиям Windows
/// (новая сессия, смена устройства по умолчанию), а уровни читаются только пока микшер на экране.
/// </summary>
public sealed class AudioMixerService : IDisposable
{
    readonly Dispatcher _ui;
    readonly MMDeviceEnumerator _en = new();
    MMDevice? _dev;
    object? _notify; // держим ссылку, чтобы клиент уведомлений жил
    AudioSessionManager? _mgr;
    readonly int _ownPid = Environment.ProcessId;

    public List<AppAudio> Apps { get; } = new();
    public string DeviceName { get; private set; } = "";
    public event Action? AppsChanged;
    public event Action? MasterChanged;
    public event Action? DeviceChanged;

    public AudioMixerService(Dispatcher ui)
    {
        _ui = ui;
        try
        {
            var nc = _en.CreateNotificationClient();
            _notify = nc;
            nc.DefaultDeviceChanged += (_, e) => { if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) _ui.BeginInvoke(() => { Bind(); DeviceChanged?.Invoke(); MasterChanged?.Invoke(); }); };
        }
        catch { }
        Bind();
    }

    void Bind()
    {
        try
        {
            if (_dev != null) _dev.AudioEndpointVolume.OnVolumeNotification -= OnMaster;
            _dev?.Dispose();
            _dev = _en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            DeviceName = _dev.FriendlyName;
            _dev.AudioEndpointVolume.OnVolumeNotification += OnMaster;
            _mgr = _dev.AudioSessionManager;
            _mgr.OnSessionCreated += (a, b) => OnSessionCreated(a, b!);
        }
        catch { _dev = null; _mgr = null; DeviceName = "Нет устройства вывода"; }
        Refresh();
    }

    void OnMaster(AudioVolumeNotificationData d) => _ui.BeginInvoke(() => MasterChanged?.Invoke());
    void OnSessionCreated(object? sender, object s) => _ui.BeginInvoke(Refresh, DispatcherPriority.Background);

    /// <summary>Пересобрать список приложений.</summary>
    public void Refresh()
    {
        Apps.Clear();
        if (_mgr == null) { AppsChanged?.Invoke(); return; }
        try
        {
            _mgr.RefreshSessions();
            var sessions = _mgr.Sessions;
            var map = new Dictionary<string, AppAudio>();
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                uint pid = s.GetProcessID;
                if (pid == _ownPid) continue;
                bool sys = s.IsSystemSoundsSession;
                string key = sys ? "system" : pid.ToString();
                if (!map.TryGetValue(key, out var app))
                {
                    string? path = sys ? null : ExePath(pid);
                    app = new AppAudio { Key = key, Name = sys ? "Системные звуки" : AppName(pid, path, s.DisplayName), ExePath = path, IsSystem = sys };
                    map[key] = app;
                    // о закрытии приложения узнаём событием, а не опросом
                    try { s.RegisterEventClient(new SessionEvents(this)); } catch { }
                }
                else try { s.RegisterEventClient(new SessionEvents(this)); } catch { }
                app.Sessions.Add(s);
            }
            // сначала то, что играет сейчас; системные звуки — в конце
            Apps.AddRange(map.Values
                .OrderBy(a => a.IsSystem)
                .ThenByDescending(a => a.Sessions.Any(x => SafeState(x) == AudioSessionState.AudioSessionStateActive))
                .ThenBy(a => a.Name));
        }
        catch { }
        AppsChanged?.Invoke();
    }

    static AudioSessionState SafeState(AudioSessionControl s) { try { return s.State; } catch { return AudioSessionState.AudioSessionStateExpired; } }

    sealed class SessionEvents(AudioMixerService owner) : IAudioSessionEventsHandler
    {
        bool _queued;
        void Kick()
        {
            if (_queued) return;
            _queued = true;
            owner._ui.BeginInvoke(() => { _queued = false; owner.Refresh(); }, DispatcherPriority.Background);
        }
        public void OnVolumeChanged(float volume, bool isMuted) => owner._ui.BeginInvoke(() => owner.AppVolumeChanged?.Invoke());
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
        public void OnStateChanged(AudioSessionState state) { if (state == AudioSessionState.AudioSessionStateExpired) Kick(); }
        public void OnSessionDisconnected(AudioSessionDisconnectReason reason) => Kick();
    }

    public event Action? AppVolumeChanged;

    // ---------------- общая громкость ----------------
    public float MasterVolume
    {
        get { try { return _dev?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; } catch { return 0; } }
        set { try { if (_dev != null) { _dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 1); if (value > 0 && _dev.AudioEndpointVolume.Mute) _dev.AudioEndpointVolume.Mute = false; } } catch { } }
    }
    public bool MasterMute
    {
        get { try { return _dev?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
        set { try { if (_dev != null) _dev.AudioEndpointVolume.Mute = value; } catch { } }
    }
    // ---------------- уровни ----------------
    // Голосовые программы (TeamSpeak, Discord…) держат на устройстве вывода петлевую сессию для эхоподавления.
    // Windows отдаёт по ней пик ВСЕГО микса — и кажется, будто звук идёт из них. Такие сессии вычисляем:
    // их уровень побитово равен общему, даже когда играют несколько приложений и общий уровень отличается от каждого.
    readonly Dictionary<AudioSessionControl, int> _mirror = new(ReferenceEqualityComparer.Instance);
    readonly List<(AppAudio App, AudioSessionControl S, float P)> _buf = new();
    static readonly string[] VoiceApps = { "ts3client", "teamspeak", "discord", "skype", "zoom", "telegram", "whatsapp", "mumble", "viber", "slack", "teams", "vesktop" };

    /// <summary>Один замер на кадр: обновляет <see cref="AppAudio.ShownPeak"/> у всех приложений, возвращает общий пик.</summary>
    public float SamplePeaks()
    {
        float m = MasterPeak;
        _buf.Clear();
        foreach (var a in Apps)
            foreach (var se in a.Sessions)
            {
                float p = 0; try { p = se.AudioMeterInformation.MasterPeakValue; } catch { }
                _buf.Add((a, se, p));
            }
        const float On = 0.002f, Eps = 1e-5f;
        if (m > On)
            foreach (var x in _buf)
            {
                bool same = Math.Abs(x.P - m) < Eps;
                _mirror.TryGetValue(x.S, out int sc);
                if (!same && x.P > On) _mirror[x.S] = -1000; // отличается от микса → настоящий источник
                else if (same && sc >= 0)
                {
                    bool othersDiffer = false;
                    foreach (var o in _buf) if (!ReferenceEquals(o.S, x.S) && o.P > On && Math.Abs(o.P - m) >= Eps) { othersDiffer = true; break; }
                    if (othersDiffer) _mirror[x.S] = Math.Min(sc + 1, 1000);
                }
            }

        foreach (var a in Apps) a.ShownPeak = 0;
        foreach (var x in _buf)
        {
            if (x.P <= 0) continue;
            _mirror.TryGetValue(x.S, out int sc);
            bool hide = sc >= 3;
            if (!hide && m > On && Math.Abs(x.P - m) < Eps)
            {
                // ничья: несколько сессий из разных приложений показывают ровно общий уровень
                bool tie = false, otherKnownReal = false;
                foreach (var o in _buf)
                    if (!ReferenceEquals(o.App, x.App) && Math.Abs(o.P - m) < Eps)
                    { tie = true; if (_mirror.TryGetValue(o.S, out int osc) && osc < 0) otherKnownReal = true; }
                if (tie && sc >= 0 && (otherKnownReal || IsVoiceApp(x.App))) hide = true;
            }
            if (!hide && x.P > x.App.ShownPeak) x.App.ShownPeak = x.P;
        }
        return m;
    }

    static bool IsVoiceApp(AppAudio a)
    {
        var n = (Path.GetFileNameWithoutExtension(a.ExePath ?? "") + " " + a.Name).ToLowerInvariant();
        foreach (var v in VoiceApps) if (n.Contains(v)) return true;
        return false;
    }

    public float MasterPeak { get { try { return _dev?.AudioMeterInformation.MasterPeakValue ?? 0; } catch { return 0; } } }

    // ---------------- устройства вывода ----------------
    public List<AudioDevice> Devices()
    {
        var list = new List<AudioDevice>();
        try
        {
            string def = _dev?.ID ?? "";
            foreach (var d in _en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == def));
                d.Dispose();
            }
        }
        catch { }
        return list;
    }

    /// <summary>Сделать устройство основным (как в меню громкости Windows) — через системный IPolicyConfig.</summary>
    public void SetDefault(string id)
    {
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            pc.SetDefaultEndpoint(id, 0); // eConsole
            pc.SetDefaultEndpoint(id, 1); // eMultimedia
            pc.SetDefaultEndpoint(id, 2); // eCommunications
            Marshal.ReleaseComObject(pc);
        }
        catch { }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr f);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr f);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr f, IntPtr m);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr m);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr m);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fx, IntPtr k, IntPtr v);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fx, IntPtr k, IntPtr v);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }

    // ---------------- имя и путь процесса ----------------
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

    static string? ExePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static string AppName(uint pid, string? path, string display)
    {
        if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@')) return display;
        if (path != null)
        {
            try
            {
                var d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
            catch { }
            return Path.GetFileNameWithoutExtension(path);
        }
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "Приложение"; }
    }

    public void Dispose()
    {
        _dev?.Dispose();
        _en.Dispose();
    }
}
