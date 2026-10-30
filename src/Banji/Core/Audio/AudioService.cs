using System.Media;
using System.Security.Cryptography;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;

namespace Banji.Core.Audio;

/// <summary>
/// “清醒一下”音效服务：所有音效都在程序内**实时合成**（44.1kHz / 16bit / 单声道 WAV 内存流），
/// 不依赖任何外部音频文件或网络，音效时长均 ≤0.3 秒、单拍、无预兆。
/// 音量取 <c>Settings.StingVolume</c>（0~1，默认 1.0 = 应用内最大音量，满振幅）。
/// 播放失败静默容错并写日志，绝不打断上课。
/// </summary>
public sealed class AudioService : IAudioService
{
    public const int SampleRate = 44100;
    public const int BitsPerSample = 16;
    public const int Channels = 1;

    /// <summary>音效数量（随机挑选的范围）。</summary>
    public const int StingCount = 5;

    private static readonly object PlayerGate = new();
    private static SoundPlayer? _player;

    private readonly Func<double> _volumeProvider;

    /// <summary>生产用无参构造：音量实时读取设置（设置未初始化时按最大音量）。</summary>
    public AudioService()
        : this(ReadVolumeFromSettings)
    {
    }

    /// <summary>测试用：注入音量来源。</summary>
    public AudioService(Func<double> volumeProvider)
    {
        _volumeProvider = volumeProvider ?? (() => 1.0);
    }

    /// <summary>“清醒一下”：随机挑一个单拍音效，以应用内最大音量播放。</summary>
    public void PlayRandomSting()
    {
        try
        {
            var variant = RandomNumberGenerator.GetInt32(StingCount);
            var volume = 1.0;
            try { volume = _volumeProvider(); } catch { volume = 1.0; }
            if (double.IsNaN(volume)) volume = 1.0;
            volume = Math.Clamp(volume, 0.0, 1.0);

            var wav = SynthesizeSting(variant, volume);
            var stream = new MemoryStream(wav, writable: false);
            var player = new SoundPlayer(stream);

            lock (PlayerGate)
            {
                var old = _player;
                _player = player;
                try { old?.Dispose(); } catch { /* ignore */ }
            }

            player.Play(); // 异步播放，不阻塞 UI 线程
        }
        catch (Exception ex)
        {
            Log.Error("清醒一下音效播放失败（已静默忽略）", ex);
        }
    }

    private static double ReadVolumeFromSettings()
    {
        try
        {
            return AppServices.IsInitialized ? AppServices.Current.Settings.Current.StingVolume : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    // =========================================================================
    // 合成（internal 给单测验证时长/振幅；同时公开成 public static 便于测试工程直接调用）
    // =========================================================================

    /// <summary>
    /// 合成指定编号的单拍音效，返回完整的 WAV 文件字节流（44 字节标准头 + PCM 数据）。
    /// </summary>
    /// <param name="variant">音效编号 0~4。</param>
    /// <param name="volume">音量 0~1（会再做一次 clamp）。</param>
    public static byte[] SynthesizeSting(int variant, double volume = 1.0)
    {
        if (variant < 0 || variant >= StingCount) throw new ArgumentOutOfRangeException(nameof(variant));
        if (double.IsNaN(volume)) volume = 1.0;
        volume = Math.Clamp(volume, 0.0, 1.0);

        var samples = variant switch
        {
            0 => Clap(),
            1 => Woodblock(),
            2 => Ding(),
            3 => Kick(),
            _ => Snap()
        };

        Normalize(samples, 0.98); // 每种音效都压到接近满振幅
        var pcm = ToPcm16(samples, volume);
        return BuildWav(pcm);
    }

    /// <summary>音效中文名（日志/设置预览用）。</summary>
    public static string StingName(int variant) => variant switch
    {
        0 => "噪声拍手",
        1 => "木块",
        2 => "叮",
        3 => "鼓点",
        _ => "响指"
    };

    /// <summary>WAV 时长（秒），便于测试断言 ≤0.3s。</summary>
    public static double WavDurationSeconds(byte[] wav)
    {
        if (wav is null || wav.Length < 44) return 0;
        var channels = BitConverter.ToInt16(wav, 22);
        var sampleRate = BitConverter.ToInt32(wav, 24);
        var bits = BitConverter.ToInt16(wav, 34);
        if (channels <= 0 || sampleRate <= 0 || bits <= 0) return 0;
        var dataBytes = wav.Length - 44;
        return dataBytes / (double)(sampleRate * channels * bits / 8);
    }

    // ------------------------------------------------------------------ 各种音效

    /// <summary>噪声拍手：宽带噪声 + 极快衰减，约 0.16s。</summary>
    private static double[] Clap()
    {
        var n = Seconds(0.16);
        var data = new double[n];
        var rng = new Random(20260101);
        double prev = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var noise = rng.NextDouble() * 2 - 1;
            // 简单一阶高通 + 短促包络，听起来像“啪”
            var filtered = noise - 0.55 * prev;
            prev = noise;
            var attack = Math.Min(1.0, t / 0.0015);
            var env = attack * Math.Exp(-t / 0.028);
            data[i] = filtered * env;
        }
        return data;
    }

    /// <summary>木块：1kHz 短正弦 + 起始瞬态噪声，约 0.11s。</summary>
    private static double[] Woodblock()
    {
        var n = Seconds(0.11);
        var data = new double[n];
        var rng = new Random(20260202);
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var attack = Math.Min(1.0, t / 0.0008);
            var env = attack * Math.Exp(-t / 0.035);
            var tone = Math.Sin(2 * Math.PI * 1050 * t) * 0.85
                       + Math.Sin(2 * Math.PI * 2100 * t) * 0.15;
            var click = i < Seconds(0.004) ? (rng.NextDouble() * 2 - 1) * 0.6 * Math.Exp(-t / 0.0012) : 0;
            data[i] = (tone + click) * env;
        }
        return data;
    }

