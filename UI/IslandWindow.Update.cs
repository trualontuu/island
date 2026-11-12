using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Island.Services;

namespace Island.UI;

/// <summary>
/// Автообновление: раз в 15 минут спрашиваем GitHub (один маленький запрос), есть ли новая сборка, тихо скачиваем.
/// Ставим, когда человек отошёл от компьютера (остров уходит в спящий режим), — без перебиваний посреди работы.
/// </summary>
public partial class IslandWindow
{
    Updater? _upd;
    DispatcherTimer? _updTimer;
    bool _updAnnounced;
    public bool JustUpdated;

    void InitUpdater()
    {
        if (SnapDir != null || _upd != null) return;
        _upd = new Updater();
        _updTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _updTimer.Tick += async (_, _) =>
        {
            _updTimer.Interval = TimeSpan.FromMinutes(15);
            if (_s.AutoUpdate) await CheckUpdate(manual: false);
        };
        _updTimer.Start();
    }

    async Task CheckUpdate(bool manual)
    {
        if (_upd == null) return;
        if (manual) Toast("Проверяю обновления…", "", Mood.Thinking, 8);
        bool ready = await _upd.CheckAsync();
        if (ready && _upd.Ready is { } v)
        {
            if (manual) { Toast($"Ставлю версию {Updater.Show(v)}…", "", Mood.Happy, 3); await Task.Delay(1200); ApplyUpdate(force: true); return; }
            if (!_updAnnounced) { _updAnnounced = true; Toast($"Скачал обновление {Updater.Show(v)} — поставится само", "", Mood.Happy, 4); }
        }
        else if (manual) Toast($"У тебя последняя версия — {Updater.Show(Updater.Current)}", "", Mood.Happy, 3);
    }

    /// <summary>Поставить скачанное обновление. Без force — только если остров ничем не занят.</summary>
    void ApplyUpdate(bool force = false)
    {
        if (_upd?.Ready == null) return;
        if (!force && (_expanded || _rec.IsRecording || _agent?.Pending != null || _overlayOpen)) return;
        if (_upd.Apply(_userHidden)) Application.Current.Shutdown();
        else if (force) Toast("Не получилось обновиться — попробую позже", "", Mood.Alert, 3);
    }

    void ShowUpdatedToast()
    {
        if (JustUpdated) Toast($"Обновлено до {Updater.Show(Updater.Current)}", "", Mood.Happy, 3.5);
        else Toast("Привет!", null, Mood.Happy, 2.2);
    }
}
