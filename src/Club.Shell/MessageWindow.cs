using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Club.Shell;

/// <summary>Повідомлення від адміністратора поверх усіх вікон; зникає саме через 15 с.</summary>
public class MessageWindow : Window
{
    private MessageWindow(string text)
    {
        Title = "Повідомлення адміністратора";
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;

        var ok = new Button { Content = "OK", Width = 90, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        ok.Click += (_, _) => Close();

        var panel = new StackPanel { Margin = new Thickness(24), MaxWidth = 480 };
        panel.Children.Add(new TextBlock { Text = text, FontSize = 18, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black });
        panel.Children.Add(ok);
        Content = panel;

        var autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        autoClose.Tick += (_, _) => { autoClose.Stop(); Close(); };
        autoClose.Start();
    }

    public static void ShowText(string text) => new MessageWindow(text).Show();
}
