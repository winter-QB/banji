using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Banji.Core.Abstractions;
using Banji.Core.Ai;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;

namespace Banji.Views;

/// <summary>
/// AI 模型管理窗口（v1.2.0）：
/// 三档可下载模型卡片 + 下载进度/速度/取消 + 选择本地 GGUF + 当前模型加载/卸载/删除 + 模型目录。
/// 所有模型都在本地运行，下载完成后自动加载。
/// </summary>
public partial class AiModelDialog : Window
{
    private readonly IAiService _ai;
    private readonly ObservableCollection<ModelCardVm> _cards = new();
    private CancellationTokenSource? _downloadCts;
    private string? _downloadingId;
    private bool _closed;

    // 下载速度估算（DTO 没有速度字段，由窗口根据进度事件自行计算）
    private DateTime? _lastProgressAt;
    private long _lastProgressBytes;
    private double _lastSpeedMb;

    public AiModelDialog()
    {
        InitializeComponent();
        _ai = AppServices.Current.Ai;
        foreach (var option in _ai.ModelOptions) _cards.Add(new ModelCardVm(option));
        ModelList.ItemsSource = _cards;

        _ai.StatusChanged += OnAiStatusChanged;
        _ai.DownloadProgress += OnAiDownloadProgress;
        Loaded += (_, _) => RefreshAll();
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        RefreshAll();
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_downloadCts is null) return;
        var result = MessageBox.Show(this, "模型还在下载中，确定取消下载并关闭窗口吗？", "班机",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        try { _downloadCts.Cancel(); } catch { /* ignore */ }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        try { _ai.StatusChanged -= OnAiStatusChanged; } catch { /* ignore */ }
        try { _ai.DownloadProgress -= OnAiDownloadProgress; } catch { /* ignore */ }
        try { _downloadCts?.Cancel(); } catch { /* ignore */ }
        try { _downloadCts?.Dispose(); } catch { /* ignore */ }
    }

    private void OnAiStatusChanged(object? sender, EventArgs e) => AppServices.RunOnUi(() =>
    {
        if (_closed) return;
        RefreshAll();
    });

    private void OnAiDownloadProgress(object? sender, AiDownloadProgress e) => AppServices.RunOnUi(() =>
    {
        try
        {
            if (_closed) return;
            var vm = _cards.FirstOrDefault(c => string.Equals(c.Option.Id, _downloadingId, StringComparison.OrdinalIgnoreCase));
            if (vm is null) return;

            var now = DateTime.Now;
            if (_lastProgressAt is { } last)
            {
                var seconds = (now - last).TotalSeconds;
                if (seconds >= 0.25)
                {
                    var mb = (e.BytesReceived - _lastProgressBytes) / seconds / (1024.0 * 1024.0);
                    if (mb >= 0) _lastSpeedMb = mb;
                    _lastProgressAt = now;
                    _lastProgressBytes = e.BytesReceived;
                }
            }
            else
            {
                _lastProgressAt = now;
                _lastProgressBytes = e.BytesReceived;
            }

            vm.ProgressPercent = e.Percent;
            vm.ProgressText = $"{e.Percent:0.0}% · {AiModelCatalog.FormatSize(e.BytesReceived)} / " +
                              $"{AiModelCatalog.FormatSize(e.TotalBytes)} · {_lastSpeedMb:0.0} MB/s · {e.Phase}";
            FooterStatusText.Text = $"{vm.Name}：{e.Phase}";
        }
        catch (Exception ex)
        {
            Log.Warn("刷新 AI 下载进度失败：" + ex.Message);
        }
    });

    // =========================================================================
    // 刷新 UI
    // =========================================================================

    private void RefreshAll()
    {
        try
        {
            var status = _ai.Status;
            CurrentStatusText.Text = AiModelCatalog.StatusText(status);
            CurrentStatusBadge.Background = status switch
            {
                AiModelStatus.Ready => System.Windows.Media.Brushes.Transparent,
                AiModelStatus.Loading => System.Windows.Media.Brushes.Transparent,
                AiModelStatus.Failed => System.Windows.Media.Brushes.Transparent,
                _ => System.Windows.Media.Brushes.Transparent
            };

            var selectedPath = CurrentModelPath();
            CurrentNameText.Text = _ai.LoadedModelName
                                   ?? (string.IsNullOrWhiteSpace(selectedPath) ? "未选择模型" : Path.GetFileName(selectedPath));
            CurrentPathText.Text = string.IsNullOrWhiteSpace(selectedPath)
                ? "还没有下载或选择模型；可以在下面选择一档下载，或挑一个本地 .gguf 文件"
                : selectedPath;

            LoadCurrentButton.Visibility = _ai.IsModelLoaded ? Visibility.Collapsed : Visibility.Visible;
            LoadCurrentButton.IsEnabled = !string.IsNullOrWhiteSpace(selectedPath)
                                          && File.Exists(selectedPath)
                                          && status != AiModelStatus.Loading;
            LoadCurrentButton.Content = status == AiModelStatus.Loading ? "加载中…" : "加载";
            UnloadButton.IsEnabled = _ai.IsModelLoaded;
            DeleteCurrentButton.IsEnabled = !string.IsNullOrWhiteSpace(selectedPath)
                                            && File.Exists(selectedPath)
                                            && _downloadCts is null;

            ModelDirText.Text = _ai.ModelDirectory;

            foreach (var vm in _cards) RefreshCard(vm);
        }
        catch (Exception ex)
        {
            Log.Warn("刷新 AI 模型管理界面失败：" + ex.Message);
        }
    }

