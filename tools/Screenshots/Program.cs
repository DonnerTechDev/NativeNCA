using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NativeNCA.App.Views;
using NativeNCA.App.Views.Dialogs;
using NativeNCA.Core.Localization;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.Updates;
using NativeNCA.Core.ViewModels;
using Wpf.Ui.Appearance;

// Renders the README screenshots (docs/screenshots/<language>) with fictional keys and pictures.
// Windows are shown off-screen, composited on a soft backdrop with an approximation of Mica,
// with a fixed blue accent so the result does not depend on the machine's personalization.
//
//   dotnet run --project tools/Screenshots -- docs/screenshots              (en, ru and kk subfolders)
//   dotnet run --project tools/Screenshots -- <dir> --lang kk               (one language)
//   dotnet run --project tools/Screenshots -- <dir> --extra                 (also the certificate and NCALayer states)
//   dotnet run --project tools/Screenshots -- <dir> --updates               (only the install, update and rollback UI)
//   dotnet run --project tools/Screenshots -- <dir> --review                (every screen and dialog, light theme, for layout review)
class Program
{
    static string Root = "";
    static string Out = "";
    static bool Extra;
    static bool Updates;
    static bool Review;
    static Lang L;

    /// <summary>Fictional data in each language: key names are the user's own, certificate data stays as НУЦ issues it.</summary>
    sealed record Lang(AppLanguage Language, string Code, string PersonalKey, string CompanyKey, string SoleKey, string LoginDescription);

    static readonly Lang[] Languages =
    {
        new(AppLanguage.English, "en", "Personal eGov", "Shanyrak Logistics LLP", "IE Akhmetov", "Sign in to your account"),
        new(AppLanguage.Russian, "ru", "Личный eGov", "ТОО «Шанырак Логистик»", "ИП Ахметов", "Вход в личный кабинет"),
        new(AppLanguage.Kazakh, "kk", "Жеке eGov", "«Шаңырақ Логистик» ЖШС", "ЖК Ахметов", "Жеке кабинетке кіру"),
    };

    /// <summary>Stand-in for CurrentUser\Root: the renderer must never install anything into Windows.</summary>
    sealed class PreviewRootStore : IRootCertificateStore
    {
        readonly List<System.Security.Cryptography.X509Certificates.X509Certificate2> _roots = new();
        public bool Contains(System.Security.Cryptography.X509Certificates.X509Certificate2 c) => _roots.Any(r => r.Thumbprint == c.Thumbprint);
        public void Add(System.Security.Cryptography.X509Certificates.X509Certificate2 c, IntPtr owner) =>
            _roots.Add(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(c.RawData));
        public void Remove(System.Security.Cryptography.X509Certificates.X509Certificate2 c, IntPtr owner) => _roots.RemoveAll(r => r.Thumbprint == c.Thumbprint);
        public IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> FindByOrganization(string organization) => _roots.ToList();
    }

