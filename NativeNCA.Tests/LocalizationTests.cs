using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NativeNCA.Core.Localization;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.ViewModels;

namespace NativeNCA.Tests;

/// <summary>
/// Tests that switch the global UI language or compare texts with <see cref="Strings"/> run one at a time,
/// so a switch in one test never changes the language another test is reading.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalizationCollection
{
    public const string Name = "Localization";
}

[Collection(LocalizationCollection.Name)]
public sealed class LocalizationTests : IDisposable
{
    private static readonly string[] Cultures = ["", "kk", "en"];
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);
    private static readonly Regex Cyrillic = new(@"[Ѐ-ӿ]", RegexOptions.Compiled);

    public void Dispose() => LocalizationSource.Instance.SetLanguage(AppLanguage.Russian);

    // ===================== Resources =====================

    internal static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NativeNCA.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("NativeNCA.slnx not found above the test output");
        }
    }

    internal static Dictionary<string, string> ReadResx(string culture)
    {
        string file = culture.Length == 0 ? "Strings.resx" : $"Strings.{culture}.resx";
        var doc = XDocument.Load(Path.Combine(RepoRoot, "NativeNCA.Core", "Localization", file));
        // Git may check the files out with CRLF; the compiled resources keep whatever the file has.
        return doc.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => ((string)d.Element("value")!).Replace("\r\n", "\n"));
    }

    [Fact]
    public void AllLanguages_HaveTheSameKeys()
    {
        var neutral = ReadResx("").Keys.ToHashSet();
        foreach (var culture in Cultures.Skip(1))
        {
            var keys = ReadResx(culture).Keys.ToHashSet();
            Assert.Empty(neutral.Except(keys).Select(k => $"{culture}: missing {k}"));
            Assert.Empty(keys.Except(neutral).Select(k => $"{culture}: extra {k}"));
        }
    }

    [Fact]
    public void Placeholders_MatchInEveryLanguage()
    {
        var neutral = ReadResx("");
        var problems = new List<string>();
        foreach (var culture in Cultures.Skip(1))
        {
            var translated = ReadResx(culture);
            foreach (var (key, value) in neutral)
            {
                if (!translated.TryGetValue(key, out var other)) continue;
                var expected = Placeholders(value);
                var actual = Placeholders(other);
                if (!expected.SetEquals(actual))
                    problems.Add($"{culture} {key}: {{{string.Join(",", actual)}}} instead of {{{string.Join(",", expected)}}}");
            }
        }
        Assert.Empty(problems);
    }

    private static HashSet<int> Placeholders(string value) =>
        Placeholder.Matches(value).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();

    [Fact]
    public void PluralResources_HaveOneFormPerRuleOfTheLanguage()
    {
        foreach (var (culture, language) in new[] { ("", "ru"), ("kk", "kk"), ("en", "en") })
        {
            foreach (var (key, value) in ReadResx(culture).Where(p => p.Key.EndsWith("_Plural", StringComparison.Ordinal)))
            {
                var forms = value.Split(Loc.PluralSeparator);
                Assert.True(forms.Length == Loc.PluralFormCount(language), $"{language} {key}: {forms.Length} forms");
                Assert.All(forms, f => Assert.Contains("{0", f));
            }
        }
    }

    [Fact]
    public void EveryLanguage_IsCompiledIntoTheAssemblies()
    {
        foreach (var culture in Cultures)
        {
            var info = CultureInfo.GetCultureInfo(culture.Length == 0 ? "ru" : culture);
            foreach (var (key, value) in ReadResx(culture))
                Assert.Equal(value, Strings.ResourceManager.GetString(key, info)?.Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public void Kazakh_UsesOfficialTerminology()
    {
        var kk = ReadResx("kk");
        string all = string.Join("\n", kk.Values);
        // ЭЦП → ЭЦҚ, НУЦ → ҰКО, ИИН → ЖСН, БИН → БСН (egov.kz, pki.gov.kz).
        foreach (var russianTerm in new[] { "ЭЦП", "НУЦ", "ИИН", "БИН", "пароль" })
            Assert.DoesNotContain(russianTerm, all);
        Assert.Equal("ЖСН", kk["Key_Iin"]);
        Assert.Contains("ЭЦҚ", kk["AddKey_Title"]);
        Assert.Contains("ҰКО", kk["Key_ExpiredMessage"]);
    }

    // ===================== No hard-coded text =====================

    /// <summary>
    /// User-visible text lives in Strings*.resx. A Cyrillic literal in product code is allowed only on a line
    /// marked <c>// NoLoc</c> (protocol messages websites expect in Russian, values stored on disk).
    /// </summary>
    [Fact]
    public void ProductCode_HasNoCyrillicLiterals()
    {
        var problems = new List<string>();
        foreach (var project in new[] { "NativeNCA.Core", "NativeNCA.App" })
        {
            var root = Path.Combine(RepoRoot, project);
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(RepoRoot, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;

                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    problems.AddRange(CyrillicLiterals(File.ReadAllText(file)).Select(l => $"{relative}:{l}"));
                else if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    problems.AddRange(CyrillicXaml(File.ReadAllText(file)).Select(t => $"{relative}: {t}"));
            }
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void CyrillicScanner_FindsLiteralsButNotComments()
    {
        const string code = """"
            // Комментарий
            /* блок
               комментария */
            var a = "Привет";
            var b = $"Ключ {x}";
            var c = @"многострочная ""строка""";
            var d = "Сайт"; // NoLoc: protocol message
            var e = "ok"; // слово
            /// <summary>Документация</summary>
            """";
        Assert.Equal(new[] { 4, 5, 6 }, CyrillicLiterals(code));
        Assert.Equal(new[] { "Text=\"Текст\"", "Здесь" },
            CyrillicXaml("<Grid><!-- коммент --><TextBlock Text=\"Текст\" Tag=\"ok\" /><Run>Здесь</Run></Grid>"));
    }

    /// <summary>Line numbers of string or char literals with Cyrillic, skipping comments and NoLoc lines.</summary>
    internal static List<int> CyrillicLiterals(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        var found = new List<int>();
        string s = string.Join("\n", lines);
        int line = 1;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n') { line++; continue; }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                i--;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + 2;
                line += s.AsSpan(i, end - i).Count('\n');
                i = end - 1;
                continue;
            }
            if (c is '"' or '\'')
            {
                int start = i, startLine = line;
                bool verbatim = i > 0 && (s[i - 1] == '@' || (i > 1 && s[i - 1] == '$' && s[i - 2] == '@'));
                int end;
                if (c == '"' && i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
                {
                    // Raw string literal: as many closing quotes as opening ones.
                    int q = 0;
                    while (i + q < s.Length && s[i + q] == '"') q++;
                    end = s.IndexOf(new string('"', q), i + q, StringComparison.Ordinal);
                    end = end < 0 ? s.Length : end + q;
                }
                else
                {
                    end = i + 1;
                    while (end < s.Length)
                    {
                        if (!verbatim && s[end] == '\\') { end += 2; continue; }
                        if (s[end] == c)
                        {
                            if (verbatim && end + 1 < s.Length && s[end + 1] == '"') { end += 2; continue; }
                            end++;
                            break;
                        }
                        if (!verbatim && s[end] == '\n') break;
                        end++;
                    }
                }

                string literal = s[start..Math.Min(end, s.Length)];
                line += literal.Count(ch => ch == '\n');
                if (Cyrillic.IsMatch(literal) && !lines[startLine - 1].Contains("// NoLoc", StringComparison.Ordinal))
                    found.Add(startLine);
                i = end - 1;
            }
        }
        return found;
    }

    /// <summary>Attribute values and text nodes with Cyrillic (comments are not looked at).</summary>
    internal static List<string> CyrillicXaml(string xaml)
    {
        var doc = XDocument.Parse(xaml);
        var found = new List<string>();
        foreach (var element in doc.Descendants())
        {
            found.AddRange(element.Attributes().Where(a => Cyrillic.IsMatch(a.Value)).Select(a => $"{a.Name.LocalName}=\"{a.Value}\""));
            found.AddRange(element.Nodes().OfType<XText>().Where(t => Cyrillic.IsMatch(t.Value)).Select(t => t.Value.Trim()));
        }
        return found;
    }

    // ===================== Language switching =====================

    [Theory]
    [InlineData("kk-KZ", "kk")]
    [InlineData("kk", "kk")]
    [InlineData("ru-RU", "ru")]
    [InlineData("ru-KZ", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("uk-UA", "en")]
    public void SameAsWindows_PicksKazakhRussianOrEnglish(string windowsLanguage, string expected) =>
        Assert.Equal(expected, LocalizationSource.ResolveCultureName(AppLanguage.System, CultureInfo.GetCultureInfo(windowsLanguage)));

    [Theory]
    [InlineData(AppLanguage.Kazakh, "kk")]
    [InlineData(AppLanguage.Russian, "ru")]
    [InlineData(AppLanguage.English, "en")]
    public void FixedLanguage_IgnoresWindows(AppLanguage language, string expected) =>
        Assert.Equal(expected, LocalizationSource.ResolveCultureName(language, CultureInfo.GetCultureInfo("de-DE")));

    [Theory]
    [InlineData(AppLanguage.System, "system")]
    [InlineData(AppLanguage.Kazakh, "kk")]
    [InlineData(AppLanguage.Russian, "ru")]
    [InlineData(AppLanguage.English, "en")]
    public void LanguageSetting_RoundTrips(AppLanguage language, string setting)
    {
        Assert.Equal(setting, LocalizationSource.ToSetting(language));
        Assert.Equal(language, LocalizationSource.FromSetting(setting));
    }

    [Fact]
    public void LanguageSetting_MissingOrUnknown_MeansSameAsWindows()
    {
        Assert.Equal(AppLanguage.System, LocalizationSource.FromSetting(null));
        Assert.Equal(AppLanguage.System, LocalizationSource.FromSetting("fr"));
    }

    [Fact]
    public void SwitchingLanguage_NotifiesBindingsAndViewModels()
    {
        var source = LocalizationSource.Instance;
        source.SetLanguage(AppLanguage.Russian);

        var raised = new List<string?>();
        int cultureChanged = 0;
        PropertyChangedEventHandler onChanged = (_, e) => raised.Add(e.PropertyName);
        EventHandler onCulture = (_, _) => cultureChanged++;
        source.PropertyChanged += onChanged;
        source.CultureChanged += onCulture;
        try
        {
            Assert.Equal("Параметры", source["Settings_Title"]);

            source.SetLanguage(AppLanguage.English);
            Assert.Contains("Item[]", raised);
            Assert.Equal(1, cultureChanged);
            Assert.Equal("Settings", source["Settings_Title"]);
            Assert.Equal("Settings", Strings.Settings_Title);

            source.SetLanguage(AppLanguage.Kazakh);
            Assert.Equal("Параметрлер", source["Settings_Title"]);
            Assert.Equal(2, cultureChanged);

            // Choosing the language that is already shown changes nothing.
            raised.Clear();
            source.SetLanguage(AppLanguage.Kazakh);
            Assert.Empty(raised);
        }
        finally
        {
            source.PropertyChanged -= onChanged;
            source.CultureChanged -= onCulture;
        }
    }

    [Fact]
    public void ViewModels_ReRaiseTheirTextsOnSwitch()
    {
        LocalizationSource.Instance.SetLanguage(AppLanguage.Russian);
        var key = new KeyItemViewModel(new VaultKeyItem { Name = "k", KeyUsage = "BOTH", NotAfter = DateTime.UtcNow.AddDays(3.5) });
        Assert.Equal("Истекает через 4 дня", key.StatusText);

        var dir = Path.Combine(Path.GetTempPath(), "nnca_loc_" + Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new SettingsViewModel(new SecureVaultService(dir, new FakeHelloProtector()));
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var option = vm.SessionCacheOptions.First(o => o.Minutes == 5);
            Assert.Equal("5 минут", option.DisplayName);

            LocalizationSource.Instance.SetLanguage(AppLanguage.English);

            Assert.Contains(string.Empty, raised);
            Assert.Equal("Expires in 4 days", key.StatusText);
            Assert.Equal("5 minutes", option.DisplayName);
            Assert.Equal("Not installed", vm.TlsStatusText);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void LocText_RendersInTheCurrentLanguage()
    {
        LocalizationSource.Instance.SetLanguage(AppLanguage.Russian);
        var text = new LocText(() => Loc.Format(Strings.Update_BannerTitle, "0.3.0"));
        Assert.Equal("Доступна версия 0.3.0", text.ToString());
        LocalizationSource.Instance.SetLanguage(AppLanguage.English);
        Assert.Equal("Version 0.3.0 is available", text.ToString());
        LocalizationSource.Instance.SetLanguage(AppLanguage.Kazakh);
        Assert.Equal("0.3.0 нұсқасы қолжетімді", text.ToString());
    }

    [Fact]
    public void LanguageChoice_IsStoredInTheVault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nnca_loc_" + Guid.NewGuid().ToString("N"));
        var context = SynchronizationContext.Current;
        try
        {
            // Without a SynchronizationContext the switch is applied at once instead of being posted.
            SynchronizationContext.SetSynchronizationContext(null);
            var vm = new SettingsViewModel(new SecureVaultService(dir, new FakeHelloProtector()));
            Assert.Equal(AppLanguage.System, vm.Language);

            vm.Language = AppLanguage.Kazakh;
            Assert.Equal("kk", LocalizationSource.Instance.Culture.Name);
            Assert.Equal("kk", new SecureVaultService(dir, new FakeHelloProtector()).GetSettings().Language);
            Assert.Equal(AppLanguage.Kazakh, new SettingsViewModel(new SecureVaultService(dir, new FakeHelloProtector())).Language);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    // ===================== Formatting =====================

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(11, 2)]
    [InlineData(12, 2)]
    [InlineData(14, 2)]
    [InlineData(21, 0)]
    [InlineData(22, 1)]
    [InlineData(25, 2)]
    [InlineData(111, 2)]
    [InlineData(0, 2)]
    public void RussianPlural_FollowsTheNumberEnding(int count, int form) =>
        Assert.Equal(form, Loc.PluralIndex("ru", count));

    [Fact]
    public void EnglishAndKazakhPlural()
    {
        Assert.Equal(0, Loc.PluralIndex("en", 1));
        Assert.Equal(1, Loc.PluralIndex("en", 2));
        Assert.Equal(1, Loc.PluralIndex("en", 0));
        Assert.Equal(0, Loc.PluralIndex("kk", 1));
        Assert.Equal(0, Loc.PluralIndex("kk", 14));
    }

    [Fact]
    public void PluralTexts_ReadNaturally()
    {
        LocalizationSource.Instance.SetLanguage(AppLanguage.Russian);
        Assert.Equal("Истекает через 1 день", Loc.Plural(1, Strings.Key_Status_ExpiresIn_Plural));
        Assert.Equal("Истекает через 3 дня", Loc.Plural(3, Strings.Key_Status_ExpiresIn_Plural));
        Assert.Equal("Истекает через 14 дней", Loc.Plural(14, Strings.Key_Status_ExpiresIn_Plural));
        Assert.Equal("Истекает через 21 день", Loc.Plural(21, Strings.Key_Status_ExpiresIn_Plural));

        LocalizationSource.Instance.SetLanguage(AppLanguage.Kazakh);
        Assert.Equal("14 күннен кейін аяқталады", Loc.Plural(14, Strings.Key_Status_ExpiresIn_Plural));

        LocalizationSource.Instance.SetLanguage(AppLanguage.English);
        Assert.Equal("Expires in 1 day", Loc.Plural(1, Strings.Key_Status_ExpiresIn_Plural));
        Assert.Equal("Expires in 14 days", Loc.Plural(14, Strings.Key_Status_ExpiresIn_Plural));
    }

    [Fact]
    public void DatesAndNumbers_FollowTheLanguage()
    {
        var date = new DateTime(2026, 12, 31);
        LocalizationSource.Instance.SetLanguage(AppLanguage.Russian);
        Assert.Equal("31.12.2026", Loc.Date(date));
        string thousands = 1536.ToString("N0", LocalizationSource.Instance.FormatCulture); // "1 536" with a no-break space
        Assert.Equal($"Двоичные данные, {thousands} байт · SHA-256 ab…", Loc.Plural(1536, Strings.Request_BinaryPreview_Plural, "ab"));
        Assert.Equal("Двоичные данные, 2 байта · SHA-256 ab…", Loc.Plural(2, Strings.Request_BinaryPreview_Plural, "ab"));

        LocalizationSource.Instance.SetLanguage(AppLanguage.English);
        Assert.Equal("Binary data, 1,536 bytes · SHA-256 ab…", Loc.Plural(1536, Strings.Request_BinaryPreview_Plural, "ab"));
    }
}
