using System.Windows.Data;
using System.Windows.Markup;
using NativeNCA.Core.Localization;

namespace NativeNCA.App.Localization;

/// <summary>
/// <c>{l:Loc Settings_Title}</c>: a one-way binding to <see cref="LocalizationSource"/>'s indexer, so the text
/// changes as soon as the language is switched. Works in setters and templates too (the binding is returned
/// unresolved there) and outside the visual tree (context menus), because the source is explicit.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocalizationSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
