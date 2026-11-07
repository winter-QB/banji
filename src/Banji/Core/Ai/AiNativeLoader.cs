using System.Reflection;
using System.Runtime.InteropServices;
using Banji.Core.Infrastructure;

namespace Banji.Core.Ai;

/// <summary>
/// LLamaSharp 原生库（llama/ggml）加载辅助。
///
/// 背景：本应用是 net8.0-windows，而 LLamaSharp 0.30.0 只提供 net10.0 / netstandard2.0 两个目标框架，
/// 于是 NuGet 解析到 netstandard2.0 程序集；该版本里 <c>NativeApi.SetDllImportResolver</c> 是空实现，
/// 不会自动去 <c>runtimes\{rid}\native\{avx}</c> 找原生库（实测直接调用会抛“native library cannot be loaded”）。
/// 这里在真正加载模型前，按 CPU 支持的 AVX 档位找到原生库目录，用 SetDllDirectory + 按依赖顺序显式加载，
/// 之后 LLamaSharp 里的 DllImport("llama") 就能解析到已加载的模块。
///
/// 单文件发布时若原生库被解压到别处（.NET 会自动把解压目录加入 DLL 搜索路径），本类找不到目录时会静默跳过，
/// 不影响系统默认的 DllImport 解析。
/// </summary>
public static class AiNativeLoader
{
    private static readonly object Gate = new();
    private static bool _attempted;
    private static bool _loaded;
    private static string _message = "尚未尝试加载";

    // NativeLibrary.Load 的句柄必须保持存活，直到进程退出（LLamaSharp 后续会再次 LoadLibrary 同名库）
    private static readonly List<IntPtr> KeepAlive = new();

    private static readonly string[] WindowsDllOrder =
        { "ggml-base.dll", "ggml-cpu.dll", "ggml.dll", "llama.dll", "mtmd.dll" };

    private static readonly string[] UnixLibOrder =
        { "libggml-base.so", "libggml-cpu.so", "libggml.so", "libllama.so", "libmtmd.so" };

    /// <summary>最近一次加载结果的说明（中文，用于日志与错误提示）。</summary>
    public static string Message
    {
        get { lock (Gate) return _message; }
    }

    /// <summary>是否已成功加载原生库（或已经尝试过且系统默认解析可用）。</summary>
    public static bool IsLoaded
    {
        get { lock (Gate) return _loaded; }
    }

