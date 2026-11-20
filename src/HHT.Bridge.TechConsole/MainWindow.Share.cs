using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using HHT.Bridge.Core.Ch9329;
using HHT.Bridge.Core.Hid;
using HHT.Bridge.Core.Remote;
using HHT.Bridge.TechConsole.Security;

namespace HHT.Bridge.TechConsole;

// Items 7–8: remote viewing (HTTPS web viewer with consent-gated control) and the multi-bench dashboard.
public partial class MainWindow : IBridgeHost
{
    private BridgeWebServer? _server;
    private BenchDiscovery? _discovery;
    private readonly BenchDirectory _benches = new();
    private X509Certificate2? _cert;
    private string _shopKey = "";
    private volatile bool _allowRemoteControl; // consent: always off at startup
    private volatile bool _targetUsbCached;

    private string CertPath => Path.Combine(AppDataDir, "console-tls.pfx");

    private void InitShare()
    {
        BenchNameBox.Text = _settings.BenchName;
        _shopKey = Dpapi.UnprotectString(_settings.ShopKeyProtected) ?? "";
        if (_shopKey.Length == 0)
        {
            _shopKey = TlsCertificates.NewAccessKey();
            _settings.ShopKeyProtected = Dpapi.ProtectString(_shopKey);
            SaveSettings();
        }
        ShopKeyBox.Text = _shopKey;
        AllowControlCheck.IsChecked = false;
        _benches.Self = () => new BenchInfo(_settings.BenchId, _settings.BenchName, "self", _settings.SharePort,
            _cert != null ? TlsCertificates.Fingerprint(_cert) : "", _launch.Ticket, _capture.IsRunning, _targetUsbCached,
            _server?.Viewers ?? 0, DateTime.UtcNow, true);
        if (_settings.ShareEnabled && HHT.Bridge.Core.Licensing.LicenseVerifier.Allows(_edition, HHT.Bridge.Core.Licensing.Feature.RemoteView)) { ShareCheck.IsChecked = true; StartSharing(); }
    }

