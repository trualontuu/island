using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Island.Services;

namespace Island.UI;

/// <summary>
/// Автообновление: раз в 15 минут спрашиваем GitHub (один маленький запрос), есть ли новая сборка, тихо скачиваем.
/// Ставим, когда человек отошёл от компьютера: остров ушёл в спящий режим или 2 минуты никто не трогал
/// клавиатуру и мышь (на случай, если спящий режим выключен), — без перебиваний посреди работы.
/// </summary>
public partial class IslandWindow
{
    Updater? _upd;
    DispatcherTimer? _updTimer;
    bool _updAnnounced;
    DispatcherTimer? _updIdleTimer;
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
        if (manual && _upd.Busy)
        {
            Toast(_upd.Progress >= 0 ? $"Уже скачиваю обновление — {_upd.Progress:P0}" : "Уже проверяю обновления…", "\uE896", Mood.Thinking, 3);
            return;
        }
        if (manual) Toast("Проверяю обновления…", "\uE895", Mood.Thinking, 30);
        // пока качается — показываем проценты (только при ручной проверке, фоновая качает молча)
        DispatcherTimer? prog = null;
        if (manual)
        {
            prog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            prog.Tick += (_, _) => { if (_upd.Progress >= 0) Toast($"Скачиваю обновление — {_upd.Progress:P0}", "\uE896", Mood.Thinking, 30); };
            prog.Start();
        }
        bool ready;
        try { ready = await _upd.CheckAsync(); }
        finally { prog?.Stop(); }
        if (ready && _upd.Ready is { } v)
        {
            if (manual) { Toast($"Ставлю версию {Updater.Show(v)}…", "", Mood.Happy, 3); await Task.Delay(1200); ApplyUpdate(force: true); return; }
            if (!_updAnnounced) { _updAnnounced = true; Toast($"Скачал обновление {Updater.Show(v)} — поставится само", "", Mood.Happy, 4); }
        }
        if (ready && _upd.Ready != null) WaitIdleForUpdate();
        else if (manual && _upd.LastError != null) Toast("Не обновилось: " + _upd.LastError, "\uE7BA", Mood.Alert, 6);
        else if (manual) Toast($"У тебя последняя версия — {Updater.Show(Updater.Current)}" + (_upd.Latest is { } l && l != Updater.Current ? $" (на сервере {Updater.Show(l)})" : ""), "\uE73E", Mood.Happy, 3.5);
    }

    /// <summary>Обновление скачано — раз в 30 с смотрим, отошёл ли человек (2 мин без клавиатуры и мыши).</summary>
    void WaitIdleForUpdate()
    {
        if (_updIdleTimer != null) return;
        _updIdleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _updIdleTimer.Tick += (_, _) =>
        {
            if (_upd?.Ready == null || !_s.AutoUpdate) return;
            if (Island.Core.Native.IdleTime() >= TimeSpan.FromMinutes(2)) ApplyUpdate();
        };
        _updIdleTimer.Start();
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
