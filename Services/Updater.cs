using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Island.Services;

/// <summary>
/// Автообновление из публичного репозитория с готовыми сборками (GitHub Releases).
/// Скачивает Island.zip, сверяет SHA-256 из описания релиза, распаковывает рядом во временную папку.
/// Применяет так: работающий exe можно ПЕРЕИМЕНОВАТЬ (но не перезаписать) — переименовываем в Island.old.exe,
/// кладём новый на его место и запускаем его с --after-update &lt;pid&gt;; новый ждёт выхода старого.
/// </summary>
public sealed class Updater
{
    public const string Repo = "trualontuu/island-releases";
    const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";

    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Island-Updater/1.0");
        return h;
    }

    public static Version Current { get; } = Norm(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Island", "update");

    public Version? Ready { get; private set; }
    string? _readyExe;
    bool _busy;

    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    public static string Show(Version v) => v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

    /// <returns>новая версия скачана и готова (или уже была готова)</returns>
    public async Task<bool> CheckAsync(bool force = false)
    {
        if (Ready != null) return true;
        if (_busy) return false;
        _busy = true;
        try
        {
            string json = await Http.GetStringAsync(Api);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return false;
            v = Norm(v);
            if (v <= Current && !force) return false;

            string? url = null; string? hash = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != "Island.zip") continue;
                url = a.GetProperty("browser_download_url").GetString();
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:")) hash = ds[7..];
            }
            var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var m = Regex.Match(body, @"sha256:\s*([0-9a-fA-F]{64})");
            if (m.Success) hash = m.Groups[1].Value; // описание релиза пишем мы сами — ему и верим в первую очередь
            if (url == null || hash == null) return false; // без контрольной суммы ничего не ставим

            Directory.CreateDirectory(Dir);
            var exe = Path.Combine(Dir, $"Island-{Show(v)}.exe");
            if (!File.Exists(exe))
            {
                var zip = Path.Combine(Dir, $"Island-{Show(v)}.zip.part");
                using (var s = await Http.GetStreamAsync(url))
                using (var f = File.Create(zip))
                    await s.CopyToAsync(f);
                string got;
                using (var f = File.OpenRead(zip)) got = Convert.ToHexString(await SHA256.HashDataAsync(f));
                if (!got.Equals(hash, StringComparison.OrdinalIgnoreCase)) { File.Delete(zip); return false; }
                using (var z = ZipFile.OpenRead(zip))
                {
                    var e = z.GetEntry("Island.exe");
                    if (e == null) { return false; }
                    e.ExtractToFile(exe + ".part", true);
                }
                File.Move(exe + ".part", exe, true);
                File.Delete(zip);
            }
            // старые скачанные версии больше не нужны
            foreach (var f in Directory.GetFiles(Dir, "Island-*"))
                if (!string.Equals(f, exe, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }
            _readyExe = exe;
            Ready = v;
            return true;
        }
        catch { return false; }
        finally { _busy = false; }
    }

    /// <summary>Подменить exe и запустить новую версию. true — можно завершаться.</summary>
    public bool Apply(bool startHidden, string? extraArgs = null)
    {
        if (_readyExe == null || !File.Exists(_readyExe)) return false;
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        var old = Path.Combine(Path.GetDirectoryName(exe)!, "Island.old.exe");
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Copy(_readyExe, exe); }
            catch { File.Move(old, exe); throw; } // откат — остаёмся на старой версии
            var args = $"--after-update {Environment.ProcessId}" + (startHidden ? " --hide" : "") + (extraArgs != null ? " " + extraArgs : "");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            try { File.Delete(_readyExe); } catch { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Запуск после обновления: дождаться выхода старой копии и убрать Island.old.exe.</summary>
    public static void AfterUpdate(string[] args)
    {
        int i = Array.IndexOf(args, "--after-update");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int pid))
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(15000); } catch { }
        }
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        var old = Path.Combine(Path.GetDirectoryName(exe)!, "Island.old.exe");
        for (int k = 0; k < 10 && File.Exists(old); k++)
        {
            try { File.Delete(old); } catch { System.Threading.Thread.Sleep(300); }
        }
    }
}
