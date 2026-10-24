# AGENTS.md

Guidance for AI coding agents working on NativeNCA. Read this before changing code.

## What this is

NativeNCA replaces NCALayer, the Java application that Kazakh websites (eGov, banks, government services) use to sign data with keys issued by НУЦ РК (the National Certification Authority). It listens on `wss://127.0.0.1:13579`, speaks the NCALayer protocol, shows a Fluent confirmation window and signs locally with keys kept in an encrypted vault. Windows only, .NET 10, WPF.

The UI is in Kazakh, Russian and English (see Localization below). Code, comments, commit messages and the English README are in English.

## Repository layout

| Path | Contents |
| --- | --- |
| `NativeNCA.Core/Crypto` | PKCS#12 reader/writer, СТ РК ГОСТ Р 34.10-2015 (512), RSA, CMS/CAdES(-T), XMLDSig, TSA client, certificate parsing |
| `NativeNCA.Core/Security` | Key vault (DPAPI, optional Windows Hello layer), Windows Hello, TLS certificate, autostart, consent cache |
| `NativeNCA.Core/Server` | `NativeNcaServer` (TLS + WebSocket transport) and `NcaProtocolHandler` (request handling, transport independent) |
| `NativeNCA.Core/Services` | Prompt service interface, `NcaLayerService` (detect, close and un-autostart the original NCALayer) |
| `NativeNCA.Core/Localization` | `Strings.resx` (Russian, neutral), `Strings.kk.resx`, `Strings.en.resx`; `LocalizationSource` (current language), `Loc` (format, dates, plurals), `LocText` |
| `NativeNCA.Core/Updates` | Versions, GitHub release check, release signatures, download/verify, install folder swap and rollback, setup (shortcut, Uninstall key) |
| `NativeNCA.Core/ViewModels` | MVVM view models (CommunityToolkit.Mvvm). Core has no WPF dependency |
| `NativeNCA.App` | WPF UI on WPF-UI 4.3.0: main window, signing overlay, dialogs, tray. Assembly name is `NativeNCA`. `{l:Loc Key}` markup extension in `Localization/` |
| `NativeNCA.Tests` | xUnit tests |
| `tools/Screenshots` | Renders `docs/screenshots/<en\|ru\|kk>` with fictional data (`--updates`: install/update UI, `--review`: every screen and dialog, `--lang kk`: one language) |
| `tools/LocalizationReview` | Writes `docs/localization-review.md` (key, Russian, Kazakh, notes) for the native-speaker review. The sheet is local-only (gitignored) |
| `tools/ReleaseSigning` | Generates the release signing key, signs and verifies release zips |
| `.github/workflows/build.yml` | CI, packaging and releases |
| `site/` | Website for GitHub Pages (static HTML/CSS/JS in Russian, English and Kazakh). See the Website section |
| `.github/workflows/pages.yml` | Builds `site/` and deploys it to GitHub Pages |

`design/` and `CLAUDE_TASK.md` are local-only and ignored by git. Never add them to a commit.

## Commands

```
dotnet build NativeNCA.slnx
dotnet test NativeNCA.Tests --filter "Category!=Network"
dotnet test NativeNCA.Tests                                  # also calls the real НУЦ TSA
dotnet run --project tools/Screenshots -- docs/screenshots   # refresh README screenshots (all three languages)
dotnet run --project tools/Screenshots -- <dir> --review     # every screen and dialog in every language, for layout checks
dotnet run --project tools/LocalizationReview -- docs/localization-review.md
dotnet publish NativeNCA.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=0.2.0-dev -o publish/win-x64
```

Without `-p:Version` the app reports `0.0.0-dev` (set in `NativeNCA.App.csproj`). CI passes the tag's version; the updater reads `AssemblyInformationalVersion` and ignores the `+commit` suffix.

Releases: pushing a tag `vX.Y.Z` makes GitHub Actions run the tests and publish self-contained x64 and ARM64 zips. Tags with a `-` suffix become pre-releases.

If NativeNCA is running from `publish/win-x64`, the exe is locked. Rename it (Windows allows renaming a running exe) or ask the user to exit from the tray before publishing. Use `--artifacts-path` with a temp folder if the running copy is in `bin/`.

## Cryptography: invariants

These were established against real НУЦ data. Do not change them without a test that proves the new behaviour against a real НУЦ signature.

