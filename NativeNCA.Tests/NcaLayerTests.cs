using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NativeNCA.Core.Services;

namespace NativeNCA.Tests;

public sealed class NcaLayerTests
{
    [Fact]
    public void FindListeningProcessId_ReturnsOwnerOfLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.Equal(Environment.ProcessId, NcaLayerService.FindListeningProcessId(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void FindListeningProcessId_FreePort_ReturnsNull()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Null(NcaLayerService.FindListeningProcessId(port));
    }

    [Fact]
    public void IsNcaLayerProcess_MatchesByInstallFolder()
    {
        using var self = Process.GetCurrentProcess();
        string ownDir = Path.GetDirectoryName(Environment.ProcessPath)!;

        Assert.True(NcaLayerService.IsNcaLayerProcess(self, [Path.GetDirectoryName(ownDir)!]));
        Assert.False(NcaLayerService.IsNcaLayerProcess(self, [Path.Combine(Path.GetTempPath(), "NCALayer")]));
    }

    [Fact]
    public void StartupApprovedMarker_MatchesTaskManagerFormat()
    {
        byte[] marker = NcaLayerService.DisabledMarker(new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, marker.Length);
        Assert.Equal(0x03, marker[0]);
        Assert.Equal(new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc), DateTime.FromFileTimeUtc(BitConverter.ToInt64(marker, 4)));

        Assert.False(NcaLayerService.IsEnabledMarker(marker));
        Assert.True(NcaLayerService.IsEnabledMarker([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(NcaLayerService.IsEnabledMarker(null));
    }

    [Fact]
    public void ShortcutMentionsNcaLayer_FindsTargetInUnicodeOrAnsi()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nnca_lnk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string unicode = Path.Combine(dir, "a.lnk");
            File.WriteAllBytes(unicode, [0x4C, 0, 0, 0, .. Encoding.Unicode.GetBytes(@"C:\Program Files\NCALayer\NCALayer.exe")]);
            string ansi = Path.Combine(dir, "b.lnk");
            File.WriteAllBytes(ansi, [0x4C, 0, 0, 0, 0x11, .. Encoding.Latin1.GetBytes(@"C:\PROGRA~1\NCALAY~1\ncalayer.exe")]);
            string other = Path.Combine(dir, "c.lnk");
            File.WriteAllBytes(other, [0x4C, 0, 0, 0, .. Encoding.Unicode.GetBytes(@"C:\Program Files\Other\other.exe")]);

            Assert.True(NcaLayerService.ShortcutMentionsNcaLayer(unicode));
            Assert.True(NcaLayerService.ShortcutMentionsNcaLayer(ansi));
            Assert.False(NcaLayerService.ShortcutMentionsNcaLayer(other));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
