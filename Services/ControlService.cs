using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Island.Core;
using NAudio.CoreAudioApi;

namespace Island.Services;

public sealed record VpnConn(string Name, bool Connected);

public sealed class ControlService : IDisposable
{
    MMDevice? _audio;
    MMDeviceEnumerator? _audioEnum;

    public event Action<float, bool>? VolumeChanged;

    // ---------------- VPN (встроенные VPN Windows) ----------------
    public static async Task<List<VpnConn>> VpnListAsync()
    {
        var names = new List<string>();
        foreach (var pbk in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
        })
        {
            try
            {
                if (!File.Exists(pbk)) continue;
                foreach (var line in await File.ReadAllLinesAsync(pbk))
                    if (line.StartsWith('[') && line.EndsWith(']')) names.Add(line[1..^1]);
            }
            catch { }
        }
        var connected = await RunAsync("rasdial", "");
        return names.Distinct().Select(n => new VpnConn(n, connected.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static async Task<string?> ToggleVpnAsync(VpnConn v)
    {
        var outp = await RunAsync("rasdial", v.Connected ? $"\"{v.Name}\" /disconnect" : $"\"{v.Name}\"");
        if (v.Connected) return null;
        return (await VpnListAsync()).Any(x => x.Name == v.Name && x.Connected) ? null : "Не подключилось — возможно, нужны логин/пароль (открой Параметры → VPN)";
    }

    static async Task<string> RunAsync(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            var s = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return s;
        }
        catch { return ""; }
    }

    // ---------------- громкость ----------------
    MMDevice? Audio()
    {
        if (_audio != null) return _audio;
        try
        {
            _audioEnum ??= new MMDeviceEnumerator();
            _audio = _audioEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _audio.AudioEndpointVolume.OnVolumeNotification += d => VolumeChanged?.Invoke(d.MasterVolume, d.Muted);
        }
        catch { _audio = null; }
        return _audio;
    }

    public (float vol, bool mute) GetVolume()
    {
        var a = Audio();
        return a == null ? (0, false) : (a.AudioEndpointVolume.MasterVolumeLevelScalar, a.AudioEndpointVolume.Mute);
    }
    public void SetVolume(float v) { var a = Audio(); if (a != null) { a.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1); if (a.AudioEndpointVolume.Mute && v > 0) a.AudioEndpointVolume.Mute = false; } }
    public void ToggleMute() { var a = Audio(); if (a != null) a.AudioEndpointVolume.Mute = !a.AudioEndpointVolume.Mute; }
    /// <summary>Сбросить устройство (например, после смены выхода звука).</summary>
    public void ResetAudio() { _audio?.Dispose(); _audio = null; }

    // ---------------- питание ----------------
    public static void Lock() => Native.LockWorkStation();
    public static void Sleep() => Native.SetSuspendState(false, false, false);
    public static void Restart() => Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
    public static void Shutdown() => Process.Start(new ProcessStartInfo("shutdown", "/s /hybrid /t 0") { CreateNoWindow = true, UseShellExecute = false });

    public static void OpenUri(string uri) { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { } }

    public void Dispose() { _audio?.Dispose(); _audioEnum?.Dispose(); }
}
