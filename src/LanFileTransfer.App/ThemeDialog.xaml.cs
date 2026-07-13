using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LanFileTransfer.App;

public partial class ThemeDialog : Window
{
    private readonly MessageBoxButton _buttons;
    private MessageBoxResult _result;

    private ThemeDialog(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        string? yesText,
        string? noText,
        string? cancelText,
        bool dangerous,
        double? width)
    {
        InitializeComponent();
        _buttons = buttons;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        if (width is { } requestedWidth) Width = requestedWidth;
        if (owner?.IsVisible == true)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        ConfigureIcon(image);
        ConfigureButtons(buttons, yesText, noText, cancelText, dangerous);
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        string? yesText = null,
        string? noText = null,
        string? cancelText = null,
        bool dangerous = false,
        double? width = null)
    {
        var dialog = new ThemeDialog(owner, message, title, buttons, image, yesText, noText, cancelText, dangerous, width);
        dialog.ShowDialog();
        return dialog._result;
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        switch (image)
        {
            case MessageBoxImage.Error:
                IconText.Text = "×";
                IconText.Foreground = BrushFrom("#B93A3A");
                IconCircle.Background = BrushFrom("#FBE8E8");
                break;
            case MessageBoxImage.Warning:
                IconText.Text = "!";
                IconText.Foreground = BrushFrom("#A56608");
                IconCircle.Background = BrushFrom("#FFF2D7");
                break;
            case MessageBoxImage.Question:
                IconText.Text = "?";
                break;
            default:
                IconText.Text = "i";
                break;
        }
    }

    private void ConfigureButtons(MessageBoxButton buttons, string? yesText, string? noText, string? cancelText, bool dangerous)
    {
        CancelButton.Visibility = buttons == MessageBoxButton.YesNoCancel || buttons == MessageBoxButton.OKCancel ? Visibility.Visible : Visibility.Collapsed;
        NoButton.Visibility = buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? Visibility.Visible : Visibility.Collapsed;

        YesButton.Content = yesText ?? (buttons is MessageBoxButton.OK or MessageBoxButton.OKCancel ? "确定" : "是");
        NoButton.Content = noText ?? "否";
        CancelButton.Content = cancelText ?? "取消";
        if (dangerous) YesButton.Style = (Style)FindResource("DangerButton");
    }

    private static SolidColorBrush BrushFrom(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        _result = _buttons is MessageBoxButton.OK or MessageBoxButton.OKCancel ? MessageBoxResult.OK : MessageBoxResult.Yes;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.No;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.Cancel;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SetDismissResult();
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SetDismissResult();
        Close();
    }

    private void SetDismissResult()
    {
        _result = _buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel or MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
    }
}
