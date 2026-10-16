using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using FModel.Settings;

namespace FModel.Localization;

public sealed class LocalizationManager : INotifyPropertyChanged
{
    private static readonly ResourceManager ResourceManager = new("FModel.Localization.Strings", typeof(LocalizationManager).Assembly);
    public static LocalizationManager Instance { get; } = new();

    private CultureInfo _currentCulture = CultureInfo.InvariantCulture;
    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        private set
        {
            if (Equals(_currentCulture, value)) return;
            _currentCulture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public string this[string key] => Get(key);

    public event PropertyChangedEventHandler PropertyChanged;

    public string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        return ResourceManager.GetString(key, CurrentCulture) ?? key;
    }

    /// <summary>The localized text of a key, or false when no resource has it.</summary>
    public bool TryGet(string key, out string value)
    {
        value = string.IsNullOrWhiteSpace(key) ? null : ResourceManager.GetString(key, CurrentCulture);
        return value != null;
    }

    /// <summary>The interface ships in English and Simplified Chinese; "System" (older settings) picks one of them.</summary>
    public static EUiLanguage Resolve(EUiLanguage language) => language switch
    {
        EUiLanguage.System => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? EUiLanguage.SimplifiedChinese : EUiLanguage.English,
        _ => language
    };

    public void ApplyUiLanguage(EUiLanguage language)
    {
        CurrentCulture = Resolve(language) == EUiLanguage.SimplifiedChinese
            ? CultureInfo.GetCultureInfo("zh-CN")
            : CultureInfo.GetCultureInfo("en");
    }
}
