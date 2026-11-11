using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Island.Services;

/// <summary>Подключение Claude Code (хуки в ~/.claude/settings.json) и opencode (плагин).</summary>
public static class AgentIntegration
{
    const string Marker = "127.0.0.1:"; // по нему узнаём наши хуки

    static string ClaudeSettings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    static string OpencodePlugin => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "plugin", "island.js");

    public static bool ClaudeInstalled()
    {
        try { return File.Exists(ClaudeSettings) && File.ReadAllText(ClaudeSettings).Contains("/claude/event"); } catch { return false; }
    }
    public static bool OpencodeInstalled() => File.Exists(OpencodePlugin);

    /// <summary>
    /// Добавляет хуки, не трогая остальные настройки. Команды — простой curl без перенаправлений,
    /// поэтому работают и в bash, и в cmd. Если остров не запущен — curl молча падает, Claude Code продолжает.
    /// </summary>
    public static string InstallClaude(int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ClaudeSettings)!);
        JsonObject root;
        if (File.Exists(ClaudeSettings))
        {
            File.Copy(ClaudeSettings, ClaudeSettings + ".island-backup", true);
            root = JsonNode.Parse(File.ReadAllText(ClaudeSettings), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject();
        }
        else root = new JsonObject();

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        string ev = $"curl -s -m 2 -X POST http://127.0.0.1:{port}/claude/event --data-binary @-";
        string perm = $"curl -s -m 115 -X POST http://127.0.0.1:{port}/claude/permission --data-binary @-";

        foreach (var name in new[] { "SessionStart", "UserPromptSubmit", "Notification", "Stop", "SessionEnd" })
            AddHook(hooks, name, null, ev, 5);
        AddHook(hooks, "PreToolUse", "*", ev, 5);
        AddHook(hooks, "PermissionRequest", "*", perm, 120);

        File.WriteAllText(ClaudeSettings, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return ClaudeSettings;
    }

    static void AddHook(JsonObject hooks, string ev, string? matcher, string command, int timeout)
    {
        var arr = hooks[ev] as JsonArray ?? new JsonArray();
        hooks[ev] = arr;
        // убираем старые записи острова (например, со старым портом)
        foreach (var g in arr.OfType<JsonObject>().ToList())
        {
            if (g["hooks"] is not JsonArray inner) continue;
            foreach (var h in inner.OfType<JsonObject>().ToList())
                if ((h["command"]?.ToString() ?? "").Contains(Marker) && (h["command"]?.ToString() ?? "").Contains("/claude/")) inner.Remove(h);
            if (inner.Count == 0) arr.Remove(g);
        }
        var group = new JsonObject();
        if (matcher != null) group["matcher"] = matcher;
        group["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = timeout });
        arr.Add(group);
    }

    public static void UninstallClaude()
    {
        if (!File.Exists(ClaudeSettings)) return;
        var root = JsonNode.Parse(File.ReadAllText(ClaudeSettings)) as JsonObject;
        if (root?["hooks"] is not JsonObject hooks) return;
        foreach (var (name, node) in hooks.ToList())
        {
            if (node is not JsonArray arr) continue;
            foreach (var g in arr.OfType<JsonObject>().ToList())
            {
                if (g["hooks"] is not JsonArray inner) continue;
                foreach (var h in inner.OfType<JsonObject>().ToList())
                    if ((h["command"]?.ToString() ?? "").Contains("/claude/event") || (h["command"]?.ToString() ?? "").Contains("/claude/permission")) inner.Remove(h);
                if (inner.Count == 0) arr.Remove(g);
            }
            if (arr.Count == 0) hooks.Remove(name);
        }
        File.WriteAllText(ClaudeSettings, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string InstallOpencode(int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OpencodePlugin)!);
        File.WriteAllText(OpencodePlugin, $$"""
            // Island — показывает статус opencode в «острове» Windows.
            const URL = "http://127.0.0.1:{{port}}/opencode/event";

            export const IslandPlugin = async ({ directory }) => {
              const post = (body) =>
                fetch(URL, { method: "POST", body: JSON.stringify({ directory, ...body }) }).catch(() => {});
              return {
                event: async ({ event }) => {
                  switch (event.type) {
                    case "session.idle": return post({ type: "done" });
                    case "session.error": return post({ type: "error" });
                    case "permission.updated":
                    case "permission.asked": return post({ type: "permission" });
                  }
                },
                "chat.message": async () => post({ type: "working" }),
                "tool.execute.before": async (input, output) =>
                  post({ type: "tool", tool: input.tool, args: output?.args ?? {} }),
              };
            };
            """);
        return OpencodePlugin;
    }

    public static void UninstallOpencode() { try { File.Delete(OpencodePlugin); } catch { } }
}
