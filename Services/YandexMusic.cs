using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Island.Core;

namespace Island.Services;

/// <summary>
/// Лайки в Яндекс Музыке через её (неофициальное) API — тем же способом, что и приложения для телевизоров:
/// вход по коду на ya.ru/device, токен хранится на этом компьютере зашифрованным (DPAPI, только для этого
/// пользователя Windows) и никуда, кроме api.music.yandex.net, не отправляется.
/// Текущий трек ищется по названию и исполнителю из Windows (SMTC) — точное совпадение названия + исполнителя,
/// при равных — ближайшая длительность. Список «Мне нравится» держим в памяти, чтобы сразу рисовать сердечко.
/// </summary>
public static class YandexMusic
{
    // клиент приложения Яндекс Музыки (из открытой библиотеки MarshalX/yandex-music-api)
    const string ClientId = "23cabbbdc6cd418abb4b39c32c41195d";
    const string ClientSecret = "53bc75238f0c4d08a118e51fe9203300";
    // адреса можно подменить для теста (локальный mock-сервер)
    static readonly string OAuth = Environment.GetEnvironmentVariable("ISLAND_YM_OAUTH") ?? "https://oauth.yandex.ru";
    static readonly string Api = Environment.GetEnvironmentVariable("ISLAND_YM_API") ?? "https://api.music.yandex.net";
    static string TokenFile => Path.Combine(Settings.Dir, Environment.GetEnvironmentVariable("ISLAND_YM_TOKENFILE") ?? "yandex.bin");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static string? _token;
    static bool _loaded;
    public static string Uid { get; private set; } = "";
    public static string Login { get; private set; } = "";
    static readonly HashSet<string> _liked = new();
    static DateTime _likedAt = DateTime.MinValue;
    static readonly Dictionary<string, string?> _resolved = new(); // "название|исполнитель" → "trackId:albumId" (null — не нашли)

    public static bool LoggedIn { get { Load(); return _token != null; } }

