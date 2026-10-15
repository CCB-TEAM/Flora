using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace FModel.Localization;

[MarkupExtensionReturnType(typeof(object))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension() { }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key
        };
        return binding.ProvideValue(serviceProvider);
    }
}
