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
        // общего лимита нет: медленная связь с GitHub качает долго, но докачивает; зависание ловим отдельно (Stall)
        var h = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Island-Updater/1.0");
        return h;
    }

    public static Version Current { get; } = Norm(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Island", "update");

    public Version? Ready { get; private set; }
    /// <summary>Что лежит на сервере (после удачной проверки).</summary>
    public Version? Latest { get; private set; }
    /// <summary>Почему не вышло в последний раз; null — проверка прошла.</summary>
    public string? LastError { get; private set; }
    /// <summary>Скачано 0…1, пока идёт загрузка; -1 — не качаем.</summary>
    public double Progress { get; private set; } = -1;
    static readonly TimeSpan Stall = TimeSpan.FromSeconds(45);
    string? _readyExe;
    bool _busy;
    public bool Busy => _busy;

    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    public static string Show(Version v) => v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

    /// <returns>новая версия скачана и готова (или уже была готова)</returns>
    public async Task<bool> CheckAsync(bool force = false)
    {
        if (Ready != null) return true;
        if (_busy) return false;
        _busy = true;
        LastError = null;
        try
        {
            string json;
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var r = await Http.GetAsync(Api, cts.Token);
                if ((int)r.StatusCode == 403 || (int)r.StatusCode == 429) { LastError = "GitHub просит подождать (лимит запросов) — попробуй через час"; return false; }
                r.EnsureSuccessStatusCode();
                json = await r.Content.ReadAsStringAsync();
            }
            catch (Exception) when (LastError == null) { LastError = "нет связи с GitHub"; return false; }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) { LastError = "непонятная версия на сервере: " + tag; return false; }
            v = Norm(v);
            Latest = v;
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
            if (url == null || hash == null) { LastError = "в версии " + Show(v) + " нет архива — её ещё выкладывают"; return false; } // без контрольной суммы ничего не ставим

            Directory.CreateDirectory(Dir);
            var exe = Path.Combine(Dir, $"Island-{Show(v)}.exe");
            if (!File.Exists(exe))
            {
                var zip = Path.Combine(Dir, $"Island-{Show(v)}.zip.part");
                if (!await Download(url, zip)) return false;
                string got;
                using (var f = File.OpenRead(zip)) got = Convert.ToHexString(await SHA256.HashDataAsync(f));
                if (!got.Equals(hash, StringComparison.OrdinalIgnoreCase)) { File.Delete(zip); LastError = "архив скачался с ошибкой — скачаю заново при следующей проверке"; return false; }
                using (var z = ZipFile.OpenRead(zip))
                {
                    var e = z.GetEntry("Island.exe");
                    if (e == null) { LastError = "в архиве нет Island.exe"; return false; }
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
        catch (Exception ex) { LastError ??= "ошибка: " + ex.Message; return false; }
        finally { _busy = false; Progress = -1; }
    }

    /// <summary>
    /// Скачать с докачкой: недокачанный .part остаётся на диске, следующая попытка продолжает с того же места (Range).
    /// Пять попыток подряд; связь замерла на 45 с — обрываем и пробуем снова.
    /// </summary>
    async Task<bool> Download(string url, string part)
    {
        long total = -1;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var cts = new System.Threading.CancellationTokenSource(Stall);
                using var r = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if ((int)r.StatusCode == 416) { File.Delete(part); continue; }   // файл на сервере другой — начнём сначала
                r.EnsureSuccessStatusCode();
                bool append = have > 0 && r.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!append) have = 0;
                total = have + (r.Content.Headers.ContentLength ?? -1);
                using var s = await r.Content.ReadAsStreamAsync(cts.Token);
                using var f = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                var buf = new byte[1 << 16];
                int n;
                while ((n = await s.ReadAsync(buf, cts.Token)) > 0)
                {
                    await f.WriteAsync(buf.AsMemory(0, n));
                    have += n;
                    if (total > 0) Progress = (double)have / total;
                    cts.CancelAfter(Stall);
                }
                if (total <= 0 || have >= total) return true;
            }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(3 + attempt * 5));
        }
        LastError = total > 0 && File.Exists(part)
            ? $"связь с GitHub рвётся — скачано {new FileInfo(part).Length * 100 / total}%, продолжу при следующей проверке"
            : "не получилось скачать архив с GitHub";
        return false;
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
