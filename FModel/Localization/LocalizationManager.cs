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

    public void ApplyUiLanguage(EUiLanguage language)
    {
        CurrentCulture = language switch
        {
            EUiLanguage.SimplifiedChinese => CultureInfo.GetCultureInfo("zh-CN"),
            EUiLanguage.English => CultureInfo.GetCultureInfo("en"),
            _ => CultureInfo.CurrentUICulture
        };
    }
}