    private void RefreshCard(ModelCardVm vm)
    {
        var path = AiModelCatalog.GetModelPath(_ai.ModelDirectory, vm.Option);
        var part = path + ".part";
        bool downloaded;
        try
        {
            downloaded = File.Exists(path) && new FileInfo(path).Length > AiModelCatalog.MinGgufSizeBytes;
        }
        catch
        {
            downloaded = false;
        }

        vm.IsDownloaded = downloaded;
        vm.HasPart = !downloaded && File.Exists(part) && new FileInfo(part).Length > 0;
        vm.IsDownloading = _downloadCts is not null
                           && string.Equals(_downloadingId, vm.Option.Id, StringComparison.OrdinalIgnoreCase);
        vm.IsLoaded = string.Equals(_ai.LoadedModelPath, path, StringComparison.OrdinalIgnoreCase);
        vm.DownloadButtonEnabled = _downloadCts is null;
    }

    /// <summary>当前选中的模型文件：优先已加载的，其次设置里记住的。</summary>
    private string? CurrentModelPath()
    {
        var loaded = _ai.LoadedModelPath;
        if (!string.IsNullOrWhiteSpace(loaded)) return loaded;
        try
        {
            var fromSettings = AppServices.Current.Settings.Current.Ai.ModelPath;
            if (!string.IsNullOrWhiteSpace(fromSettings) && File.Exists(fromSettings)) return fromSettings;
        }
        catch (Exception ex)
        {
            Log.Warn("读取 AI 模型设置失败：" + ex.Message);
        }
        return null;
    }

