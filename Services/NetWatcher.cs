using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Island.Services;

/// <summary>
/// Есть ли интернет. Windows сообщает о любых сменах сети событием (без опроса); на каждое событие —
/// своя проверка: короткий HTTP-запрос к нескольким надёжным адресам (Яндекс, Google, Cloudflare).
/// Именно запрос, а не просто TCP-соединение: VPN (Happ/xray) «принимает» соединение сам, мгновенно,
/// даже когда до сервера не достучаться, — а ответ на запрос может прийти только с той стороны.
/// Пока интернета нет — перепроверяем каждые 5 с, чтобы быстро сказать «вернулся».
/// Короткие «моргания» (подключение VPN, переключение Wi-Fi) не считаются: пропажа должна длиться ≥ 6 с.
/// </summary>
public sealed class NetWatcher : IDisposable
{
    static readonly string[] Targets = { "http://ya.ru/", "http://www.gstatic.com/generate_204", "http://cp.cloudflare.com/" };
    // соединение держим открытым между проверками: тогда время запроса — это честный путь туда-обратно
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30), UseCookies = false });
    const int LostAfterMs = 6000;

    readonly Dispatcher _ui;
    readonly DispatcherTimer _timer;
    bool _checking;
    long _failSince = -1;

    public bool Online { get; private set; } = true;
    public DateTime LostAt { get; private set; }
    /// <summary>(online, сколько не было интернета)</summary>
    public event Action<bool, TimeSpan>? Changed;

    public NetWatcher(Dispatcher ui)
    {
        _ui = ui;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await Check();
        NetworkChange.NetworkAddressChanged += OnNetChange;
        NetworkChange.NetworkAvailabilityChanged += OnNetChange;
        _ = Check();
    }

    void OnNetChange(object? s, EventArgs e) => _ui.BeginInvoke(() =>
    {
        // адреса сменились — проверим чуть позже, когда сеть устаканится
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Start();
    });

    async Task Check()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            bool ok = await Reachable();
            long now = Environment.TickCount64;
            if (ok)
            {
                _failSince = -1;
                if (!Online) { Online = true; Changed?.Invoke(true, DateTime.UtcNow - LostAt); }
                _timer.Stop();
            }
            else
            {
                if (_failSince < 0) _failSince = now;
                if (Online && now - _failSince >= LostAfterMs)
                {
                    Online = false; LostAt = DateTime.UtcNow - TimeSpan.FromMilliseconds(now - _failSince);
                    Changed?.Invoke(false, TimeSpan.Zero);
                }
                _timer.Interval = TimeSpan.FromSeconds(Online ? 2 : 5); // пока подтверждаем пропажу — чаще
                _timer.Start();
            }
        }
        finally { _checking = false; }
    }

    static async Task<bool> Reachable()
    {
        foreach (var u in Targets)
            if (await Ping(u, 3000) >= 0) return true;
        return false;
    }

    /// <summary>Время HTTP-запроса HEAD в мс (любой ответ сервера = связь есть); -1 — не удалось.</summary>
    public static async Task<int> Ping(string url = "http://ya.ru/", int timeoutMs = 1500)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return -1; }
    }

    public void Dispose()
    {
        _timer.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetChange;
        NetworkChange.NetworkAvailabilityChanged -= OnNetChange;
    }
}
