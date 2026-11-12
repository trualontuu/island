using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Island.Services;

/// <summary>Одна загрузка из браузера: временный файл (.crdownload / .part) растёт, потом переименовывается.</summary>
public sealed class Download
{
    public string TempPath = "", Name = "";
    public long Size, PrevSize;
    public double Speed; // байт/с, сглаженная
    public DateTime Started = DateTime.UtcNow;
}

/// <summary>
/// Загрузки браузеров в папке «Загрузки»: Chrome/Edge/Яндекс/Opera пишут *.crdownload, Firefox — *.part.
/// Узнаём по событиям файловой системы (FileSystemWatcher), размер опрашиваем раз в секунду — только пока что-то качается.
/// </summary>
public sealed class DownloadWatcher : IDisposable
{
    static readonly string[] TempExt = { ".crdownload", ".part", ".partial", ".download" };

    readonly Dispatcher _ui;
    readonly FileSystemWatcher? _fsw;
    readonly DispatcherTimer _poll;
    readonly Dictionary<string, Download> _active = new(StringComparer.OrdinalIgnoreCase);

    public string Folder { get; }
    public IReadOnlyCollection<Download> Active => _active.Values;
    /// <summary>Список активных загрузок изменился или обновились размеры.</summary>
    public event Action? Changed;
    /// <summary>Загрузка завершилась: полный путь к готовому файлу.</summary>
    public event Action<string>? Finished;

    public DownloadWatcher(Dispatcher ui, string? folder = null)
    {
        _ui = ui;
        Folder = folder ?? DownloadsFolder();
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Poll();
        if (!Directory.Exists(Folder)) return;
        _fsw = new FileSystemWatcher(Folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName, InternalBufferSize = 16384 };
        _fsw.Created += (_, e) => _ui.BeginInvoke(() => OnCreated(e.FullPath));
        _fsw.Deleted += (_, e) => _ui.BeginInvoke(() => OnDeleted(e.FullPath));
        _fsw.Renamed += (_, e) => _ui.BeginInvoke(() => OnRenamed(e.OldFullPath, e.FullPath));
        _fsw.EnableRaisingEvents = true;
        // браузер мог начать качать ещё до запуска острова
        try { foreach (var f in Directory.EnumerateFiles(Folder)) if (IsTemp(f)) OnCreated(f); } catch { }
    }

    static bool IsTemp(string path) => TempExt.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    static string NiceName(string tempPath)
    {
        var n = Path.GetFileName(tempPath);
        foreach (var x in TempExt) if (n.EndsWith(x, StringComparison.OrdinalIgnoreCase)) { n = n[..^x.Length]; break; }
        return n.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) ? "файл" : n;
    }

    void OnCreated(string path)
    {
        if (!IsTemp(path) || _active.ContainsKey(path)) return;
        _active[path] = new Download { TempPath = path, Name = NiceName(path) };
        if (!_poll.IsEnabled) _poll.Start();
        Changed?.Invoke();
    }

    void OnDeleted(string path)
    {
        if (_active.Remove(path)) { Changed?.Invoke(); if (_active.Count == 0) _poll.Stop(); } // отменили
    }

    void OnRenamed(string from, string to)
    {
        if (!_active.Remove(from, out var d))
        {
            if (IsTemp(to)) OnCreated(to); // «Unconfirmed 123.crdownload» → «имя.crdownload», начало мы пропустили
            return;
        }
        if (IsTemp(to)) { d.TempPath = to; d.Name = NiceName(to); _active[to] = d; Changed?.Invoke(); return; }
        if (_active.Count == 0) _poll.Stop();
        Changed?.Invoke();
        Finished?.Invoke(to);
    }

    void Poll()
    {
        foreach (var d in _active.Values.ToList())
        {
            long size = d.Size;
            try
            {
                // через открытый дескриптор: размер в каталоге у файла, который пишется, обновляется с опозданием
                using var fs = new FileStream(d.TempPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                size = fs.Length;
            }
            catch (FileNotFoundException) { _active.Remove(d.TempPath); continue; }
            catch { }
            double inst = Math.Max(0, size - d.Size);
            d.Speed = d.Size == 0 && d.PrevSize == 0 ? inst : d.Speed * 0.6 + inst * 0.4;
            d.PrevSize = d.Size; d.Size = size;
        }
        if (_active.Count == 0) _poll.Stop();
        Changed?.Invoke();
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public void Dispose() { _poll.Stop(); _fsw?.Dispose(); }
}