- **GOST 2015 (512).** Curve TC26 512 paramSetA, hash GOST R 34.11-2015 (Streebog-512). Public key in SPKI is an OCTET STRING with X‖Y, each 64 bytes little-endian. Signature is r‖s, each 64 bytes little-endian. The digest is interpreted as a little-endian integer, which is what BouncyCastle's `ECGost3410Signer` does. CMS `messageDigest` is the raw Streebog output.
- **OIDs.** Key `1.2.398.3.10.1.1.2.2`, curve `…2.2.1`, signature `1.2.398.3.10.1.1.2.3.2`, digest `1.2.398.3.10.1.3.3`. Algorithm identifiers carry NULL parameters. See `KzOids`.
- **Ground truth.** `NativeNCA.Tests/TestData/ncalayer_manifest.p7s` is NCALayer's update manifest, signed by НУЦ with Kalkan. `GostOracleTests` verifies it. It also contains a real НУЦ time-stamp token.
- **PKCS#12.** BouncyCastle's `Pkcs12Store` cannot load НУЦ GOST keys because it does not know the OIDs. `Pkcs12Reader` decrypts bags without interpreting the key. The private scalar is found heuristically and accepted only if `d·G` matches the certificate's public key.
- **CMS shape** (as Kalkan): signer identified by issuer+serial, signed attributes contentType, signingTime, signingCertificateV2, messageDigest, signer certificate embedded.
- **TSA.** `http://tsp.pki.gov.kz:80`, imprint is SHA-256 of the signature value, policy `1.2.398.3.3.2.6.2`, certReq=true. The token goes into the unsigned attribute id-aa-signatureTimeStampToken.
- **XMLDSig.** Enveloped, `Reference URI=""`, transforms enveloped-signature + C14N WithComments, SignedInfo with C14N. GOST URIs are `urn:ietf:params:xml:ns:pkigovkz:xmlsec:algorithms:gostr34102015-gostr34112015-512` and `...:gostr34112015-512`. The GOST SignatureValue byte order is assumed to match CMS (r‖s little-endian). This has not been checked against a real НУЦ XML signature; treat it as the first suspect if a site rejects GOST XML.
- **Key purpose.** НУЦ issues one GOST 2015 key per person for both login and signing, and its EKU lists only emailProtection. `CertificateInfo` therefore treats GOST 2015 keys with digitalSignature as `BOTH`. Only the legacy RSA pair is split into AUTH and SIGN. GOST 34.310-2004 keys are unsupported (all expired).
- **References.** KalkanPy (Apache-2.0, pure-Python reimplementation cross-checked against KalkanCrypt.dll) documents the same conventions. Do not decompile or copy НУЦ/NCALayer binaries; reading constant strings from NCALayer bundles to learn JSON field names is as far as we go.

## Protocol

- `kz.gov.pki.knca.commonUtils`: responses are `{"code":"200","responseObject":...}`; errors and cancellation are `{"code":"500","message":"action.canceled"}`. The first message after connect is `{"result":{"version":"1.4"}}`. `--heartbeat--` is echoed.
- `kz.gov.pki.knca.basics` `sign`: responses are `{"status":true,"body":{"result":...}}`. Cancel is `{"status":true,"body":{}}`, and errors are `{"status":false,"code","message","details"}`. A `tsaProfile` key in `signingParams` means "add a time-stamp". `data` may be a string or an array, but `body.result` is always an array, as in NCALayer: eGov login sends a single string and reads `body.result[0]`.
- Only one confirmation overlay is shown at a time (`_promptGate`). Requests from a connection run concurrently so heartbeats keep flowing, and closing the browser tab cancels the pending overlay.
- `getKeyInfo` reveals only the stored certificate (`RequiresPrivateKey = false`): no password and no Windows Hello.

## Vault and Windows Hello

