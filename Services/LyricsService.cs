using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Island.Services;

public sealed record LyricLine(TimeSpan At, string Text);

/// <summary>Текст песни: Synced — строки со временем (караоке), иначе только Plain.</summary>
public sealed class Lyrics
{
    public List<LyricLine> Lines = new();
    public string? Plain;
    public bool Instrumental;
    public bool Synced => Lines.Count > 0;
    public bool Empty => !Synced && string.IsNullOrWhiteSpace(Plain) && !Instrumental;
}

/// <summary>
/// Тексты песен из бесплатной открытой базы LRCLIB (lrclib.net, ключ не нужен).
/// Найденное (и «не найдено») кэшируется в %APPDATA%\Island\lyrics — повторно сеть не трогаем.
/// </summary>
public static class LyricsService
{
    static readonly HttpClient Http = MakeHttp();
    static readonly Dictionary<string, Lyrics?> Mem = new();
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "lyrics");
    static readonly TimeSpan RetryMissing = TimeSpan.FromDays(3);

    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Island/1.0 (desktop lyrics)");
        return h;
    }

    public static string Key(string artist, string title) => Norm(artist) + "|" + Norm(title);

    /// <returns>null — текста нет (или сеть недоступна)</returns>
    public static async Task<Lyrics?> GetAsync(string artist, string title, string album, TimeSpan duration, CancellationToken ct)
    {
        var key = Key(artist, title);
        lock (Mem) if (Mem.TryGetValue(key, out var m)) return m;

        var file = Path.Combine(Dir, Hash(key) + ".json");
        try
        {
            if (File.Exists(file))
            {
                var c = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(file, ct));
                if (c != null && (c.Found || DateTime.UtcNow - c.At < RetryMissing))
                {
                    var l = c.Found ? Build(c.Synced, c.Plain, c.Instrumental) : null;
                    lock (Mem) Mem[key] = l;
                    return l;
                }
            }
        }
        catch { }

        Lyrics? res;
        try { res = await FetchAsync(artist, title, album, duration, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // нет сети — не кэшируем, попробуем в следующий раз

        lock (Mem) Mem[key] = res;
        try
        {
            Directory.CreateDirectory(Dir);
            var entry = new CacheEntry { Found = res != null, At = DateTime.UtcNow, Synced = res == null ? null : ToLrc(res), Plain = res?.Plain, Instrumental = res?.Instrumental ?? false };
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(entry), CancellationToken.None);
        }
        catch { }
        return res;
    }

    static async Task<Lyrics?> FetchAsync(string artist, string title, string album, TimeSpan duration, CancellationToken ct)
    {
        // 1) точное совпадение (с длительностью — самый надёжный вариант)
        var q = $"artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        if (album.Length > 0) q += $"&album_name={Uri.EscapeDataString(album)}";
        if (duration.TotalSeconds > 1) q += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
        using (var r = await Http.GetAsync("https://lrclib.net/api/get?" + q, ct))
        {
            if (r.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                var l = FromJson(doc.RootElement);
                if (l != null) return l;
            }
            // 404 / 400 (например, без длительности) — просто идём искать
        }

        // 2) поиск по «очищенным» названию и исполнителю: (Official Video), feat., второй артист и т.п.
        var ca = CleanArtist(artist); var ct2 = CleanTitle(title);
        using var s = await Http.GetAsync($"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(ct2)}&artist_name={Uri.EscapeDataString(ca)}", ct);
        s.EnsureSuccessStatusCode();
        using var arr = JsonDocument.Parse(await s.Content.ReadAsStringAsync(ct));
        if (arr.RootElement.ValueKind != JsonValueKind.Array) return null;
        JsonElement? best = null; double bestScore = double.MaxValue;
        foreach (var e in arr.RootElement.EnumerateArray())
        {
            bool synced = e.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String && sl.GetString()!.Length > 0;
            bool plain = e.TryGetProperty("plainLyrics", out var pl) && pl.ValueKind == JsonValueKind.String && pl.GetString()!.Length > 0;
            if (!synced && !plain) continue;
            double d = e.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number && duration.TotalSeconds > 1 ? Math.Abs(du.GetDouble() - duration.TotalSeconds) : 0;
            if (duration.TotalSeconds > 1 && d > 6) continue; // другая версия трека — тайминги разъедутся
            double score = d + (synced ? 0 : 1000);
            if (score < bestScore) { bestScore = score; best = e.Clone(); }
        }
        return best == null ? null : FromJson(best.Value);
    }

    static Lyrics? FromJson(JsonElement e)
    {
        string? synced = e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        string? plain = e.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        bool instr = e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        var l = Build(synced, plain, instr);
        return l.Empty ? null : l;
    }

    static readonly Regex Tag = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);

    static Lyrics Build(string? synced, string? plain, bool instrumental)
    {
        var l = new Lyrics { Plain = string.IsNullOrWhiteSpace(plain) ? null : plain.Trim(), Instrumental = instrumental };
        if (!string.IsNullOrWhiteSpace(synced))
        {
            foreach (var raw in synced.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var ms = Tag.Matches(line);
                if (ms.Count == 0) continue;
                var text = line[(ms[^1].Index + ms[^1].Length)..].Trim();
                foreach (Match m in ms)
                {
                    int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                    l.Lines.Add(new LyricLine(TimeSpan.FromSeconds(min * 60 + sec), text));
                }
            }
            l.Lines.Sort((a, b) => a.At.CompareTo(b.At));
        }
        return l;
    }

    static string ToLrc(Lyrics l) => l.Synced
        ? string.Join("\n", l.Lines.Select(x => $"[{(int)x.At.TotalMinutes:00}:{x.At.Seconds:00}.{x.At.Milliseconds / 10:00}] {x.Text}"))
        : "";

    static readonly Regex Junk = new(@"\s*[\(\[][^\)\]]*(official|video|lyric|audio|клип|remaster|visuali[sz]er|mv|hd|4k|premiere|премьера|live|explicit)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Feat = new(@"\s*[\(\[]?\s*(feat\.?|ft\.?|featuring|при уч\.)\s.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string CleanTitle(string t)
    {
        t = Junk.Replace(t, "");
        t = Feat.Replace(t, "");
        return t.Trim(' ', '-', '–', '—');
    }

    static string CleanArtist(string a)
    {
        a = Regex.Replace(a, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
        a = Feat.Replace(a, "");
        var cut = a.IndexOfAny(new[] { ',', '&', ';', '/' });
        if (cut > 0) a = a[..cut];
        return a.Trim();
    }

    static string Norm(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");
    static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..20];

    sealed class CacheEntry
    {
        public bool Found { get; set; }
        public DateTime At { get; set; }
        public string? Synced { get; set; }
        public string? Plain { get; set; }
        public bool Instrumental { get; set; }
    }
}
