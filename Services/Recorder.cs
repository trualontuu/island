using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Island.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Island.Services;

#pragma warning disable CS0618 // классические WASAPI-классы NAudio: проверенные и простые, нам хватает

/// <summary>
/// Запись экрана: видео — ffmpeg (ddagrab + NVENC, захват через видеокарту, почти без нагрузки на CPU;
/// запасные варианты — libx264 и gdigrab). Звук системы — WASAPI loopback (+ микрофон по желанию),
/// после остановки всё склеивается в один mp4 с синхронизацией по меткам времени.
/// </summary>
public sealed class Recorder
{
    readonly Settings _s;
    Process? _ff;
    WasapiLoopbackCapture? _loop;
    WasapiCapture? _mic;
    WasapiOut? _silence;
    WaveFileWriter? _loopWriter, _micWriter;
    string _dir = "", _video = "", _sysWav = "", _micWav = "";
    DateTime _audioStart, _videoStart;

    public bool IsRecording { get; private set; }
    public DateTime StartedAt { get; private set; }
    public event Action<string>? Error;

    public Recorder(Settings s) => _s = s;

    public async Task<bool> StartAsync()
    {
        if (IsRecording) return true;
        _dir = Path.Combine(Path.GetTempPath(), "island_rec_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _video = Path.Combine(_dir, "v.mp4");
        _sysWav = Path.Combine(_dir, "sys.wav");
        _micWav = Path.Combine(_dir, "mic.wav");

        var (b, _, idx) = CaptureService.MonitorUnderCursor();
        int fps = Math.Clamp(_s.RecordFps, 10, 120);
        int w = b.W & ~1, h = b.H & ~1;

        string[] variants =
        {
            // 1) захват и кодирование целиком на GPU (NVIDIA)
            $"-f lavfi -i ddagrab=output_idx={idx}:framerate={fps}:draw_mouse=1 -c:v h264_nvenc -preset p4 -rc vbr -cq 23 -b:v 0",
            // 2) AMD / Intel
            $"-f lavfi -i ddagrab=output_idx={idx}:framerate={fps}:draw_mouse=1 -c:v h264_amf -quality balanced -rc cqp -qp_i 23 -qp_p 23",
            $"-f lavfi -i ddagrab=output_idx={idx}:framerate={fps}:draw_mouse=1 -c:v h264_qsv -global_quality 23",
            // 3) захват на GPU, кодирование на CPU
            $"-f lavfi -i ddagrab=output_idx={idx}:framerate={fps}:draw_mouse=1 -vf hwdownload,format=bgra -c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p",
            // 4) совсем старое железо
            $"-f gdigrab -framerate {Math.Min(fps, 30)} -offset_x {b.Left} -offset_y {b.Top} -video_size {w}x{h} -i desktop -c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p",
        };

        StartAudio();
        foreach (var v in variants)
        {
            if (await TryStartFfmpeg(v)) { IsRecording = true; StartedAt = DateTime.Now; return true; }
        }
        StopAudio();
        Error?.Invoke("ffmpeg не смог начать запись. Проверь путь к ffmpeg в настройках.");
        return false;
    }

    async Task<bool> TryStartFfmpeg(string args)
    {
        try { if (File.Exists(_video)) File.Delete(_video); } catch { }
        var psi = new ProcessStartInfo(_s.FfmpegPath, $"-hide_banner -y {args} -movflags +faststart \"{_video}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true,
        };
        Process p;
        try { p = Process.Start(psi)!; } catch { return false; }

        var firstFrame = new TaskCompletionSource<bool>();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) { firstFrame.TrySetResult(false); return; }
            if (e.Data.StartsWith("frame=")) firstFrame.TrySetResult(true);
        };
        p.BeginErrorReadLine();
        var ok = await Task.WhenAny(firstFrame.Task, Task.Delay(4000)) == firstFrame.Task && firstFrame.Task.Result;
        if (!ok || p.HasExited)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            return false;
        }
        // ffmpeg пишет «frame=» примерно раз в 0.5 с — первая строка ≈ старт видео
        _videoStart = DateTime.UtcNow.AddMilliseconds(-500);
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _ff = p;
        return true;
    }

    void StartAudio()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            // loopback молчит, когда ничего не играет → звук «съезжает». Играем тишину, чтобы поток шёл всегда.
            _silence = new WasapiOut(dev, AudioClientShareMode.Shared, true, 100);
            _silence.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
            _silence.Play();

            _loop = new WasapiLoopbackCapture(dev);
            _loopWriter = new WaveFileWriter(_sysWav, _loop.WaveFormat);
            _loop.DataAvailable += (_, e) => _loopWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            _loop.StartRecording();

            if (_s.RecordMic)
            {
                var micDev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                _mic = new WasapiCapture(micDev);
                _micWriter = new WaveFileWriter(_micWav, _mic.WaveFormat);
                _mic.DataAvailable += (_, e) => _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);
                _mic.StartRecording();
            }
            _audioStart = DateTime.UtcNow;
        }
        catch { StopAudio(); }
    }

    void StopAudio()
    {
        try { _loop?.StopRecording(); } catch { }
        try { _mic?.StopRecording(); } catch { }
        try { _silence?.Stop(); } catch { }
        _loop?.Dispose(); _mic?.Dispose(); _silence?.Dispose();
        _loopWriter?.Dispose(); _micWriter?.Dispose();
        _loop = null; _mic = null; _silence = null; _loopWriter = null; _micWriter = null;
    }

    /// <returns>путь к готовому mp4 или null</returns>
    public async Task<string?> StopAsync()
    {
        if (!IsRecording || _ff == null) return null;
        IsRecording = false;
        try { await _ff.StandardInput.WriteAsync("q"); await _ff.StandardInput.FlushAsync(); } catch { }
        if (!await Task.Run(() => _ff.WaitForExit(8000))) { try { _ff.Kill(); } catch { } }
        _ff.Dispose(); _ff = null;
        StopAudio();

        Directory.CreateDirectory(_s.CaptureFolder);
        string outPath = Path.Combine(_s.CaptureFolder, $"Recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
        bool hasSys = File.Exists(_sysWav) && new FileInfo(_sysWav).Length > 1024;
        bool hasMic = File.Exists(_micWav) && new FileInfo(_micWav).Length > 1024;

        if (!hasSys && !hasMic) { File.Move(_video, outPath, true); Cleanup(); return outPath; }

        // смещение звука относительно видео
        double off = (_audioStart - _videoStart).TotalSeconds;
        string Off(string wav) => off >= 0 ? $"-itsoffset {off:0.000} -i \"{wav}\"".Replace(',', '.') : $"-ss {-off:0.000} -i \"{wav}\"".Replace(',', '.');

        string inputs = $"-i \"{_video}\" " + (hasSys ? Off(_sysWav) + " " : "") + (hasMic ? Off(_micWav) + " " : "");
        string map = hasSys && hasMic
            ? "-filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[a]\" -map 0:v -map \"[a]\""
            : "-map 0:v -map 1:a";
        var psi = new ProcessStartInfo(_s.FfmpegPath, $"-hide_banner -y {inputs}{map} -c:v copy -c:a aac -b:a 192k -movflags +faststart \"{outPath}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        try
        {
            using var mux = Process.Start(psi)!;
            await mux.WaitForExitAsync();
            if (mux.ExitCode != 0) File.Move(_video, outPath, true);
        }
        catch { try { File.Move(_video, outPath, true); } catch { return null; } }
        Cleanup();
        return outPath;
    }

    void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }
}