- Files are in `%APPDATA%\NativeNCA\Vault`: `vault.json` (metadata), `keys/*.enc`, `avatars/*.png`. TLS certificates are described in the TLS section below.
- Secrets (key containers, saved passwords) are always DPAPI (CurrentUser) with a fixed entropy. Changing the entropy breaks existing vaults.
- With Windows Hello protection on, the DPAPI payload is an envelope: magic `00 4E 4E 48 01 A5 5A 00`, then nonce, tag, and AES-256-GCM ciphertext under a master key. The master key is RSA-PKCS#1-wrapped by a key in the Microsoft Passport KSP named `<SID>//NativeNCA/Vault/MasterKey`. One unlock lasts `SessionCacheMinutes`, minimum 2.
- `WindowsHelloKeyProtector` uses ncrypt.dll P/Invoke on purpose: the managed `CngKey` API mishandles this provider (`CngKey.Exists` throws `NTE_NO_KEY` instead of returning false). Anything that shows the Hello UI must run off the UI thread with the owner HWND set.
- Real Windows Hello cannot run in tests; `FakeHelloProtector` covers the logic. Ask the user to verify Hello-related changes on their machine.
- `vault.json` has `FormatVersion` (absent means 1, `SecureVaultService.SupportedFormatVersion` is the newest this build writes). Bump both only for a change an older build would misread or lose data on (a rollback runs the older build on the same vault). A build that finds a newer format opens the vault read-only: every change throws `VaultFormatTooNewException`, `MarkKeyUsed` is skipped, the main window shows a banner. Adding an optional property is not a format change.
- Avatars are not secret and are stored unencrypted. Every change uses a new file name so caches never show the old picture.
- `RefreshCertificateMetadata` re-derives KeyUsage, IIN and BIN from the stored certificate on load, so rule fixes reach existing vaults.
- Never log or display passwords, private keys or decrypted containers.

## TLS

NCALayer does not need to be installed. `TlsCertificateService` picks the certificate in this order:

1. **Own certificate.** `LocalCertificateAuthority` creates a per-machine root (ECDSA P-256, pathLen 0, critical nameConstraints permitting only dNSName `localhost`, `127.0.0.1/32` and `::1/128`) and one leaf (SAN localhost, 127.0.0.1, ::1; EKU serverAuth; KeyUsage digitalSignature). The root's private key is ephemeral and disposed right after signing the leaf: never persist it. Lifetimes are 25 and 20 years (local roots are exempt from public limits; renewing means a new Windows prompt). Files are in `%APPDATA%\NativeNCA\Tls`: `root.cer` (public) and `localhost.p12.dpapi` (leaf PKCS#12, DPAPI CurrentUser with entropy `NativeNCA.Tls.Entropy.v1`). Used only while the root is in CurrentUser\Root and nothing has expired.
2. **NCALayer fallback.** NCALayer's shared `127.0.0.1` certificate (issued by НУЦ RSA 2022, root installed by NCALayer), read from `keystore.jks` in its bundles with BouncyCastle `JksStore` (public password `nb1B3dau`) and cached as `%APPDATA%\NativeNCA\tls_127_0_0_1.pfx`. Its key is identical in every installation, so it is only a fallback.
3. Otherwise `TlsCertificateMissingException`, shown in the server banner with a "Настроить сертификат" button.

Adding or removing a root in CurrentUser\Root makes Windows show its own security prompt; `CurrentUserRootStore` turns a declined prompt into `OperationCanceledException` and must run off the UI thread. The first start shows `TlsSetupDialog` before that prompt; "Не сейчас" or a declined prompt writes `tls_setup_declined` so it is not offered unprompted again. Tests and `tools/Screenshots` use a fake `IRootCertificateStore`: never install anything into the real store from them. SChannel cannot use ephemeral keys, so the server loads the leaf with `UserKeySet`. Chrome, Edge and Firefox 120+ trust user-added roots from the Windows store; no browser configuration is needed.

