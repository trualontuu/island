using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Island.Services;

namespace Island.UI;

public partial class IslandWindow
{
    sealed class ClipItem
    {
        public string? Text;
        public BitmapSource? Image, Thumb;
        public DateTime Time = DateTime.Now;
    }

    readonly List<ClipItem> _clips = new();
    readonly List<string> _shelf = new();
    bool _clipDirty = true;

    // ---------------- история буфера ----------------
    void OnClipboardChanged()
    {
        if (DateTime.UtcNow < ClipHelper.OwnWriteUntil) return;
        // читаем буфер чуть позже — владелец часто ещё держит его открытым
        Dispatcher.BeginInvoke(ReadClipboard, DispatcherPriority.Background);
    }

    void ReadClipboard()
    {
        try
        {
            ClipItem? item = null;
            if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(t) || t.Length > 200_000) return;
                _clips.RemoveAll(c => c.Text == t);
                item = new ClipItem { Text = t };
            }
            else if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img == null) return;
                double k = Math.Min(1, 220.0 / Math.Max(img.PixelWidth, img.PixelHeight));
                var thumb = new TransformedBitmap(img, new ScaleTransform(k, k));
                thumb.Freeze();
                var full = new WriteableBitmap(img); full.Freeze();
                item = new ClipItem { Image = full, Thumb = thumb };
                // полноразмерные картинки держим только для 5 последних — экономим память
                foreach (var old in _clips.Where(c => c.Image != null).Skip(4)) old.Image = null;
            }
            if (item == null) return;
            _clips.Insert(0, item);
            if (_clips.Count > Math.Max(5, _s.ClipboardHistory)) _clips.RemoveRange(_s.ClipboardHistory, _clips.Count - _s.ClipboardHistory);
            _clipDirty = true;
            if (_expanded && _page == "shelf") RefreshShelf();
        }
        catch { }
    }

    void RefreshShelf()
    {
        if (_clipDirty)
        {
            _clipDirty = false;
            ClipList.Children.Clear();
            if (_clips.Count == 0) ClipList.Children.Add(Note("Пока пусто — скопируй что-нибудь"));
            foreach (var c in _clips)
            {
                FrameworkElement content;
                if (c.Text != null)
                    content = new TextBlock { Text = c.Text.Replace("\r", "").Replace('\n', ' ').Trim(), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36, TextWrapping = TextWrapping.Wrap };
                else
                    content = new Image { Source = c.Thumb, MaxHeight = 70, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform };
                var b = new Button { Style = (Style)FindResource("Row"), Content = content, ToolTip = c.Text != null && c.Text.Length > 60 ? c.Text[..Math.Min(600, c.Text.Length)] : null };
                var item = c;
                b.Click += (_, _) =>
                {
                    if (item.Text != null) ClipHelper.SetText(item.Text);
                    else if (item.Image != null) ClipHelper.SetImage(item.Image);
                    else if (item.Thumb != null) ClipHelper.SetImage(item.Thumb);
                    _clips.Remove(item); _clips.Insert(0, item); _clipDirty = true;
                    Toast("Скопировано", "", Mood.Happy, 1.5);
                };
                ClipList.Children.Add(b);
            }
        }

        ShelfList.Children.Clear();
        ShelfHint.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var path in _shelf) ShelfList.Children.Add(ShelfChip(path));
    }

    void ClipClear_Click(object s, RoutedEventArgs e) { _clips.Clear(); _clipDirty = true; RefreshShelf(); Native_Trim(); }

    static void Native_Trim() => Island.Core.Native.TrimMemory();

    // ---------------- полка файлов ----------------
    void AddToShelf(IEnumerable<string> files)
    {
        foreach (var f in files) if (!_shelf.Contains(f)) _shelf.Add(f);
        if (_expanded && _page == "shelf") RefreshShelf();
        else Toast($"На полке: {_shelf.Count}", "", Mood.Happy, 1.8);
    }

    FrameworkElement ShelfChip(string path)
    {
        bool exists = File.Exists(path) || Directory.Exists(path);
        var sp = new StackPanel { Width = 74, Margin = new Thickness(4) };
        sp.Children.Add(new Image { Source = FileIcon(path), Width = 32, Height = 32, Margin = new Thickness(0, 4, 0, 4), Opacity = exists ? 1 : 0.35 });
        sp.Children.Add(new TextBlock { Text = Path.GetFileName(path.TrimEnd('\\')), FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, TextWrapping = TextWrapping.Wrap });
        var bd = new Border { Child = sp, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path + "\nПеретащи куда нужно · двойной клик — открыть · ПКМ — убрать" };
        bd.MouseEnter += (_, _) => bd.Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        bd.MouseLeave += (_, _) => bd.Background = Brushes.Transparent;

        Point down = default;
        bd.PreviewMouseLeftButtonDown += (_, e) =>
        {
            down = e.GetPosition(bd);
            if (e.ClickCount == 2) { ControlService.OpenUri(path); e.Handled = true; }
        };
        bd.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || !exists) return;
            var p = e.GetPosition(bd);
            if (Math.Abs(p.X - down.X) < 5 && Math.Abs(p.Y - down.Y) < 5) return;
            _pinned = true; // не сворачиваемся во время перетаскивания
            var res = DragDrop.DoDragDrop(bd, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move);
            _pinned = false;
            if (res == DragDropEffects.Move || !File.Exists(path) && !Directory.Exists(path)) { _shelf.Remove(path); RefreshShelf(); }
        };
        bd.MouseRightButtonUp += (_, e) => { _shelf.Remove(path); RefreshShelf(); e.Handled = true; };
        return bd;
    }

    void ShelfClear_Click(object s, RoutedEventArgs e) { _shelf.Clear(); RefreshShelf(); }

    void ShelfCopy_Click(object s, RoutedEventArgs e)
    {
        var ex = _shelf.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (ex.Length == 0) return;
        ClipHelper.SetFiles(ex);
        Toast($"Скопировано файлов: {ex.Length}", "", Mood.Happy, 1.8);
    }
}
