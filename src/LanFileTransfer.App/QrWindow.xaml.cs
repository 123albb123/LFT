using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LanFileTransfer.App.Services;
using QRCoder;

namespace LanFileTransfer.App;

public partial class QrWindow : Window
{
    private readonly string _address;

    public QrWindow(string address, string heading = "扫码打开")
    {
        InitializeComponent();
        _address = address;
        HeadingText.Text = heading;
        Title = heading;
        AddressTextBox.Text = address;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(address, QRCodeGenerator.ECCLevel.Q);
        var bytes = new PngByteQRCode(data).GetGraphic(10, [20, 33, 27], [255, 255, 255], drawQuietZones: true);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        QrImage.Source = image;
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ClipboardService.TrySetTextAsync(_address))
        {
            ThemeDialog.Show(this, "Windows 剪贴板正被其他程序占用，请稍后重试。", "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