    private void ShutdownShare() => StopSharing();

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (ShareCheck.IsChecked == true && !Require(HHT.Bridge.Core.Licensing.Feature.RemoteView)) { ShareCheck.IsChecked = false; return; }
        if (ShareCheck.IsChecked == true) StartSharing(); else StopSharing();
        _settings.ShareEnabled = _server != null;
        SaveSettings();
    }

    private void StartSharing()
    {
        if (_server != null) return;
        try
        {
            _cert ??= TlsCertificates.LoadOrCreate(CertPath, "hht-bridge-" + _settings.BenchId, Dpapi.Protect, Dpapi.Unprotect);
            _server = new BridgeWebServer(this, new BridgeWebOptions
            {
                Port = _settings.SharePort,
                AccessKey = _shopKey,
                Certificate = _cert,
                AllowControl = () => _allowRemoteControl,
            }, _benches, msg => _log.Warn(msg));
            _server.ViewersChanged += () => Dispatcher.InvokeAsync(UpdateShareUi);
            _server.Start();
            _discovery = new BenchDiscovery(_benches, () => new BenchBeacon(_settings.BenchId, _settings.BenchName, _settings.SharePort,
                TlsCertificates.Fingerprint(_cert), _launch.Ticket, _capture.IsRunning, _targetUsbCached, _server?.Viewers ?? 0));
            try { _discovery.Start(); }
            catch (SocketException ex) { _log.Warn($"Bench discovery unavailable (UDP {BenchDiscovery.DefaultPort}): {ex.Message}"); _discovery = null; }
            _log.Audit($"Sharing started on port {_settings.SharePort} (HTTPS). Remote control: {(_allowRemoteControl ? "allowed" : "view only")}.");
        }
        catch (Exception ex)
        {
            _server?.Dispose();
            _server = null;
            ShareCheck.IsChecked = false;
            _log.Error($"Could not start sharing: {ex.Message}");
            MessageBox.Show(this, $"Could not start sharing on port {_settings.SharePort}:\n{ex.Message}\n\n" +
                                  "If Windows Firewall asked, allow access on Private networks.", "Share", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateShareUi();
    }

    private void StopSharing()
    {
        if (_server == null) return;
        _discovery?.Dispose();
        _discovery = null;
        _server.Dispose();
        _server = null;
        _controller?.ReleaseEverything(true, _lastAbsX, _lastAbsY);
        _log.Audit("Sharing stopped.");
        UpdateShareUi();
    }

    private void AllowControl_Click(object sender, RoutedEventArgs e)
    {
        _allowRemoteControl = AllowControlCheck.IsChecked == true;
        if (!_allowRemoteControl) _server?.RevokeControl();
        _log.Audit(_allowRemoteControl ? "Remote control ALLOWED by technician." : "Remote control disallowed.");
        UpdateShareUi();
    }

    private void Revoke_Click(object sender, RoutedEventArgs e)
    {
        _server?.RevokeControl();
        _log.Audit("Technician took back control from the remote viewer.");
        UpdateShareUi();
    }

    private void BenchName_Changed(object sender, RoutedEventArgs e)
    {
        var name = BenchNameBox.Text.Trim();
        if (name.Length == 0 || name == _settings.BenchName) return;
        _settings.BenchName = name.Length > 40 ? name[..40] : name;
        SaveSettings();
    }

    private void ShopKey_Changed(object sender, RoutedEventArgs e)
    {
        var key = ShopKeyBox.Text.Trim().ToUpperInvariant();
        if (key == _shopKey) return;
        if (key.Length < 12)
        {
            MessageBox.Show(this, "The access key must be at least 12 characters.", "Share");
            ShopKeyBox.Text = _shopKey;
            return;
        }
        SetShopKey(key);
    }

    private void NewKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Generate a new access key? Other benches and saved logins will need the new key.", "Share",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetShopKey(TlsCertificates.NewAccessKey());
    }

    private void SetShopKey(string key)
    {
        _shopKey = key;
        ShopKeyBox.Text = key;
        _settings.ShopKeyProtected = Dpapi.ProtectString(key);
        SaveSettings();
        _log.Audit("Shop access key changed.");
        if (_server != null) { StopSharing(); StartSharing(); }
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_shopKey); } catch (Exception) { }
    }

    private void ShareUrl_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ShareUrlList.SelectedItem is string url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(HHT.Bridge.Core.Licensing.Feature.Dashboard)) return;
        if (_server == null) { MessageBox.Show(this, "Turn on sharing first.", "Dashboard"); return; }
        Process.Start(new ProcessStartInfo($"https://localhost:{_settings.SharePort}/dashboard#k={Uri.EscapeDataString(_shopKey)}") { UseShellExecute = true });
    }

    private void UpdateShareUi()
    {
        bool on = _server != null;
        ShareUrlList.Items.Clear();
        if (on)
        {
            foreach (var ip in LocalAddresses())
            {
                ShareUrlList.Items.Add($"https://{ip}:{_settings.SharePort}/");
                ShareUrlList.Items.Add($"https://{ip}:{_settings.SharePort}/dashboard");
            }
            FingerprintText.Text = $"Certificate SHA-256: {TlsCertificates.Fingerprint(_cert!)}\nBrowsers warn about the self-signed certificate the first time; check this fingerprint, then continue.";
        }
        else FingerprintText.Text = "";

        int viewers = _server?.Viewers ?? 0;
        bool remoteCtl = _server?.HasRemoteController == true;
        ShareStatusText.Text = !on ? "Not shared." :
            $"Sharing on port {_settings.SharePort}. {viewers} viewer(s). " +
            (remoteCtl ? "A remote technician is controlling the target." : _allowRemoteControl ? "Remote control allowed." : "View only.");
        RevokeButton.IsEnabled = remoteCtl;
        SharePill.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SharePillText.Text = remoteCtl ? "Remote control" : $"Shared · {viewers} viewer(s)";
        RemoteBanner.Visibility = remoteCtl ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IEnumerable<string> LocalAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    yield return ua.Address.ToString();
        }
    }

    // ------------------------------------------------------------------ IBridgeHost (called from server threads)

    (byte[]? Jpeg, long Sequence) IBridgeHost.GetFrame() => _capture.GetJpeg();

    BridgeStatus IBridgeHost.GetStatus() => new(
        _settings.BenchId, _settings.BenchName, _launch.Ticket, _log.Technician,
        _capture.IsRunning, _capture.Width, _capture.Height, Math.Round(_capture.MeasuredFps, 1),
        _controller != null, _targetUsbCached, _allowRemoteControl, _server?.Viewers ?? 0, _server?.HasRemoteController == true);

    void IBridgeHost.RemoteKeyboard(KeyboardReport report) => _controller?.SendKeyboard(report);

    void IBridgeHost.RemoteMouse(MouseButtons buttons, int x, int y, sbyte wheel)
    {
        var c = _controller;
        if (c == null) return;
        if (wheel != 0 || buttons != _remoteButtons) { c.ButtonsAbsolute(buttons, x, y, wheel); _remoteButtons = buttons; }
        else c.MoveAbsolute(buttons, x, y);
    }
    private MouseButtons _remoteButtons;

    void IBridgeHost.RemoteSpecialKey(string id)
    {
        var key = SpecialKeys.Find(id);
        if (key != null) Dispatcher.InvokeAsync(() => SendSpecial(key));
    }

    void IBridgeHost.RemoteText(string text) => Dispatcher.InvokeAsync(() => TypeAsync(text));

    void IBridgeHost.RemoteReleaseAll()
    {
        _remoteButtons = MouseButtons.None;
        _controller?.ReleaseEverything(true, _lastAbsX, _lastAbsY);
    }

    void IBridgeHost.RemoteEvent(string message)
    {
        _log.Audit(message);
        Dispatcher.InvokeAsync(UpdateShareUi);
    }
}