    // =========================================================================
    // 下载
    // =========================================================================

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button button || button.DataContext is not ModelCardVm vm) return;
            if (_downloadCts is not null)
            {
                MessageBox.Show(this, "已有一个模型正在下载，请等它完成或先取消。", "班机",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _downloadCts = new CancellationTokenSource();
            _downloadingId = vm.Option.Id;
            _lastProgressAt = null;
            _lastProgressBytes = 0;
            _lastSpeedMb = 0;
            FooterStatusText.Text = $"开始下载：{vm.Name}";
            RefreshAll();

            var token = _downloadCts.Token;
            var option = vm.Option;
            var ok = false;
            try
            {
                ok = await Task.Run(() => _ai.DownloadModelAsync(option, token), token);
            }
            catch (OperationCanceledException)
            {
                FooterStatusText.Text = "下载已取消（可点「继续下载」断点续传）";
            }
            catch (Exception ex)
            {
                Log.Error("下载 AI 模型失败", ex);
                FooterStatusText.Text = "下载失败：" + ShortMessage(ex);
            }

            if (_closed) return;
            if (ok)
                FooterStatusText.Text = $"下载完成并已加载：{option.Name}";
            else if (!token.IsCancellationRequested)
                FooterStatusText.Text = string.IsNullOrEmpty(FooterStatusText.Text) || FooterStatusText.Text.StartsWith("开始")
                    ? "下载失败，请检查网络后重试"
                    : FooterStatusText.Text;
        }
        finally
        {
            try { _downloadCts?.Dispose(); } catch { /* ignore */ }
            _downloadCts = null;
            _downloadingId = null;
            RefreshAll();
        }
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        try { _downloadCts?.Cancel(); } catch { /* ignore */ }
        FooterStatusText.Text = "正在取消下载…";
    }

    private async void OnLoadCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ModelCardVm vm) return;
        var path = AiModelCatalog.GetModelPath(_ai.ModelDirectory, vm.Option);
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "模型文件不存在，请先下载。", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await LoadModelPathAsync(path);
    }

    // =========================================================================
    // 当前模型操作
    // =========================================================================

    private async void OnLoadCurrentClick(object sender, RoutedEventArgs e)
    {
        var path = CurrentModelPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "还没有可加载的模型文件。请先下载一档模型，或用「选择本地 GGUF 文件」。", "班机",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await LoadModelPathAsync(path);
    }

    private async Task LoadModelPathAsync(string path)
    {
        if (!AiModelCatalog.ValidateGgufFile(path, out var reason))
        {
            MessageBox.Show(this, reason, "无法加载模型", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FooterStatusText.Text = "正在加载模型…";
        try
        {
            _ai.Unload();
            var ok = await Task.Run(() => _ai.LoadModelAsync(path));
            FooterStatusText.Text = ok ? "模型加载成功，可以开始聊天了" : "模型加载失败，详情见日志";
        }
        catch (Exception ex)
        {
            Log.Error("加载 AI 模型失败", ex);
            FooterStatusText.Text = "模型加载失败：" + ShortMessage(ex);
        }
        finally
        {
            RefreshAll();
        }
    }

    private void OnUnloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _ai.Unload();
            FooterStatusText.Text = "模型已卸载，内存已释放";
        }
        catch (Exception ex)
        {
            Log.Error("卸载 AI 模型失败", ex);
            FooterStatusText.Text = "卸载失败：" + ShortMessage(ex);
        }
        RefreshAll();
    }

    private void OnDeleteCurrentClick(object sender, RoutedEventArgs e)
    {
        var path = CurrentModelPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "当前没有可删除的模型文件。", "班机", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(this,
            $"确定删除模型文件吗？\n\n{path}\n\n删除后需要重新下载（会释放磁盘空间）。",
            "删除模型", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (string.Equals(_ai.LoadedModelPath, path, StringComparison.OrdinalIgnoreCase)) _ai.Unload();
            if (File.Exists(path)) File.Delete(path);
            var part = path + ".part";
            if (File.Exists(part)) File.Delete(part);
            FooterStatusText.Text = "模型文件已删除";
        }
        catch (Exception ex)
        {
            Log.Error("删除 AI 模型文件失败", ex);
            MessageBox.Show(this, "删除失败：" + ShortMessage(ex), "班机", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshAll();
    }

    // =========================================================================
    // 本地文件 / 目录
    // =========================================================================

    private async void OnChooseLocalClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择本地 GGUF 模型",
                Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;

            if (!AiModelCatalog.ValidateGgufFile(dialog.FileName, out var reason))
            {
                MessageBox.Show(this, reason, "无法使用这个文件", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LoadModelPathAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error("选择本地 GGUF 文件失败", ex);
            MessageBox.Show(this, "选择文件失败：" + ShortMessage(ex), "班机", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_ai.ModelDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _ai.ModelDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开模型目录失败", ex);
            MessageBox.Show(this, "打开模型目录失败：" + ShortMessage(ex), "班机", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshAll();

    private static string ShortMessage(Exception ex)
    {
        var msg = ex is AggregateException agg && agg.InnerException is not null ? agg.InnerException.Message : ex.Message;
        return msg.Length > 200 ? msg[..200] : msg;
    }

    // =========================================================================
    // 卡片 ViewModel
    // =========================================================================

    public sealed class ModelCardVm : INotifyPropertyChanged
    {
        private bool _isDownloaded;
        private bool _isDownloading;
        private bool _isLoaded;
        private bool _hasPart;
        private bool _downloadButtonEnabled = true;
        private double _progressPercent;
        private string _progressText = "";

        public ModelCardVm(AiModelOption option) => Option = option;

        public AiModelOption Option { get; }
        public string Name => Option.Name;
        public string Description => Option.Description;
        public string SizeText => Option.SizeText;
        public Visibility RecommendedVisibility => Option.Recommended ? Visibility.Visible : Visibility.Collapsed;

        public bool IsDownloaded
        {
            get => _isDownloaded;
            set { if (_isDownloaded == value) return; _isDownloaded = value; RaiseAll(); }
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            set { if (_isDownloading == value) return; _isDownloading = value; RaiseAll(); }
        }

        public bool IsLoaded
        {
            get => _isLoaded;
            set { if (_isLoaded == value) return; _isLoaded = value; RaiseAll(); }
        }

        public bool HasPart
        {
            get => _hasPart;
            set { if (_hasPart == value) return; _hasPart = value; RaiseAll(); }
        }

        public bool DownloadButtonEnabled
        {
            get => _downloadButtonEnabled;
            set { if (_downloadButtonEnabled == value) return; _downloadButtonEnabled = value; Raise(nameof(DownloadButtonEnabled)); }
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            set { if (Math.Abs(_progressPercent - value) < 0.05) return; _progressPercent = value; Raise(nameof(ProgressPercent)); }
        }

        public string ProgressText
        {
            get => _progressText;
            set { if (_progressText == value) return; _progressText = value; Raise(nameof(ProgressText)); }
        }

        public string StatusLine => IsDownloading ? "下载中" : IsLoaded ? "使用中" : IsDownloaded ? "已下载" : "";
        public string DownloadButtonText => HasPart ? "继续下载" : "下载";
        public Visibility DownloadButtonVisibility => !IsDownloaded && !IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CancelButtonVisibility => IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LoadButtonVisibility => IsDownloaded && !IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        public string LoadButtonText => IsLoaded ? "当前模型" : "加载";
        public bool LoadButtonEnabled => !IsLoaded;
        public Visibility ProgressVisibility => IsDownloading ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaiseAll()
        {
            Raise(nameof(IsDownloaded));
            Raise(nameof(IsDownloading));
            Raise(nameof(IsLoaded));
            Raise(nameof(StatusLine));
            Raise(nameof(DownloadButtonText));
            Raise(nameof(DownloadButtonVisibility));
            Raise(nameof(CancelButtonVisibility));
            Raise(nameof(LoadButtonVisibility));
            Raise(nameof(LoadButtonText));
            Raise(nameof(LoadButtonEnabled));
            Raise(nameof(ProgressVisibility));
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
