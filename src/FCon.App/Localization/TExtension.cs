using System.Windows.Data;
using System.Windows.Markup;
using FCon.Core.Localization;

namespace FCon.App.Localization;

/// <summary>
/// <c>{loc:T Key}</c> in XAML. Produces a one-way binding to the localizer's indexer
/// rather than a value, so the text follows a language change without reloading the
/// window. Works on any dependency property, including column headers and tooltips.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Localizer.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
