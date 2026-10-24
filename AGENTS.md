# AGENTS.md

Guidance for AI coding agents working on NativeNCA. Read this before changing code.

## What this is

NativeNCA replaces NCALayer, the Java application that Kazakh websites (eGov, banks, government services) use to sign data with keys issued by НУЦ РК (the National Certification Authority). It listens on `wss://127.0.0.1:13579`, speaks the NCALayer protocol, shows a Fluent confirmation window and signs locally with keys kept in an encrypted vault. Windows only, .NET 10, WPF.

The UI is in Russian. Code, comments, commit messages and the English README are in English.

## Repository layout

| Path | Contents |
| --- | --- |
| `NativeNCA.Core/Crypto` | PKCS#12 reader/writer, СТ РК ГОСТ Р 34.10-2015 (512), RSA, CMS/CAdES(-T), XMLDSig, TSA client, certificate parsing |
| `NativeNCA.Core/Security` | Key vault (DPAPI, optional Windows Hello layer), Windows Hello, TLS certificate, autostart, consent cache |
| `NativeNCA.Core/Server` | `NativeNcaServer` (TLS + WebSocket transport) and `NcaProtocolHandler` (request handling, transport independent) |
| `NativeNCA.Core/ViewModels` | MVVM view models (CommunityToolkit.Mvvm). Core has no WPF dependency |
| `NativeNCA.App` | WPF UI on WPF-UI 4.3.0: main window, signing overlay, dialogs, tray. Assembly name is `NativeNCA` |
| `NativeNCA.Tests` | xUnit tests |
| `tools/Screenshots` | Renders `docs/screenshots` with fictional data |
| `.github/workflows/build.yml` | CI, packaging and releases |

`design/` and `CLAUDE_TASK.md` are local-only and ignored by git. Never add them to a commit.

## Commands

```
dotnet build NativeNCA.slnx
dotnet test NativeNCA.Tests --filter "Category!=Network"
dotnet test NativeNCA.Tests                                  # also calls the real НУЦ TSA
dotnet run --project tools/Screenshots -- docs/screenshots   # refresh README screenshots
dotnet publish NativeNCA.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

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
- `kz.gov.pki.knca.basics` `sign`: responses are `{"status":true,"body":{"result":...}}`. Cancel is `{"status":true,"body":{}}`, and errors are `{"status":false,"code","message","details"}`. A `tsaProfile` key in `signingParams` means "add a time-stamp". `data` may be a string or an array, and the result mirrors it.
- Only one confirmation overlay is shown at a time (`_promptGate`). Requests from a connection run concurrently so heartbeats keep flowing, and closing the browser tab cancels the pending overlay.
- `getKeyInfo` reveals only the stored certificate (`RequiresPrivateKey = false`): no password and no Windows Hello.

## Vault and Windows Hello

- Files are in `%APPDATA%\NativeNCA\Vault`: `vault.json` (metadata), `keys/*.enc`, `avatars/*.png`. The TLS certificate copy is `%APPDATA%\NativeNCA\tls_127_0_0_1.pfx`.
- Secrets (key containers, saved passwords) are always DPAPI (CurrentUser) with a fixed entropy. Changing the entropy breaks existing vaults.
- With Windows Hello protection on, the DPAPI payload is an envelope: magic `00 4E 4E 48 01 A5 5A 00`, then nonce, tag, and AES-256-GCM ciphertext under a master key. The master key is RSA-PKCS#1-wrapped by a key in the Microsoft Passport KSP named `<SID>//NativeNCA/Vault/MasterKey`. One unlock lasts `SessionCacheMinutes`, minimum 2.
- `WindowsHelloKeyProtector` uses ncrypt.dll P/Invoke on purpose: the managed `CngKey` API mishandles this provider (`CngKey.Exists` throws `NTE_NO_KEY` instead of returning false). Anything that shows the Hello UI must run off the UI thread with the owner HWND set.
- Real Windows Hello cannot run in tests; `FakeHelloProtector` covers the logic. Ask the user to verify Hello-related changes on their machine.
- Avatars are not secret and are stored unencrypted. Every change uses a new file name so caches never show the old picture.
- `RefreshCertificateMetadata` re-derives KeyUsage, IIN and BIN from the stored certificate on load, so rule fixes reach existing vaults.
- Never log or display passwords, private keys or decrypted containers.

## TLS

Browsers trust NCALayer's `127.0.0.1` certificate, which is issued by НУЦ RSA 2022 and whose root NCALayer installs. `TlsCertificateService` reads `keystore.jks` from NCALayer's bundles with BouncyCastle `JksStore`; the public password is `nb1B3dau`. It then caches a PFX copy. NCALayer and NativeNCA cannot run at the same time because they share port 13579.

## UI notes (WPF-UI 4.3.0)

- Shared styles are in `NativeNCA.App/Resources/Styles.xaml`. Use DynamicResource theme brushes, never hard-coded colours (except the avatar crop mask). Key avatars use the `KeyAvatarView` template.
- Pack URIs must use `pack://application:,,,/NativeNCA;component/...` (assembly name `NativeNCA`).
- Some `SymbolRegular` values have no glyph in the bundled font (for example `DocumentSignature24`, `DocumentKey20`). Check new icons visually.
- `ui:InfoBar` does not render child content. Use a custom Border for banners with buttons.
- Subclassed `ContentDialog` needs `<Style BasedOn="{StaticResource {x:Type ui:ContentDialog}}" TargetType="local:...">` in its resources. It is created with the window's `ContentDialogHost`.
- The tray `NotifyIcon` registers on `Loaded`, so a background start shows the window off-screen once and hides it. WPF-UI's tray converter always uses the first frame of a `BitmapFrame` and assumes premultiplied pixels: pass an exact-size `Pbgra32` bitmap (see `LoadTrayIcon`).
- List selection follows NavigationView: fill plus accent pill, no accent outline. Radio cards in the overlay keep the outline.
- Settings apply immediately, with no Save button, as in Windows 11 Settings.
- The app icon (`Assets/app.ico`) contains 15 frames from 16 to 256 px, with a simplified drawing below 40 px. The window title bar intentionally uses the monochrome `ShieldKeyhole24` glyph.

## Working style for this repo

- Verify UI changes by rendering with `tools/Screenshots` (or a similar off-screen harness) and looking at the result. Mica is approximated there.
- Keep commits focused and split unrelated changes. Write messages in English, imperative mood.
- The README exists in three languages (`README.md`, `README.ru.md`, `README.kk.md`). Keep them in sync, write natural text and do not use emoji.
