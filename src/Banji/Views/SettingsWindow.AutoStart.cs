using Microsoft.Win32;
using Banji.Core.Infrastructure;

namespace Banji.Views;

/// <summary>开机自启动注册（HKCU Run，无需管理员权限）。</summary>
public static class AutoStartRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Banji";

    /// <summary>注册表里是否已有开机启动项。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            Log.Error("读取开机启动项失败", ex);
            return false;
        }
    }

    /// <summary>写入/移除开机启动项。失败时返回 false 并给出友好中文错误。</summary>
    public static bool TryApply(bool enable, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法写入开机启动项（当前用户注册表不可用）。";
                return false;
            }

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    error = "找不到班机程序路径，无法设置开机启动。";
                    return false;
                }
                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(enable ? "设置开机启动失败" : "取消开机启动失败", ex);
            error = $"{(enable ? "设置" : "取消")}开机启动失败：{ex.Message}";
            return false;
        }
    }
}
