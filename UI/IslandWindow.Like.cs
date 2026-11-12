using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Island.Services;

namespace Island.UI;

/// <summary>
/// Сердечко у плеера: лайк текущего трека в Яндекс Музыке (кнопка на главной и Ctrl+Alt+L).
/// Видно, только пока играет Яндекс Музыка. Без входа клик открывает настройки со входом по коду.
/// </summary>
public partial class IslandWindow
{
    string? _likeTrack;           // "trackId:albumId" текущего трека, когда нашли
    string _likeFor = "";         // для какого трека (название|исполнитель) это значение
    bool _likeBusy;
    static readonly SolidColorBrush LikeRed = new(Color.FromRgb(255, 69, 88));

    bool LikeAvailable => _media.Info.HasMedia && YandexMusic.IsYandexSource(_media.Info);

    /// <summary>Сменился трек — узнаём его в каталоге и лайкнут ли он. Сеть — только если открыта главная или нажали лайк.</summary>
    async Task<string?> ResolveLike()
    {
        var m = _media.Info;
        var key = m.Title + "|" + m.Artist;
        if (key == _likeFor && _likeTrack != null) return _likeTrack;
        if (!YandexMusic.LoggedIn || !LikeAvailable) return null;
        try
        {
            await YandexMusic.RefreshLikes();
            var t = await YandexMusic.Resolve(m);
            if (_media.Info.Title + "|" + _media.Info.Artist != key) return null; // пока искали — трек сменился
            _likeFor = key; _likeTrack = t;
            return t;
        }
        catch { return null; }
    }

    async void UpdateLikeButton()
    {
        if (!LikeAvailable) { LikeBtn.Visibility = Visibility.Collapsed; return; }
        LikeBtn.Visibility = Visibility.Visible;
        var key = _media.Info.Title + "|" + _media.Info.Artist;
        if (key != _likeFor) { _likeTrack = null; ShowLike(false); }
        if (!YandexMusic.LoggedIn) { ShowLike(false, "Лайк в Яндекс Музыке — нужно один раз войти (откроются настройки)"); return; }
        var t = await ResolveLike();
        if (key != _media.Info.Title + "|" + _media.Info.Artist) return;
        if (t == null) { ShowLike(false, "Этого трека не нашлось в каталоге Яндекс Музыки"); return; }
        ShowLike(YandexMusic.IsLiked(t));
    }

    void ShowLike(bool liked, string? tip = null)
    {
        LikeBtn.Content = liked ? "" : "";
        if (liked) LikeBtn.Foreground = LikeRed; else LikeBtn.ClearValue(ForegroundProperty);
        var hk = string.IsNullOrWhiteSpace(_s.HotkeyLike) ? "" : "\n" + _s.HotkeyLike;
        LikeBtn.ToolTip = (tip ?? (liked ? "В «Мне нравится» — нажми, чтобы убрать" : "Добавить в «Мне нравится»")) + hk;
    }

    void Like_Click(object sender, RoutedEventArgs e) => ToggleLike();

    async void ToggleLike()
    {
        if (!LikeAvailable) { Toast("Лайк — только для Яндекс Музыки", "", Mood.Alert, 2); return; }
        if (!YandexMusic.LoggedIn) { Toast("Войди в Яндекс в настройках", "", Mood.Alert, 2.5); OpenSettings("yandex"); return; }
        if (_likeBusy) return;
        _likeBusy = true;
        try
        {
            var t = await ResolveLike();
            if (t == null) { Toast("Трек не нашёлся в Яндекс Музыке", "", Mood.Alert, 2.5); return; }
            bool now = await YandexMusic.SetLike(t, !YandexMusic.IsLiked(t));
            if (!YandexMusic.LoggedIn) return;
            Toast(now ? "Добавлено в «Мне нравится»" : "Убрано из «Мне нравится»", now ? "" : "", now ? Mood.Happy : Mood.Alert, 1.8);
            ShowLike(now);
        }
        catch (UnauthorizedAccessException) { YandexMusic.Logout(); Toast("Яндекс просит войти заново", "", Mood.Alert, 3); ShowLike(false); }
        catch { Toast("Не получилось — нет связи с Яндексом", "", Mood.Alert, 2.5); }
        finally { _likeBusy = false; }
    }

    /// <summary>Вошли или вышли в настройках — перерисовать сердечко.</summary>
    public void OnYandexAccountChanged() { _likeFor = ""; _likeTrack = null; if (_expanded && _page == "home") UpdateLikeButton(); }
}
