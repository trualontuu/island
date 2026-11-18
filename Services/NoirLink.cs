using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Island.Services;

/// <summary>
/// Свой плеер Noir (spspsps/player): в Windows он виден как «Player.exe». Лайк и его состояние берём у него
/// по именованному каналу — точно для играющего трека, без поиска по каталогу Яндекса.
/// </summary>
public static class NoirLink
{
    static string PipeName => "Player.Control." + Environment.UserName;

    public sealed record NoirState(bool Liked, bool Sc);

    public static bool IsNoir(string aumid)
    {
        var a = aumid.ToLowerInvariant();
        return a == "player.exe" || a.EndsWith(@"\programs\player\player.exe");
    }

    public static Task<NoirState?> State() => Send("state");
    public static Task<NoirState?> Like() => Send("like");

    static async Task<NoirState?> Send(string cmd)
    {
        try
        {
            using var cts = new CancellationTokenSource(4000);
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(300, cts.Token);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { cmd }) + "\n");
            await pipe.WriteAsync(bytes, cts.Token);
            await pipe.FlushAsync(cts.Token);
            using var r = new StreamReader(pipe, Encoding.UTF8);
            var line = await r.ReadLineAsync(cts.Token);
            if (line == null) return null;
            using var d = JsonDocument.Parse(line);
            var e = d.RootElement;
            if (!e.TryGetProperty("liked", out var l)) return null;
            bool sc = e.TryGetProperty("sc", out var s) && s.ValueKind == JsonValueKind.True;
            return new NoirState(l.ValueKind == JsonValueKind.True, sc);
        }
        catch { return null; }
    }
}
