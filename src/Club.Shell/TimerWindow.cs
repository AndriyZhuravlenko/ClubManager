using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Club.Shared;

namespace Club.Shell;

/// <summary>
/// Компактний таймер у кутку екрана. У звичайному стані — лише напівпрозорий час;
/// кнопки «Програми» і «Завершити» з'являються, коли навести мишу. Таймер можна
/// перетягнути мишею в будь-яке місце екрана.
/// </summary>
public class TimerWindow : Window
{
    private readonly TextBlock _text = new() { FontSize = 15, Foreground = Brushes.White, Margin = new Thickness(10, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _collapse = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private double? _anchorRight;   // правий край вікна: при розгортанні вікно росте ліворуч, не виходячи за екран
    private readonly Border _border;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _endsAtUtc;
    private SessionKind _kind;

    // Автозавершення через неактивність (клієнтські й гостьові сеанси)
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IdleWarning = TimeSpan.FromMinutes(1);
    private bool _idleWarned;
    private bool _idleLoggingOut;

    public TimerWindow()
    {
        Title = "Club timer";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // не забирає фокус у гри
        Background = Brushes.Transparent;
        AllowsTransparency = true;

        var finish = new Button { Content = "Завершити", FontSize = 13, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 8, 4), Focusable = false };
        finish.Click += async (_, _) => await FinishAsync();

        var apps = new Button { Content = "Програми", FontSize = 13, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 6, 4), Focusable = false };
        apps.Click += (_, _) => LauncherWindow.ShowLauncher();

        _buttons.Children.Add(apps);
        _buttons.Children.Add(finish);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_text);
        row.Children.Add(_buttons);

        _border = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x19, 0x17)),
            Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = "Наведіть, щоб побачити кнопки. Перетягніть, щоб перемістити."
        };
        Content = _border;
        Opacity = 0.6;

        // Наведення миші — показати кнопки; мишу прибрали — через 1,5 с згорнути
        MouseEnter += (_, _) => { _collapse.Stop(); Expand(true); };
        MouseLeave += (_, _) => _collapse.Start();
        _collapse.Tick += (_, _) => { _collapse.Stop(); Expand(false); };

        // Перетягування мишею
        _border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
            DragMove();
            _anchorRight = Left + ActualWidth;
        };

        _tick.Tick += (_, _) => Refresh();
        Loaded += (_, _) => PlaceInitially();
        SizeChanged += (_, _) => KeepRightEdge();
    }

    private void Expand(bool expanded)
    {
        _buttons.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        Opacity = expanded ? 1.0 : 0.6;
    }

    private static bool FindButton(DependencyObject element)
    {
        for (var e = element; e != null; e = VisualTreeHelper.GetParent(e))
            if (e is Button) return true;
        return false;
    }

    public void Start(DateTime? endsAtUtc, SessionKind kind)
    {
        _endsAtUtc = endsAtUtc;
        _kind = kind;
        _idleWarned = false;
        _idleLoggingOut = false;
        Refresh();
        if (!IsVisible) Show();
        _tick.Start();
    }

    public new void Hide()
    {
        _tick.Stop();
        base.Hide();
    }

    private void Refresh()
    {
        CheckIdle();

        if (_kind == SessionKind.Service) { _text.Text = "Службовий сеанс"; return; }
        if (_endsAtUtc == null || _kind == SessionKind.None) { _text.Text = "Сеанс без обмеження"; return; }

        var left = _endsAtUtc.Value - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        _text.Text = "Залишилось " + (left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss"));

        // Останні 5 хвилин — червоний фон
        _border.Background = left <= TimeSpan.FromMinutes(5)
            ? new SolidColorBrush(Color.FromArgb(0xDD, 0xA3, 0x2D, 0x2D))
            : new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x19, 0x17));
    }

    /// <summary>
    /// Якщо користувач довго нічого не робить (пішов або заблокував Windows через Win+L),
    /// сеанс завершується, щоб ПК звільнився для інших. За хвилину — попередження.
    /// </summary>
    private void CheckIdle()
    {
        if (_kind == SessionKind.Service || _idleLoggingOut) return;

        var idle = GetIdleTime();
        if (idle < IdleLimit - IdleWarning)
        {
            _idleWarned = false;
            return;
        }

        if (!_idleWarned)
        {
            _idleWarned = true;
            MessageWindow.ShowText("Ви неактивні майже 30 хвилин. Якщо не продовжите роботу, через 1 хвилину сеанс буде завершено.");
        }

        if (idle >= IdleLimit)
        {
            _idleLoggingOut = true;
            _ = AgentClient.SendAsync(new ShellRequest("logout-idle"));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Скільки часу минуло від останнього натискання клавіші чи руху миші.</summary>
    private static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>Клієнт сам завершує сеанс — невикористаний час залишається на балансі.</summary>
    private async Task FinishAsync()
    {
        // Про баланс згадуємо лише клієнтам — у гостьових і службових сеансах балансу немає
        var question = _kind switch
        {
            SessionKind.Client => "Завершити сеанс? Невикористаний час залишиться на вашому балансі.",
            SessionKind.Service => "Завершити службовий сеанс?",
            _ => "Завершити сеанс?"
        };
        var answer = MessageBox.Show(this, question, "Завершення сеансу", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var result = await AgentClient.SendAsync(new ShellRequest("logout"));
        if (!result.Ok)
            MessageBox.Show(this, result.Error ?? "Не вдалося завершити сеанс", "Помилка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // Спочатку — правий верхній кут; далі вікно тримає свій правий край на місці
    private void PlaceInitially()
    {
        if (_anchorRight != null) return;
        var area = SystemParameters.WorkArea;
        _anchorRight = area.Right - 12;
        Top = area.Top + 12;
        KeepRightEdge();
    }

    private void KeepRightEdge()
    {
        if (_anchorRight == null) return;
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, _anchorRight.Value - ActualWidth);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        base.OnClosing(e);
    }
}
