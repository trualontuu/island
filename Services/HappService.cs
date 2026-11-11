using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace Island.Services;

public enum HappState { NotInstalled, Off, Proxy, Tun }

/// <summary>
/// Happ (FlyFrog): статус — по TUN-адаптеру «happ-tun» и процессу ядра xray;
/// управление — через его же ссылки happ://connect / happ://disconnect / happ://open.
/// </summary>
public static class HappService
{
    public static readonly string Exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"FlyFrogLLC\Happ\Happ.exe");

    public static bool Installed => File.Exists(Exe);

    public static HappState State()
    {
        if (!Installed) return HappState.NotInstalled;
        try
        {
            if (NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name.StartsWith("happ", StringComparison.OrdinalIgnoreCase) && n.OperationalStatus == OperationalStatus.Up))
                return HappState.Tun;
        }
        catch { }
        var x = Process.GetProcessesByName("xray");
        bool proxy = x.Any(p => { try { return p.MainModule?.FileName?.Contains("Happ", StringComparison.OrdinalIgnoreCase) ?? true; } catch { return true; } });
        foreach (var p in x) p.Dispose();
        return proxy ? HappState.Proxy : HappState.Off;
    }

    static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    public static void OpenApp() => Open("happ://open");

    /// <summary>Переключить и дождаться смены состояния (до 12 с).</summary>
    public static async Task<HappState> ToggleAsync(bool on)
    {
        Open(on ? "happ://connect" : "happ://disconnect");
        for (int i = 0; i < 48; i++)
        {
            await Task.Delay(250);
            var s = await Task.Run(State);
            if (on ? s is HappState.Tun or HappState.Proxy : s == HappState.Off) return s;
        }
        return await Task.Run(State);
    }
    /// <summary>Название выбранного сервера — Happ хранит его открыто в реестре (сам список серверов зашифрован).</summary>
    public static string? CurrentServer()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Happ\OrganizationDefaults\Preferences");
            var n = k?.GetValue("lastServerName") as string;
            return string.IsNullOrWhiteSpace(n) ? null : n.Trim();
        }
        catch { return null; }
    }
}
