using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LanFileTransfer.App.Infrastructure;
using LanFileTransfer.App.Models;
using LanFileTransfer.App.Services;
using Microsoft.Win32;

namespace LanFileTransfer.App;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly PortableConfigStore _config;
    private readonly AppLogService _log;
    private readonly EventHub _events;
    private readonly TransferRegistry _transfers;
    private readonly FileCatalog _catalog;
    private readonly NetworkAddressService _network;
    private readonly HttpFileServer _server;
    private bool _allowClose;
    private bool _isClosing;
    private string? _startupNotice;

    public MainWindow(AppRuntime runtime)
    {
        InitializeComponent();
        DataContext = this;

        _paths = runtime.Paths;
        _config = runtime.Config;
        _log = runtime.Log;
        _events = new EventHub();
        _transfers = new TransferRegistry(_events);
        _network = new NetworkAddressService();
        _catalog = new FileCatalog(_paths, _config, _events, _log);
        _server = new HttpFileServer(_config, _catalog, _network, _events, _transfers, new WebAssetProvider(), _log);

        _catalog.FilesChanged += Catalog_FilesChanged;
        _server.RunningChanged += Server_RunningChanged;
        _log.EntryAdded += Log_EntryAdded;
        DuplicateBehaviorCombo.ItemsSource = new[] { "覆盖", "自动重命名", "拒绝" };
        BindingModeCombo.ItemsSource = new[] { "自动推荐网卡", "指定当前 IP", "所有网卡" };
        LoadSettingsIntoUi(_config.Current);
        RefreshNetworkAddresses();
        RefreshFiles();
        UpdateServiceUi();
        if (_config.RecoveryInfo is { } recovery)
        {
            _startupNotice = recovery.BackupSucceeded
                ? $"配置文件已损坏，已备份为：{Path.GetFileName(recovery.BackupFile!)}。已恢复为默认设置。"
                : "配置文件已损坏，无法备份原文件，但已恢复为默认设置。";
            _log.Error("配置已恢复", recovery.OriginalException);
            if (recovery.BackupException is not null) _log.Warning($"配置备份失败：{recovery.BackupException.Message}");
            if (recovery.SaveException is not null) _log.Warning($"默认配置保存失败：{recovery.SaveException.Message}");
        }
    }

    public ObservableCollection<SharedFileItem> Files { get; } = [];

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitWindowToWorkArea();
        foreach (var entry in _log.Recent)
        {
            AppendLog(entry);
        }

        if (!string.IsNullOrWhiteSpace(_startupNotice))
        {
            MessageBox.Show(this, _startupNotice, "配置已恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await StartServerWithFeedbackAsync();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartServerWithFeedbackAsync();

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _server.StopAsync();
        }
        catch (Exception exception)
        {
            _log.Error("停止服务失败", exception);
            ShowError("停止服务失败", exception.Message);
        }
        UpdateServiceUi();
    }

    private async Task StartServerWithFeedbackAsync(bool allowPortRecovery = true)
    {
        try
        {
            await _server.StartAsync();
        }
        catch (ServerStartException exception) when (exception.Kind == ServerStartFailureKind.PortInUse && allowPortRecovery)
        {
            _log.Error("服务启动失败", exception);
            await OfferPortRecoveryAsync(exception.Port);
        }
        catch (ServerStartException exception)
        {
            _log.Error("服务启动失败", exception);
            ShowError("服务启动失败", exception.Message);
        }
        catch (Exception exception)
        {
            _log.Error("服务启动失败", exception);
            ShowError("服务启动失败", $"无法启动 HTTP 服务。\n\n{exception.Message}");
        }
        UpdateServiceUi();
    }

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要共享的文件" };
        if (dialog.ShowDialog(this) == true)
        {
            _ = ImportFilesAsync(dialog.FileNames);
        }
    }

    private async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        IsEnabled = false;
        try
        {
            foreach (var path in paths)
            {
                try
                {
                    var item = await _catalog.ImportAsync(path);
                    _log.Info($"添加 · {item.Name} · 本机 {GetDisplayIp()}");
                }
                catch (Exception exception)
                {
                    _log.Error($"添加文件失败：{Path.GetFileName(path)}", exception);
                    ShowError("添加文件失败", $"{Path.GetFileName(path)}\n{exception.Message}");
                }
            }
        }
        finally
        {
            IsEnabled = true;
            RefreshFiles();
        }
    }

    private async void DeleteFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesGrid.SelectedItems.Cast<SharedFileItem>().ToArray();
        if (selected.Length == 0) return;
        if (MessageBox.Show(this, $"确定删除选中的 {selected.Length} 个文件吗？此操作不可撤销。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var file in selected)
        {
            try
            {
                await _catalog.DeleteAsync(file.Name);
                _log.Info($"删除 · {file.Name} · 本机 {GetDisplayIp()}");
            }
            catch (Exception exception) { _log.Error($"删除失败：{file.Name}", exception); }
        }
        RefreshFiles();
    }

    private void EditFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not SharedFileItem file) return;
        if (!IsEditableTextFile(file.Name))
        {
            ShowError("不支持直接编辑", "为避免执行危险文件，只允许直接编辑常见文本和配置文件。已知危险或未知类型请使用“打开文件目录”。");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_catalog.ResolveExisting(file.Name)) { UseShellExecute = true });
            _log.Info($"编辑 · {file.Name} · 本机 {GetDisplayIp()}");
        }
        catch (Exception exception) { ShowError("无法打开文件", exception.Message); }
    }

    private void OpenDirectoryButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_catalog.DirectoryPath);

    private async void CopyFileLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not SharedFileItem file) return;
        await CopyTextWithFeedbackAsync($"{GetBaseAddress()}/{Uri.EscapeDataString(file.Name)}");
    }

    private async void CopyAddressButton_Click(object sender, RoutedEventArgs e)
    {
        await CopyTextWithFeedbackAsync(GetWebAddress());
    }

    private void OpenWebButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_server.IsRunning) { ShowError("服务未运行", "请先启动服务。"); return; }
        Process.Start(new ProcessStartInfo(GetWebAddress()) { UseShellExecute = true });
    }

    private void QrButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new QrWindow(GetWebAddress(), "扫码打开 Web 页面") { Owner = this };
        window.ShowDialog();
    }

    private void FilesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) is DataGridRow row)
        {
            row.IsSelected = true;
            FilesGrid.Focus();
        }
    }

    private void FileContextMenu_Opening(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is null)
        {
            e.Handled = true;
        }
    }

    private void ContextCopyFileLink_Click(object sender, RoutedEventArgs e) => CopyFileLinkButton_Click(sender, e);

    private void ContextEditFile_Click(object sender, RoutedEventArgs e) => EditFileButton_Click(sender, e);

    private void ContextDeleteFile_Click(object sender, RoutedEventArgs e) => DeleteFilesButton_Click(sender, e);

    private void ContextOpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not SharedFileItem file) return;
        try
        {
            var path = _catalog.ResolveExisting(file.Name);
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowError("无法打开文件目录", exception.Message);
        }
    }

    private void ContextFileQr_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not SharedFileItem file) return;
        var link = $"{GetBaseAddress()}/{Uri.EscapeDataString(file.Name)}";
        new QrWindow(link, "扫码打开文件") { Owner = this }.ShowDialog();
    }

    private void BrowseDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择共享目录",
            InitialDirectory = _paths.ResolveUploadDirectory(UploadDirectoryTextBox.Text)
        };
        if (dialog.ShowDialog(this) == true) UploadDirectoryTextBox.Text = dialog.FolderName;
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        AppSettings updated;
        try
        {
            if (!int.TryParse(PortTextBox.Text, out var port)) throw new InvalidDataException("端口号格式不正确。");
            if (!decimal.TryParse(MaxUploadGbTextBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var gigabytes) || gigabytes <= 0) throw new InvalidDataException("上传上限必须是大于 0 的数字。");
            var maxBytes = checked((long)(gigabytes * 1024 * 1024 * 1024));
            updated = new AppSettings
            {
                Port = port,
                UploadDirectory = UploadDirectoryTextBox.Text.Trim(),
                LanOnly = LanOnlyCheckBox.IsChecked == true,
                AllowWebUpload = AllowWebUploadCheckBox.IsChecked == true,
                AllowWebDelete = AllowWebDeleteCheckBox.IsChecked == true,
                ReadOnlyMode = ReadOnlyModeCheckBox.IsChecked == true,
                BindingMode = BindingModeCombo.SelectedIndex switch { 1 => BindingMode.Specific, 2 => BindingMode.AllInterfaces, _ => BindingMode.Auto },
                BoundAddress = BindingModeCombo.SelectedIndex == 1 ? GetDisplayIp() : null,
                DuplicateBehavior = DuplicateBehaviorCombo.SelectedIndex switch { 1 => DuplicateBehavior.AutoRename, 2 => DuplicateBehavior.Reject, _ => DuplicateBehavior.Overwrite },
                MaxUploadBytes = maxBytes
            };
            PortableConfigStore.Validate(updated, _paths);
        }
        catch (Exception exception)
        {
            ShowError("设置无效", exception.Message); return;
        }

        var previous = _config.Current;
        var wasRunning = _server.IsRunning;
        var needsRestart = previous.Port != updated.Port || previous.MaxUploadBytes != updated.MaxUploadBytes ||
                           previous.BindingMode != updated.BindingMode || !string.Equals(previous.BoundAddress, updated.BoundAddress, StringComparison.OrdinalIgnoreCase) ||
                           !string.Equals(_paths.ResolveUploadDirectory(previous.UploadDirectory), _paths.ResolveUploadDirectory(updated.UploadDirectory), StringComparison.OrdinalIgnoreCase);
        try
        {
            if (needsRestart && wasRunning) await _server.StopAsync();
            _config.Replace(updated, persist: false);
            if (!string.Equals(previous.UploadDirectory, updated.UploadDirectory, StringComparison.OrdinalIgnoreCase)) _catalog.ChangeDirectory(updated.UploadDirectory);
            if (needsRestart && wasRunning) await _server.StartAsync();
            _config.Replace(updated, persist: true);
            _log.Info("设置已保存。");
            RefreshFiles(); UpdateAddress(); UpdateServiceUi();
        }
        catch (Exception exception)
        {
            _log.Error("应用新设置失败，正在恢复原设置", exception);
            try
            {
                await _server.StopAsync();
                _config.Replace(previous, persist: true);
                _catalog.ChangeDirectory(previous.UploadDirectory);
                if (wasRunning) await _server.StartAsync();
            }
            catch (Exception rollbackException) { _log.Error("恢复原设置失败", rollbackException); }
            LoadSettingsIntoUi(previous); UpdateServiceUi();
            ShowError("保存设置失败", exception.Message);
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_paths.LogsDirectory);

    private void IpAddressCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAddress();

    private void BindingModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAddress();

    private void RefreshNetworkButton_Click(object sender, RoutedEventArgs e) => RefreshNetworkAddresses();

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ContextHintText.Visibility = ActualWidth < 980 ? Visibility.Collapsed : Visibility.Visible;
        LanWarningText.Visibility = ActualWidth < 920 ? Visibility.Collapsed : Visibility.Visible;
        BottomRow.Height = new GridLength(ActualHeight < 690 ? 165 : 176);
        SettingsColumn.Width = ActualWidth < 980 ? new GridLength(1, GridUnitType.Star) : new GridLength(0.95, GridUnitType.Star);
        LogColumn.Width = ActualWidth < 980 ? new GridLength(1, GridUnitType.Star) : new GridLength(1.25, GridUnitType.Star);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _ = ImportFilesAsync(paths.Where(File.Exists));
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _isClosing) return;
        e.Cancel = true;
        if (_transfers.ActiveCount > 0 && MessageBox.Show(this, $"当前有 {_transfers.ActiveCount} 个传输正在进行。退出会中断传输，确定继续吗？", "传输进行中", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _isClosing = true;
        IsEnabled = false;
        try { await _server.StopAsync(); }
        catch (Exception exception) { _log.Error("退出时停止服务失败", exception); }
        _catalog.Dispose();
        _allowClose = true;
        Close();
    }

    private void Catalog_FilesChanged()
    {
        Dispatcher.BeginInvoke(RefreshFiles);
    }

    private void Server_RunningChanged(bool running)
    {
        Dispatcher.BeginInvoke(UpdateServiceUi);
    }

    private void Log_EntryAdded(string entry)
    {
        Dispatcher.BeginInvoke(() => AppendLog(entry));
    }

    private void RefreshFiles()
    {
        var selectedName = (FilesGrid.SelectedItem as SharedFileItem)?.Name;
        Files.Clear();
        try
        {
            var index = 1;
            foreach (var file in _catalog.GetFiles()) Files.Add(file with { Index = index++ });
        }
        catch (IOException exception)
        {
            _log.Warning($"共享目录不可访问，暂时无法刷新列表：{exception.Message}");
        }
        FileCountText.Text = $"{Files.Count} 个文件";
        if (selectedName is not null)
        {
            FilesGrid.SelectedItem = Files.FirstOrDefault(item => string.Equals(item.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RefreshNetworkAddresses()
    {
        var addresses = _network.GetDisplayAddresses().Select(address => address.ToString()).ToList();
        if (addresses.Count == 0) addresses.Add(IPAddress.Loopback.ToString());
        IpAddressCombo.ItemsSource = addresses;
        var configured = _config.Current.BoundAddress;
        IpAddressCombo.SelectedItem = configured is not null && addresses.Contains(configured) ? configured : addresses[0];
        UpdateAddress();
    }

    private void LoadSettingsIntoUi(AppSettings settings)
    {
        PortTextBox.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        UploadDirectoryTextBox.Text = settings.UploadDirectory;
        LanOnlyCheckBox.IsChecked = settings.LanOnly;
        AllowWebUploadCheckBox.IsChecked = settings.AllowWebUpload;
        AllowWebDeleteCheckBox.IsChecked = settings.AllowWebDelete;
        ReadOnlyModeCheckBox.IsChecked = settings.ReadOnlyMode;
        BindingModeCombo.SelectedIndex = settings.BindingMode switch { BindingMode.Specific => 1, BindingMode.AllInterfaces => 2, _ => 0 };
        DuplicateBehaviorCombo.SelectedIndex = settings.DuplicateBehavior switch { DuplicateBehavior.AutoRename => 1, DuplicateBehavior.Reject => 2, _ => 0 };
        MaxUploadGbTextBox.Text = (settings.MaxUploadBytes / (1024m * 1024 * 1024)).ToString("0.##", CultureInfo.CurrentCulture);
    }

    private void UpdateServiceUi()
    {
        var running = _server.IsRunning;
        StatusText.Text = running ? "服务运行中" : "服务已停止";
        StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#147A4A" : "#8B3535"));
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#21A866" : "#C04A4A"));
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        UpdateAddress();
    }

    private void UpdateAddress()
    {
        AddressTextBox.Text = GetWebAddress();
    }

    private string GetBaseAddress()
    {
        var ip = _server.BoundAddress?.ToString() ?? IpAddressCombo.SelectedItem?.ToString() ?? IPAddress.Loopback.ToString();
        return $"http://{ip}:{_config.Current.Port}";
    }

    private string GetWebAddress() => $"{GetBaseAddress()}/web";

    private string GetDisplayIp() => IpAddressCombo.SelectedItem?.ToString() ?? IPAddress.Loopback.ToString();

    private void AppendLog(string entry)
    {
        LogTextBox.AppendText(entry + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private async Task CopyTextWithFeedbackAsync(string text)
    {
        try
        {
            if (!await ClipboardService.TrySetTextAsync(text))
            {
                ShowError("复制失败", "Windows 剪贴板正被其他程序占用，请稍后重试。");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log.Error("复制失败", exception);
            ShowError("复制失败", exception.Message);
        }
    }

    private void OpenDirectory(string directory)
    {
        try { Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception exception) { ShowError("无法打开目录", exception.Message); }
    }

    private void FitWindowToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 28));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 28));
    }

    private void ShowError(string title, string message) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private async Task OfferPortRecoveryAsync(int failedPort)
    {
        var choice = MessageBox.Show(this,
            $"端口 {failedPort} 已被占用。\n\n选择“是”改用 28081；选择“否”自动寻找可用高位端口；选择“取消”保持当前设置。",
            "端口被占用", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return;

        var port = choice == MessageBoxResult.Yes ? 28081 : FindAvailableHighPort();
        if (port is null)
        {
            ShowError("未找到可用端口", "未能找到可用高位端口，请在基础设置中手动指定端口。");
            return;
        }

        try
        {
            _config.Replace(_config.Current with { Port = port.Value }, persist: true);
            LoadSettingsIntoUi(_config.Current);
            UpdateAddress();
            _log.Info($"端口已改为 {port.Value}，正在重新启动服务。");
            await StartServerWithFeedbackAsync(allowPortRecovery: false);
        }
        catch (Exception exception)
        {
            _log.Error("自动切换端口失败", exception);
            ShowError("切换端口失败", exception.Message);
        }
    }

    private static int? FindAvailableHighPort()
    {
        for (var port = 28081; port <= 65535; port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
            }
        }
        return null;
    }

    private static bool IsEditableTextFile(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".txt" or ".conf" or ".config" or ".json" or ".xml" or ".csv" or ".log" or ".md" or ".ini" or ".yaml" or ".yml" or ".properties";
}
