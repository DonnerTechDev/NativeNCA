using System.Security.Cryptography;
using System.Text;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Security;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Utilities.Collections;

namespace NativeNCA.Tests;

/// <summary>Software stand-in for the Windows Hello key: same contract, no biometrics.</summary>
internal sealed class FakeHelloProtector : IVaultKeyProtector
{
    private RSA? _rsa;
    public int UnwrapCalls { get; private set; }
    public bool CancelNext { get; set; }

    public bool IsAvailable => true;
    public void CreateKey(IntPtr ownerWindow) => _rsa = RSA.Create(2048);
    public byte[] Wrap(byte[] secret) => _rsa!.Encrypt(secret, RSAEncryptionPadding.Pkcs1);

    public byte[] Unwrap(byte[] wrapped, IntPtr ownerWindow, string reason)
    {
        UnwrapCalls++;
        if (CancelNext) { CancelNext = false; throw new OperationCanceledException(); }
        if (_rsa == null) throw new VaultKeyLostException();
        return _rsa.Decrypt(wrapped, RSAEncryptionPadding.Pkcs1);
    }

    public void DeleteKey() => _rsa = null;
}

public sealed class HelloVaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nnca_tests_" + Guid.NewGuid().ToString("N"));
    private readonly FakeHelloProtector _protector = new();
    private readonly SecureVaultService _vault;

    public HelloVaultTests() => _vault = new SecureVaultService(_dir, _protector);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Core.Models.VaultKeyItem AddKey(bool savePassword = true)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".p12");
        File.WriteAllBytes(path, TestKeys.CreateGostPfx());
        return _vault.AddKey(path, TestKeys.Password, null, savePassword);
    }

    [Fact]
    public async Task Enabling_protection_reencrypts_and_locking_blocks_access()
    {
        var item = AddKey();
        await _vault.EnableHelloProtectionAsync(IntPtr.Zero);

        Assert.True(_vault.IsHelloProtected);
        Assert.True(_vault.GetSettings().RequireWindowsHelloForSigning);

        // Still unlocked right after enabling (no second prompt needed)
        Assert.Equal(TestKeys.Password, _vault.GetSavedPassword(_vault.GetKey(item.Id)!));

        _vault.Lock();
        Assert.False(_vault.IsUnlocked);
        Assert.Throws<VaultLockedException>(() => _vault.OpenKey(_vault.GetKey(item.Id)!, TestKeys.Password));
        Assert.Null(_vault.GetSavedPassword(_vault.GetKey(item.Id)!));

        await _vault.UnlockAsync(IntPtr.Zero, "test");
        Assert.Equal(1, _protector.UnwrapCalls);
        var key = _vault.OpenKey(_vault.GetKey(item.Id)!, TestKeys.Password);
        Assert.True(CmsVerifier.Verify(CmsSigner.Sign(key, new byte[] { 1 }, true)).IsValid);

        // One unlock covers further operations in the session
        await _vault.UnlockAsync(IntPtr.Zero, "test");
        Assert.Equal(1, _protector.UnwrapCalls);
    }

    [Fact]
    public async Task Protected_vault_survives_restart_and_needs_unlock()
    {
        var item = AddKey();
        await _vault.EnableHelloProtectionAsync(IntPtr.Zero);

        var reopened = new SecureVaultService(_dir, _protector);
        Assert.True(reopened.IsHelloProtected);
        Assert.False(reopened.IsUnlocked);
        Assert.Throws<VaultLockedException>(() => reopened.DecryptKeyFile(reopened.GetKey(item.Id)!));

        await reopened.UnlockAsync(IntPtr.Zero, "test");
        Assert.Equal(TestKeys.Password, reopened.GetSavedPassword(reopened.GetKey(item.Id)!));
    }

    [Fact]
    public async Task Cancelled_hello_keeps_vault_locked()
    {
        AddKey();
        await _vault.EnableHelloProtectionAsync(IntPtr.Zero);
        _vault.Lock();

        _protector.CancelNext = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => _vault.UnlockAsync(IntPtr.Zero, "test"));
        Assert.False(_vault.IsUnlocked);
    }

    [Fact]
    public async Task Disabling_protection_restores_plain_dpapi_storage()
    {
        var item = AddKey();
        await _vault.EnableHelloProtectionAsync(IntPtr.Zero);
        _vault.Lock();

        await _vault.DisableHelloProtectionAsync(IntPtr.Zero);

        Assert.False(_vault.IsHelloProtected);
        Assert.True(_vault.IsUnlocked);
        var reopened = new SecureVaultService(_dir, new FakeHelloProtector());
        Assert.Equal(TestKeys.Password, reopened.GetSavedPassword(reopened.GetKey(item.Id)!));
    }

    [Fact]
    public async Task Adding_keys_while_protected_requires_unlock()
    {
        await _vault.EnableHelloProtectionAsync(IntPtr.Zero);
        _vault.Lock();

        Assert.Throws<VaultLockedException>(() => AddKey());

        await _vault.UnlockAsync(IntPtr.Zero, "test");
        var item = AddKey();
        Assert.True(_vault.VerifyKeyPassword(item.Id, TestKeys.Password));
    }
}