    /// <summary>
    /// 幂等地准备原生库；多次调用只有第一次真正执行。
    /// 返回 true 表示找到了并成功加载 llama 原生库；false 表示没找到（此时仍可能由系统默认路径解析成功）。
    /// </summary>
    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_attempted) return _loaded;
            _attempted = true;
            try
            {
                _loaded = LoadCore();
            }
            catch (Exception ex)
            {
                _message = "准备原生推理库时出错：" + ex.Message;
                Log.Warn("AI 原生库准备失败：" + ex);
                _loaded = false;
            }
            return _loaded;
        }
    }

    private static bool LoadCore()
    {
        // 关键前置：LLamaSharp 0.30 的原生库用新版 MSVC 构建，需要 msvcp140 >= 14.4x。
        // Windows 11 自带的 msvcp140 是 14.26（旧版 STL ABI），直接用会在 ggml 的 C++ 互斥量初始化处空指针崩溃。
        // 必须在加载 llama/ggml 之前，把一个新版本 msvcp140 显式加载进进程（同名模块后续会被 import 直接复用）。
        EnsureCompatibleVCppRuntime();

        var dirs = CandidateDirectories();
        if (dirs.Count == 0)
        {
            _message = "未找到本地原生推理库目录（发布版若已内嵌原生库会自动解码，可忽略）";
            return false;
        }

        foreach (var (dir, avxLabel) in dirs)
        {
            if (TryLoadFrom(dir, avxLabel, out var error))
            {
                _message = $"已加载原生推理库（目录：{dir}，AVX 档位：{avxLabel}）";
                Log.Info("AI 原生库：" + _message);
                return true;
            }
            Log.Warn($"AI 原生库加载失败（{dir} / {avxLabel}）：{error}");
        }

        _message = "原生推理库加载失败，请确认安装包完整（缺失 llama/ggml 动态库）";
        Log.Error("AI 原生库全部候选目录均加载失败");
        return false;
    }

    private static bool TryLoadFrom(string dir, string avxLabel, out string error)
    {
        error = "";
        var order = OperatingSystem.IsWindows() ? WindowsDllOrder : UnixLibOrder;
        var expected = order[^2]; // llama / libllama
        var llamaPath = Path.Combine(dir, expected);
        if (!File.Exists(llamaPath))
        {
            error = "目录中缺少 " + expected;
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            // 让依赖（ggml-base/ggml/msvcp 等）可以从同目录解析
            try { NativeMethods.SetDllDirectoryW(dir); } catch { /* 非致命 */ }
        }

        var okCount = 0;
        foreach (var file in order)
        {
            var full = Path.Combine(dir, file);
            if (!File.Exists(full)) continue;
            try
            {
                var handle = NativeLibrary.Load(full);
                KeepAlive.Add(handle);
                okCount++;
            }
            catch (Exception ex)
            {
                // 核心库（llama）失败才算失败；ggml-cpu 变体等可降级
                if (file == expected)
                {
                    error = $"加载 {file} 失败：{ex.Message}";
                    return false;
                }
            }
        }

        if (okCount == 0)
        {
            error = "目录中的原生库均无法加载";
            return false;
        }
        return true;
    }

    // =========================================================================
    // VC++ 运行库兼容性（llama.cpp 0.30+ 的硬性要求）
    // =========================================================================

    /// <summary>能支撑 LLamaSharp 0.30 原生库的最低 msvcp140 版本（低于它会崩溃，而不是友好报错）。</summary>
    public static readonly Version MinVCppRuntimeVersion = new(14, 40);

    private static readonly string[] VCppCompanionDlls =
    {
        "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll", "msvcp140_1.dll", "msvcp140_2.dll",
        "vcruntime140.dll", "vcruntime140_1.dll"
    };

    /// <summary>
    /// 在加载 llama/ggml 之前，把一个足够新的 msvcp140.dll 预加载到当前进程。
    /// 查找顺序：程序目录（推荐打包时随包分发）→ 原生库目录 → 系统已知安装位置（Edge/WebView2/OneDrive/Office，
    /// 用于未随包携带时的开发机兜底）。失败只记日志，不阻断流程。
    /// </summary>
    /// <returns>成功加载的新版 msvcp140 版本；找不到返回 null。</returns>
    public static Version? EnsureCompatibleVCppRuntime()
    {
        try
        {
            var systemVersion = DescribeMsvcpVersion(Path.Combine(Environment.SystemDirectory, "msvcp140.dll"));
            var candidate = FindNewestMsvcp140();
            if (candidate is null)
            {
                Log.Warn($"AI 兼容性：未找到较新的 msvcp140.dll（系统版本 {systemVersion}，建议安装最新 VC++ 2015-2022 运行库，" +
                         "或把 msvcp140.dll 放到程序目录）。若模型加载失败，多半是这个原因。");
                return null;
            }

            var (path, version) = candidate.Value;
            if (version < MinVCppRuntimeVersion)
            {
                Log.Warn($"AI 兼容性：找到的 msvcp140.dll 版本 {version} 低于要求的 {MinVCppRuntimeVersion}（{path}），" +
                         "模型加载可能失败；请安装最新 VC++ 运行库");
                return null;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                // 把它们依赖的伴生 DLL 也从同目录加载，避免混用系统旧版本
                foreach (var companion in VCppCompanionDlls)
                {
                    var companionPath = Path.Combine(dir, companion);
                    if (!File.Exists(companionPath)) continue;
                    try
                    {
                        var h = NativeLibrary.Load(companionPath);
                        KeepAlive.Add(h);
                    }
                    catch
                    {
                        // 伴生库加载失败一般不影响，继续
                    }
                }
            }

            try
            {
                var msvcpHandle = NativeLibrary.Load(path);
                KeepAlive.Add(msvcpHandle);
            }
            catch (Exception ex)
            {
                Log.Warn($"AI 兼容性：加载新版 msvcp140 失败（{path}）：{ex.Message}");
                return null;
            }

            Log.Info($"AI 兼容性：已预加载 VC++ 运行库 msvcp140 {version}（来源：{path}；系统自带为 {systemVersion}）");
            return version;
        }
        catch (Exception ex)
        {
            Log.Warn("AI 兼容性：准备 VC++ 运行库失败（不影响继续尝试）：" + ex.Message);
            return null;
        }
    }

    private static (string Path, Version Version)? FindNewestMsvcp140()
    {
        var candidates = new List<(string Path, Version Version)>();
        var baseDir = AppContext.BaseDirectory;

        void Add(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                if (!IsX64Pe(path)) return;
                var version = ParseFileVersion(path);
                if (version is null) return;
                if (candidates.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))) return;
                candidates.Add((path, version));
            }
            catch
            {
                // 忽略无法读取的文件
            }
        }

        // 1) 程序目录（打包应随包分发；单文件发布时 AppContext.BaseDirectory 是临时解包目录，
        //    所以必须额外用 Exe 真实目录，确保随包的 msvcp140.dll 优先命中）
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (!string.IsNullOrWhiteSpace(exeDir)) Add(Path.Combine(exeDir, "msvcp140.dll"));
        Add(Path.Combine(baseDir, "msvcp140.dll"));
        // 2) 原生库目录（runtimes\rid\native\*）
        foreach (var (dir, _) in CandidateDirectories())
            Add(Path.Combine(dir, "msvcp140.dll"));

        // 3) 已知的系统安装位置（开发机兜底；Edge 几乎全 Windows 都有，版本通常很新）
        foreach (var searchRoot in KnownMsvcpSearchRoots())
        {
            try
            {
                if (!Directory.Exists(searchRoot)) continue;
                foreach (var f in Directory.GetFiles(searchRoot, "msvcp140.dll", SearchOption.TopDirectoryOnly)) Add(f);
            }
            catch
            {
                // 目录不存在/无权限，忽略
            }
        }

        foreach (var root in KnownMsvcpGlobRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                    Add(Path.Combine(dir, "msvcp140.dll"));
                Add(Path.Combine(root, "msvcp140.dll"));
            }
            catch
            {
                // ignore
            }
        }

        if (candidates.Count == 0) return null;
        var best = candidates.OrderByDescending(c => c.Version).First();
        return (best.Path, best.Version);
    }

    private static IEnumerable<string> KnownMsvcpSearchRoots()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(pf, "Microsoft", "Edge", "Application");
        yield return Path.Combine(pf86, "Microsoft", "Edge", "Application");
        yield return Path.Combine(pf86, "Microsoft", "EdgeCore");
        yield return Path.Combine(pf86, "Microsoft", "EdgeWebView", "Application");
        yield return Path.Combine(local, "Microsoft", "EdgeWebView", "Application");
        yield return Path.Combine(pf, "Microsoft", "OneDrive");
        yield return Path.Combine(pf, "Microsoft", "Office", "root", "Office16");
        yield return Path.Combine(pf, "Oray", "AweSun");
    }

    /// <summary>这些目录的下一级子目录里可能直接放着 msvcp140.dll。</summary>
    private static IEnumerable<string> KnownMsvcpGlobRoots()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf, "Microsoft", "Edge", "Application");
        yield return Path.Combine(pf86, "Microsoft", "Edge", "Application");
        yield return Path.Combine(pf86, "Microsoft", "EdgeCore");
        yield return Path.Combine(pf86, "Microsoft", "EdgeWebView", "Application");
        yield return Path.Combine(pf, "Microsoft", "OneDrive");
    }

    private static Version? ParseFileVersion(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            if (string.IsNullOrWhiteSpace(info.FileVersion)) return null;
            var text = info.FileVersion.Split(' ')[0];
            return Version.TryParse(text, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeMsvcpVersion(string path)
    {
        if (!File.Exists(path)) return "不存在";
        var v = ParseFileVersion(path);
        return v?.ToString() ?? "未知";
    }

    /// <summary>粗判 PE 是否为 x64（避免误加载 32 位运行库）。</summary>
    private static bool IsX64Pe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs);
            if (fs.Length < 0x40) return false;
            fs.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > fs.Length) return false;
            fs.Position = peOffset + 4;
            var machine = reader.ReadUInt16();
            return machine == 0x8664; // IMAGE_FILE_MACHINE_AMD64
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按优先级返回 (目录, AVX 档位) 候选列表；多个 AVX 档位按 CPU 支持从高到低排列。</summary>
    public static List<(string Directory, string AvxLabel)> CandidateDirectories()
    {
        var result = new List<(string Directory, string AvxLabel)>();
        var baseDir = AppContext.BaseDirectory;
        var rid = RuntimeInformation.RuntimeIdentifier;
        var avxLevels = SupportedAvxLevels();

        void AddIfExists(string dir, string label)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
                var probe = Path.Combine(dir, OperatingSystem.IsWindows() ? "llama.dll" : "libllama.so");
                if (!File.Exists(probe)) return;
                if (result.Any(r => string.Equals(r.Directory, dir, StringComparison.OrdinalIgnoreCase))) return;
                result.Add((dir, label));
            }
            catch
            {
                // 路径探测失败忽略
            }
        }

        // 1) 走 run.bat 时 bin 目录的 runtimes\{rid}\native\{avx}
        foreach (var avx in avxLevels)
            AddIfExists(Path.Combine(baseDir, "runtimes", rid, "native", avx), avx);

        // 2) runtimes\{rid}\native
        AddIfExists(Path.Combine(baseDir, "runtimes", rid, "native"), "any");
        // 3) 程序目录下的 native 子目录
        AddIfExists(Path.Combine(baseDir, "native"), "any");
        // 4) 原生库直接躺在程序目录（打包脚本可能这样拷）
        AddIfExists(baseDir, "any");

        // 5) 开发机兜底：NuGet 全局缓存里的 LLamaSharp.Backend.Cpu（单元测试/未生成 runtimes 时）
        AddNuGetCacheCandidates(result, rid, avxLevels, AddIfExists);

        return result;
    }

    private static void AddNuGetCacheCandidates(
        List<(string Directory, string AvxLabel)> result,
        string rid,
        List<string> avxLevels,
        Action<string, string> addIfExists)
    {
        try
        {
            var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (string.IsNullOrWhiteSpace(packagesRoot))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile)) return;
                packagesRoot = Path.Combine(userProfile, ".nuget", "packages");
            }

            var backendRoot = Path.Combine(packagesRoot, "llamasharp.backend.cpu");
            if (!Directory.Exists(backendRoot)) return;

            // 优先高版本
            var versionDirs = Directory.GetDirectories(backendRoot)
                .Select(d => new { Dir = d, Name = Path.GetFileName(d) })
                .OrderByDescending(v => ParseVersion(v.Name))
                .Select(v => v.Dir);

            foreach (var versionDir in versionDirs)
            {
                var runtimesRoot = Path.Combine(versionDir, "LLamaSharpRuntimes", rid, "native");
                if (!Directory.Exists(runtimesRoot)) continue;
                foreach (var avx in avxLevels) addIfExists(Path.Combine(runtimesRoot, avx), avx);
                addIfExists(runtimesRoot, "any");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("扫描 NuGet 缓存中的 LLamaSharp 原生库失败：" + ex.Message);
        }
    }

    private static Version ParseVersion(string name)
        => Version.TryParse(name, out var v) ? v : new Version(0, 0);

    /// <summary>CPU 支持的 AVX 档位（高→低）；非 x86 或无法判定时返回 noavx。</summary>
    public static List<string> SupportedAvxLevels()
    {
        var levels = new List<string>();
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                return new List<string> { "noavx" };

            if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported
                && System.Runtime.Intrinsics.X86.Avx512BW.IsSupported
                && System.Runtime.Intrinsics.X86.Avx512DQ.IsSupported)
                levels.Add("avx512");
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) levels.Add("avx2");
            if (System.Runtime.Intrinsics.X86.Avx.IsSupported) levels.Add("avx");
        }
        catch
        {
            // 忽略：极端平台下按 noavx 处理
        }
        levels.Add("noavx");
        return levels;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetDllDirectoryW(string lpPathName);
    }
}


