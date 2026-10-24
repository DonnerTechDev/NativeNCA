using System.Globalization;

namespace NativeNCA.Core.Localization;

/// <summary>Formatting helpers for <see cref="Strings"/> in the current language.</summary>
public static class Loc
{
    /// <summary>Separates the plural forms of a <c>*_Plural</c> resource.</summary>
    public const char PluralSeparator = '|';

    public static CultureInfo Culture => LocalizationSource.Instance.Culture;

    public static CultureInfo FormatCulture => LocalizationSource.Instance.FormatCulture;

    /// <summary><see cref="string.Format(IFormatProvider, string, object[])"/> with the chosen culture's number format.</summary>
    public static string Format(string format, params object?[] args) => string.Format(FormatCulture, format, args);

    /// <summary>Short date in the chosen culture (31.12.2026 / 12/31/2026).</summary>
    public static string Date(DateTime value) => value.ToString("d", FormatCulture);

    /// <summary>Short time in the chosen culture (14:32 / 2:32 PM).</summary>
    public static string Time(DateTime value) => value.ToString("t", FormatCulture);

    /// <summary>
    /// Picks the plural form for <paramref name="count"/> from a <c>*_Plural</c> resource and formats it with
    /// <c>{0}</c> = count. Russian has three forms (1 день, 2 дня, 5 дней), English two, Kazakh one
    /// (a noun after a numeral stays singular: 5 күн).
    /// </summary>
    public static string Plural(long count, string forms, params object?[] extra)
    {
        var options = forms.Split(PluralSeparator);
        string form = options[Math.Min(PluralIndex(Culture.TwoLetterISOLanguageName, count), options.Length - 1)];
        return Format(form, [count, .. extra]);
    }

    public static int PluralIndex(string language, long count)
    {
        long n = Math.Abs(count);
        return language switch
        {
            "ru" => n % 10 == 1 && n % 100 != 11 ? 0
                  : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? 1
                  : 2,
            "en" => n == 1 ? 0 : 1,
            _ => 0
        };
    }

    /// <summary>Number of plural forms a <c>*_Plural</c> resource has in <paramref name="language"/>.</summary>
    public static int PluralFormCount(string language) => language switch
    {
        "ru" => 3,
        "en" => 2,
        _ => 1
    };
}

/// <summary>
/// Text that is rendered in the current language each time it is shown: view models keep these for messages
/// that stay on screen (banners, status lines) and re-raise them when the language changes.
/// </summary>
public sealed class LocText
{
    private readonly Func<string> _render;

    public LocText(Func<string> render) => _render = render;

    /// <summary>Text that is not translated (data, or a message already in the current language).</summary>
    public static LocText Raw(string text) => new(() => text);

    public override string ToString() => _render();
}
