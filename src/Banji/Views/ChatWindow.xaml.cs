using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Banji.Core.Abstractions;
using Banji.Core.Ai;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;

namespace Banji.Views;

/// <summary>
/// 与班机聊天的窗口（v1.2.0）。
/// Win11 风格、气泡列表、流式逐字显示；聊天历史只保存在内存，不落盘。
/// 模型未安装时显示下载引导，不阻塞宠物窗。
/// </summary>
public partial class ChatWindow : Window
{
    private readonly IAiService _ai;
    private readonly ObservableCollection<ChatMessageVm> _messages = new();
    private readonly List<AiChatMessage> _history = new();
    private CancellationTokenSource? _generationCts;
    private bool _isGenerating;
    private bool _closed;
    private bool _greeted;

    private static readonly string[] FallbackGreetings =
    {
        "嗨，我是班机！今天哪节课最想吐槽？",
        "我在这儿。课间想问点什么，随时说。",
        "刚擦干净屏幕，脑袋也清空了——有事尽管问，没事聊两句也行。",
        "上课别犯困，困了就眯十秒，我帮你看着老师。"
    };

    public ChatWindow()
    {
        InitializeComponent();
        _ai = AppServices.Current.Ai;
        MessageList.ItemsSource = _messages;
        _ai.StatusChanged += OnAiStatusChanged;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
        UpdateModelStatus();
        UpdateEmptyState();
        UpdateBusyUi();
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        if (_ai.IsModelLoaded) _ = AddGreetingAsync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        try { _ai.StatusChanged -= OnAiStatusChanged; } catch { /* ignore */ }
        try { _generationCts?.Cancel(); } catch { /* ignore */ }
        try { _generationCts?.Dispose(); } catch { /* ignore */ }
    }

    private void OnAiStatusChanged(object? sender, EventArgs e) => AppServices.RunOnUi(() =>
    {
        if (_closed) return;
        UpdateModelStatus();
        if (_ai.IsModelLoaded && !_greeted && _messages.Count == 0) _ = AddGreetingAsync();
    });

    // =========================================================================
    // 状态刷新
    // =========================================================================

