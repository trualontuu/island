using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Island.Services;

namespace Island.UI;

/// <summary>Загрузки браузера в капсуле и уведомления «интернет пропал / вернулся», пинг на вкладке «Система».</summary>
public partial class IslandWindow
{
    DownloadWatcher? _dl;
    NetWatcher? _net;
    Action? _toastClick;           // клик по капсуле, пока висит это уведомление
    bool _capsuleDownload;         // капсула сейчас показывает загрузку
    int _pingMs = -2;              // -2 — ещё не мерили, -1 — нет ответа
    bool _pinging;

    void InitNet()
    {
        if (SnapDir != null) return;
        if (_s.DownloadsInCapsule && _dl == null)
        {
            try
            {
                _dl = new DownloadWatcher(Dispatcher);
                _dl.Changed += OnDownloadsChanged;
                _dl.Finished += OnDownloadFinished;
            }
            catch { _dl = null; }
        }
        else if (!_s.DownloadsInCapsule && _dl != null) { _dl.Dispose(); _dl = null; if (!_expanded) UpdateCompact(); }

        if (_s.NetAlerts && _net == null) { _net = new NetWatcher(Dispatcher); _net.Changed += OnNetChanged; }
        else if (!_s.NetAlerts && _net != null) { _net.Dispose(); _net = null; }
    }

    // ---------------- загрузки ----------------
    /// <summary>Показываем только загрузки дольше пары секунд — мелкие файлы не мигают в капсуле.</summary>
    Download? ShownDownload() => _dl?.Active.Where(d => (DateTime.UtcNow - d.Started).TotalSeconds >= 2).OrderByDescending(d => d.Started).FirstOrDefault();

    string DownloadLine()
    {
        var d = ShownDownload();
        if (d == null) return "";
        int more = _dl!.Active.Count - 1;
        var name = d.Name.Length > 22 ? d.Name[..20] + "…" : d.Name;
        var spd = d.Speed > 0 ? $" · {Speed(d.Speed)}" : "";
        return $"{name}  {Size(d.Size)}{spd}" + (more > 0 ? $"  +{more}" : "");
    }

    void OnDownloadsChanged()
    {
        bool want = ShownDownload() != null;
        if (want != _capsuleDownload) { if (!_expanded) UpdateCompact(); return; }
        if (_capsuleDownload && !_expanded) CompactText.Text = DownloadLine(); // ширина постоянная — просто меняем текст
    }

    void OnDownloadFinished(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length > 34) name = name[..32] + "…";
        Toast("Скачано: " + name, "", Mood.Happy, 5);
        _toastClick = () => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { } };
    }

    static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} ГБ" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0} МБ" : $"{b / 1024.0:0} КБ";

    // ---------------- интернет ----------------
    void OnNetChanged(bool online, TimeSpan down)
    {
        if (!online) { Toast("Интернет пропал", "", Mood.Alert, 5); return; }
        var t = down.TotalMinutes >= 1 ? $"{(int)down.TotalMinutes} мин {down.Seconds} с" : $"{Math.Max(1, (int)down.TotalSeconds)} с";
        Toast($"Интернет вернулся · не было {t}", "", Mood.Happy, 4);
    }

    /// <summary>Пинг для вкладки «Система»: раз в секунду, только пока вкладка открыта.</summary>
    async void PingTick()
    {
        if (_pinging) return;
        _pinging = true;
        try { _pingMs = await NetWatcher.Ping(); }
        finally { _pinging = false; }
    }
}
