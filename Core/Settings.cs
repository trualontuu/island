using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Island.Core;

public sealed class Settings
{
    /// <summary>Чем отвечает чат: openrouter | deepseek | custom.</summary>
    public string Provider { get; set; } = "openrouter";
    /// <summary>Ключ, адрес и модель для OpenAI-совместимых провайдеров (OpenRouter, DeepSeek, свой).</summary>
    public Dictionary<string, AiProvider> Ai { get; set; } = new();

    public string SystemPrompt { get; set; } = "Ты — краткий помощник внутри «острова» Windows. Отвечай по-русски, коротко и по делу.";

    public string HotkeyShelf { get; set; } = "Alt+Space";
    public string HotkeyChat { get; set; } = "Ctrl+Alt+Space";
    public string HotkeyScreenshot { get; set; } = "Ctrl+Alt+S";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public bool RecordMic { get; set; } = false;
    public int RecordFps { get; set; } = 60;
    public string CaptureFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Island");

    public bool HideInFullscreen { get; set; } = true;
    public bool AnimateEqualizer { get; set; } = true;
    public bool Mascot { get; set; } = true;
    public bool Autostart { get; set; } = false;
    /// <summary>Программный рендер вместо видеокарты (меньше памяти, но анимации менее плавные).</summary>
    public bool SoftwareRendering { get; set; } = false;
    /// <summary>Клик по значку громкости Windows открывает наш микшер.</summary>
    public bool VolumeIconMixer { get; set; } = true;
    /// <summary>Остров уезжает за край экрана, если мышь не двигалась 20 секунд.</summary>
    public bool AutoSleep { get; set; } = true;
    public int AgentPort { get; set; } = 47321;
    public int ClipboardHistory { get; set; } = 30;

    public List<string> PinnedApps { get; set; } = new()
    {
        @"C:\Windows\explorer.exe",
        @"C:\Windows\System32\cmd.exe",
        @"C:\Windows\System32\taskmgr.exe",
        @"C:\Windows\System32\notepad.exe",
    };

    [JsonIgnore] public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");
    [JsonIgnore] static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Opts) ?? new Settings();
        }
        catch { }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
    }

    public AiProvider AiFor(string provider)
    {
        if (!Ai.TryGetValue(provider, out var a)) Ai[provider] = a = AiProvider.Preset(provider);
        if (string.IsNullOrWhiteSpace(a.BaseUrl)) a.BaseUrl = AiProvider.Preset(provider).BaseUrl;
        if (string.IsNullOrWhiteSpace(a.Model)) a.Model = AiProvider.Preset(provider).Model;
        // бесплатный DeepSeek V4 Flash на OpenRouter стал платным — старое значение по умолчанию меняем на новое
        if (provider == "openrouter" && a.Model == "deepseek/deepseek-v4-flash:free") a.Model = AiProvider.Preset(provider).Model;
        return a;
    }

    /// <summary>Известный провайдер (старые настройки могли хранить «anthropic» — его больше нет).</summary>
    public string ChatProvider => Provider is "openrouter" or "deepseek" or "custom" ? Provider : "openrouter";
}

public sealed class AiProvider
{
    public string Key { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";

    public static AiProvider Preset(string p) => p switch
    {
        // бесплатные модели OpenRouter помечены «:free» (лимит запросов); список меняется — модель можно сменить в настройках
        "openrouter" => new() { BaseUrl = "https://openrouter.ai/api/v1", Model = "google/gemma-4-31b-it:free" },
        "deepseek" => new() { BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat" },
        _ => new() { BaseUrl = "https://api.openai.com/v1", Model = "" },
    };

    public static string KeyUrl(string p) => p switch
    {
        "openrouter" => "https://openrouter.ai/keys",
        "deepseek" => "https://platform.deepseek.com/api_keys",
        _ => "",
    };
}