NCALayer and NativeNCA cannot run at the same time because websites hardcode port 13579. When the port is busy, `NcaLayerService` finds the owner (GetExtendedTcpTable, then the process image path under NCALayer's install folder; NCALayer.exe launches the bundled `jre\bin\javaw.exe`, which owns the port) and the banner offers "Закрыть NCALayer" (kills it, then retries) and "Отключить автозапуск NCALayer". NCALayer's installer autostarts it with an `NCALayer.lnk` in the Startup folder; autostart is turned off the way Task Manager does it (an odd first byte in `Explorer\StartupApproved`), so the user can turn it back on in Settings. Machine-wide entries need administrator rights, so the app opens `ms-settings:startupapps` for those.

## Install, update and rollback

Everything lives in the one `NativeNCA.exe`; there is no separate installer or updater.

- **Install folder.** `%LOCALAPPDATA%\Programs\NativeNCA` (per user, no admin): `NativeNCA.exe`, and after an update `NativeNCA.prev.exe` + `NativeNCA.prev.json` (`{"Version":"0.1.0"}`). `AppInstallation` owns these names and all renames. Registration (`SetupService`): Start menu `NativeNCA.lnk` (IShellLink via COM interop) and `HKCU\...\Uninstall\NativeNCA` with `UninstallString = "...\NativeNCA.exe" --uninstall`. The installed copy refreshes `DisplayVersion` on start.
- **Start-up** (`App.Application_Startup`). `--uninstall` shows `SetupWindow` (uninstall mode). A Release build started from anywhere but the install folder shows `SetupWindow` (install mode) unless it is `--background`, a hand-over, `--portable` or has a file named `portable` next to it. Debug builds are always portable. Installing closes a running NativeNCA (exit event, or kill for versions before 0.2.0), copies the exe (over an older install it becomes the previous version), moves an existing Run entry to the installed path and starts the installed copy with `--installed`.
- **Instances** (`InstanceSignals`). `Local\NativeNCA.SingleInstance` mutex (whoever serves the port), `...ShowMainWindow` and `...Exit` events, `...HandOffReady`. A hand-over (`--updated-from X`, `--rolled-back-from X`, `--installed`) waits up to 30 s for the mutex instead of signalling "show". `App.HandOffAsync` stops the server, releases the mutex, starts the new exe and waits up to 20 s for HandOffReady (set after the new server started) or the process exiting. If it exits, the old process takes the mutex and port back and the renames are undone. A timeout with the process alive counts as success (0.1.0 cannot signal).
- **Update check.** `releases/latest` of `DonnerTechDev/NativeNCA` (stable only), with User-Agent and ETag; 403/429 back off until `x-ratelimit-reset`; offline is silent. At start (8 s delay) and every 6 h while "Автоматически проверять обновления" is on. State is in `%APPDATA%\NativeNCA\update.json` (not the vault): `AutoCheck`, `NotifiedVersion` (the tray balloon shows once per version), `SkippedVersion` (rolled back from: not offered unless checked by hand).
- **Release signature.** CI signs each zip: `<zip>.sig` is base64 ECDSA P-256/SHA-256 (P1363 r‖s) over the text `NativeNCA release signature v1\nversion: X\narch: win-x64\nsha256: <hex>\n`. The updater rebuilds the text from the version and architecture it expects, so another version's or architecture's signature fails. The public key is `ReleaseSignature.PublicKeyBase64`; the private key is only in the `RELEASE_SIGNING_KEY` secret and in the maintainer's file outside the repo. Never commit a private key. Tag builds fail without the secret. Changing the key means old installs reject new releases until users reinstall by hand.
- **Download.** Only HTTPS to github.com / objects.githubusercontent.com / release-assets.githubusercontent.com, redirects followed by hand and each hop checked. Asset names must be exactly `NativeNCA-<version>-<arch>.zip` and `.zip.sig`. After the signature, `NativeNCA.exe` is extracted and its own ProductVersion must equal the release version, then it becomes `NativeNCA.new.exe`.
- **Swap.** Delete old prev, rename `NativeNCA.exe` → `NativeNCA.prev.exe` (allowed while running), `new` → `NativeNCA.exe`, write prev json, hand over. Any failure renames back. Rollback renames the running exe to `NativeNCA.old.exe` and prev back to `NativeNCA.exe`; the installed copy deletes `old`/`new` leftovers on start. Only one previous version is kept; after a rollback there is none.
- **Uninstall.** Closes the running instance, removes the Run entry, shortcut and Uninstall key, optionally the TLS root (`RemoveOwnCertificate`), the Hello key and `%APPDATA%\NativeNCA`, then a hidden `cmd` (working dir %TEMP%) retries `rmdir` of the install folder for ~30 s after the process exits.
- **Tray notification.** WPF-UI's NotifyIcon has no balloon API. `TrayNotification` reads the icon's hWnd/uID/callback message from its internal manager by reflection and calls `Shell_NotifyIcon(NIM_MODIFY, NIF_INFO)`; clicks arrive as `NIN_BALLOONUSERCLICK` on the hook window. Re-check after a WPF-UI upgrade.
- **Tests** use temp folders and fake HTTP handlers only. Never touch the real install folder, Start menu, Uninstall key or Run key from tests or tools.

## Localization

- **No hard-coded text.** Every user-visible string lives in `NativeNCA.Core/Localization/Strings*.resx`: XAML, code-behind, view-model texts, exception messages a user can see, toasts, tray, setup window. `ProductCode_HasNoCyrillicLiterals` fails on a Cyrillic literal in `NativeNCA.Core` or `NativeNCA.App`. The exceptions are marked `// NoLoc` on the same line: protocol messages websites receive (they stay Russian, like NCALayer's) and values stored on disk (`CertificateInfo.GostAlgorithm`). Log or debug text nobody sees is English. Certificate data (names, organisations) is shown as issued.
- **Adding a string.** Add the key to all three `.resx` files (same key set and the same `{0}` placeholders: both are tested). Name it by area: `Common_`, `Keys_`, `Key_`, `Settings_`, `Server_`, `Tls_`, `Update_`, `Setup_`, `Overlay_`, `Request_`, `Hello_`, `Vault_`, `Status_`, `AddKey_`, `Avatar_`, `ChangePassword_`, `Crypto_`, `Tray_`. The build generates `Strings` (public, in Core), so a misspelt key does not compile. In XAML use `Text="{l:Loc Key}"` (`xmlns:l="clr-namespace:NativeNCA.App.Localization"`); it works in setters, templates and context menus. In code use `Strings.Key` and `Loc.Format(Strings.Key, args)`, never `string.Format` with the current culture. Put a reviewer note for an uncertain term into `<comment>` of `Strings.kk.resx` and regenerate the review sheet.
- **Plurals.** Keys ending in `_Plural` hold the forms separated by `|`: Russian three (1 день / 2 дня / 5 дней), English two, Kazakh one (a noun after a numeral stays singular). Use `Loc.Plural(count, Strings.X_Plural, extra args)`; `{0}` is the count.
- **Live switching.** `LocalizationSource.SetLanguage` sets `Strings.Culture`, raises `Item[]` (all `{l:Loc}` bindings) and `CultureChanged`. View models re-raise their computed texts on `CultureChanged` (`OnPropertyChanged(string.Empty)`). Messages that stay on screen (banners, status lines, overlay errors) are kept as `LocText` so they re-render in the new language; toasts and open dialogs are not re-translated. Dates and numbers use `Loc.Date`, `Loc.Time` and `Loc.FormatCulture` (the Windows regional format if it is in the chosen language, otherwise kk-KZ, ru-RU or en-US).
- **Language setting.** `VaultDatabase.Language` (`system`, `kk`, `ru`, `en`; optional, not a format change). "Как в Windows" maps a Kazakh Windows display language to Kazakh, Russian to Russian and anything else to English. The setup and uninstall windows always use the Windows display language. Tests that switch the language or compare texts with `Strings` belong to the `Localization` xUnit collection, which does not run in parallel.
- **Layout.** Kazakh is the longest language. After changing texts, render `--review` and check the narrow (minimum size) settings screens, the overlay and the dialogs. Kazakh hyphenated words (`Windows‑тан`, `аты‑жөні`) use a non-breaking hyphen (U+2011) so WPF does not split them.
- **Kazakh terminology** (as on egov.kz and pki.gov.kz): ЭЦП → ЭЦҚ (электрондық цифрлық қолтаңба), НУЦ РК → ҚР ҰКО (Ұлттық куәландырушы орталық), ИИН → ЖСН, БИН → БСН, пароль → құпиясөз, ключ → кілт, хранилище → қойма, подпись (действие) → қол қою, подпись (результат) → қолтаңба, метка времени → уақыт белгісі, сертификат → сертификат, корневой → түбірлік, закрытый ключ → жабық кілт, физическое лицо → жеке тұлға, ИП → ЖК, ТОО → ЖШС, параметры → параметрлер, учётная запись → тіркелгі, меню «Пуск» → «Бастау» мәзірі, обновление → жаңарту, версия → нұсқа. Russian legal and Windows terms are not calqued; Windows UI names in Kazakh that could not be confirmed are noted in the review sheet.
- **English** follows Windows 11 wording: sentence case, contractions, "Select" for buttons, Settings > Apps > Startup.

## UI notes (WPF-UI 4.3.0)

- Shared styles are in `NativeNCA.App/Resources/Styles.xaml`. Use DynamicResource theme brushes, never hard-coded colours (except the avatar crop mask). Key avatars use the `KeyAvatarView` template.
- Pack URIs must use `pack://application:,,,/NativeNCA;component/...` (assembly name `NativeNCA`).
- Some `SymbolRegular` values have no glyph in the bundled font (for example `DocumentSignature24`, `DocumentKey20`). Check new icons visually.
- `ui:InfoBar` does not render child content. Use a custom Border for banners with buttons (`BannerBorder` in the main window). Banners stack in `BannerHost`: server error, read-only vault, update, after-restart; the host collapses when all are hidden.
- `FluentWindow` has MinHeight 320 by default; `SizeToContent` windows that can be shorter set `MinHeight="0"`.
- Subclassed `ContentDialog` needs `<Style BasedOn="{StaticResource {x:Type ui:ContentDialog}}" TargetType="local:...">` in its resources. It is created with the window's `ContentDialogHost`.
- The tray `NotifyIcon` registers on `Loaded`, so a background start shows the window off-screen once and hides it. WPF-UI's tray converter always uses the first frame of a `BitmapFrame` and assumes premultiplied pixels: pass an exact-size `Pbgra32` bitmap (see `LoadTrayIcon`).
- List selection follows NavigationView: fill plus accent pill, no accent outline. Radio cards in the overlay keep the outline.
- Settings apply immediately, with no Save button, as in Windows 11 Settings.
- The app icon (`Assets/app.ico`) contains 15 frames from 16 to 256 px, with a simplified drawing below 40 px. The window title bar intentionally uses the monochrome `ShieldKeyhole24` glyph.

## Website

- `site/` is plain HTML, CSS and JS with no framework or bundler. `site/build.py` copies it to `_site/`, converts `docs/screenshots/*.png` to WebP, copies `docs/icon.png` and `app.ico`, inlines the icon sprite, replaces `__SITE_URL__`, `__BUILD_DATE__`, `__DOWNLOAD_URL__` and `__DOWNLOAD_META__`, adds FAQPage JSON-LD built from each page's FAQ expanders and bakes the latest releases into `release.json`. It stops if a placeholder is left. Build locally with `pip install pillow` and `python site/build.py`, then serve `_site/` with any static server.
- Three languages, one page each: `site/index.html` (Russian, the default and `x-default`), `site/en/index.html`, `site/kk/index.html`. They share `site/assets` and the screenshots; pages in subfolders set `data-root="../"` on `<html>` and use `../` paths. Every page links the other two with `hreflang`, the sitemap lists all three, and each has its own Open Graph image (`og.png`, `og-en.png`, `og-kk.png`). A change to the copy goes into all three pages; Kazakh terms follow `README.kk.md`.
- Strings set from JS (download button text, the Java parody log, alt texts, the donate fallback) live in `I18N` in `site/assets/site.js`, keyed by the page's `lang`. The download button text baked at build time is in `DL_TEXT` in `build.py`.
- Screenshots on the site come from `docs/screenshots`, so refreshing them with `tools/Screenshots` updates the site too. The site expects the current file names (`main`, `overlay`, `add-key`, `avatar-crop`, `settings`, each `-light` and `-dark`). The app UI is Russian for now, so all three languages show the same screenshots.
- Download buttons link straight to the newest release zip: `build.py` writes the `-win-x64.zip` URL into the HTML (the Site workflow rebuilds after every tagged release), then `site.js` switches ARM64 visitors to `-win-arm64.zip` and picks up a release newer than the build from the GitHub API. Without any release the buttons lead to the Releases page. Keep the `-win-x64.zip` / `-win-arm64.zip` suffixes in `build.yml`.
- Icons are Fluent UI System Icons in `site/assets/icons.svg`, inlined into each page at `<!--ICONS-->`. Donation links live in `CONFIG.donate` in `site/assets/site.js`.
- Copy is natural, plain and without emoji. Claims about security, updates and network access must match the code and the README: the app goes online only to GitHub (update checks, can be turned off) and to the НУЦ TSA (when timestamps are on).

## Working style for this repo

- Verify UI changes by rendering with `tools/Screenshots` (or a similar off-screen harness) and looking at the result. Mica is approximated there.
- Keep commits focused and split unrelated changes. Write messages in English, imperative mood.
- The README exists in three languages (`README.md`, `README.ru.md`, `README.kk.md`). Keep them in sync, write natural text and do not use emoji.
