using System.IO;
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
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.Updates;
using NativeNCA.Core.ViewModels;
using Wpf.Ui.Appearance;

// Renders the README screenshots (docs/screenshots) with fictional keys and pictures.
// Windows are shown off-screen, composited on a soft backdrop with an approximation of Mica,
// with a fixed blue accent so the result does not depend on the machine's personalization.
//
//   dotnet run --project tools/Screenshots -- docs/screenshots
//   dotnet run --project tools/Screenshots -- <dir> --extra    (also the certificate and NCALayer states, for review)
//   dotnet run --project tools/Screenshots -- <dir> --updates  (only the install, update and rollback UI, for review)
class Program
{
    static string Out = "";
    static bool Extra;
    static bool Updates;

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
        Out = args[0];
        Extra = args.Contains("--extra");
        Updates = args.Contains("--updates");
        Directory.CreateDirectory(Out);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NativeNCA;component/Resources/Styles.xaml") });

        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Run(); } catch (Exception ex) { File.WriteAllText(Path.Combine(Out, "crash.txt"), ex.ToString()); }
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
        // A client is passed only so "Проверить обновления" looks enabled; nothing calls it.
        var client = new GitHubReleaseClient(new System.Net.Http.HttpClient());
        return new UpdateViewModel(AppVersion.Parse("0.2.0"), new AppInstallation(dir), installed, store, client, downloader: null)
        {
            CheckStatusText = "Установлена последняя версия · Проверено 14:32"
        };
    }

    static ReleaseInfo FakeRelease(string version) =>
        new(AppVersion.Parse(version), "v" + version, new Uri($"https://github.com/DonnerTechDev/NativeNCA/releases/tag/v{version}"), Array.Empty<ReleaseAsset>());

    static SecureVaultService MakeVault(int count = 3)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nnca_preview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = new VaultDatabase { UseTimestamp = true };
        db.Keys.Add(new VaultKeyItem { Name = "Личный eGov", FullName = "АХМЕТОВ ДАНИЯР СЕРИКОВИЧ", Iin = "880412300517", KeyAlgorithm = "ГОСТ", KeyUsage = "BOTH", NotBefore = DateTime.UtcNow.AddDays(-120), NotAfter = DateTime.UtcNow.AddDays(245), IsPasswordSaved = true, Issuer = "C=KZ, CN=ҰЛТТЫҚ КУӘЛАНДЫРУШЫ ОРТАЛЫҚ (GOST) 2022", LastUsedAt = DateTime.UtcNow, CertificateSerial = "3f1a" });
        db.Keys.Add(new VaultKeyItem { Name = "ТОО «Шанырак Логистик»", FullName = "АХМЕТОВ ДАНИЯР СЕРИКОВИЧ", Iin = "880412300517", Bin = "190540012388", Organization = "ТОО «ШАНЫРАК ЛОГИСТИК»", KeyAlgorithm = "ГОСТ", KeyUsage = "BOTH", NotBefore = DateTime.UtcNow.AddDays(-300), NotAfter = DateTime.UtcNow.AddDays(65), IsPasswordSaved = true, Issuer = "C=KZ, CN=ҰЛТТЫҚ КУӘЛАНДЫРУШЫ ОРТАЛЫҚ (GOST) 2022", LastUsedAt = DateTime.UtcNow.AddHours(-3), CertificateSerial = "3f1b" });
        db.Keys.Add(new VaultKeyItem { Name = "ИП Ахметов", FullName = "АХМЕТОВ ДАНИЯР СЕРИКОВИЧ", Iin = "880412300517", Organization = "ИП АХМЕТОВ", KeyAlgorithm = "ГОСТ", KeyUsage = "BOTH", NotBefore = DateTime.UtcNow.AddDays(-350), NotAfter = DateTime.UtcNow.AddDays(14), IsPasswordSaved = false, Issuer = "C=KZ, CN=ҰЛТТЫҚ КУӘЛАНДЫРУШЫ ОРТАЛЫҚ (GOST) 2022", LastUsedAt = DateTime.UtcNow.AddDays(-2), CertificateSerial = "3f1c" });
        db.Keys = db.Keys.Take(count).ToList();
        File.WriteAllText(Path.Combine(dir, "vault.json"), JsonSerializer.Serialize(db));
        var vault = new SecureVaultService(dir);
        var keys = vault.GetKeys();
        // Square crop from the middle of the landscape, and a logo for the company key
        vault.SetAvatar(keys.First(k => k.Name == "Личный eGov").Id, CropSquare(Landscape(384, 256)));
        if (count > 1) vault.SetAvatar(keys.First(k => k.Name.StartsWith("ТОО")).Id, Logo(256));
        return vault;
    }

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

    static async Task RunUpdates(ApplicationTheme theme, string t, bool dark)
    {
        // Update available, stacked under a server problem.
        var vm = new SettingsViewModel(MakeVault(), MakeTls(installed: true)) { IsServerRunning = true };
        var updates = MakeUpdates(withPrevious: true);
        var main = new MainWindow(vm, updates) { Width = 1080, Height = 700 };
        Place(main); main.Show(); await Idle(); ApplyTheme(theme); await Idle();

        updates.AvailableRelease = FakeRelease("0.3.0");
        await Idle(); Snap(main, $"u-banner-{t}", dark);

        updates.UpdateError = "Подпись обновления недействительна. Обновление не установлено";
        vm.ServerErrorKind = NativeNCA.Core.Server.ServerErrorKind.NcaLayerRunning;
        vm.ServerErrorTitle = "Запущен NCALayer";
        vm.ServerError = "Оригинальный NCALayer занимает порт 13579, который сайты используют для подписи. " +
                         "Две программы не могут работать одновременно — закройте NCALayer, и NativeNCA запустится сам.";
        await Idle(); Snap(main, $"u-stacked-{t}", dark);

        vm.ServerError = null;
        updates.UpdateError = null;
        updates.IsDownloading = true;
        updates.IsDownloadIndeterminate = false;
        updates.DownloadFraction = 0.42;
        updates.DownloadText = "Загрузка 28,4 из 67,7 МБ · 42 %";
        await Idle(); Snap(main, $"u-downloading-{t}", dark);

        updates.IsDownloading = false;
        updates.AvailableRelease = null;
        updates.ApplyStartupArguments(new[] { UpdateViewModel.UpdatedFromArgument, "0.1.0" });
        await Idle(); Snap(main, $"u-updated-{t}", dark);

        ((ToggleButton)main.FindName("SettingsNavButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        updates.DismissRestartBannerCommand.Execute(null);
        await Idle();
        var settingsScroll = (ScrollViewer)main.FindName("SettingsView");
        var section = (FrameworkElement)main.FindName("UpdatesSection");
        section.BringIntoView(); await Idle();
        settingsScroll.ScrollToVerticalOffset(section.TranslatePoint(new Point(0, 0), (UIElement)settingsScroll.Content).Y - 16);
        await Idle(); Snap(main, $"u-settings-{t}", dark);
        settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"u-settings-end-{t}", dark);
        main.CloseForExit();

        // A copy that is not installed: install row instead of rollback and uninstall.
        var portableVm = new SettingsViewModel(MakeVault(), MakeTls(installed: true)) { IsServerRunning = true };
        var portable = MakeUpdates(installed: false);
        portable.AvailableRelease = FakeRelease("0.3.0");
        var main2 = new MainWindow(portableVm, portable) { Width = 1080, Height = 700 };
        Place(main2); main2.Show(); await Idle(); ApplyTheme(theme); await Idle();
        ((ToggleButton)main2.FindName("SettingsNavButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Idle();
        var scroll2 = (ScrollViewer)main2.FindName("SettingsView");
        var section2 = (FrameworkElement)main2.FindName("UpdatesSection");
        scroll2.ScrollToVerticalOffset(section2.TranslatePoint(new Point(0, 0), (UIElement)scroll2.Content).Y - 16);
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
        await SnapSetup(SetupWindow.ForUninstall(v, _ => Task.FromResult<string>(null)), $"u-uninstall-{t}");
        await SnapSetup(SetupWindow.ForUninstall(v, _ => Task.FromResult<string>(null)), $"u-uninstall-done-{t}", w =>
            ((Wpf.Ui.Controls.Button)w.FindName("PrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
    }

    static async Task Run()
    {
        foreach (var theme in new[] { ApplicationTheme.Dark, ApplicationTheme.Light })
        {
            bool dark = theme == ApplicationTheme.Dark;
            string t = dark ? "dark" : "light";
            ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, true);

            if (Updates)
            {
                await RunUpdates(theme, t, dark);
                continue;
            }

            var vm = new SettingsViewModel(MakeVault(), MakeTls(installed: true)) { IsServerRunning = true };
            var main = new MainWindow(vm, MakeUpdates()) { Width = 1080, Height = 700 };
            Place(main); main.Show(); await Idle();
            ApplicationAccentColorManager.Apply(Color.FromRgb(0x00, 0x78, 0xD4), theme, false); ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, false); ApplicationAccentColorManager.Apply(Color.FromRgb(0x00, 0x78, 0xD4), theme, false); await Idle();
            Snap(main, $"main-{t}", dark);

            ((ToggleButton)main.FindName("SettingsNavButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Idle(); Snap(main, $"settings-{t}", dark);
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

            if (Extra)
            {
                var host = (Wpf.Ui.Controls.ContentDialogHost)main.FindName("DialogHost");
                var settingsScroll = (ScrollViewer)main.FindName("SettingsView");
                ((ToggleButton)main.FindName("SettingsNavButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Idle(); settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"x-settings-own-{t}", dark);

                // NCALayer certificate in use, NCALayer running and starting with Windows.
                vm.TlsSource = TlsCertificateSource.NcaLayer;
                vm.IsServerRunning = false;
                vm.IsNcaLayerAutostartEnabled = true;
                vm.ServerErrorKind = NativeNCA.Core.Server.ServerErrorKind.NcaLayerRunning;
                vm.ServerErrorTitle = "Запущен NCALayer";
                vm.ServerError = "Оригинальный NCALayer занимает порт 13579, который сайты используют для подписи. " +
                                 "Две программы не могут работать одновременно — закройте NCALayer, и NativeNCA запустится сам.";
                await Idle(); settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"x-ncalayer-{t}", dark);

                // No certificate at all.
                vm.TlsSource = TlsCertificateSource.None;
                vm.ServerErrorKind = NativeNCA.Core.Server.ServerErrorKind.Certificate;
                vm.ServerErrorTitle = "Нет сертификата для браузеров";
                vm.ServerError = "Нет сертификата, которому доверяют браузеры. Нажмите «Настроить сертификат» — NativeNCA создаст собственный сертификат для этого компьютера.";
                await Idle(); settingsScroll.ScrollToEnd(); await Idle(); Snap(main, $"x-no-certificate-{t}", dark);

                var setup = new TlsSetupDialog(host);
                _ = setup.ShowAsync(); await Idle(); Snap(main, $"x-tls-setup-{t}", dark);
                setup.Hide(); await Idle();
            }

            main.CloseForExit();

            var req = new SigningPromptRequest
            {
                Origin = "https://idp.egov.kz",
                Description = "Вход в личный кабинет",
                DataToSignPreview = "<login><timeTicket>1729771200000</timeTicket><nonce>7d1f0c2a-3b4e</nonce></login>",
                RequiredUsage = KeyUsageFilter.Auth
            };
            var ovm = new SignOverlayViewModel(MakeVault(2), new ConsentCache(), req);
            var ov = new SignOverlayWindow(ovm);
            Place(ov); ov.Show(); await Idle();
            ApplicationAccentColorManager.Apply(Color.FromRgb(0x00, 0x78, 0xD4), theme, false); ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, false); ApplicationAccentColorManager.Apply(Color.FromRgb(0x00, 0x78, 0xD4), theme, false); await Idle();
            Snap(ov, $"overlay-{t}", dark);
            ov.Close();
        }
    }
}