    /// <summary>“叮”：C7 基频 + 八度泛音，清脆短铃，约 0.28s。</summary>
    private static double[] Ding()
    {
        var n = Seconds(0.28);
        var data = new double[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var attack = Math.Min(1.0, t / 0.001);
            var env = attack * Math.Exp(-t / 0.09);
            var tone = Math.Sin(2 * Math.PI * 2093 * t) * 0.72
                       + Math.Sin(2 * Math.PI * 4186 * t) * 0.28 * Math.Exp(-t / 0.05)
                       + Math.Sin(2 * Math.PI * 6278 * t) * 0.10 * Math.Exp(-t / 0.03);
            data[i] = tone * env;
        }
        return data;
    }

    /// <summary>鼓点：频率从 150Hz 快速下滑到 50Hz 的低频“咚”，约 0.24s。</summary>
    private static double[] Kick()
    {
        var n = Seconds(0.24);
        var data = new double[n];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var freq = 50 + 100 * Math.Exp(-t / 0.025);
            phase += 2 * Math.PI * freq / SampleRate;
            var attack = Math.Min(1.0, t / 0.0015);
            var env = attack * Math.Exp(-t / 0.07);
            data[i] = Math.Sin(phase) * env;
        }
        return data;
    }

    /// <summary>响指：极短高频噪声爆点，约 0.07s。</summary>
    private static double[] Snap()
    {
        var n = Seconds(0.07);
        var data = new double[n];
        var rng = new Random(20260303);
        double prev = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var noise = rng.NextDouble() * 2 - 1;
            var high = noise - 0.85 * prev; // 更强的高通，做出“哒”的脆感
            prev = noise;
            var env = Math.Exp(-t / 0.009);
            data[i] = high * env;
        }
        return data;
    }

    // ------------------------------------------------------------------ 工具

    private static int Seconds(double seconds) => Math.Max(1, (int)Math.Round(seconds * SampleRate));

    /// <summary>归一化到目标峰值（默认 0.98，避免削波同时保留“满振幅”的响度）。</summary>
    private static void Normalize(double[] samples, double peak)
    {
        var max = 0.0;
        foreach (var s in samples) max = Math.Max(max, Math.Abs(s));
        if (max <= 1e-9) return;
        var scale = peak / max;
        for (var i = 0; i < samples.Length; i++) samples[i] *= scale;

        // 末尾 3ms 淡出，避免截断爆音
        var fade = Math.Min(samples.Length, Seconds(0.003));
        for (var i = 0; i < fade; i++)
        {
            var k = i / (double)fade;
            samples[samples.Length - 1 - i] *= k;
        }
    }

    private static byte[] ToPcm16(double[] samples, double volume)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i] * volume;
            var v = (short)Math.Clamp(Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>拼标准 44 字节 WAV 头。</summary>
    private static byte[] BuildWav(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        var blockAlign = Channels * BitsPerSample / 8;
        var byteRate = SampleRate * blockAlign;

        WriteAscii(wav, 0, "RIFF");
        WriteInt32(wav, 4, 36 + pcm.Length);
        WriteAscii(wav, 8, "WAVE");
        WriteAscii(wav, 12, "fmt ");
        WriteInt32(wav, 16, 16); // fmt chunk size
        WriteInt16(wav, 20, 1); // PCM
        WriteInt16(wav, 22, Channels);
        WriteInt32(wav, 24, SampleRate);
        WriteInt32(wav, 28, byteRate);
        WriteInt16(wav, 32, blockAlign);
        WriteInt16(wav, 34, BitsPerSample);
        WriteAscii(wav, 36, "data");
        WriteInt32(wav, 40, pcm.Length);
        Buffer.BlockCopy(pcm, 0, wav, 44, pcm.Length);
        return wav;
    }

    private static void WriteAscii(byte[] buffer, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++) buffer[offset + i] = (byte)text[i];
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
