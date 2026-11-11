using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Island.UI;

public partial class IslandWindow
{
    TextBox? _streamBox;
    readonly StringBuilder _pending = new();
    DispatcherTimer? _flush;

    void ChatInput_TextChanged(object s, TextChangedEventArgs e) =>
        ChatPlaceholder.Visibility = ChatInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void ChatInput_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int i = ChatInput.CaretIndex;
            ChatInput.Text = ChatInput.Text.Insert(i, Environment.NewLine);
            ChatInput.CaretIndex = i + Environment.NewLine.Length;
        }
        else SendChat();
        e.Handled = true;
    }

    void ChatSend_Click(object s, RoutedEventArgs e)
    {
        if (_chat.Busy) { _chat.Cancel(); return; }
        SendChat();
    }

    void ChatNew_Click(object s, RoutedEventArgs e)
    {
        _chat.Reset();
        ChatList.Children.Clear();
        ChatList.Children.Add(ChatHint);
        ChatInput.Focus();
    }

    async void SendChat()
    {
        var text = ChatInput.Text.Trim();
        if (text.Length == 0 || _chat.Busy) return;
        ChatInput.Clear();
        ChatList.Children.Remove(ChatHint);
        AddBubble(text, user: true);
        _streamBox = AddBubble("", user: false);
        ChatSend.Content = "";
        MascotBig.Mood = Mood.Thinking;
        UpdateCompact();

        // кусочки текста копим и выводим раз в 50 мс — меньше перерисовок
        _flush ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => FlushStream(), Dispatcher);
        _flush.Start();
        try
        {
            var final = await _chat.SendAsync(text, chunk => { lock (_pending) _pending.Append(chunk); });
            FlushStream();
            if (_streamBox != null) _streamBox.Text = final;
            MascotBig.Mood = Mood.Happy;
        }
        catch (Exception ex)
        {
            FlushStream();
            if (_streamBox != null) { _streamBox.Text = ex.Message; _streamBox.Foreground = new SolidColorBrush(Color.FromRgb(255, 105, 97)); }
            MascotBig.Mood = Mood.Alert;
        }
        finally
        {
            _flush.Stop();
            _streamBox = null;
            ChatSend.Content = "";
            UpdateCompact();
            if (!_expanded) Toast("Ответ готов", null, Mood.Happy, 3);
        }
    }

    void FlushStream()
    {
        string add;
        lock (_pending) { add = _pending.ToString(); _pending.Clear(); }
        if (add.Length == 0 || _streamBox == null) return;
        _streamBox.AppendText(add);
        ChatScroll.ScrollToEnd();
    }

    TextBox AddBubble(string text, bool user)
    {
        var tb = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Brushes.White, FontSize = 13.5, CaretBrush = Brushes.Transparent,
            SelectionBrush = new SolidColorBrush(Color.FromArgb(120, 10, 132, 255)), Padding = new Thickness(0),
        };
        var b = new Border
        {
            Child = tb, CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 8, 12, 8), MaxWidth = 520,
            Margin = new Thickness(user ? 80 : 0, 4, user ? 0 : 60, 4),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = user ? (Brush)FindResource("Accent") : (Brush)FindResource("Card"),
        };
        if (!user && text.Length == 0) tb.Text = "";
        ChatList.Children.Add(b);
        ChatScroll.ScrollToEnd();
        return tb;
    }
}