/// <summary>Local RFC 3161 authority for tests (RSA, timeStamping EKU).</summary>
internal sealed class TestTimestampAuthority : ITimestampProvider
{
    private readonly AsymmetricCipherKeyPair _keys;
    private readonly X509Certificate _cert;

    public TestTimestampAuthority()
    {
        var gen = new RsaKeyPairGenerator();
        gen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        _keys = gen.GenerateKeyPair();

        var certGen = new X509V3CertificateGenerator();
        certGen.SetSerialNumber(BigInteger.ValueOf(1));
        certGen.SetIssuerDN(new X509Name("CN=TEST TSA, C=KZ"));
        certGen.SetSubjectDN(new X509Name("CN=TEST TSA, C=KZ"));
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(_keys.Public);
        certGen.AddExtension(X509Extensions.ExtendedKeyUsage, true, new ExtendedKeyUsage(KeyPurposeID.id_kp_timeStamping));
        _cert = certGen.Generate(new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory("SHA256WITHRSA", _keys.Private));
    }

    public Task<byte[]> GetTokenAsync(byte[] signatureValue, CancellationToken ct = default)
    {
        var requestGenerator = new TimeStampRequestGenerator();
        requestGenerator.SetCertReq(true);
        var request = requestGenerator.Generate(TspAlgorithms.Sha256, SHA256.HashData(signatureValue));
        var generator = new TimeStampTokenGenerator(_keys.Private, _cert, TspAlgorithms.Sha256, NcaTimestampService.Policy);
        generator.SetCertificates(CollectionUtilities.CreateStore(new[] { _cert }));
        var token = generator.Generate(request, BigInteger.One, DateTime.UtcNow);
        return Task.FromResult(token.GetEncoded());
    }
}

public class TimestampTests
{
    [Fact]
    public async Task Timestamp_is_attached_and_verified()
    {
        var key = SigningKey.Load(TestKeys.CreateGostPfx(), TestKeys.Password);
        byte[] cms = CmsSigner.Sign(key, Encoding.UTF8.GetBytes("документ"), attached: true);

        byte[] stamped = await CmsSigner.AddTimestampAsync(cms, new TestTimestampAuthority());

        var result = CmsVerifier.Verify(stamped);
        Assert.True(result.IsValid, result.Error);
        Assert.NotNull(result.TimestampTime);
        Assert.Null(CmsVerifier.Verify(cms).TimestampTime);
    }

    [Fact]
    public async Task Timestamp_for_another_signature_is_rejected()
    {
        var key = SigningKey.Load(TestKeys.CreateGostPfx(), TestKeys.Password);
        byte[] cms = CmsSigner.Sign(key, new byte[] { 1 }, attached: true);
        var tsa = new TestTimestampAuthority();

        // Token issued for a different signature value
        var foreignTsa = new ForeignTokenProvider(tsa);
        byte[] stamped = await CmsSigner.AddTimestampAsync(cms, foreignTsa);

        Assert.False(CmsVerifier.Verify(stamped).IsValid);
    }

    private sealed class ForeignTokenProvider(ITimestampProvider inner) : ITimestampProvider
    {
        public Task<byte[]> GetTokenAsync(byte[] signatureValue, CancellationToken ct = default) =>
            inner.GetTokenAsync(new byte[] { 9, 9, 9 }, ct);
    }

    /// <summary>Talks to the real НУЦ TSA. Excluded in CI with --filter "Category!=Network".</summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task Real_NCA_TSA_issues_valid_token_for_gost_signature()
    {
        var key = SigningKey.Load(TestKeys.CreateGostPfx(), TestKeys.Password);
        byte[] cms = CmsSigner.Sign(key, Encoding.UTF8.GetBytes("NativeNCA TSA test"), attached: true);

        byte[] stamped = await CmsSigner.AddTimestampAsync(cms, new NcaTimestampService());

        var result = CmsVerifier.Verify(stamped);
        Assert.True(result.IsValid, result.Error);
        Assert.InRange(result.TimestampTime!.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
    }
}