    /// <summary>Трек играет в Яндекс Музыке (приложение или сайт в Яндекс Браузере).</summary>
    public static bool IsYandexSource(MediaInfo m) =>
        m.AppId.Contains("yandex.desktop.music", StringComparison.OrdinalIgnoreCase) || m.AppId.Contains("yandexmusic", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- токен
    static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(TokenFile)) return;
            var parts = Encoding.UTF8.GetString(Dpapi(File.ReadAllBytes(TokenFile), false)).Split('\n');
            _token = parts[0].Length > 0 ? parts[0] : null;
            if (parts.Length > 1) Uid = parts[1];
            if (parts.Length > 2) Login = parts[2];
        }
        catch { _token = null; }
    }

    static void Save()
    {
        Directory.CreateDirectory(Settings.Dir);
        File.WriteAllBytes(TokenFile, Dpapi(Encoding.UTF8.GetBytes($"{_token}\n{Uid}\n{Login}"), true));
    }

    public static void Logout()
    {
        _token = null; Uid = ""; Login = ""; _liked.Clear(); _likedAt = DateTime.MinValue; _resolved.Clear();
        try { File.Delete(TokenFile); } catch { }
    }

    // ---------------------------------------------------------------- вход по коду
    public sealed record DeviceCode(string UserCode, string Url, string Code, int Interval, int ExpiresIn);

    public static async Task<DeviceCode> StartLogin()
    {
        var id = new string(Enumerable.Range(0, 10).Select(_ => "abcdefghijklmnopqrstuvwxyz0123456789"[Random.Shared.Next(36)]).ToArray());
        using var r = await Http.PostAsync(OAuth + "/device/code", Form(("client_id", ClientId), ("device_id", id), ("device_name", "Island (" + Environment.MachineName + ")")));
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var e = d.RootElement;
        return new DeviceCode(Str(e, "user_code"), Str(e, "verification_url") is { Length: > 0 } u ? u : "https://ya.ru/device", Str(e, "device_code"),
            e.TryGetProperty("interval", out var i) ? i.GetInt32() : 5, e.TryGetProperty("expires_in", out var x) ? x.GetInt32() : 300);
    }

    /// <summary>Ждём, пока человек введёт код на ya.ru/device. true — вошли.</summary>
    public static async Task<bool> WaitLogin(DeviceCode dc, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(dc.ExpiresIn);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, dc.Interval)), ct);
            string body;
            try
            {
                using var r = await Http.PostAsync(OAuth + "/token", Form(("grant_type", "device_code"), ("code", dc.Code), ("client_id", ClientId), ("client_secret", ClientSecret)), ct);
                body = await r.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; } // сеть моргнула — попробуем ещё
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("access_token", out var t))
            {
                _token = t.GetString(); _loaded = true;
                await LoadAccount();
                Save();
                _ = RefreshLikes(true);
                return true;
            }
            var err = Str(d.RootElement, "error");
            if (err != "authorization_pending" && err != "slow_down") return false; // код отклонён или истёк
        }
        return false;
    }

    static async Task LoadAccount()
    {
        using var d = await Get("/account/status");
        var acc = d.RootElement.GetProperty("result").GetProperty("account");
        Uid = acc.GetProperty("uid").ToString();
        Login = Str(acc, "login") is { Length: > 0 } l ? l : Str(acc, "displayName");
    }

    // ---------------------------------------------------------------- лайки
    /// <summary>Список «Мне нравится»: при входе, потом не чаще раза в 5 минут (лайкнули в самом приложении — увидим).</summary>
    public static async Task RefreshLikes(bool force = false)
    {
        if (!LoggedIn || Uid.Length == 0) return;
        if (!force && DateTime.UtcNow - _likedAt < TimeSpan.FromMinutes(5)) return;
        _likedAt = DateTime.UtcNow;
        try
        {
            using var d = await Get($"/users/{Uid}/likes/tracks");
            var tracks = d.RootElement.GetProperty("result").GetProperty("library").GetProperty("tracks");
            _liked.Clear();
            foreach (var t in tracks.EnumerateArray()) _liked.Add(t.GetProperty("id").ToString());
        }
        catch (UnauthorizedAccessException) { Logout(); } // токен отозвали в аккаунте Яндекса
        catch { _likedAt = DateTime.MinValue; }
    }

    public static bool IsLiked(string trackKey) => _liked.Contains(trackKey.Split(':')[0]);

    /// <summary>Поставить или снять лайк. Возвращает новое состояние.</summary>
    public static async Task<bool> SetLike(string trackKey, bool like)
    {
        using var r = await Post($"/users/{Uid}/likes/tracks/" + (like ? "add-multiple" : "remove"), ("track-ids", trackKey));
        var id = trackKey.Split(':')[0];
        if (like) _liked.Add(id); else _liked.Remove(id);
        return like;
    }

    // ---------------------------------------------------------------- какой это трек
    /// <summary>"trackId:albumId" трека, который сейчас играет, или null — не нашли в каталоге.</summary>
    public static async Task<string?> Resolve(MediaInfo m)
    {
        var key = m.Title + "|" + m.Artist;
        if (_resolved.TryGetValue(key, out var hit)) return hit;
        string? found = null;
        using (var d = await Get("/search?type=track&page=0&nocorrect=false&text=" + Uri.EscapeDataString((m.Artist + " " + m.Title).Trim())))
        {
            var res = d.RootElement.GetProperty("result");
            if (res.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Object && tr.TryGetProperty("results", out var list))
                found = Pick(list, m);
        }
        _resolved[key] = found;
        if (_resolved.Count > 300) _resolved.Clear();
        return found;
    }

    static string? Pick(JsonElement list, MediaInfo m)
    {
        string title = Norm(m.Title);
        var artists = m.Artist.Split(new[] { ',', '&', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(Norm).Where(a => a.Length > 0).ToList();
        double dur = m.Duration.TotalMilliseconds;
        string? best = null; double bestScore = double.MinValue;
        foreach (var t in list.EnumerateArray())
        {
            if (!t.TryGetProperty("id", out var idEl)) continue;
            var tTitle = Norm(Str(t, "title"));
            var ver = Norm(Str(t, "version"));
            bool titleOk = tTitle == title || (ver.Length > 0 && Norm(Str(t, "title") + " " + Str(t, "version")) == title);
            if (!titleOk) continue;
            var tArtists = t.TryGetProperty("artists", out var ar) ? ar.EnumerateArray().Select(a => Norm(Str(a, "name"))).ToList() : new();
            bool artistOk = artists.Count == 0 || artists.Any(a => tArtists.Any(x => x == a || x.Contains(a) || a.Contains(x)));
            if (!artistOk) continue;
            double score = 0;
            if (dur > 0 && t.TryGetProperty("durationMs", out var dm)) score -= Math.Abs(dm.GetDouble() - dur) / 1000.0;
            if (ver.Length == 0) score += 0.5; // без пометок «remix», «live» — вероятнее оригинал
            if (score > bestScore)
            {
                bestScore = score;
                var album = t.TryGetProperty("albums", out var al) && al.GetArrayLength() > 0 ? al[0].GetProperty("id").ToString() : "";
                best = idEl.ToString() + (album.Length > 0 ? ":" + album : "");
            }
        }
        return best;
    }

    static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant().Replace('ё', 'е'))
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- HTTP
    static HttpRequestMessage Req(HttpMethod method, string path)
    {
        var q = new HttpRequestMessage(method, Api + path);
        q.Headers.TryAddWithoutValidation("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
        if (_token != null) q.Headers.TryAddWithoutValidation("Authorization", "OAuth " + _token);
        return q;
    }

    static async Task<JsonDocument> Get(string path)
    {
        using var q = Req(HttpMethod.Get, path);
        return await Send(q);
    }

    static async Task<JsonDocument> Post(string path, params (string, string)[] form)
    {
        using var q = Req(HttpMethod.Post, path);
        q.Content = Form(form);
        return await Send(q);
    }

    static async Task<JsonDocument> Send(HttpRequestMessage q)
    {
        using var r = await Http.SendAsync(q);
        if (r.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("Яндекс не принял вход");
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync());
    }

    static FormUrlEncodedContent Form(params (string k, string v)[] kv) => new(kv.Select(p => new KeyValuePair<string, string>(p.k, p.v)));

    static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

    // ---------------------------------------------------------------- DPAPI
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    static byte[] Dpapi(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var inp = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            var outp = new Blob();
            const int UiForbidden = 1;
            bool ok = protect ? CryptProtectData(ref inp, "Island", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outp)
                              : CryptUnprotectData(ref inp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outp);
            if (!ok) throw new InvalidOperationException("DPAPI");
            var res = new byte[outp.Size];
            Marshal.Copy(outp.Data, res, 0, outp.Size);
            LocalFree(outp.Data);
            return res;
        }
        finally { h.Free(); }
    }
}
