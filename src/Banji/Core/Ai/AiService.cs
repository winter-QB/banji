using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Settings;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Banji.Core.Ai;

/// <summary>
/// v1.2.0 端侧生成式 AI 的生产实现：LLamaSharp + 本地 GGUF（CPU 推理）。
///
/// 设计要点：
/// - 模型管理：hf-mirror 流式下载 + .part 断点续传（HTTP Range）+ 进度事件 + 取消；
/// - 推理：每次生成创建独立 LLamaContext，串行化执行（SemaphoreSlim），不阻塞 UI；
/// - 宠物台词：后台预取一批短句进内存缓存，点击时立即取；缓存空就立刻返回 null（调用方回退本地台词）；
/// - 全部异常吞掉并写中文日志，绝不让主程序崩溃。
///
/// 线程约定：<see cref="StatusChanged"/> / <see cref="DownloadProgress"/> / onDelta 都可能在后台线程触发，
/// UI 订阅方需要自行 Dispatcher 切换（窗口代码已处理）。
/// </summary>
public sealed class AiService : IAiService, IDisposable
{
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly ISettingsService _settings;
    private readonly List<AiModelOption> _options;
    private readonly object _gate = new();
    private readonly object _quipGate = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _generateLock = new(1, 1);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly Queue<string> _quipCache = new();
    /// <summary>v1.4：值日场景专用台词缓存（优先给“放学后/值日提醒”场景）。</summary>
    private readonly Queue<string> _dutyQuipCache = new();

    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private volatile AiModelStatus _status = AiModelStatus.NotInstalled;
    private string? _loadedName;
    private string? _loadedPath;
    private int _quipPrefetchRunning;
    private int _dutyQuipPrefetchRunning;
    /// <summary>值日台词缓存对应的日期（跨天自动作废）。</summary>
    private DateOnly _dutyQuipCacheDate;
    private bool _disposed;

    // 最近一次生成/加载的统计（UI 显示速度用）
    private double _lastTokensPerSecond;
    private int _lastGeneratedTokens;
    private long _lastLoadMilliseconds;

