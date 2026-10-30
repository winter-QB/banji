using Banji.Core.Audio;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>“清醒一下”音效合成单测：不依赖真实音频设备，只验证 WAV 数据本身。</summary>
public static class TimerAudioTests
{
    [BanjiTest(Name = "音效 WAV 时长均不超过 0.3 秒")]
    public static void 音效时长不超过03秒()
    {
        for (var variant = 0; variant < AudioService.StingCount; variant++)
        {
            var wav = AudioService.SynthesizeSting(variant, 1.0);
            var seconds = AudioService.WavDurationSeconds(wav);
            Assert.True(seconds > 0.02, $"音效#{variant} 时长过短：{seconds:F4}s");
            Assert.True(seconds <= 0.3, $"音效#{variant}（{AudioService.StingName(variant)}）时长超过 0.3s：{seconds:F4}s");
        }
    }

    [BanjiTest(Name = "音效 WAV 头合法（44.1kHz/16bit/单声道/满振幅）")]
    public static void 音效Wav头合法且满振幅()
    {
        for (var variant = 0; variant < AudioService.StingCount; variant++)
        {
            var wav = AudioService.SynthesizeSting(variant, 1.0);

            Assert.True(wav.Length > 44, "WAV 数据为空");
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
            Assert.Equal((short)1, BitConverter.ToInt16(wav, 20), "应为 PCM 格式");
            Assert.Equal((short)AudioService.Channels, BitConverter.ToInt16(wav, 22));
            Assert.Equal(AudioService.SampleRate, BitConverter.ToInt32(wav, 24));
            Assert.Equal((short)AudioService.BitsPerSample, BitConverter.ToInt16(wav, 34));
            Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40), "data 块长度应为总长度 - 44");

            // 满振幅：峰值至少达到 16bit 满幅的 ~80%
            var max = 0;
            for (var i = 44; i + 1 < wav.Length; i += 2)
            {
                var v = Math.Abs((int)BitConverter.ToInt16(wav, i));
                if (v > max) max = v;
            }
            Assert.True(max >= 26000, $"音效#{variant} 峰值振幅过低：{max}");
        }
    }

    [BanjiTest(Name = "音量按设置缩放（默认 1.0 = 应用内最大音量）")]
    public static void 音量按比例缩放()
    {
        var full = Peak(AudioService.SynthesizeSting(2, 1.0));
        var half = Peak(AudioService.SynthesizeSting(2, 0.5));
        var mute = Peak(AudioService.SynthesizeSting(2, 0.0));

        Assert.Equal(0, mute);
        Assert.True(half > full * 0.35 && half < full * 0.65,
            $"0.5 音量峰值应约为满音量一半：full={full}, half={half}");
    }

    [BanjiTest(Name = "StingVolume 默认值 1.0 且在 0~1 之间")]
    public static void 默认音量为最大()
    {
        var settings = new AppSettings();
        settings.Normalize();
        Assert.Equal(1.0, settings.StingVolume);
        Assert.InRange(settings.StingVolume, 0.0, 1.0);
    }

    [BanjiTest(Name = "合成器与设置无关可重复调用（线程安全无异常）")]
    public static void 合成器可重复调用()
    {
        for (var i = 0; i < 20; i++)
        {
            var v = i % AudioService.StingCount;
            var wav = AudioService.SynthesizeSting(v, 1.0);
            Assert.True(wav.Length > 44);
        }
    }

    private static int Peak(byte[] wav)
    {
        var max = 0;
        for (var i = 44; i + 1 < wav.Length; i += 2)
        {
            var v = Math.Abs((int)BitConverter.ToInt16(wav, i));
            if (v > max) max = v;
        }
        return max;
    }
    [BanjiTest(Name = "按钮四态透明度语义：10% 为轻微透明，0% 完全不透明")]
    public static void 按钮透明度语义()
    {
        // 开发文档的“透明度”是透明程度：常态 10% → alpha 90%，悬浮/按下/强调 0% → 完全不透明
        var idle = Banji.Controls.Toolbar.ToolButtonStyler.CreateBrush("#317FFF", 10);
        Assert.Equal((byte)230, idle.Color.A, "透明度 10% 应约为 90% 不透明");
        var hover = Banji.Controls.Toolbar.ToolButtonStyler.CreateBrush("#5998FF", 0);
        Assert.Equal((byte)255, hover.Color.A, "透明度 0% 应完全不透明");
        var transparent = Banji.Controls.Toolbar.ToolButtonStyler.CreateBrush("#FF3030", 100);
        Assert.Equal((byte)0, transparent.Color.A, "透明度 100% 应完全透明");
    }

    [BanjiTest(Name = "按钮颜色解析容错")]
    public static void 按钮颜色解析容错()
    {
        var fallback = System.Windows.Media.Colors.Red;
        Assert.Equal(fallback, Banji.Controls.Toolbar.ToolButtonStyler.ParseColor("not-a-color", fallback));
        Assert.Equal(fallback, Banji.Controls.Toolbar.ToolButtonStyler.ParseColor("", fallback));
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x31, 0x7F, 0xFF),
            Banji.Controls.Toolbar.ToolButtonStyler.ParseColor("317FFF", fallback));
    }
}

/// <summary>功能区按钮默认样式单测：必须严格等于开发文档第 4 章附录。</summary>
public static class TimerButtonStyleTests
{
    [BanjiTest(Name = "按钮默认四态颜色/透明度符合文档附录")]
    public static void 按钮默认样式符合文档()
    {
        var s = new AppSettings();
        s.Normalize();
        var b = s.Buttons;

        Assert.Equal("#317FFF", b.IdleColor);
        Assert.Equal(10, b.IdleOpacityPercent);
        Assert.Equal("#5998FF", b.HoverColor);
        Assert.Equal(0, b.HoverOpacityPercent);
        Assert.Equal("#0051D6", b.PressedColor);
        Assert.Equal(0, b.PressedOpacityPercent);
        Assert.Equal("#FF3030", b.AccentColor);
        Assert.Equal(0, b.AccentOpacityPercent);

        Assert.Equal(1.5, b.BorderThickness);
        Assert.Equal(8.0, b.CornerRadius); // Win11 输入法预览框观感
        Assert.Equal(4.0, b.ShadowOffset); // 右下 45°，约 3 磅/4px
        Assert.Equal(14.0, b.FontSize);
    }

    [BanjiTest(Name = "按钮样式越界收敛且非法颜色回退默认")]
    public static void 按钮样式收敛()
    {
        var b = new ButtonStyleSettings
        {
            IdleColor = "not-a-color",
            HoverOpacityPercent = 250,
            BorderThickness = -3,
            CornerRadius = 999,
            ShadowOffset = 999,
            FontSize = 1
        };
        b.Normalize();
        Assert.Equal("#317FFF", b.IdleColor);
        Assert.Equal(100, b.HoverOpacityPercent);
        Assert.Equal(0.0, b.BorderThickness);
        Assert.Equal(40.0, b.CornerRadius);
        Assert.Equal(24.0, b.ShadowOffset);
        Assert.Equal(8.0, b.FontSize);
    }
}
