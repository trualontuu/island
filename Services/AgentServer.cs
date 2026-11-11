using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Island.Services;

public enum AgentStatus { Idle, Working, Waiting, Done }

public sealed class AgentRequest
{
    public required string Kind;            // "permission" | "question"
    public required string Title;           // что спрашивают
    public string Detail = "";
    public List<string> Options = new();    // для вопроса — варианты
    public required JsonObject Raw;         // исходный JSON хука
    public readonly TaskCompletionSource<string?> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Крошечный HTTP-сервер на 127.0.0.1 (TcpListener, без прав админа и без HttpListener/ASP.NET).
/// Claude Code шлёт сюда JSON из хуков, opencode — из плагина.
/// </summary>
public sealed class AgentServer : IDisposable
{
    readonly TcpListener _listener;
    readonly Dispatcher _ui;
    readonly CancellationTokenSource _cts = new();

    public AgentStatus Status { get; private set; } = AgentStatus.Idle;
    public string Source { get; private set; } = "";
    public string Project { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public AgentRequest? Pending { get; private set; }

    public event Action? Changed;

    public AgentServer(int port, Dispatcher ui)
    {
        _ui = ui;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public bool Start()
    {
        try { _listener.Start(); }
        catch { return false; }
        _ = AcceptLoop();
        return true;
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Handle(c);
        }
    }

    async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            var s = client.GetStream();
            var (path, body) = await ReadRequest(s);
            string response = await Route(path, body);
            var bytes = Encoding.UTF8.GetBytes(response);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(head);
            await s.WriteAsync(bytes);
        }
        catch { }
    }

    static async Task<(string path, string body)> ReadRequest(NetworkStream s)
    {
        var buf = new byte[64 * 1024];
        var ms = new MemoryStream();
        int headerEnd = -1, contentLength = 0;
        string path = "/";
        while (true)
        {
            int n = await s.ReadAsync(buf);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            if (headerEnd < 0)
            {
                int idx = data.IndexOf("\r\n\r\n"u8);
                if (idx < 0) { if (ms.Length > 1 << 20) break; continue; }
                headerEnd = idx + 4;
                var headers = Encoding.ASCII.GetString(data[..idx]).Split("\r\n");
                var first = headers[0].Split(' ');
                if (first.Length > 1) path = first[1];
                foreach (var h in headers.Skip(1))
                    if (h.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(h[15..].Trim(), out contentLength);
            }
            if (ms.Length - headerEnd >= contentLength) break;
        }
        var all = ms.GetBuffer();
        string body = headerEnd < 0 ? "" : Encoding.UTF8.GetString(all, headerEnd, Math.Max(0, (int)ms.Length - headerEnd));
        return (path, body);
    }

    async Task<string> Route(string path, string body)
    {
        JsonObject j;
        try { j = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) as JsonObject ?? new JsonObject(); }
        catch { j = new JsonObject(); }

        if (path.StartsWith("/claude/permission")) return await OnClaudePermission(j);
        // пустой ответ: вывод хука UserPromptSubmit попадает в контекст Claude — там должно быть пусто
        if (path.StartsWith("/claude/event")) { await _ui.InvokeAsync(() => OnClaudeEvent(j)); return ""; }
        if (path.StartsWith("/opencode/event")) { await _ui.InvokeAsync(() => OnOpencodeEvent(j)); return "{}"; }
        if (path.StartsWith("/ping")) return "{\"ok\":true}";
        return "{}";
    }

    // ---------------- Claude Code ----------------
    void OnClaudeEvent(JsonObject j)
    {
        Source = "Claude Code";
        Project = ProjectName(j["cwd"]?.GetValue<string>());
        string ev = j["hook_event_name"]?.GetValue<string>() ?? "";
        switch (ev)
        {
            case "SessionStart": Set(AgentStatus.Idle, "сессия начата"); break;
            case "UserPromptSubmit": Set(AgentStatus.Working, "Работаю…"); break;
            case "PreToolUse": Set(AgentStatus.Working, DescribeTool(j["tool_name"]?.GetValue<string>(), j["tool_input"] as JsonObject)); break;
            case "Notification":
                var msg = j["message"]?.GetValue<string>() ?? "Ждёт тебя";
                if (Status != AgentStatus.Working || msg.Contains("waiting", StringComparison.OrdinalIgnoreCase))
                    Set(AgentStatus.Waiting, Translate(msg));
                break;
            case "Stop": Set(AgentStatus.Done, "Готово"); break;
            case "SessionEnd": Set(AgentStatus.Idle, ""); Source = ""; break;
        }
    }

