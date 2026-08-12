using System;
using System.Windows;
using Microsoft.Win32;

namespace HeartRater.Services;

/// <summary>
/// 跟随系统深浅色主题：读注册表 AppsUseLightTheme，监听系统切换并热替换主题字典。
/// 所有颜色资源以 DynamicResource 引用，切换即时生效。
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    // 相对 URI 从当前程序集加载（程序集名无关，便于复用/测试）
    private const string LightThemeUri = "Themes/LightTheme.xaml";
    private const string DarkThemeUri = "Themes/DarkTheme.xaml";

    private static ResourceDictionary? _lightTheme;
    private static ResourceDictionary? _darkTheme;
    private static bool _hooked;
    private static bool _currentIsLight = true;

    /// <summary>当前是否亮色主题。</summary>
    public static bool IsLightTheme => _currentIsLight;

    /// <summary>初始化：读取系统主题并挂载（保持 WinUIStyles 在主题字典之后）。</summary>
    public static void Initialize(ResourceDictionary appResources)
    {
        _lightTheme = LoadTheme(LightThemeUri);
        _darkTheme = LoadTheme(DarkThemeUri);

        // 把两套主题字典插入应用资源末尾（在 WinUIStyles 之后，控件模板的 DynamicResource 才能解析）
        appResources.MergedDictionaries.Add(_lightTheme);
        appResources.MergedDictionaries.Add(_darkTheme);

        // 立即应用当前系统主题（默认亮色，若系统为深色则切换）
        var systemLight = IsSystemLightTheme();
        if (!systemLight)
        {
            ApplyTheme(light: false);
        }

        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    private static ResourceDictionary LoadTheme(string uri)
    {
        return new ResourceDictionary { Source = new Uri(uri, UriKind.Relative) };
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // UserPreferenceCategory.General 与颜色相关；主题切换属于 General
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        var systemLight = IsSystemLightTheme();
        if (systemLight != _currentIsLight)
        {
            ApplyTheme(systemLight);
        }
    }

    private static void ApplyTheme(bool light)
    {
        _currentIsLight = light;

        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        // 亮/暗字典交换可见性（把当前主题置底使其生效）
        var dict = app.Resources.MergedDictionaries;
        var active = light ? _lightTheme : _darkTheme;
        var inactive = light ? _darkTheme : _lightTheme;
        dict.Remove(active);
        dict.Remove(inactive);
        dict.Add(inactive);
        dict.Add(active);
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i != 0;
        }
        catch
        {
            return true; // 读不到默认亮色
        }
    }
}
