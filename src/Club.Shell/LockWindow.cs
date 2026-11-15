using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Club.Shared;

namespace Club.Shell;

/// <summary>
/// Повноекранне вікно "Робоче місце вільне" з формою входу клієнта.
/// Розташоване поверх усіх вікон; звичайним способом (Alt+F4, хрестик) не закривається.
/// </summary>
public class LockWindow : Window
{
    private readonly TextBlock _clock = new() { FontSize = 64, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 24) };
    private readonly TextBlock _note = new() { FontSize = 20, Foreground = new SolidColorBrush(Color.FromRgb(0xFA, 0xC7, 0x75)), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 24, 0, 0), MaxWidth = 600 };
    private readonly TextBox _login = new() { FontSize = 18, Padding = new Thickness(6) };
    private readonly PasswordBox _password = new() { FontSize = 18, Padding = new Thickness(6) };
    private readonly Button _loginButton = new() { Content = "Увійти", FontSize = 18, Padding = new Thickness(6), Margin = new Thickness(0, 16, 0, 0), IsDefault = true };
    private readonly TextBlock _error = new() { FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x95, 0x95)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public LockWindow()
    {
        Title = "Club";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Topmost = true;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x19, 0x17));

        var form = new StackPanel { Width = 340 };
        form.Children.Add(Label("Логін"));
        form.Children.Add(_login);
        form.Children.Add(Label("Пароль"));
        form.Children.Add(_password);
        form.Children.Add(_loginButton);
        form.Children.Add(_error);

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(_clock);
        panel.Children.Add(new TextBlock { Text = "Робоче місце вільне", FontSize = 40, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "Увійдіть у свій обліковий запис або зверніться до адміністратора", FontSize = 18, Foreground = Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 28) });
        panel.Children.Add(form);
        panel.Children.Add(_note);
        Content = panel;

        _loginButton.Click += async (_, _) => await LoginAsync();

        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => _clock.Text = DateTime.Now.ToString("HH:mm");
        tick.Start();
        _clock.Text = DateTime.Now.ToString("HH:mm");

        // Якщо користувач переключився на інше вікно — повертаємо екран блокування наверх
        Deactivated += (_, _) =>
        {
            if (IsVisible) Dispatcher.InvokeAsync(() => { Topmost = true; Activate(); });
        };
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = 14, Foreground = Brushes.LightGray, Margin = new Thickness(0, 10, 0, 4) };

    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(_login.Text) || _password.Password.Length == 0)
        {
            _error.Text = "Введіть логін і пароль";
            return;
        }

        _loginButton.IsEnabled = false;
        _error.Text = "Перевірка…";
        var result = await AgentClient.SendAsync(new ShellRequest("login", _login.Text.Trim(), _password.Password));
        _loginButton.IsEnabled = true;
        _password.Clear();

        // Якщо вхід успішний — агент надішле стан "розблоковано", і це вікно сховається само
        _error.Text = result.Ok ? "" : result.Error ?? "Помилка входу";
    }

    public void ShowLocked(string? note)
    {
        _note.Text = note ?? "";
        if (!IsVisible)
        {
            // Нове блокування — очищаємо форму від даних попереднього клієнта
            _login.Clear();
            _password.Clear();
            _error.Text = "";
            Show();
        }
        WindowState = WindowState.Maximized;
        Activate();
        Keyboard.Focus(_login);
    }

    public void Unlock() => Hide();

    // Закрити вікно не можна — тільки сховати (коли починається сеанс)
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        base.OnClosing(e);
    }
}
