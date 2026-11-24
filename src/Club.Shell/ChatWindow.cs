using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Club.Shared;

namespace Club.Shell;

/// <summary>
/// Чат користувача з адміністратором. Відкривається кнопкою «Чат» на таймері; коли адміністратор
/// відповідає, вікно відкривається саме (без перехоплення фокуса, щоб не заважати грі).
/// Переписка існує лише протягом сеансу.
/// </summary>
public class ChatWindow : Window
{
    private static ChatWindow? _instance;
    private static List<ChatLine> _lines = [];
    private static int _seenAdminMessages;

    /// <summary>Скільки відповідей адміністратора користувач ще не бачив (для кнопки «Чат»).</summary>
    public static int Unread { get; private set; }
    public static event Action? UnreadChanged;

    private readonly StackPanel _list = new() { Margin = new Thickness(10) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input = new()
    {
        FontSize = 14, Padding = new Thickness(6), TextWrapping = TextWrapping.Wrap, AcceptsReturn = false,
        MaxLength = ChatLine.MaxLength, MinHeight = 34, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly Button _send = new() { Content = "Надіслати", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { Foreground = Brushes.Firebrick, FontSize = 12, Margin = new Thickness(10, 0, 10, 4), TextWrapping = TextWrapping.Wrap };

    private ChatWindow()
    {
        Title = "Чат з адміністратором";
        Width = 380;
        Height = 480;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        WindowStyle = WindowStyle.ToolWindow;
        Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF9));

        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var inputRow = new DockPanel { Margin = new Thickness(10, 4, 10, 10), LastChildFill = true };
        DockPanel.SetDock(_send, Dock.Right);
        inputRow.Children.Add(_send);
        inputRow.Children.Add(_input);

        var root = new DockPanel();
        var hint = new TextBlock
        {
            Text = "Напишіть адміністратору — відповідь з'явиться тут.",
            Foreground = Brushes.DimGray, FontSize = 12, Margin = new Thickness(10, 8, 10, 0), TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(inputRow, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(inputRow);
        root.Children.Add(_status);
        root.Children.Add(_scroll);
        Content = root;

        _send.Click += async (_, _) => await SendAsync();
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SendAsync(); }
        };
        Activated += (_, _) => MarkRead();

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
        Render();
    }

    /// <summary>Відкрити чат (кнопка на таймері).</summary>
    public static void Open()
    {
        _instance ??= new ChatWindow();
        _instance.Show();
        _instance.Activate();
        _instance._input.Focus();
        MarkRead();
    }

    /// <summary>Новий стан переписки від агента. Нова відповідь адміністратора відкриває вікно без фокуса.</summary>
    public static void SetLines(List<ChatLine>? lines)
    {
        _lines = lines ?? new List<ChatLine>();
        var adminCount = _lines.Count(l => l.FromAdmin);
        if (adminCount < _seenAdminMessages) _seenAdminMessages = 0;     // новий сеанс
        var hasNew = adminCount > _seenAdminMessages;

        if (hasNew && (_instance == null || !_instance.IsVisible))
        {
            _instance ??= new ChatWindow();
            _instance.ShowActivated = false;      // не забирати фокус у гри
            _instance.Show();
            _instance.ShowActivated = true;
        }
        _instance?.Render();

        Unread = adminCount - _seenAdminMessages;
        if (_instance is { IsActive: true }) MarkRead();
        UnreadChanged?.Invoke();
    }

    /// <summary>Кінець сеансу: закрити вікно і забути переписку.</summary>
    public static void Reset()
    {
        _lines = [];
        _seenAdminMessages = 0;
        Unread = 0;
        UnreadChanged?.Invoke();
        if (_instance != null)
        {
            _instance._input.Text = "";
            _instance._status.Text = "";
            _instance.Hide();
            _instance.Render();
        }
    }

    private static void MarkRead()
    {
        _seenAdminMessages = _lines.Count(l => l.FromAdmin);
        if (Unread == 0) return;
        Unread = 0;
        UnreadChanged?.Invoke();
    }

    private void Render()
    {
        _list.Children.Clear();
        if (_lines.Count == 0)
            _list.Children.Add(new TextBlock { Text = "Повідомлень ще немає.", Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) });

        foreach (var line in _lines)
        {
            var bubble = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(line.FromAdmin ? 0 : 50, 4, line.FromAdmin ? 50 : 0, 4),
                HorizontalAlignment = line.FromAdmin ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Background = line.FromAdmin ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xE5, 0xE4)),
                BorderThickness = new Thickness(line.FromAdmin ? 1 : 0)
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = $"{(line.FromAdmin ? "Адміністратор" : "Ви")} · {line.At.ToLocalTime():HH:mm}",
                FontSize = 11, Foreground = line.FromAdmin ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(0xE0, 0xE7, 0xFF))
            });
            panel.Children.Add(new TextBlock
            {
                Text = line.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap,
                Foreground = line.FromAdmin ? Brushes.Black : Brushes.White
            });
            bubble.Child = panel;
            _list.Children.Add(bubble);
        }
        _scroll.ScrollToEnd();
    }

    private async Task SendAsync()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        _send.IsEnabled = false;
        _status.Text = "";
        var result = await AgentClient.SendAsync(new ShellRequest("chat", Text: text));
        _send.IsEnabled = true;
        if (result.Ok) _input.Text = "";
        else _status.Text = result.Error ?? "Не вдалося надіслати повідомлення";
        _input.Focus();
    }

    // Хрестик лише ховає вікно: переписка зберігається до кінця сеансу
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
