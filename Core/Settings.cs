using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Island.Core;

public sealed class Settings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "low";
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

    public string EffectiveApiKey => !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim()
        : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
}