    public AiService(ISettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _options = AiModelCatalog.CreateOptions();
        try
        {
            Directory.CreateDirectory(ModelDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn("创建 AI 模型目录失败（稍后下载会重试）：" + ex.Message);
        }

        try { AppDomain.CurrentDomain.ProcessExit += OnProcessExit; }
        catch { /* ignore */ }
    }

    // =========================================================================
    // 契约属性
    // =========================================================================

    public AiModelStatus Status { get { lock (_gate) return _status; } }

    public bool IsModelLoaded
    {
        get
        {
            lock (_gate) return _status == AiModelStatus.Ready && _weights is not null;
        }
    }

    public string? LoadedModelName { get { lock (_gate) return _loadedName; } }

    public string? LoadedModelPath { get { lock (_gate) return _loadedPath; } }

    public string ModelDirectory => Path.Combine(AppPaths.Root, "models");

    public IReadOnlyList<AiModelOption> ModelOptions => _options;

    public event EventHandler? StatusChanged;
    public event EventHandler<AiDownloadProgress>? DownloadProgress;

    // =========================================================================
    // 扩展信息（非契约字段，UI/QA 展示用）
    // =========================================================================

    /// <summary>最近一次生成的平均速度（tokens/s）；未生成过为 0。</summary>
    public double LastTokensPerSecond { get { lock (_gate) return _lastTokensPerSecond; } }

    /// <summary>最近一次生成输出的 token 数。</summary>
    public int LastGeneratedTokens { get { lock (_gate) return _lastGeneratedTokens; } }

    /// <summary>最近一次模型加载耗时（毫秒）。</summary>
    public long LastLoadMilliseconds { get { lock (_gate) return _lastLoadMilliseconds; } }

    /// <summary>推理线程数（解析后的实际值，0 配置时为自动值）。</summary>
    public int ResolvedThreads
    {
        get
        {
            try { return AiHardware.ResolveThreads(_settings.Current.Ai.Threads); }
            catch { return AiHardware.AutoThreadCount(); }
        }
    }

    /// <summary>给定模型 Id 是否已经下载到模型目录（UI 用）。</summary>
    public bool IsModelDownloaded(AiModelOption option)
    {
        try
        {
            if (option is null || string.IsNullOrWhiteSpace(option.FileName)) return false;
            var path = AiModelCatalog.GetModelPath(ModelDirectory, option);
            return File.Exists(path) && new FileInfo(path).Length > AiModelCatalog.MinGgufSizeBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>模型目录下该选项的目标文件路径（UI 用）。</summary>
    public string GetModelPath(AiModelOption option)
        => option is null || string.IsNullOrWhiteSpace(option.FileName)
            ? ModelDirectory
            : AiModelCatalog.GetModelPath(ModelDirectory, option);

    // =========================================================================
    // 模型加载 / 卸载 / 自动加载
    // =========================================================================

    public async Task<bool> LoadModelAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            Log.Warn("AI 模型加载失败：没有指定模型路径");
            SetStatus(AiModelStatus.Failed);
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(modelPath.Trim());
        }
        catch (Exception ex)
        {
            Log.Warn("AI 模型加载失败：路径不合法 :: " + ex.Message);
            SetStatus(AiModelStatus.Failed);
            return false;
        }

        if (!AiModelCatalog.ValidateGgufFile(fullPath, out var reason))
        {
            Log.Warn($"AI 模型加载失败：{reason}（{fullPath}）");
            SetStatus(AiModelStatus.Failed);
            return false;
        }

        try
        {
            await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Info("AI 模型加载已取消（等待加载锁时）");
            return false;
        }

        var weights = default(LLamaWeights);
        try
        {
            if (_disposed) return false;

            var ai = _settings.Current.Ai;
            var threads = AiHardware.ResolveThreads(ai.Threads);
            var contextSize = Math.Clamp(ai.ContextSize, 512, 8192);
            var batchSize = AiHardware.ResolveBatchSize(contextSize);

            var fileName = Path.GetFileName(fullPath);
            SetStatus(AiModelStatus.Loading, fileName, fullPath);
            Log.Info($"AI 模型开始加载：{fullPath}（{AiModelCatalog.FormatSize(new FileInfo(fullPath).Length)}，" +
                     $"线程 {threads}，上下文 {contextSize}）");

            // netstandard2.0 版 LLamaSharp 不会自动定位 runtimes\{rid}\native 下的原生库，先显式预加载
            if (!AiNativeLoader.EnsureLoaded())
                Log.Warn("AI 原生库预加载未成功，尝试交给系统默认解析：" + AiNativeLoader.Message);

            var parameters = new ModelParams(fullPath)
            {
                ContextSize = (uint)contextSize,
                Threads = threads,
                GpuLayerCount = 0,
                BatchSize = (uint)batchSize
            };

            var sw = Stopwatch.StartNew();
            weights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();

            if (weights is null) throw new InvalidOperationException("模型加载返回空对象");
            if (_disposed)
            {
                weights.Dispose();
                weights = null;
                return false;
            }

            // 创建一次上下文做可用性自检（也是 tokenizer 来源），随后释放
            try
            {
                using var probe = weights.CreateContext(parameters);
                _ = probe.ContextSize;
            }
            catch (Exception ex)
            {
                Log.Error("AI 模型已加载但上下文自检失败（模型文件可能不兼容）", ex);
                throw;
            }

            LLamaWeights? old;
            lock (_gate)
            {
                old = _weights;
                _weights = weights;
                _modelParams = parameters;
                _loadedName = fileName;
                _loadedPath = fullPath;
                _status = AiModelStatus.Ready;
                _lastLoadMilliseconds = sw.ElapsedMilliseconds;
                _lastTokensPerSecond = 0;
                _lastGeneratedTokens = 0;
            }
            ClearQuipCache();

            try { old?.Dispose(); } catch (Exception ex) { Log.Warn("释放旧模型失败：" + ex.Message); }

            // 记住当前模型，下次启动可自动加载
            UpdateSettingsSafe(x =>
            {
                x.Ai.ModelPath = fullPath;
                x.Ai.ModelName = fileName;
                var matched = AiModelCatalog.FindByFileName(_options, fileName);
                if (matched is not null) x.Ai.LastModelId = matched.Id;
            });

            Log.Info($"AI 模型加载完成：{fileName}，耗时 {sw.ElapsedMilliseconds} ms（线程 {threads}）");
            RaiseStatusChanged();
            return true;
        }
        catch (OperationCanceledException)
        {
            try { weights?.Dispose(); } catch { /* ignore */ }
            Log.Info("AI 模型加载已取消");
            if (IsModelLoaded) return false;
            SetStatus(AiModelStatus.NotInstalled);
            return false;
        }
        catch (Exception ex)
        {
            try { weights?.Dispose(); } catch { /* ignore */ }
            Log.Error("AI 模型加载失败（将回退为未加载状态）", ex);
            SetStatus(AiModelStatus.Failed);
            return false;
        }
        finally
        {
            try { _loadLock.Release(); } catch { /* ignore */ }
        }
    }

    public void Unload()
    {
        LLamaWeights? old;
        lock (_gate)
        {
            old = _weights;
            _weights = null;
            _modelParams = null;
            _loadedName = null;
            _loadedPath = null;
            if (_status != AiModelStatus.Failed) _status = AiModelStatus.NotInstalled;
            _lastTokensPerSecond = 0;
            _lastGeneratedTokens = 0;
        }
        ClearQuipCache();

        if (old is null)
        {
            Log.Info("AI 模型卸载：当前没有已加载的模型");
        }
        else
        {
            // 若正好在推理，等它结束再释放，避免访问已释放的原生句柄
            if (_generateLock.Wait(TimeSpan.FromSeconds(5)))
            {
                try { old.Dispose(); } catch (Exception ex) { Log.Warn("释放 AI 模型失败：" + ex.Message); }
                finally { _generateLock.Release(); }
            }
            else
            {
                _ = Task.Run(() =>
                {
                    var got = false;
                    try
                    {
                        got = _generateLock.Wait(TimeSpan.FromSeconds(30));
                        old.Dispose();
                    }
                    catch (Exception ex) { Log.Warn("后台释放 AI 模型失败：" + ex.Message); }
                    finally { if (got) _generateLock.Release(); }
                });
            }
            Log.Info("AI 模型已卸载，内存已释放");
        }

        RaiseStatusChanged();
    }

    public void TryAutoLoad()
    {
        try
        {
            var ai = _settings.Current.Ai;
            if (!ai.AutoLoadModel)
            {
                Log.Info("AI 自动加载：设置里已关闭，跳过");
                return;
            }

            var path = ai.ModelPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Log.Info("AI 自动加载：设置里还没有可用的模型文件，跳过（可在「AI 模型管理」里下载）");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var ok = await LoadModelAsync(path).ConfigureAwait(false);
                    Log.Info($"AI 自动加载结果：{(ok ? "成功" : "失败（已静默，不弹窗）")}");
                }
                catch (Exception ex)
                {
                    Log.Warn("AI 自动加载异常（已静默）：" + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("AI 自动加载调度失败（静默）：" + ex.Message);
        }
    }

    // =========================================================================
    // 模型下载（流式 + 断点续传）
    // =========================================================================

    public async Task<bool> DownloadModelAsync(AiModelOption option, CancellationToken cancellationToken = default)
    {
        if (option is null)
        {
            Log.Warn("AI 模型下载失败：模型选项为空");
            return false;
        }
        if (string.IsNullOrWhiteSpace(option.Url) || string.IsNullOrWhiteSpace(option.FileName))
        {
            Log.Warn($"AI 模型下载失败：模型 {option.Id} 缺少下载地址或文件名");
            RaiseDownloadProgress(0, 0, "下载失败：模型配置不完整");
            return false;
        }
        if (!Uri.TryCreate(option.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Warn($"AI 模型下载失败：下载地址不合法（{option.Url}）");
            RaiseDownloadProgress(0, 0, "下载失败：下载地址不合法");
            return false;
        }

        string targetPath;
        try
        {
            Directory.CreateDirectory(ModelDirectory);
            targetPath = AiModelCatalog.GetModelPath(ModelDirectory, option);
        }
        catch (Exception ex)
        {
            Log.Error("AI 模型下载失败：无法创建模型目录", ex);
            RaiseDownloadProgress(0, 0, "下载失败：无法创建模型目录");
            return false;
        }

        var partPath = targetPath + ".part";
        var acquired = false;
        try
        {
            acquired = await _downloadLock.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (!acquired)
        {
            Log.Warn("AI 模型下载：已有另一个下载任务在进行，忽略本次请求");
            return false;
        }

        try
        {
            Log.Info($"AI 模型下载开始：{option.Name} → {targetPath}（源：{uri.Host}）");
            var result = await DownloadCoreAsync(option, uri, targetPath, partPath, cancellationToken).ConfigureAwait(false);
            if (!result)
            {
                // 失败/取消时保留 .part 以便下次断点续传；仅当文件被判定损坏时 DownloadCoreAsync 会自行清理
                return false;
            }

            RaiseDownloadProgress(new FileInfo(targetPath).Length, new FileInfo(targetPath).Length, "已完成");
            Log.Info($"AI 模型下载完成：{option.Name}（{AiModelCatalog.FormatSize(new FileInfo(targetPath).Length)}）");

            UpdateSettingsSafe(x =>
            {
                x.Ai.ModelPath = targetPath;
                x.Ai.ModelName = option.FileName;
                x.Ai.LastModelId = option.Id;
            });

            // 下载完成后自动加载（取消令牌已不适用，避免用户取消下载连带取消加载）
            var loaded = await LoadModelAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
            Log.Info($"AI 下载后自动加载结果：{(loaded ? "成功" : "失败")}");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Info("AI 模型下载已取消（已保留 .part，可断点续传）");
            RaiseDownloadProgress(GetFileLengthSafe(partPath), option.SizeBytes, "已取消");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"AI 模型下载失败：{option.Name}", ex);
            RaiseDownloadProgress(GetFileLengthSafe(partPath), option.SizeBytes, "下载失败：" + ShortMessage(ex));
            return false;
        }
        finally
        {
            try { _downloadLock.Release(); } catch { /* ignore */ }
        }
    }

    private async Task<bool> DownloadCoreAsync(AiModelOption option, Uri uri, string targetPath, string partPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = GetFileLengthSafe(partPath);
            if (existing > 0 && option.SizeBytes > 0 && existing > option.SizeBytes + 1024 * 1024)
            {
                // .part 比预期还大，肯定不可信，重新下载
                TryDeleteFile(partPath);
                existing = 0;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Banji/1.2.0 (Windows; classroom pet; GGUF downloader)");
            if (existing > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
                Log.Info($"AI 模型断点续传：从 {AiModelCatalog.FormatSize(existing)} 处继续（可能的总大小 {option.SizeText}）");
            }

            RaiseDownloadProgress(existing, option.SizeBytes, existing > 0 ? "准备中（断点续传）" : "准备中");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                var remoteTotal = ParseTotalBytes(response, 0);
                if (remoteTotal > 0 && existing >= remoteTotal)
                {
                    // .part 已经完整，直接进入校验/落盘
                    Log.Info("AI 模型下载：服务器返回 416，但本地 .part 已达到总大小，按完整文件处理");
                    return await FinalizeDownloadAsync(option, partPath, targetPath, remoteTotal, cancellationToken)
                        .ConfigureAwait(false);
                }
                Log.Warn("AI 模型下载：服务器拒绝断点续传（416），删除 .part 后重试完整下载");
                TryDeleteFile(partPath);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.Error($"AI 模型下载 HTTP 失败：{(int)response.StatusCode} {response.ReasonPhrase}（{uri}）");
                RaiseDownloadProgress(existing, option.SizeBytes, $"下载失败：HTTP {(int)response.StatusCode}");
                return false;
            }

            var isResume = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            var startOffset = isResume ? existing : 0;
            if (!isResume) existing = 0;

            long total;
            if (isResume)
            {
                total = ParseTotalBytes(response, startOffset + (response.Content.Headers.ContentLength ?? 0));
            }
            else
            {
                total = response.Content.Headers.ContentLength ?? option.SizeBytes;
            }
            if (total <= 0) total = option.SizeBytes;

            var mode = isResume && File.Exists(partPath) ? FileMode.Append : FileMode.Create;
            await using var fileStream = new FileStream(partPath, mode, FileAccess.Write, FileShare.None,
                1024 * 128, useAsync: true);

            await using var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[1024 * 128];
            var received = startOffset;
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long lastBytes = received;
            long lastUiReport = 0;

            RaiseDownloadProgress(received, total, "下载中");

            while (true)
            {
                var read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0) break;

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                var now = sw.Elapsed;
                if ((now - lastReport).TotalMilliseconds >= 200)
                {
                    var speedMb = lastReport.TotalSeconds > 0
                        ? (received - lastBytes) / (now - lastReport).TotalSeconds / (1024.0 * 1024.0)
                        : 0;
                    lastReport = now;
                    lastBytes = received;
                    RaiseDownloadProgress(received, total, $"下载中 {speedMb:0.0} MB/s");
                    lastUiReport = received;
                }
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            var finalSize = GetFileLengthSafe(partPath);
            if (total > 0 && finalSize < total)
            {
                Log.Warn($"AI 模型下载不完整：已收到 {finalSize} / {total} 字节，保留 .part 以便续传");
                RaiseDownloadProgress(finalSize, total, "下载中（连接中断）");
                if (attempt == 0) continue; // 再来一次（会走续传）
                return false;
            }
            if (finalSize <= AiModelCatalog.MinGgufSizeBytes)
            {
                Log.Error($"AI 模型下载失败：文件只有 {finalSize} 字节，已删除");
                TryDeleteFile(partPath);
                RaiseDownloadProgress(0, total, "下载失败：文件不完整");
                return false;
            }

            var avgSpeed = finalSize / Math.Max(0.001, sw.Elapsed.TotalSeconds) / (1024.0 * 1024.0);
            Log.Info($"AI 模型下载数据接收完成：{AiModelCatalog.FormatSize(finalSize)}，平均 {avgSpeed:0.0} MB/s，耗时 {sw.Elapsed.TotalSeconds:0.0}s");
            return await FinalizeDownloadAsync(option, partPath, targetPath, total > 0 ? total : finalSize, cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> FinalizeDownloadAsync(AiModelOption option, string partPath, string targetPath,
        long totalBytes, CancellationToken cancellationToken)
    {
        var partSize = GetFileLengthSafe(partPath);
        if (totalBytes > 0 && partSize != totalBytes)
        {
            Log.Warn($"AI 模型下载校验失败：期望 {totalBytes} 字节，实际 {partSize} 字节（保留 .part 以便续传）");
            RaiseDownloadProgress(partSize, totalBytes, "下载中（等待续传）");
            return false;
        }

        RaiseDownloadProgress(partSize, totalBytes, "校验中");

        // 若目标文件正被当前加载的模型内存映射占用，先卸载
        if (string.Equals(LoadedModelPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("AI 模型下载：目标模型正在使用，先卸载再覆盖");
            Unload();
        }

        try
        {
            File.Move(partPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 若被其他进程占用，退化为复制+删除
            Log.Warn("AI 模型文件落盘失败，尝试复制方式：" + ex.Message);
            try
            {
                File.Copy(partPath, targetPath, overwrite: true);
                TryDeleteFile(partPath);
            }
            catch (Exception ex2)
            {
                Log.Error("AI 模型文件落盘失败", ex2);
                RaiseDownloadProgress(partSize, totalBytes, "下载失败：无法写入模型文件");
                return false;
            }
        }

        await Task.Yield();
        RaiseDownloadProgress(totalBytes > 0 ? totalBytes : GetFileLengthSafe(targetPath),
            totalBytes > 0 ? totalBytes : GetFileLengthSafe(targetPath), "已完成");
        return true;
    }

    // =========================================================================
    // 聊天生成
    // =========================================================================

    public async Task<string?> GenerateChatReplyAsync(IReadOnlyList<AiChatMessage> history,
        Action<string>? onDelta, CancellationToken cancellationToken = default)
    {
        if (history is null || history.Count == 0)
        {
            Log.Warn("AI 聊天生成：历史为空，返回 null");
            return null;
        }
        if (!IsModelLoaded)
        {
            Log.Warn("AI 聊天生成：模型未加载，返回 null（UI 应引导下载/加载模型）");
            return null;
        }

        if (!TryGetModelSnapshot(out var weights, out var modelParams) || weights is null || modelParams is null)
            return null;

        var ai = SafeSettings();
        var maxTokens = Math.Clamp(ai.MaxTokens, 32, 1024);
        var temperature = Math.Clamp(ai.Temperature, 0.0, 1.5);
        var sb = new StringBuilder();
        var tokenCount = 0;
        var sw = Stopwatch.StartNew();
        var started = false;

        try
        {
            await _generateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            started = true;

            using var context = weights.CreateContext(modelParams);
            var counter = MakeTokenCounter(context);
            var budget = AiPromptBuilder.ComputePromptBudget((int)context.ContextSize, maxTokens);
            var dynamicContext = TryCaptureDynamicContextBlock();
            if (!string.IsNullOrEmpty(dynamicContext))
                Log.Info("AI 动态上下文：" + TruncateForLog(dynamicContext));
            var prompt = AiPromptBuilder.BuildChatPrompt(history, budget, counter, out var droppedMessages,
                dynamicContext);
            var promptTokens = counter(prompt);
            Log.Info($"AI 生成开始：prompt≈{promptTokens} tokens（丢弃历史 {droppedMessages} 条，" +
                     $"动态上下文 {(string.IsNullOrEmpty(dynamicContext) ? 0 : dynamicContext!.Length)} 字），最多生成 {maxTokens} tokens");

            var executor = new InteractiveExecutor(context);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = new List<string> { AiPromptBuilder.ImEnd, AiPromptBuilder.ImStart },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = (float)temperature,
                    TopP = 0.9f,
                    TopK = 40,
                    RepeatPenalty = 1.1f
                }
            };

            await foreach (var delta in executor.InferAsync(prompt, inferenceParams, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(delta)) continue;
                tokenCount++;
                sb.Append(delta);
                InvokeDelta(onDelta, delta);
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Log.Info($"AI 生成已被取消（已生成 {tokenCount} tokens）");
            var partial = AiTextSanitizer.CleanAssistantText(sb.ToString());
            return AiTextSanitizer.IsUsableReply(partial) ? partial : null;
        }
        catch (Exception ex)
        {
            Log.Error("AI 聊天生成失败（状态保持已就绪，可重试）", ex);
            return null;
        }
        finally
        {
            if (started)
            {
                try { _generateLock.Release(); } catch { /* ignore */ }
            }
        }

        sw.Stop();
        var text = AiTextSanitizer.CleanAssistantText(sb.ToString());
        var tps = tokenCount / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        lock (_gate)
        {
            _lastTokensPerSecond = tps;
            _lastGeneratedTokens = tokenCount;
        }
        Log.Info($"AI 回复（{tokenCount} tokens / {sw.Elapsed.TotalSeconds:0.0}s，{tps:0.0} tok/s）：{TruncateForLog(text)}");
        return AiTextSanitizer.IsUsableReply(text) ? text : null;
    }

    // =========================================================================
    // 宠物台词 / 开场白
    // =========================================================================

    public Task<string?> TryGenerateQuipAsync(string? contextHint = null, CancellationToken cancellationToken = default)
    {
        // 绝不阻塞：缓存里有就立刻取，没有就返回 null 并触发后台预取
        if (!IsModelLoaded) return Task.FromResult<string?>(null);

        // v1.4：值日相关场景（放学后/值日提醒）优先取带值日信息的台词；没有就后台预热、本次回退本地台词
        if (AiPromptContextBuilder.IsDutyScenarioHint(contextHint))
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            lock (_quipGate)
            {
                // 跨天/跨周后旧台词里的“第 X 组 / 单双周”可能过期，直接作废重新预热
                if (_dutyQuipCacheDate != default && _dutyQuipCacheDate != today) _dutyQuipCache.Clear();
                while (_dutyQuipCache.Count > 0)
                {
                    var dutyQuip = _dutyQuipCache.Dequeue();
                    if (string.IsNullOrWhiteSpace(dutyQuip)) continue;
                    TriggerDutyQuipPrefetch(contextHint);
                    return Task.FromResult<string?>(dutyQuip);
                }
            }
            TriggerDutyQuipPrefetch(contextHint);
            return Task.FromResult<string?>(null);
        }

        lock (_quipGate)
        {
            while (_quipCache.Count > 0)
            {
                var quip = _quipCache.Dequeue();
                if (string.IsNullOrWhiteSpace(quip)) continue;
                TriggerQuipPrefetch(contextHint);
                return Task.FromResult<string?>(quip);
            }
        }

        TriggerQuipPrefetch(contextHint);
        return Task.FromResult<string?>(null);
    }

    public async Task<string?> TryGenerateGreetingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsModelLoaded) return null;
        var prompt = AiPromptBuilder.BuildGreetingPrompt(null, TryCaptureDynamicContextBlock());
        var text = await RunRawPromptAsync(prompt, maxTokens: 64, temperature: 0.85, nonBlocking: true,
            cancellationToken).ConfigureAwait(false);
        var greeting = AiTextSanitizer.CleanAssistantText(text);
        greeting = AiTextSanitizer.LimitLength(greeting, 60);
        if (!AiTextSanitizer.IsUsableReply(greeting)) return null;
        Log.Info("AI 开场白：" + greeting);
        return greeting;
    }

    /// <summary>触发后台预取宠物台词（最多缓存 8 条；已在预取或缓存足够则跳过）。</summary>
    private void TriggerQuipPrefetch(string? contextHint = null)
    {
        try
        {
            if (!IsModelLoaded) return;
            var ai = SafeSettings();
            if (!ai.EnableAiQuips) return;

            lock (_quipGate)
            {
                if (_quipCache.Count >= 5) return;
            }
            if (Interlocked.CompareExchange(ref _quipPrefetchRunning, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!IsModelLoaded) return;
                    var prompt = AiPromptBuilder.BuildQuipPrompt(contextHint, 5);
                    var raw = await RunRawPromptAsync(prompt, maxTokens: 180, temperature: 0.95,
                        nonBlocking: true, CancellationToken.None).ConfigureAwait(false);
                    var lines = AiTextSanitizer.ParseQuipLines(raw, 5);
                    if (lines.Count == 0)
                    {
                        Log.Info("AI 台词预取：本次没有解析出可用台词（将回退本地台词）");
                        return;
                    }
                    lock (_quipGate)
                    {
                        foreach (var line in lines)
                        {
                            if (_quipCache.Count >= 8) break;
                            if (_quipCache.Contains(line)) continue;
                            _quipCache.Enqueue(line);
                        }
                    }
                    Log.Info($"AI 台词预取成功 {lines.Count} 条：{string.Join(" / ", lines)}");
                }
                catch (Exception ex)
                {
                    Log.Warn("AI 台词预取失败（不影响本地台词）：" + ShortMessage(ex));
                }
                finally
                {
                    Interlocked.Exchange(ref _quipPrefetchRunning, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("AI 台词预取调度失败：" + ShortMessage(ex));
            Interlocked.Exchange(ref _quipPrefetchRunning, 0);
        }
    }

    /// <summary>
    /// v1.4：值日相关场景的后台台词预取。system prompt 注入今天的值日/值周与单双周上下文，
    /// 生成 3 条短台词进“值日台词缓存”；没有值日/单双周配置或失败时静默放弃（调用方回退本地台词）。
    /// </summary>
    private void TriggerDutyQuipPrefetch(string? contextHint = null)
    {
        try
        {
            if (!IsModelLoaded) return;
            var ai = SafeSettings();
            if (!ai.EnableAiQuips) return;

            lock (_quipGate)
            {
                if (_dutyQuipCache.Count >= 3) return;
            }

            if (Interlocked.CompareExchange(ref _dutyQuipPrefetchRunning, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!IsModelLoaded) return;

                    // 采集放到后台线程：不占用宠物点击所在的 UI 线程
                    var dynamicContext = TryCaptureDynamicContextBlock();
                    if (string.IsNullOrWhiteSpace(dynamicContext))
                    {
                        // 没有值日/单双周配置时不浪费一次生成，交给本地台词
                        return;
                    }

                    var hint = string.IsNullOrWhiteSpace(contextHint) ? "放学后的值日提醒" : contextHint;
                    var prompt = AiPromptBuilder.BuildQuipPrompt(hint, 3, dynamicContext, dutyFocus: true);
                    var raw = await RunRawPromptAsync(prompt, maxTokens: 150, temperature: 0.9,
                        nonBlocking: true, CancellationToken.None).ConfigureAwait(false);
                    var lines = AiTextSanitizer.ParseQuipLines(raw, 3);
                    if (lines.Count == 0)
                    {
                        Log.Info("AI 值日台词预取：本次没有解析出可用台词（回退本地台词）");
                        return;
                    }
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    lock (_quipGate)
                    {
                        _dutyQuipCacheDate = today;
                        foreach (var line in lines)
                        {
                            if (_dutyQuipCache.Count >= 4) break;
                            if (_dutyQuipCache.Contains(line)) continue;
                            _dutyQuipCache.Enqueue(line);
                        }
                    }
                    Log.Info($"AI 值日台词预取成功 {lines.Count} 条：{string.Join(" / ", lines)}");
                }
                catch (Exception ex)
                {
                    Log.Warn("AI 值日台词预取失败（回退本地台词）：" + ShortMessage(ex));
                }
                finally
                {
                    Interlocked.Exchange(ref _dutyQuipPrefetchRunning, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("AI 值日台词预取调度失败：" + ShortMessage(ex));
            Interlocked.Exchange(ref _dutyQuipPrefetchRunning, 0);
        }
    }

    /// <summary>
    /// v1.4：采集动态上下文块（当前日期/星期、单双周、值日摘要），拼成可放进 system prompt 的文本。
    /// AppServices 未初始化（如单元测试）、服务未配置或任何异常时返回 null，行为与 v1.3 一致。
    /// </summary>
    private static string? TryCaptureDynamicContextBlock()
    {
        try
        {
            if (!AppServices.IsInitialized) return null;
            return AiPromptContextProvider.TryCaptureSystemBlock();
        }
        catch (Exception ex)
        {
            Log.Warn("采集 AI 动态上下文失败（已忽略，不影响生成）：" + ex.Message);
            return null;
        }
    }

    private async Task<string?> RunRawPromptAsync(string prompt, int maxTokens, double temperature, bool nonBlocking,
        CancellationToken cancellationToken)
    {
        if (!TryGetModelSnapshot(out var weights, out var modelParams) || weights is null || modelParams is null)
            return null;

        var acquired = false;
        var sb = new StringBuilder();
        try
        {
            if (nonBlocking)
            {
                acquired = await _generateLock.WaitAsync(0, cancellationToken).ConfigureAwait(false);
                if (!acquired) return null;
            }
            else
            {
                await _generateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
            }

            using var context = weights.CreateContext(modelParams);
            var executor = new InteractiveExecutor(context);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = new List<string> { AiPromptBuilder.ImEnd, AiPromptBuilder.ImStart },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = (float)Math.Clamp(temperature, 0.0, 1.5),
                    TopP = 0.92f,
                    TopK = 50,
                    RepeatPenalty = 1.1f
                }
            };

            await foreach (var delta in executor.InferAsync(prompt, inferenceParams, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(delta)) sb.Append(delta);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("AI 短文本生成失败：" + ShortMessage(ex));
            return null;
        }
        finally
        {
            if (acquired) { try { _generateLock.Release(); } catch { /* ignore */ } }
        }

        var text = AiTextSanitizer.CleanReply(sb.ToString());
        return AiTextSanitizer.IsUsableReply(text) ? text : null;
    }

    // =========================================================================
    // 内部工具
    // =========================================================================

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>一次性取“权重 + 上下文参数”快照，避免与 Unload 并发时出现半空状态。</summary>
    private bool TryGetModelSnapshot(out LLamaWeights? weights, out ModelParams? modelParams)
    {
        lock (_gate)
        {
            weights = _weights;
            modelParams = _modelParams;
            return weights is not null && modelParams is not null;
        }
    }

    private AiSettings SafeSettings()
    {
        try { return _settings.Current.Ai; }
        catch { return new AiSettings(); }
    }

    private Func<string, int> MakeTokenCounter(LLamaContext context)
    {
        try
        {
            return text =>
            {
                try
                {
                    return Math.Max(1, context.Tokenize(text, false, true).Length);
                }
                catch
                {
                    return AiPromptBuilder.EstimateTokens(text);
                }
            };
        }
        catch
        {
            return AiPromptBuilder.EstimateTokens;
        }
    }

    /// <summary>
    /// 安全地更新设置：若当前不在 UI 线程，则调度到 UI 线程再改。
    /// 这样 Settings.Changed 的订阅方（各窗口）可以安全地按 UI 线程假设处理，不会因为 AI 后台任务而跨线程崩溃。
    /// </summary>
    private void UpdateSettingsSafe(Action<AppSettings> mutate)
    {
        if (mutate is null) return;
        try
        {
            var app = Application.Current;
            var dispatcher = app?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try { _settings.Update(mutate); }
                    catch (Exception ex) { Log.Warn("保存 AI 设置失败（不影响当前使用）：" + ex.Message); }
                }));
                return;
            }
            _settings.Update(mutate);
        }
        catch (Exception ex)
        {
            Log.Warn("保存 AI 设置失败（不影响当前使用）：" + ex.Message);
        }
    }

    private static void InvokeDelta(Action<string>? onDelta, string delta)
    {
        if (onDelta is null) return;
        try { onDelta(delta); }
        catch (Exception ex) { Log.Warn("AI 流式回调处理失败（已忽略）：" + ShortMessage(ex)); }
    }

    private void SetStatus(AiModelStatus status, string? name = null, string? path = null)
    {
        lock (_gate)
        {
            _status = status;
            if (name is not null) _loadedName = name;
            if (path is not null) _loadedPath = path;
        }
        RaiseStatusChanged();
    }

    private void ClearQuipCache()
    {
        lock (_quipGate)
        {
            _quipCache.Clear();
            _dutyQuipCache.Clear();
            _dutyQuipCacheDate = default;
        }
    }

    private void RaiseStatusChanged()
    {
        var handler = StatusChanged;
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler)d)(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("AI 状态变化事件处理失败", ex); }
        }
    }

    private void RaiseDownloadProgress(long received, long total, string phase)
    {
        var handler = DownloadProgress;
        if (handler is null) return;
        var args = new AiDownloadProgress
        {
            BytesReceived = Math.Max(0, received),
            TotalBytes = Math.Max(0, total),
            Phase = string.IsNullOrWhiteSpace(phase) ? "下载中" : phase
        };
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler<AiDownloadProgress>)d)(this, args); }
            catch (Exception ex) { Log.Error("AI 下载进度事件处理失败", ex); }
        }
    }

    private static long GetFileLengthSafe(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn($"删除文件失败（{path}）：{ex.Message}"); }
    }

    private static long ParseTotalBytes(HttpResponseMessage response, long fallback)
    {
        try
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.HasLength == true && range.Length is not null && range.Length > 0)
                return range.Length.Value;
        }
        catch { /* ignore */ }
        return fallback;
    }

    private static string ShortMessage(Exception ex)
    {
        var msg = ex is AggregateException agg && agg.InnerException is not null ? agg.InnerException.Message : ex.Message;
        return msg.Length > 200 ? msg[..200] : msg;
    }

    private static string TruncateForLog(string text)
        => string.IsNullOrEmpty(text) ? "" : (text.Length > 300 ? text[..300] + "…" : text);

    private void OnProcessExit(object? sender, EventArgs e)
    {
        try { Unload(); } catch { /* 进程退出，尽力而为 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; } catch { /* ignore */ }
        Unload();
    }
}