    private void UpdateModelStatus()
    {
        try
        {
            var status = _ai.Status;
            var (text, color) = status switch
            {
                AiModelStatus.Ready => ($"已就绪 · {_ai.LoadedModelName ?? "本地模型"}", Color.FromRgb(0x22, 0xC5, 0x5E)),
                AiModelStatus.Loading => ("模型加载中…", Color.FromRgb(0xFF, 0x9F, 0x1C)),
                AiModelStatus.Failed => ("模型加载失败", Color.FromRgb(0xEF, 0x44, 0x44)),
                _ => ("未安装模型", Color.FromRgb(0x9A, 0xA3, 0xAF))
            };
            StatusText.Text = text;
            StatusDot.Fill = new SolidColorBrush(color);

            if (status == AiModelStatus.Ready)
            {
                ModelInfoText.Text = $"{_ai.LoadedModelName} · 全部在本地运行，聊天记录不落盘";
                BannerPanel.Visibility = Visibility.Collapsed;
            }
            else if (status == AiModelStatus.Loading)
            {
                ModelInfoText.Text = "正在把模型读进内存，第一次需要几秒…";
                BannerPanel.Visibility = Visibility.Visible;
                BannerText.Text = "模型正在加载，稍等一会儿就能聊天了。";
                BannerButton.Visibility = Visibility.Collapsed;
            }
            else if (status == AiModelStatus.Failed)
            {
                ModelInfoText.Text = "本地离线运行，聊天记录只保存在内存";
                BannerPanel.Visibility = Visibility.Visible;
                BannerText.Text = "模型加载失败。可以到「模型管理」里重新选择模型文件，或重新下载。";
                BannerButton.Content = "打开模型管理";
                BannerButton.Visibility = Visibility.Visible;
            }
            else
            {
                ModelInfoText.Text = "本地离线运行，聊天记录只保存在内存";
                BannerPanel.Visibility = Visibility.Visible;
                BannerText.Text = "还没有安装 AI 模型。下载一个本地模型（推荐 1.5B）后，班机就能离线陪你聊天了。";
                BannerButton.Content = "下载模型";
                BannerButton.Visibility = Visibility.Visible;
                EmptyPanel.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_messages.Count == 0)
                {
                    // 空状态引导更明确
                    BottomHint.Text = "点击右上角「模型管理」下载模型；模型只在本地运行，不会上传聊天内容。";
                }
            }
            UpdateBusyUi();
        }
        catch (Exception ex)
        {
            Log.Warn("刷新聊天窗口模型状态失败：" + ex.Message);
        }
    }

    private void UpdateBusyUi()
    {
        var ready = _ai.IsModelLoaded;
        SendButton.IsEnabled = !_isGenerating;
        SendButton.Content = _isGenerating ? "生成中…" : "发送";
        StopButton.Visibility = _isGenerating ? Visibility.Visible : Visibility.Collapsed;
        QuickPanel.IsEnabled = !_isGenerating;
        if (!_isGenerating) SendButton.IsEnabled = true; // 未加载时也允许点击（会给出提示）
        _ = ready;
    }

    private void UpdateEmptyState()
    {
        EmptyPanel.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollToEnd()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { MessageScroll.ScrollToEnd(); } catch { /* ignore */ }
            }));
        }
        catch (Exception)
        {
            // 忽略：窗口可能已关闭
        }
    }

    // =========================================================================
    // 交互
    // =========================================================================

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendAsync();
        }
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        InputPlaceholder.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => SendAsync();

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        try { _generationCts?.Cancel(); } catch { /* ignore */ }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        try { _generationCts?.Cancel(); } catch { /* ignore */ }
        _messages.Clear();
        _history.Clear();
        _greeted = false;
        SpeedText.Text = "";
        BottomHint.Text = "AI 生成内容仅供参考；模型只在本地运行，不会上传聊天内容。";
        UpdateEmptyState();
        if (_ai.IsModelLoaded) _ = AddGreetingAsync();
    }

    private void OnManageModelsClick(object sender, RoutedEventArgs e)
    {
        try { AppServices.Current.ShowAiModelManager(); }
        catch (Exception ex) { Log.Error("打开模型管理窗口失败", ex); }
    }

    private void OnBannerButtonClick(object sender, RoutedEventArgs e)
    {
        try { AppServices.Current.ShowAiModelManager(); }
        catch (Exception ex) { Log.Error("打开模型管理窗口失败", ex); }
    }

    private void OnQuickPromptClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string prompt) return;
        InputBox.Text = prompt;
        InputBox.CaretIndex = prompt.Length;
        InputBox.Focus();
        SendAsync();
    }

    // =========================================================================
    // 发送 / 生成
    // =========================================================================

    private async void SendAsync()
    {
        if (_isGenerating) return;
        var text = (InputBox.Text ?? "").Trim();
        if (text.Length == 0) return;

        InputBox.Clear();
        InputPlaceholder.Visibility = Visibility.Visible;
        AddMessage(isUser: true, text);
        _history.Add(new AiChatMessage { Role = AiChatRole.User, Content = text });

        if (!_ai.IsModelLoaded)
        {
            AddMessage(false, "班机还没加载 AI 模型。点右上角「模型管理」下载或加载一个本地模型，就能聊天啦。");
            return;
        }

        _isGenerating = true;
        _generationCts = new CancellationTokenSource();
        UpdateBusyUi();

        var vm = AddMessage(false, "");
        vm.IsStreaming = true;
        var sb = new StringBuilder();
        var tokenCount = 0;
        var sw = Stopwatch.StartNew();
        var lastDispatch = 0L;

        try
        {
            var historySnapshot = _history.ToList();
            var reply = await _ai.GenerateChatReplyAsync(historySnapshot, delta =>
            {
                if (string.IsNullOrEmpty(delta)) return;
                sb.Append(delta);
                Interlocked.Increment(ref tokenCount);
                var now = sw.ElapsedMilliseconds;
                if (now - lastDispatch < 60) return;
                lastDispatch = now;
                var snapshot = AiTextSanitizer.CleanReply(sb.ToString());
                AppServices.RunOnUi(() =>
                {
                    vm.Text = snapshot;
                    ScrollToEnd();
                });
            }, _generationCts.Token);

            sw.Stop();
            var final = string.IsNullOrWhiteSpace(reply)
                ? AiTextSanitizer.CleanAssistantText(sb.ToString())
                : reply!;
            if (string.IsNullOrWhiteSpace(final))
                final = "（这次没想出来…换个说法再试试？）";

            vm.IsStreaming = false;
            vm.Text = final;
            _history.Add(new AiChatMessage { Role = AiChatRole.Assistant, Content = final });

            if (tokenCount > 0 && sw.Elapsed.TotalSeconds > 0.15)
            {
                var tps = tokenCount / sw.Elapsed.TotalSeconds;
                SpeedText.Text = $"上次速度 {tps:0.0} tokens/s · {tokenCount} tokens";
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            vm.IsStreaming = false;
            var partial = AiTextSanitizer.CleanAssistantText(sb.ToString());
            vm.Text = string.IsNullOrWhiteSpace(partial) ? "（已停止生成）" : partial + "\n（已停止生成）";
            SpeedText.Text = "已停止生成";
        }
        catch (Exception ex)
        {
            Log.Error("聊天窗口生成回复失败", ex);
            vm.IsStreaming = false;
            vm.Text = "刚才走神了，没生成成功。再试一次？";
            SpeedText.Text = "";
        }
        finally
        {
            sw.Stop();
            _isGenerating = false;
            try { _generationCts?.Dispose(); } catch { /* ignore */ }
            _generationCts = null;
            UpdateBusyUi();
            ScrollToEnd();
        }
    }

    private ChatMessageVm AddMessage(bool isUser, string text)
    {
        var vm = new ChatMessageVm
        {
            IsUser = isUser,
            Text = text,
            TimeText = DateTime.Now.ToString("HH:mm")
        };
        _messages.Add(vm);
        UpdateEmptyState();
        ScrollToEnd();
        return vm;
    }

    private async Task AddGreetingAsync()
    {
        if (_greeted) return;
        _greeted = true;
        try
        {
            var greeting = await _ai.TryGenerateGreetingAsync();
            if (_closed) return;
            var text = string.IsNullOrWhiteSpace(greeting)
                ? FallbackGreetings[Random.Shared.Next(FallbackGreetings.Length)]
                : greeting!;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed) return;
                if (_history.Any(m => m.Role == AiChatRole.User)) return; // 用户已经先开口了
                _history.Add(new AiChatMessage { Role = AiChatRole.Assistant, Content = text });
                AddMessage(isUser: false, text);
            });
        }
        catch (Exception ex)
        {
            Log.Warn("生成聊天开场白失败（使用本地开场白）：" + ex.Message);
            if (!_closed)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_closed || _messages.Count > 0) return;
                    AddMessage(isUser: false, FallbackGreetings[Random.Shared.Next(FallbackGreetings.Length)]);
                });
            }
        }
    }

    // =========================================================================
    // 气泡 ViewModel
    // =========================================================================

    public sealed class ChatMessageVm : INotifyPropertyChanged
    {
        private string _text = "";
        private bool _isStreaming;

        public bool IsUser { get; init; }
        public string TimeText { get; init; } = "";

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value ?? "";
                Raise(nameof(Text));
                Raise(nameof(DisplayText));
            }
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set
            {
                if (_isStreaming == value) return;
                _isStreaming = value;
                Raise(nameof(IsStreaming));
                Raise(nameof(DisplayText));
            }
        }

        public string DisplayText => _isStreaming
            ? (_text.Length > 0 ? _text + " ▍" : "▍")
            : _text;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
