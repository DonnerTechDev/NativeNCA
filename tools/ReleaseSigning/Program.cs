using NativeNCA.Core.Updates;

// Release signing for the NativeNCA updater (see ReleaseSignature in NativeNCA.Core).
//
//   dotnet run --project tools/ReleaseSigning -- keygen <private-key.pem>
//       Creates a new ECDSA P-256 key pair. Writes the private key (keep it outside the repository and put
//       its text into the RELEASE_SIGNING_KEY secret) and prints the public key for ReleaseSignature.PublicKeyBase64.
//
//   dotnet run --project tools/ReleaseSigning -- sign <zip> <version> <arch> [<private-key.pem>]
//       Writes <zip>.sig. Without a key file the PEM is read from the RELEASE_SIGNING_KEY environment variable.
//       Fails if the key does not match the public key built into NativeNCA.
//
//   dotnet run --project tools/ReleaseSigning -- verify <zip> <version> <arch>
//       Checks <zip>.sig against the built-in public key.
//
// <arch> is win-x64 or win-arm64, <version> is the tag without "v" (0.2.0).
return Run(args);

static int Run(string[] args)
{
    try
    {
        switch (args.FirstOrDefault())
        {
            case "keygen" when args.Length == 2:
                return KeyGen(args[1]);
            case "sign" when args.Length is 4 or 5:
                return Sign(args[1], args[2], args[3], args.Length == 5 ? args[4] : null);
            case "verify" when args.Length == 4:
                return Verify(args[1], args[2], args[3]);
            default:
                Console.Error.WriteLine("Usage: keygen <private-key.pem> | sign <zip> <version> <arch> [<private-key.pem>] | verify <zip> <version> <arch>");
                return 2;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 1;
    }
}

static int KeyGen(string privateKeyPath)
{
    privateKeyPath = Path.GetFullPath(privateKeyPath);
    if (File.Exists(privateKeyPath))
        throw new InvalidOperationException($"{privateKeyPath} already exists; refusing to overwrite a signing key");
    if (IsInsideGitRepository(privateKeyPath))
        throw new InvalidOperationException("the private key must not be written inside a git repository");

    var (privatePem, publicKey) = ReleaseSignature.GenerateKeyPair();
    Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
    File.WriteAllText(privateKeyPath, privatePem);

    Console.WriteLine($"Private key: {privateKeyPath}");
    Console.WriteLine("Public key (ReleaseSignature.PublicKeyBase64):");
    Console.WriteLine(publicKey);
    return 0;
}

static int Sign(string zipPath, string versionText, string arch, string? keyPath)
{
    var version = ParseVersion(versionText);
    CheckArch(arch);

    string pem = keyPath != null
        ? File.ReadAllText(keyPath)
        : Environment.GetEnvironmentVariable("RELEASE_SIGNING_KEY") ?? "";
    if (string.IsNullOrWhiteSpace(pem))
        throw new InvalidOperationException("no signing key: pass a PEM file or set RELEASE_SIGNING_KEY");

    if (ReleaseSignature.GetPublicKey(pem) != ReleaseSignature.PublicKeyBase64)
        throw new InvalidOperationException("this key does not match the public key built into NativeNCA (ReleaseSignature.PublicKeyBase64)");

    byte[] hash = UpdateDownloader.HashFile(zipPath);
    string signature = ReleaseSignature.Sign(pem, version, arch, hash);
    if (!ReleaseSignature.Verify(signature, version, arch, hash))
        throw new InvalidOperationException("the new signature does not verify");

    File.WriteAllText(zipPath + ReleaseSignature.SignatureExtension, signature + "\n");
    Console.WriteLine($"Signed {Path.GetFileName(zipPath)} ({version}, {arch}, sha256 {Convert.ToHexStringLower(hash)})");
    return 0;
}

static int Verify(string zipPath, string versionText, string arch)
{
    var version = ParseVersion(versionText);
    CheckArch(arch);
    string signature = File.ReadAllText(zipPath + ReleaseSignature.SignatureExtension);
    bool ok = ReleaseSignature.Verify(signature, version, arch, UpdateDownloader.HashFile(zipPath));
    Console.WriteLine(ok ? "Signature is valid" : "Signature is NOT valid");
    return ok ? 0 : 1;
}

static AppVersion ParseVersion(string text) =>
    AppVersion.TryParse(text, out var v) ? v! : throw new ArgumentException($"not a version: {text}");

static void CheckArch(string arch)
{
    if (arch is not ("win-x64" or "win-arm64")) throw new ArgumentException($"unknown architecture: {arch}");
}

static bool IsInsideGitRepository(string path)
{
    for (var dir = new DirectoryInfo(Path.GetDirectoryName(path)!); dir != null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            return true;
    }
    return false;
}
