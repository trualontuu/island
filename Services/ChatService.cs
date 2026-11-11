using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Island.Core;

namespace Island.Services;

/// <summary>
/// Чат с Claude: стриминг, история в памяти, блоки размышлений возвращаются в историю без изменений
/// (иначе API отклонит продолжение диалога). Отказы модели перехватывает серверный fallback.
/// </summary>
public sealed class ChatService
{
    readonly Settings _settings;
    readonly List<BetaMessageParam> _history = new();
    AnthropicClient? _client;
    string _clientKey = "";
    CancellationTokenSource? _cts;

    public bool Busy { get; private set; }

    public ChatService(Settings s) => _settings = s;

    public void Reset() { Cancel(); _history.Clear(); }
    public void Cancel() => _cts?.Cancel();

    /// <param name="onText">кусочки ответа по мере генерации (вызывается из фонового потока)</param>
    public async Task<string> SendAsync(string userText, Action<string> onText)
    {
        var key = _settings.EffectiveApiKey;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Нет API-ключа: укажи его в настройках острова или в переменной ANTHROPIC_API_KEY.");
        if (_client == null || _clientKey != key) { _client = new AnthropicClient { ApiKey = key }; _clientKey = key; }

        _history.Add(new BetaUserMessageParam { Content = userText });
        _cts = new CancellationTokenSource();
        Busy = true;

        var text = new StringBuilder();
        var blocks = new List<BetaContentBlockParam>();
        StringBuilder? thinking = null; string signature = "";
        bool inText = false;
        string? stop = null;

        try
        {
            var p = new MessageCreateParams
            {
                Model = _settings.Model,
                MaxTokens = 64000,
                System = _settings.SystemPrompt,
                OutputConfig = new BetaOutputConfig { Effort = ParseEffort(_settings.Effort) },
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
                Messages = new List<BetaMessageParam>(_history),
            };

            await foreach (var ev in _client.Beta.Messages.CreateStreaming(p, _cts.Token))
            {
                if (ev.TryPickContentBlockStart(out var start))
                {
                    FlushText();
                    var cb = start.ContentBlock.Value;
                    if (cb is BetaThinkingBlock) { thinking = new StringBuilder(); signature = ""; }
                    else if (cb is BetaTextBlock) inText = true;
                }
                else if (ev.TryPickContentBlockDelta(out var d))
                {
                    if (d.Delta.TryPickText(out var t)) { text.Append(t.Text); onText(t.Text); }
                    else if (d.Delta.TryPickThinking(out var th)) thinking?.Append(th.Thinking);
                    else if (d.Delta.TryPickSignature(out var sig)) signature += sig.Signature;
                }
                else if (ev.TryPickContentBlockStop(out _))
                {
                    if (thinking != null) { blocks.Add(new BetaThinkingBlockParam { Thinking = thinking.ToString(), Signature = signature }); thinking = null; }
                    FlushText();
                }
                else if (ev.TryPickDelta(out var md) && md.Delta.StopReason is { } sr)
                {
                    stop = sr.ToString();
                }
            }
            FlushText();

            if (stop != null && stop.Contains("refusal", StringComparison.OrdinalIgnoreCase) && text.Length == 0)
            {
                _history.RemoveAt(_history.Count - 1);
                return "Модель отказалась отвечать на этот запрос.";
            }
            if (blocks.Count == 0) blocks.Add(new BetaTextBlockParam { Text = " " });
            _history.Add(new BetaAssistantMessageParam { Content = blocks });
            return text.ToString();
        }
        catch (OperationCanceledException)
        {
            _history.RemoveAt(_history.Count - 1);
            return text.Length > 0 ? text + " …(остановлено)" : "(остановлено)";
        }
        catch (AnthropicApiException ex)
        {
            _history.RemoveAt(_history.Count - 1);
            throw new InvalidOperationException("Ошибка API: " + ex.Message, ex);
        }
        catch
        {
            if (_history.Count > 0 && _history[^1].Value is BetaUserMessageParam) _history.RemoveAt(_history.Count - 1);
            throw;
        }
        finally { Busy = false; }

        void FlushText()
        {
            if (!inText) return;
            inText = false;
            // текущий текстовый блок — всё, что накопилось после предыдущих блоков
            int already = 0;
            foreach (var b in blocks) if (b.TryPickText(out var tb)) already += tb.Text.Length;
            var s = text.ToString(already, text.Length - already);
            if (s.Length > 0) blocks.Add(new BetaTextBlockParam { Text = s });
        }
    }

    static Effort ParseEffort(string e) => e.ToLowerInvariant() switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Low,
    };
}
