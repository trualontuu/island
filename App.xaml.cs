using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Island.Core;
using Island.UI;

namespace Island;

public partial class App : Application
{
    Mutex? _single;
    EventWaitHandle? _quit, _hide;
    SystemHost? _host;
    IslandWindow? _island;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--snap")
        {
            base.OnStartup(e);
            _host = new SystemHost();
            var ss = Settings.Load();
            if (Environment.GetEnvironmentVariable("ISLAND_THEME") is { Length: > 0 } th) ss.Theme = th;
            if (double.TryParse(Environment.GetEnvironmentVariable("ISLAND_OPACITY"), System.Globalization.CultureInfo.InvariantCulture, out var op)) ss.Opacity = op;
            Island.UI.Theme.Apply(ss);
            _island = new IslandWindow(ss, _host) { SnapDir = e.Args[1], ShowActivated = false };
            _island.Show();
            return;
        }

        // Один ярлык = и открыть, и закрыть: если остров уже запущен — просим его закрыться и выходим.
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Island.DynamicIsland.Quit");
        _hide = new EventWaitHandle(false, EventResetMode.AutoReset, "Island.DynamicIsland.HideToggle");
        _single = new Mutex(true, "Island.DynamicIsland.Singleton", out bool first);
        bool hideArg = Array.IndexOf(e.Args, "--hide") >= 0;
        // «Island.exe --hide» — скрыть/показать уже запущенный остров (без выхода)
        if (!first) { (hideArg ? _hide : _quit).Set(); Shutdown(); return; }
        // ждём сигнал без отдельного потока (пул потоков, ноль CPU)
        ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => Dispatcher.BeginInvoke(() => _island?.Goodbye()), null, -1, true);
        ThreadPool.RegisterWaitForSingleObject(_hide, (_, _) => Dispatcher.BeginInvoke(() => _island?.ToggleHidden()), null, -1, false);


        base.OnStartup(e);
        var settings = Settings.Load();
        Island.UI.Theme.Apply(settings);
        _host = new SystemHost();
        _island = new IslandWindow(settings, _host);
        _host.TrayLeftClick += () => _island.Expand("home", true);
        _host.TrayRightClick += () => _island.ShowMenu();
        _host.ShowTray("Island");
        _island.StartHidden = hideArg; // «--hide» при первом запуске — стартуем сразу скрытым
        _island.Show();

        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            try { _island?.Toast("Ошибка: " + ex.Exception.Message, "", Mood.Alert, 4); } catch { }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