    /// <summary>Certificate service in a temp folder; with <paramref name="installed"/> the own certificate is set up.</summary>
    static TlsCertificateService MakeTls(bool installed)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nnca_preview_tls_" + Guid.NewGuid().ToString("N"));
        var tls = new TlsCertificateService(dir, Path.Combine(dir, "no-ncalayer"), new PreviewRootStore());
        if (installed) tls.InstallOwnCertificate();
        return tls;
    }

    [STAThread]
    static void Main(string[] args)
    {
        Root = args[0];
        Extra = args.Contains("--extra");
        Updates = args.Contains("--updates");
        Review = args.Contains("--review");
        int langIndex = Array.IndexOf(args, "--lang");
        var languages = langIndex >= 0 ? Languages.Where(l => l.Code == args[langIndex + 1]).ToArray() : Languages;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NativeNCA;component/Resources/Styles.xaml") });

        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                foreach (var lang in languages)
                {
                    L = lang;
                    LocalizationSource.Instance.SetLanguage(lang.Language);
                    Out = Path.Combine(Root, lang.Code);
                    Directory.CreateDirectory(Out);
                    await Run();
                }
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(Path.Combine(Root, "crash.txt"), ex.ToString());
            }
            app.Shutdown();
        });
        app.Run();
    }

    /// <summary>Synthetic "photo": steppe landscape with mountains and sun.</summary>
    static byte[] Landscape(int w, int h)
    {
        var c = new Canvas { Width = 1200, Height = 800 };
        void Add(UIElement e) => c.Children.Add(e);
        Add(new System.Windows.Shapes.Rectangle { Width = 1200, Height = 800, Fill = new LinearGradientBrush(Color.FromRgb(0x4F, 0x9D, 0xE8), Color.FromRgb(0xF6, 0xC9, 0x8B), 90) });
        var sun = new System.Windows.Shapes.Ellipse { Width = 170, Height = 170, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0x8A)) };
        Canvas.SetLeft(sun, 760); Canvas.SetTop(sun, 170); Add(sun);
        Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,560 L180,330 L330,470 L520,240 L700,480 L860,360 L1200,600 L1200,800 L0,800 Z"), Fill = new LinearGradientBrush(Color.FromRgb(0x5B, 0x6E, 0x8C), Color.FromRgb(0x33, 0x40, 0x58), 90) });
        Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M520,240 L470,300 L505,290 L530,320 L560,290 L585,300 Z"), Fill = Brushes.White });
        Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,640 C300,580 700,700 1200,620 L1200,800 L0,800 Z"), Fill = new LinearGradientBrush(Color.FromRgb(0xC9, 0xA2, 0x4D), Color.FromRgb(0x8E, 0x6A, 0x2C), 90) });
        return RenderPng(c, 1200, 800, w, h);
    }

    /// <summary>Synthetic company logo.</summary>
    static byte[] Logo(int size)
    {
        var c = new Canvas { Width = 256, Height = 256 };
        c.Children.Add(new System.Windows.Shapes.Rectangle { Width = 256, Height = 256, Fill = new LinearGradientBrush(Color.FromRgb(0xFF, 0x8A, 0x3D), Color.FromRgb(0xE0, 0x3E, 0x52), 45) });
        c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M128,52 L196,92 L196,168 L128,208 L60,168 L60,92 Z"), Fill = Brushes.White, Opacity = 0.95 });
        c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M128,92 L162,112 L162,148 L128,168 L94,148 L94,112 Z"), Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x55, 0x48)) });
        return RenderPng(c, 256, 256, size, size);
    }

    static byte[] RenderPng(Canvas c, double cw, double ch, int w, int h)
    {
        var box = new Viewbox { Width = w, Height = h, Child = c, Stretch = Stretch.Fill };
        box.Measure(new Size(w, h)); box.Arrange(new Rect(0, 0, w, h)); box.UpdateLayout();
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bmp.Render(box);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    /// <summary>A fixed check time, so every language shows the same moment.</summary>
    static readonly DateTime CheckedAt = new(2026, 10, 24, 14, 32, 0);

    /// <summary>
    /// Update state for an installed 0.2.0 in a temp folder (never the real install folder), optionally with
    /// 0.1.0 kept for rollback. Nothing is checked online.
    /// </summary>
    static UpdateViewModel MakeUpdates(bool installed = true, bool withPrevious = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nnca_preview_install_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (withPrevious)
        {
            File.WriteAllText(Path.Combine(dir, AppInstallation.PreviousExeName), "");
            File.WriteAllText(Path.Combine(dir, AppInstallation.PreviousInfoName), "{\"Version\":\"0.1.0\"}");
        }
        var store = new UpdateStateStore(Path.Combine(dir, "update.json"));
        // A client is passed only so "Check for updates" looks enabled; nothing calls it.
        var client = new GitHubReleaseClient(new System.Net.Http.HttpClient());
        return new UpdateViewModel(AppVersion.Parse("0.2.0"), new AppInstallation(dir), installed, store, client, downloader: null)
        {
            CheckStatus = new LocText(() => Loc.Format(Strings.Update_UpToDate, Loc.Format(Strings.Update_CheckedAt, Loc.Time(CheckedAt))))
        };
    }

    static ReleaseInfo FakeRelease(string version) =>
        new(AppVersion.Parse(version), "v" + version, new Uri($"https://github.com/northbound-oss/NativeNCA/releases/tag/v{version}"), Array.Empty<ReleaseAsset>());

    static SecureVaultService MakeVault(int count = 3)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nnca_preview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = new VaultDatabase { UseTimestamp = true };
        db.Keys.Add(new VaultKeyItem { Name = L.PersonalKey, FullName = "АХМЕТОВ ДАНИЯР СЕРИКОВИЧ", Iin = "880412300517", KeyAlgorithm = CertificateInfoGost, KeyUsage = "BOTH", NotBefore = DateTime.UtcNow.AddDays(-120), NotAfter = DateTime.UtcNow.AddDays(245), IsPasswordSaved = true, Issuer = "C=KZ, CN=ҰЛТТЫҚ КУӘЛАНДЫРУШЫ ОРТАЛЫҚ (GOST) 2022", LastUsedAt = DateTime.UtcNow, CertificateSerial = "3f1a" });
        db.Keys.Add(new VaultKeyItem { Name = L.CompanyKey, FullName = "АХМЕТОВ ДАНИЯР СЕРИКОВИЧ", Iin = "880412300517", Bin = "190540012388", Organization = "ТОО «ШАНЫРАК ЛОГИСТИК»", KeyAlgorithm = CertificateInfoGost, KeyUsage = "BOTH", NotBefore = DateTime.UtcNow.AddDays(-300), NotAfter = DateTime.UtcNow.AddDays(65), IsPasswordSaved = true, Issuer = "C=KZ, CN=ҰЛТТЫҚ КУӘЛАНДЫРУШЫ ОРТАЛЫҚ (GOST) 2022", LastUsedAt = DateTime.UtcNow.AddHours(-3), CertificateSerial = "3f1b" });
        db.Keys.Add(new VaultKeyItem { Name = L.SoleKey, FullName = "АХМЕТОВ ДАНИЯР СЕРИКОВИЧ", Iin = "880412300517", Organization = "ИП АХМЕТОВ", KeyAlgorithm = CertificateInfoGost, KeyUsage = "BOTH", NotBefore = DateTime.UtcNow.AddDays(-350), NotAfter = DateTime.UtcNow.AddDays(14), IsPasswordSaved = false, Issuer = "C=KZ, CN=ҰЛТТЫҚ КУӘЛАНДЫРУШЫ ОРТАЛЫҚ (GOST) 2022", LastUsedAt = DateTime.UtcNow.AddDays(-2), CertificateSerial = "3f1c" });
        db.Keys = db.Keys.Take(count).ToList();
        File.WriteAllText(Path.Combine(dir, "vault.json"), JsonSerializer.Serialize(db));
        var vault = new SecureVaultService(dir);
        var keys = vault.GetKeys();
        // Square crop from the middle of the landscape, and a logo for the company key
        if (count > 0) vault.SetAvatar(keys.First(k => k.Name == L.PersonalKey).Id, CropSquare(Landscape(384, 256)));
        if (count > 1) vault.SetAvatar(keys.First(k => k.Name == L.CompanyKey).Id, Logo(256));
        return vault;
    }

    static string CertificateInfoGost => NativeNCA.Core.Crypto.CertificateInfo.GostAlgorithm;

    static byte[] CropSquare(byte[] png)
    {
        var frame = BitmapFrame.Create(new MemoryStream(png));
        int side = Math.Min(frame.PixelWidth, frame.PixelHeight);
        var cropped = new CroppedBitmap(frame, new Int32Rect((frame.PixelWidth - side) / 2, (frame.PixelHeight - side) / 2, side, side));
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(cropped));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    static async Task Idle()
    {
        for (int i = 0; i < 6; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(600);
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    static void Place(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -6000; w.Top = 0; w.ShowActivated = false;
    }

    /// <summary>Window content on a soft backdrop with rounded corners and a shadow, at 2x scale.</summary>
    static void Snap(Window w, string name, bool dark)
    {
        const double scale = 2.0, margin = 56;
        var root = (FrameworkElement)w.Content;
        double wd = root.ActualWidth, ht = root.ActualHeight;
        double cw = wd + margin * 2, ch = ht + margin * 2;

        var backdrop = dark
            ? new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x45), Color.FromRgb(0x10, 0x4E, 0x6B), 35)
            : new LinearGradientBrush(Color.FromRgb(0xCF, 0xE3, 0xF7), Color.FromRgb(0xE9, 0xDD, 0xF6), 35);

        // Composite visual tree: backdrop, shadowed rounded frame, window snapshot clipped to the frame.
        var canvas = new Canvas { Width = cw, Height = ch, Background = backdrop };
        var frame = new Border
        {
            Width = wd, Height = ht, CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.FindResource("ApplicationBackgroundBrush"),
            Effect = new DropShadowEffect { BlurRadius = 48, ShadowDepth = 12, Direction = 270, Opacity = dark ? 0.55 : 0.25 }
        };
        Canvas.SetLeft(frame, margin); Canvas.SetTop(frame, margin);
        canvas.Children.Add(frame);

        // Mica approximation: the (blurred) desktop shows through the base theme colour,
        // giving the window the same subtle tint a focused Windows 11 window has.
        var mica = new Grid { Width = wd, Height = ht, Clip = new RectangleGeometry(new Rect(0, 0, wd, ht), 8, 8) };
        mica.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = dark
                ? new LinearGradientBrush(Color.FromRgb(0x24, 0x3A, 0x63), Color.FromRgb(0x10, 0x5E, 0x80), 35)
                : new LinearGradientBrush(Color.FromRgb(0xB9, 0xD6, 0xF5), Color.FromRgb(0xE4, 0xCC, 0xF5), 35)
        });
        mica.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
            Opacity = dark ? 0.84 : 0.80
        });
        Canvas.SetLeft(mica, margin); Canvas.SetTop(mica, margin);
        canvas.Children.Add(mica);

        var snapshot = new RenderTargetBitmap((int)(wd * scale), (int)(ht * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        snapshot.Render(root);
        var image = new Image { Source = snapshot, Width = wd, Height = ht, Clip = new RectangleGeometry(new Rect(0, 0, wd, ht), 8, 8) };
        Canvas.SetLeft(image, margin); Canvas.SetTop(image, margin);
        canvas.Children.Add(image);

        var outline = new Border
        {
            Width = wd, Height = ht, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0))
        };
        Canvas.SetLeft(outline, margin); Canvas.SetTop(outline, margin);
        canvas.Children.Add(outline);

        canvas.Measure(new Size(cw, ch));
        canvas.Arrange(new Rect(0, 0, cw, ch));
        canvas.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)(cw * scale), (int)(ch * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(canvas);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(Out, name + ".png"));
        enc.Save(fs);
    }

    static void ApplyTheme(ApplicationTheme theme)
    {
        ApplicationAccentColorManager.Apply(Color.FromRgb(0x00, 0x78, 0xD4), theme, false);
        ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
        ApplicationAccentColorManager.Apply(Color.FromRgb(0x00, 0x78, 0xD4), theme, false);
    }

    static void OpenSettings(MainWindow main) =>
        ((ToggleButton)main.FindName("SettingsNavButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    static void ScrollTo(MainWindow main, string elementName, double offset = -16)
    {
        var scroll = (ScrollViewer)main.FindName("SettingsView");
        var element = (FrameworkElement)main.FindName(elementName);
        scroll.ScrollToVerticalOffset(element.TranslatePoint(new Point(0, 0), (UIElement)scroll.Content).Y + offset);
    }

    static SignOverlayWindow MakeOverlay(int keys, SigningPromptRequest request = null)
    {
        request ??= new SigningPromptRequest
        {
            Origin = "https://idp.egov.kz",
            Description = LocText.Raw(L.LoginDescription),
            DataToSignPreview = LocText.Raw("<login><timeTicket>1729771200000</timeTicket><nonce>7d1f0c2a-3b4e</nonce></login>"),
            RequiredUsage = KeyUsageFilter.Auth
        };
        return new SignOverlayWindow(new SignOverlayViewModel(MakeVault(keys), new ConsentCache(), request));
    }

    static async Task RunUpdates(ApplicationTheme theme, string t, bool dark)
    {
        // Update available, stacked under a server problem.
        var vm = new SettingsViewModel(MakeVault(), MakeTls(installed: true)) { IsServerRunning = true };
        var updates = MakeUpdates(withPrevious: true);
        var main = new MainWindow(vm, updates) { Width = 1080, Height = 700 };
        Place(main); main.Show(); await Idle(); ApplyTheme(theme); await Idle();

        updates.AvailableRelease = FakeRelease("0.3.0");
        await Idle(); Snap(main, $"u-banner-{t}", dark);

        updates.UpdateErrorText = new LocText(() => Strings.Update_BadSignature);
        vm.ServerErrorKind = NativeNCA.Core.Server.ServerErrorKind.NcaLayerRunning;
        vm.ServerErrorTitle = new LocText(() => Strings.Server_NcaLayerTitle);
        vm.ServerErrorText = new LocText(() => Strings.Server_NcaLayerMessage);
        await Idle(); Snap(main, $"u-stacked-{t}", dark);

        vm.ServerErrorText = null;
        updates.UpdateErrorText = null;
        updates.IsDownloading = true;
        updates.IsDownloadIndeterminate = false;
        updates.DownloadFraction = 0.42;
        updates.DownloadStatus = new LocText(() => Loc.Format(Strings.Update_Downloading, 28.4.ToString("0.0", Loc.FormatCulture), 67.7.ToString("0.0", Loc.FormatCulture), 0.42));
        await Idle(); Snap(main, $"u-downloading-{t}", dark);

        updates.IsDownloading = false;
        updates.AvailableRelease = null;
        updates.ApplyStartupArguments(new[] { UpdateViewModel.UpdatedFromArgument, "0.1.0" });
        await Idle(); Snap(main, $"u-updated-{t}", dark);

        updates.ApplyStartupArguments(new[] { UpdateViewModel.RolledBackFromArgument, "0.3.0" });
        await Idle(); Snap(main, $"u-rolled-back-{t}", dark);

        OpenSettings(main);
        updates.DismissRestartBannerCommand.Execute(null);
        await Idle();
        var settingsScroll = (ScrollViewer)main.FindName("SettingsView");
        ScrollTo(main, "UpdatesSection");
        await Idle(); Snap(main, $"u-settings-{t}", dark);
        settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"u-settings-end-{t}", dark);
        main.CloseForExit();

        // A copy that is not installed: install row instead of rollback and uninstall.
        var portableVm = new SettingsViewModel(MakeVault(), MakeTls(installed: true)) { IsServerRunning = true };
        var portable = MakeUpdates(installed: false);
        portable.AvailableRelease = FakeRelease("0.3.0");
        var main2 = new MainWindow(portableVm, portable) { Width = 1080, Height = 700 };
        Place(main2); main2.Show(); await Idle(); ApplyTheme(theme); await Idle();
        OpenSettings(main2);
        await Idle();
        ScrollTo(main2, "UpdatesSection");
        await Idle(); Snap(main2, $"u-portable-{t}", dark);
        main2.CloseForExit();

        // Vault from a newer version.
        var newerDir = Path.Combine(Path.GetTempPath(), "nnca_preview_newer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(newerDir);
        File.WriteAllText(Path.Combine(newerDir, "vault.json"), "{\"FormatVersion\":2,\"Keys\":[]}");
        var roVm = new SettingsViewModel(new SecureVaultService(newerDir), MakeTls(installed: true)) { IsServerRunning = true };
        var main3 = new MainWindow(roVm, MakeUpdates()) { Width = 1080, Height = 700 };
        Place(main3); main3.Show(); await Idle(); ApplyTheme(theme); await Idle();
        Snap(main3, $"u-vault-readonly-{t}", dark);
        main3.CloseForExit();

        // Setup windows.
        async Task SnapSetup(SetupWindow w, string name, Action<SetupWindow> before = null)
        {
            Place(w); w.Show(); await Idle(); ApplyTheme(theme); await Idle();
            before?.Invoke(w); await Idle();
            Snap(w, name, dark);
            w.Close();
        }

        var v = AppVersion.Parse("0.2.0");
        await SnapSetup(SetupWindow.ForInstall(v, null, () => Task.CompletedTask, () => { }), $"u-setup-install-{t}");
        await SnapSetup(SetupWindow.ForInstall(v, AppVersion.Parse("0.1.0"), () => Task.CompletedTask, () => { }), $"u-setup-upgrade-{t}");
        await SnapSetup(SetupWindow.ForInstall(v, v, () => Task.CompletedTask, () => { }), $"u-setup-installed-{t}");
        await SnapSetup(SetupWindow.ForInstall(v, AppVersion.Parse("0.3.0"), () => Task.CompletedTask, () => { }), $"u-setup-newer-{t}");
        await SnapSetup(SetupWindow.ForInstall(v, null, () => throw new InvalidOperationException(Strings.Setup_CloseRunningFailed), () => { }),
            $"u-setup-error-{t}", w => ((Wpf.Ui.Controls.Button)w.FindName("PrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        await SnapSetup(SetupWindow.ForUninstall(v, _ => Task.FromResult<string>(null)), $"u-uninstall-{t}");
        await SnapSetup(SetupWindow.ForUninstall(v, _ => Task.FromResult<string>(null)), $"u-uninstall-done-{t}", w =>
            ((Wpf.Ui.Controls.Button)w.FindName("PrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        await SnapSetup(SetupWindow.ForUninstall(v, _ => Task.FromResult(Strings.Uninstall_RootLeft)), $"u-uninstall-warning-{t}", w =>
            ((Wpf.Ui.Controls.Button)w.FindName("PrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
    }

    /// <summary>The dialogs built in MainWindow's code-behind, opened through their handlers.</summary>
    static async Task RunDialogs(ApplicationTheme theme, string t, bool dark)
    {
        var vm = new SettingsViewModel(MakeVault(), MakeTls(installed: true)) { IsServerRunning = true, IsNcaLayerAutostartEnabled = true };
        var updates = MakeUpdates(withPrevious: true);
        var main = new MainWindow(vm, updates) { Width = 1080, Height = 700 };
        Place(main); main.Show(); await Idle(); ApplyTheme(theme); await Idle();
        var host = (Wpf.Ui.Controls.ContentDialogHost)main.FindName("DialogHost");

        async Task SnapHandler(string handler, string name, object sender = null)
        {
            var method = typeof(MainWindow).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new MissingMethodException(handler);
            var result = method.Invoke(main, method.GetParameters().Length == 0 ? null : new[] { sender, new RoutedEventArgs() });
            await Idle();
            Snap(main, name, dark);
            if (FindDialog(host) is { } dialog) dialog.Hide();
            await Idle();
        }

        vm.SelectedKey = vm.Keys[0];
        await Idle();
        await SnapHandler("OnRenameKeyClick", $"d-rename-{t}");
        await SnapHandler("OnDeleteKeyClick", $"d-delete-key-{t}");
        ((ToggleButton)main.FindName("HelloVaultToggle")).IsChecked = true;
        await SnapHandler("OnHelloVaultToggleClick", $"d-hello-vault-{t}");
        await SnapHandler("OnRemoveTlsCertificateClick", $"d-tls-remove-{t}");
        await SnapHandler("OnCloseNcaLayerClick", $"d-close-ncalayer-{t}");
        await SnapHandler("OnDisableNcaLayerAutostartClick", $"d-disable-autostart-{t}");
        await SnapHandler("OnRollbackClick", $"d-rollback-{t}");
        await SnapHandler("OfferInstallAsync", $"d-install-{t}");

        // Save password: the switch is "on" when clicked.
        vm.SelectedKey = vm.Keys.First(k => !k.IsPasswordSaved);
        await Idle();
        ((ToggleButton)main.FindName("SavePasswordToggle")).IsChecked = true;
        await SnapHandler("OnSavePasswordToggleClick", $"d-save-password-{t}");

        var change = new ChangePasswordDialog(host, vm, vm.Keys[0]);
        _ = change.ShowAsync(); await Idle(); Snap(main, $"d-change-password-{t}", dark);
        change.Hide(); await Idle();

        var tlsSetup = new TlsSetupDialog(host);
        _ = tlsSetup.ShowAsync(); await Idle(); Snap(main, $"d-tls-setup-{t}", dark);
        tlsSetup.Hide(); await Idle();

        var addKey = new AddKeyDialog(host, vm);
        _ = addKey.ShowAsync(); await Idle();
        typeof(AddKeyDialog).GetMethod("ShowError", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(addKey, new object[] { Strings.AddKey_WrongPasswordOrDamaged });
        await Idle(); Snap(main, $"d-add-key-error-{t}", dark);
        addKey.Hide(); await Idle();

        var crop = new AvatarCropDialog(host, null);
        _ = crop.ShowAsync(); await Idle(); Snap(main, $"d-avatar-empty-{t}", dark);
        crop.Hide(); await Idle();

        // Toast with the longest error text (the snackbar shows one message at a time).
        main.ShowError(Strings.NcaLayer_AutostartNeedsSettings);
        await Idle(); Snap(main, $"d-toast-error-{t}", dark);

        // Empty vault: welcome screen.
        main.CloseForExit();
        var emptyVm = new SettingsViewModel(MakeVault(0), MakeTls(installed: true)) { IsServerRunning = true };
        var empty = new MainWindow(emptyVm, MakeUpdates()) { Width = 1080, Height = 700 };
        Place(empty); empty.Show(); await Idle(); ApplyTheme(theme); await Idle();
        Snap(empty, $"d-welcome-{t}", dark);
        empty.CloseForExit();

        // Overlay states: certificate only, no keys, unsafe origin with an error and a password.
        var select = MakeOverlay(2, new SigningPromptRequest
        {
            Origin = "https://cabinet.example.kz",
            Description = new LocText(() => Strings.Request_ShareCertificate),
            DataToSignPreview = new LocText(() => Strings.Request_ShareCertificatePreview),
            RequiredUsage = KeyUsageFilter.Any,
            RequiresPrivateKey = false
        });
        Place(select); select.Show(); await Idle(); ApplyTheme(theme); await Idle();
        Snap(select, $"d-overlay-certificate-{t}", dark); select.Close();

        var none = MakeOverlay(0, new SigningPromptRequest
        {
            Origin = "http://localhost:8080",
            Description = new LocText(() => Loc.Format(Strings.Request_SignDocuments, 3)),
            DataToSignPreview = new LocText(() => Loc.Plural(18734, Strings.Request_BinaryPreview_Plural, "9F2C41D07A3B55E1")),
        });
        Place(none); none.Show(); await Idle(); ApplyTheme(theme); await Idle();
        Snap(none, $"d-overlay-no-keys-{t}", dark); none.Close();

        var vault = MakeVault(3);
        var sign = new SignOverlayViewModel(vault, new ConsentCache(), new SigningPromptRequest
        {
            Origin = "",
            Description = new LocText(() => Strings.Request_SignCms),
            DataToSignPreview = new LocText(() => Loc.Plural(2, Strings.Request_BinaryPreview_Plural, "0011223344556677")),
            RequiredUsage = KeyUsageFilter.Sign
        });
        sign.SelectedKey = sign.MatchingKeys.First(k => !k.IsPasswordSaved);
        var signWindow = new SignOverlayWindow(sign);
        Place(signWindow); signWindow.Show(); await Idle(); ApplyTheme(theme); await Idle();
        typeof(SignOverlayViewModel).GetMethod("RequireManualPassword", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sign, new object[] { new LocText(() => Strings.Overlay_SavedPasswordWrong) });
        await Idle(); Snap(signWindow, $"d-overlay-error-{t}", dark); signWindow.Close();
    }

    static Wpf.Ui.Controls.ContentDialog FindDialog(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Wpf.Ui.Controls.ContentDialog dialog) return dialog;
            if (FindDialog(child) is { } found) return found;
        }
        return null;
    }

    static async Task Run()
    {
        var themes = Review ? new[] { ApplicationTheme.Light } : new[] { ApplicationTheme.Dark, ApplicationTheme.Light };
        foreach (var theme in themes)
        {
            bool dark = theme == ApplicationTheme.Dark;
            string t = dark ? "dark" : "light";
            ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, true);

            if (Updates || Review)
            {
                await RunUpdates(theme, t, dark);
                if (!Review) continue;
                await RunDialogs(theme, t, dark);
            }

            var vm = new SettingsViewModel(MakeVault(), MakeTls(installed: true)) { IsServerRunning = true };
            var main = new MainWindow(vm, MakeUpdates()) { Width = 1080, Height = 700 };
            Place(main); main.Show(); await Idle(); ApplyTheme(theme); await Idle();
            Snap(main, $"main-{t}", dark);

            OpenSettings(main);
            await Idle(); Snap(main, $"settings-{t}", dark);

            if (Review)
            {
                var settingsScroll = (ScrollViewer)main.FindName("SettingsView");
                ScrollTo(main, "UpdatesSection"); await Idle(); Snap(main, $"settings-middle-{t}", dark);
                settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"settings-end-{t}", dark);

                // The narrowest window: long texts must wrap, not clip.
                main.Width = main.MinWidth; main.Height = main.MinHeight; await Idle();
                settingsScroll.ScrollToHome(); await Idle(); Snap(main, $"settings-narrow-{t}", dark);
                ScrollTo(main, "UpdatesSection"); await Idle(); Snap(main, $"settings-narrow-middle-{t}", dark);
                settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"settings-narrow-end-{t}", dark);
                vm.SelectedKey = vm.Keys[2]; await Idle(); Snap(main, $"main-narrow-{t}", dark);
                main.Width = 1080; main.Height = 700; await Idle();
            }

            vm.SelectedKey = vm.Keys[0]; await Idle();

            var dlg = new AddKeyDialog((Wpf.Ui.Controls.ContentDialogHost)main.FindName("DialogHost"), vm);
            _ = dlg.ShowAsync(); await Idle(); Snap(main, $"add-key-{t}", dark);
            dlg.Hide(); await Idle();

            string photo = Path.Combine(Path.GetTempPath(), "nnca_preview_photo.png");
            File.WriteAllBytes(photo, Landscape(1200, 800));
            var crop = new AvatarCropDialog((Wpf.Ui.Controls.ContentDialogHost)main.FindName("DialogHost"), photo);
            _ = crop.ShowAsync(); await Idle(); Snap(main, $"avatar-crop-{t}", dark);
            crop.Hide(); await Idle();
            File.Delete(photo);

            if (Extra || Review)
            {
                var host = (Wpf.Ui.Controls.ContentDialogHost)main.FindName("DialogHost");
                var settingsScroll = (ScrollViewer)main.FindName("SettingsView");
                OpenSettings(main);
                await Idle(); settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"x-settings-own-{t}", dark);

                // NCALayer certificate in use, NCALayer running and starting with Windows.
                vm.TlsSource = TlsCertificateSource.NcaLayer;
                vm.IsServerRunning = false;
                vm.IsNcaLayerAutostartEnabled = true;
                vm.ServerErrorKind = NativeNCA.Core.Server.ServerErrorKind.NcaLayerRunning;
                vm.ServerErrorTitle = new LocText(() => Strings.Server_NcaLayerTitle);
                vm.ServerErrorText = new LocText(() => Strings.Server_NcaLayerMessage);
                await Idle(); settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"x-ncalayer-{t}", dark);

                // No certificate at all.
                vm.TlsSource = TlsCertificateSource.None;
                vm.ServerErrorKind = NativeNCA.Core.Server.ServerErrorKind.Certificate;
                vm.ServerErrorTitle = new LocText(() => Strings.Server_NoCertificateTitle);
                vm.ServerErrorText = new LocText(() => Strings.Tls_MissingMessage);
                await Idle(); settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"x-no-certificate-{t}", dark);

                // Another program on the port.
                vm.ServerErrorKind = NativeNCA.Core.Server.ServerErrorKind.Other;
                vm.ServerErrorTitle = new LocText(() => Strings.Server_PortBusyTitle);
                vm.ServerErrorText = new LocText(() => Loc.Format(Strings.Server_PortBusyOwner, "java.exe") + " " + Strings.Server_PortBusyMessage);
                await Idle(); settingsScroll.ScrollToHome(); await Idle(); Snap(main, $"x-port-busy-{t}", dark);

                var setup = new TlsSetupDialog(host);
                _ = setup.ShowAsync(); await Idle(); Snap(main, $"x-tls-setup-{t}", dark);
                setup.Hide(); await Idle();
            }

            if (Review)
            {
                // Live switch: the open window follows the language without a restart.
                var current = LocalizationSource.Instance.Language;
                var other = current == AppLanguage.Kazakh ? AppLanguage.English : AppLanguage.Kazakh;
                vm.ServerErrorText = null;
                vm.Language = other;
                await Idle(); ((ScrollViewer)main.FindName("SettingsView")).ScrollToHome(); await Idle();
                Snap(main, $"live-switch-to-{LocalizationSource.ToSetting(other)}-{t}", dark);
                vm.Language = current;
                await Idle();
            }

            main.CloseForExit();

            var ov = MakeOverlay(2);
            Place(ov); ov.Show(); await Idle(); ApplyTheme(theme); await Idle();
            Snap(ov, $"overlay-{t}", dark);
            ov.Close();
        }
    }
}
