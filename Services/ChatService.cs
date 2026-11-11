using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Island.Core;

namespace Island.Services;

/// <summary>
/// Чат с нейросетью через любой OpenAI-совместимый API (OpenRouter — бесплатные модели, DeepSeek, свой сервер):
/// ответ приходит потоком (SSE), история диалога — в памяти до «Новый чат».
/// </summary>
public sealed class ChatService
{
    readonly Settings _settings;
    CancellationTokenSource? _cts;

    public bool Busy { get; private set; }

    public ChatService(Settings s) => _settings = s;

    public void Reset() { Cancel(); _oaHistory.Clear(); }
    public void Cancel() => _cts?.Cancel();

    // ================= OpenAI-совместимые: OpenRouter, DeepSeek, свой сервер =================
    readonly List<(string Role, string Text)> _oaHistory = new();
    static readonly System.Net.Http.HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <param name="onText">кусочки ответа по мере генерации (вызывается из фонового потока)</param>
    public async Task<string> SendAsync(string userText, Action<string> onText)
    {
        var prov = _settings.ChatProvider;
        var cfg = _settings.AiFor(prov);
        if (string.IsNullOrWhiteSpace(cfg.Key))
            throw new InvalidOperationException(prov == "openrouter"
                ? "Нет ключа OpenRouter. Бесплатный ключ: openrouter.ai/keys → вставь его в настройках острова."
                : "Нет API-ключа — укажи его в настройках острова.");
        if (string.IsNullOrWhiteSpace(cfg.Model)) throw new InvalidOperationException("Не указана модель — впиши её в настройках острова.");

        _oaHistory.Add(("user", userText));
        _cts = new CancellationTokenSource();
        Busy = true;
        var text = new StringBuilder();
        try
        {
            // OpenRouter: если модель убрали из бесплатных или она упёрлась в лимит — повторяем через
            // «openrouter/free» (сам выбирает доступную бесплатную модель). Ошибка у пользователя — только если не вышло и так.
            string model = cfg.Model.Trim();
            System.Net.Http.HttpResponseMessage resp;
            while (true)
            {
                resp = await Http.SendAsync(BuildRequest(cfg, model, prov), System.Net.Http.HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                if (resp.IsSuccessStatusCode) break;
                int code = (int)resp.StatusCode;
                var err = await resp.Content.ReadAsStringAsync(_cts.Token);
                resp.Dispose();
                if (prov == "openrouter" && model != FreeRouter && code is 404 or 429 or 400 or 503) { model = FreeRouter; continue; }
                throw new InvalidOperationException(FriendlyError(code, err));
            }
            using var _resp = resp;
            using var stream = await resp.Content.ReadAsStreamAsync(_cts.Token);
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(_cts.Token)) != null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // пустые строки и служебные «: ...»
                var data = line.Substring(5).Trim();
                if (data == "[DONE]") break;
                if (data.Length == 0) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var e))
                    throw new InvalidOperationException("Ошибка модели: " + (e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString()));
                if (!root.TryGetProperty("choices", out var ch) || ch.GetArrayLength() == 0) continue;
                if (ch[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var piece = c.GetString();
                    if (!string.IsNullOrEmpty(piece)) { text.Append(piece); onText(piece); }
                }
            }
            var answer = text.ToString().Trim();
            if (answer.Length == 0) answer = "(пустой ответ — попробуй ещё раз или выбери другую модель)";
            _oaHistory.Add(("assistant", answer));
            return answer;
        }
        catch (OperationCanceledException)
        {
            if (text.Length > 0) { _oaHistory.Add(("assistant", text.ToString())); return text + " …(остановлено)"; }
            _oaHistory.RemoveAt(_oaHistory.Count - 1);
            return "(остановлено)";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            _oaHistory.RemoveAt(_oaHistory.Count - 1);
            throw new InvalidOperationException("Нет связи с сервером нейросети: " + ex.Message, ex);
        }
        catch
        {
            if (_oaHistory.Count > 0 && _oaHistory[^1].Role == "user") _oaHistory.RemoveAt(_oaHistory.Count - 1);
            throw;
        }
        finally { Busy = false; }
    }

    const string FreeRouter = "openrouter/free";

    System.Net.Http.HttpRequestMessage BuildRequest(AiProvider cfg, string model, string prov)
    {
        using var body = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("stream", true);
            w.WriteStartArray("messages");
            if (!string.IsNullOrWhiteSpace(_settings.SystemPrompt))
            { w.WriteStartObject(); w.WriteString("role", "system"); w.WriteString("content", _settings.SystemPrompt); w.WriteEndObject(); }
            foreach (var (role, t) in _oaHistory)
            { w.WriteStartObject(); w.WriteString("role", role); w.WriteString("content", t); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, cfg.BaseUrl.Trim().TrimEnd('/') + "/chat/completions")
        {
            Content = new System.Net.Http.ByteArrayContent(body.ToArray()),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.Key.Trim());
        if (prov == "openrouter") req.Headers.TryAddWithoutValidation("X-Title", "Island");
        return req;
    }

    static string FriendlyError(int code, string body)
    {
        string msg = body;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                msg = e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() ?? body : e.ToString();
        }
        catch { }
        if (msg.Length > 300) msg = msg[..300] + "…";
        return code switch
        {
            401 => "Ключ не подошёл (401). Проверь его в настройках.",
            402 => "На балансе нет денег (402). Бесплатные модели OpenRouter оканчиваются на «:free».",
            404 => "Модель не найдена (404): " + msg + "\nБесплатные модели меняются — выбери другую на openrouter.ai/models?max_price=0",
            429 => "Лимит бесплатных запросов исчерпан (429) — подожди минуту и повтори.",
            _ => $"Ошибка {code}: {msg}",
        };
    }
}