    async Task<string> OnClaudePermission(JsonObject j)
    {
        string tool = j["tool_name"]?.GetValue<string>() ?? "";
        var input = j["tool_input"] as JsonObject ?? new JsonObject();
        AgentRequest req;
        if (tool == "AskUserQuestion" && input["questions"] is JsonArray qs && qs.Count == 1 && qs[0] is JsonObject q && q["multiSelect"]?.GetValue<bool>() != true)
        {
            req = new AgentRequest
            {
                Kind = "question",
                Title = q["question"]?.GetValue<string>() ?? "Вопрос",
                Options = (q["options"] as JsonArray)?.Select(o => o?["label"]?.GetValue<string>() ?? "").Where(x => x != "").ToList() ?? new(),
                Raw = j,
            };
        }
        else if (tool == "AskUserQuestion") return ""; // сложные анкеты — в терминале
        else
        {
            req = new AgentRequest { Kind = "permission", Title = "Разрешить " + tool + "?", Detail = DescribeTool(tool, input), Raw = j };
        }

        await _ui.InvokeAsync(() =>
        {
            Source = "Claude Code";
            Project = ProjectName(j["cwd"]?.GetValue<string>());
            Pending?.Reply.TrySetResult(null);
            Pending = req;
            Set(AgentStatus.Waiting, req.Kind == "question" ? "Вопрос" : "Нужно разрешение");
        });

        // ждём ответа с острова; не дождались — пустой ответ = обычный диалог в терминале
        var done = await Task.WhenAny(req.Reply.Task, Task.Delay(TimeSpan.FromSeconds(110)));
        string? choice = done == req.Reply.Task ? req.Reply.Task.Result : null;
        await _ui.InvokeAsync(() =>
        {
            if (Pending == req) { Pending = null; Set(AgentStatus.Working, choice == null ? "Ответ в терминале" : "Ответ с острова ✓"); }
        });
        if (choice == null) return "";

        JsonObject decision;
        if (req.Kind == "question")
        {
            var updated = (JsonObject)input.DeepClone();
            updated["answers"] = new JsonObject { [req.Title] = choice };
            decision = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = updated };
        }
        else decision = choice == "allow"
            ? new JsonObject { ["behavior"] = "allow" }
            : new JsonObject { ["behavior"] = "deny", ["message"] = "Отклонено с острова" };

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision }
        }.ToJsonString();
    }

    /// <summary>Ответ из UI: "allow"/"deny" для разрешений, текст варианта — для вопроса, null — «в терминале».</summary>
    public void Answer(string? choice) => Pending?.Reply.TrySetResult(choice);

    // ---------------- opencode ----------------
    void OnOpencodeEvent(JsonObject j)
    {
        Source = "opencode";
        Project = ProjectName(j["directory"]?.GetValue<string>());
        string type = j["type"]?.GetValue<string>() ?? "";
        switch (type)
        {
            case "working": Set(AgentStatus.Working, "Работаю…"); break;
            case "tool": Set(AgentStatus.Working, DescribeTool(j["tool"]?.GetValue<string>(), j["args"] as JsonObject)); break;
            case "permission": Set(AgentStatus.Waiting, "Нужно разрешение (в терминале)"); break;
            case "error": Set(AgentStatus.Waiting, "Ошибка"); break;
            case "done": Set(AgentStatus.Done, "Готово"); break;
        }
    }

    // ---------------- helpers ----------------
    void Set(AgentStatus st, string detail)
    {
        Status = st; Detail = detail;
        Changed?.Invoke();
    }

    public void Dismiss() { if (Status == AgentStatus.Done) { Status = AgentStatus.Idle; Changed?.Invoke(); } }

    static string ProjectName(string? cwd) => string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('\\', '/'));

    static string DescribeTool(string? tool, JsonObject? input)
    {
        tool ??= "";
        string? Str(string k) => input?[k] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        string arg = Str("file_path") ?? Str("filePath") ?? Str("path") ?? Str("pattern") ?? Str("command") ?? Str("url") ?? Str("description") ?? "";
        if (arg.Contains('\\') || arg.Contains('/')) { var f = Path.GetFileName(arg.Split(' ')[0]); if (f.Length > 0 && !arg.Contains(' ')) arg = f; }
        arg = arg.Replace('\n', ' ');
        if (arg.Length > 42) arg = arg[..41] + "…";
        string name = tool.ToLowerInvariant() switch
        {
            "edit" or "multiedit" => "Правлю",
            "write" => "Пишу",
            "read" => "Читаю",
            "bash" or "powershell" => "Команда",
            "grep" or "glob" => "Ищу",
            "webfetch" or "websearch" => "В сети",
            "agent" or "task" => "Агент",
            _ => tool,
        };
        return arg.Length > 0 ? $"{name}: {arg}" : name;
    }

    static string Translate(string m) =>
        m.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase) ? "Ждёт тебя" :
        m.Contains("permission", StringComparison.OrdinalIgnoreCase) ? "Нужно разрешение" : m;

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        Pending?.Reply.TrySetResult(null);
    }
}
