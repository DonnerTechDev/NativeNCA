using System.ComponentModel;
using System.Globalization;

namespace NativeNCA.Core.Localization;

/// <summary>The "Язык" setting: follow Windows or a fixed language.</summary>
public enum AppLanguage
{
    System,
    Kazakh,
    Russian,
    English
}

/// <summary>
/// Current UI language for the whole application. XAML binds to the indexer (<c>[Key]</c>) through the
/// <c>{l:Loc Key}</c> markup extension; switching the language raises <c>Item[]</c>, so every bound text
/// changes at once, and <see cref="CultureChanged"/>, so view models re-raise their computed texts.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    public static LocalizationSource Instance { get; } = new();

    /// <summary>Windows display language and regional format, captured before the app changes them.</summary>
    public static CultureInfo SystemUICulture { get; } = CultureInfo.CurrentUICulture;
    public static CultureInfo SystemFormatCulture { get; } = CultureInfo.CurrentCulture;

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    private LocalizationSource()
    {
        // Russian is the neutral language of Strings.resx: deterministic until the app picks a language.
        Culture = Russian;
        FormatCulture = CultureInfo.GetCultureInfo("ru-RU");
        Strings.Culture = Culture;
    }

    /// <summary>Neutral UI culture: ru, kk or en.</summary>
    public CultureInfo Culture { get; private set; }

    /// <summary>Specific culture for dates and numbers in the chosen language.</summary>
    public CultureInfo FormatCulture { get; private set; }

    public AppLanguage Language { get; private set; } = AppLanguage.Russian;

    public string this[string key] => Strings.ResourceManager.GetString(key, Culture) ?? key;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed (on the thread that changed it).</summary>
    public event EventHandler? CultureChanged;

    /// <summary>"Как в Windows": Kazakh and Russian Windows get their language, everything else English.</summary>
    public static string ResolveCultureName(AppLanguage language, CultureInfo systemUiCulture) => language switch
    {
        AppLanguage.Kazakh => "kk",
        AppLanguage.Russian => "ru",
        AppLanguage.English => "en",
        _ => systemUiCulture.TwoLetterISOLanguageName switch
        {
            "kk" => "kk",
            "ru" => "ru",
            _ => "en"
        }
    };

    public void SetLanguage(AppLanguage language) => SetLanguage(language, SystemUICulture);

    public void SetLanguage(AppLanguage language, CultureInfo systemUiCulture)
    {
        Language = language;
        SetCulture(ResolveCultureName(language, systemUiCulture));
    }

    private void SetCulture(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        bool changed = !culture.Equals(Culture);

        Culture = culture;
        FormatCulture = SpecificFormatCulture(culture);
        Strings.Culture = culture;

        if (!changed) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Windows regional format when it is in the same language (ru-KZ, en-GB), otherwise a default.</summary>
    private static CultureInfo SpecificFormatCulture(CultureInfo language)
    {
        if (SystemFormatCulture.TwoLetterISOLanguageName == language.TwoLetterISOLanguageName && !SystemFormatCulture.IsNeutralCulture)
            return SystemFormatCulture;

        return CultureInfo.GetCultureInfo(language.Name switch
        {
            "kk" => "kk-KZ",
            "ru" => "ru-RU",
            _ => "en-US"
        });
    }

    public static string ToSetting(AppLanguage language) => language switch
    {
        AppLanguage.Kazakh => "kk",
        AppLanguage.Russian => "ru",
        AppLanguage.English => "en",
        _ => "system"
    };

    public static AppLanguage FromSetting(string? value) => value switch
    {
        "kk" => AppLanguage.Kazakh,
        "ru" => AppLanguage.Russian,
        "en" => AppLanguage.English,
        _ => AppLanguage.System
    };
}
