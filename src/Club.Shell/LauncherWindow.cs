using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Club.Shared;

namespace Club.Shell;

/// <summary>
/// Вітрина дозволених програм: адміністратор задає каталог у панелі,
/// клієнт запускає програми звідси однією кнопкою.
/// </summary>
public class LauncherWindow : Window
{
    private static LauncherWindow? _instance;
    private static List<AppInfo> _apps = [];

    private readonly WrapPanel _tiles = new() { Margin = new Thickness(16) };

    private LauncherWindow()
    {
        Title = "Програми клубу";
        Width = 720;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x29, 0x25, 0x24));
        Content = new ScrollViewer { Content = _tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Render();
    }

    public static void SetApps(List<AppInfo> apps)
    {
        _apps = apps;
        _instance?.Render();
    }

    public static void ShowLauncher()
    {
        _instance ??= new LauncherWindow();
        _instance.Render();
        _instance.Show();
        _instance.Activate();
    }

    public static void HideIfOpen() => _instance?.Hide();

    private void Render()
    {
        _tiles.Children.Clear();
        if (_apps.Count == 0)
        {
            _tiles.Children.Add(new TextBlock { Text = "Адміністратор ще не додав жодної програми", Foreground = Brushes.LightGray, FontSize = 16 });
            return;
        }

        foreach (var app in _apps)
        {
            var tile = new Button
            {
                Width = 200,
                Height = 90,
                Margin = new Thickness(8),
                FontSize = 16,
                Content = new TextBlock { Text = app.Name, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                ToolTip = app.Path
            };
            tile.Click += (_, _) => Launch(app);
            _tiles.Children.Add(tile);
        }
    }

    private void Launch(AppInfo app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(app.Path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(app.Path) ?? ""
            });
            Hide();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не вдалося запустити «{app.Name}»: {ex.Message}", "Помилка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Закриття вікна лише ховає його — вітрину можна відкрити знову кнопкою «Програми»
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
