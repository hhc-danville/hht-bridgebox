using System.Windows;
using System.Windows.Media;
using HHT.Bridge.Core.Hardware;
using HHT.Bridge.TechConsole.Security;
using HHT.Bridge.TechConsole.Serial;
using Microsoft.Win32;

namespace HHT.Bridge.TechConsole;

// Items 9–11 (console side): Bridge Box controller (power/reset/LEDs/USB switch) and the virtual-media module.
public partial class MainWindow
{
    private BridgeBoxController? _box;
    private VirtualMediaClient? _vm;
    private bool _boxPolling;

    private void InitHardware()
    {
        VmUrlBox.Text = _settings.VirtualMediaUrl ?? "";
        VmTokenBox.Password = Dpapi.UnprotectString(_settings.VirtualMediaTokenProtected) ?? "";
        VmFingerprintBox.Text = _settings.VirtualMediaFingerprint ?? "";
    }

    private void ShutdownHardware()
    {
        _box?.Dispose();
        _box = null;
        _vm?.Dispose();
        _vm = null;
    }

    // ------------------------------------------------------------------ controller (items 10–11)

    private async void FindController_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(HHT.Bridge.Core.Licensing.Feature.Hardware)) return;
        DisconnectController();
        FindControllerButton.IsEnabled = false;
        ControllerStatusText.Text = "Searching...";
        try
        {
            // Skip the port the CH9329 is using.
            var ports = SerialPortCatalog.List().Select(p => p.Port).Where(p => p != _controller?.PortName).ToList();
            if (_settings.ControllerPort != null && ports.Remove(_settings.ControllerPort)) ports.Insert(0, _settings.ControllerPort);
            var hit = await Task.Run(() => BridgeBoxController.FindAsync(ports, (p, b) => new SerialTransport(p, b),
                m => Dispatcher.InvokeAsync(() => _log.Info(m))));
            if (hit == null)
            {
                ControllerStatusText.Text = "No Bridge Box controller found (needs the v0.5 controller board).";
                return;
            }
            var box = new BridgeBoxController(new SerialTransport(hit.Value.Port, BridgeBoxController.DefaultBaud));
            box.Faulted += ex => Dispatcher.InvokeAsync(() => { _log.Error($"Controller link failed: {ex.Message}"); DisconnectController(); });
            box.Start();
            if (await box.IdentifyAsync() == null) { box.Dispose(); ControllerStatusText.Text = "Controller stopped answering."; return; }
            _box = box;
            _settings.ControllerPort = hit.Value.Port;
            SaveSettings();
            ControllerStatusText.Text = $"Controller {box.Info!.Version} on {hit.Value.Port}. Features: {string.Join(", ", box.Info.Features)}.";
            ControllerButtons.IsEnabled = true;
            DisconnectControllerButton.IsEnabled = true;
            _log.Audit($"Bridge Box controller connected on {hit.Value.Port}.");
            await PollControllerAsync();
        }
        finally { FindControllerButton.IsEnabled = true; }
    }

    private void DisconnectController_Click(object sender, RoutedEventArgs e) => DisconnectController();

    private void DisconnectController()
    {
        if (_box == null) return;
        _box.Dispose();
        _box = null;
        ControllerButtons.IsEnabled = false;
        DisconnectControllerButton.IsEnabled = false;
        ControllerStatusText.Text = "Not connected.";
        PwrLedDot.Fill = HddLedDot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x5A, 0x63));
        UsbSideText.Text = "";
    }

    /// <summary>Called from the 1.5 s status timer.</summary>
    private async Task PollControllerAsync()
    {
        if (_box == null || _boxPolling) return;
        _boxPolling = true;
        try
        {
            var st = await _box.GetStatusAsync();
            if (st == null || _box == null) return;
            var on = (Brush)FindResource("HhtGreen");
            var off = new SolidColorBrush(Color.FromRgb(0x55, 0x5A, 0x63));
            PwrLedDot.Fill = st.PowerLed ? on : off;
            HddLedDot.Fill = st.HddLed ? (Brush)FindResource("HhtAmber") : off;
            UsbSideText.Text = $"Shared USB port is connected to the {(st.Usb == UsbSide.Target ? "customer PC" : "laptop")}." + (st.Busy ? " (busy)" : "");
        }
        finally { _boxPolling = false; }
    }

    private async Task BoxCommand(string label, Func<BridgeBoxController, Task<(bool Ok, string Reply)>> cmd, string? confirm = null)
    {
        if (_box == null) return;
        if (confirm != null && MessageBox.Show(this, confirm, label, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, reply) = await cmd(_box);
        if (ok) _log.Audit($"Controller: {label}.");
        else _log.Warn($"Controller: {label} failed ({reply}).");
        await PollControllerAsync();
    }

    private async void PowerShort_Click(object sender, RoutedEventArgs e) => await BoxCommand("Power button pressed", b => b.PressPowerAsync(false));
    private async void PowerLong_Click(object sender, RoutedEventArgs e) =>
        await BoxCommand("Forced power off (6 s hold)", b => b.PressPowerAsync(true), "Hold the power button for 6 seconds? Unsaved work on the customer PC will be lost.");
    private async void Reset_Click(object sender, RoutedEventArgs e) =>
        await BoxCommand("Reset pressed", b => b.ResetAsync(), "Press the customer PC's reset button? Unsaved work will be lost.");
    private async void UsbHost_Click(object sender, RoutedEventArgs e) => await BoxCommand("Shared USB port → laptop", b => b.SetUsbAsync(UsbSide.Host));
    private async void UsbTarget_Click(object sender, RoutedEventArgs e) => await BoxCommand("Shared USB port → customer PC", b => b.SetUsbAsync(UsbSide.Target));

    // ------------------------------------------------------------------ virtual media (item 9)

    private async void VmConnect_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(HHT.Bridge.Core.Licensing.Feature.Hardware)) return;
        _vm?.Dispose();
        _vm = null;
        VmButtons.IsEnabled = false;
        try
        {
            var vm = new VirtualMediaClient(VmUrlBox.Text.Trim(), VmTokenBox.Password, VmFingerprintBox.Text.Trim());
            var st = await vm.StatusAsync();
            _vm = vm;
            _settings.VirtualMediaUrl = VmUrlBox.Text.Trim();
            _settings.VirtualMediaTokenProtected = Dpapi.ProtectString(VmTokenBox.Password);
            _settings.VirtualMediaFingerprint = VmFingerprintBox.Text.Trim();
            SaveSettings();
            VmButtons.IsEnabled = true;
            _log.Audit($"Virtual media module connected ({VmUrlBox.Text.Trim()}, agent {st.Version}).");
            ShowVmStatus(st);
        }
        catch (Exception ex)
        {
            VmStatusText.Text = $"Could not connect: {ex.Message}";
        }
    }

    private void ShowVmStatus(VmStatus st)
    {
        VmImageList.Items.Clear();
        foreach (var i in st.Images) VmImageList.Items.Add(new VmItem(i));
        VmStatusText.Text = (st.GadgetActive ? "" : "USB gadget NOT active on the module. ") +
                            (st.Mounted != null ? $"Mounted: {st.Mounted} as {(st.Mode == "cdrom" ? "CD-ROM" : "USB disk")}. " : "Nothing mounted. ") +
                            $"{st.FreeBytes / 1_000_000_000.0:0.0} GB free.";
    }

    private sealed record VmItem(VmImage Image)
    {
        public override string ToString() => $"{Image.Name}   ({Image.Size / 1_000_000.0:0} MB)";
    }

    private async Task VmAction(string label, Func<VirtualMediaClient, Task> action)
    {
        if (_vm == null) return;
        VmButtons.IsEnabled = false;
        try
        {
            await action(_vm);
            _log.Audit($"Virtual media: {label}.");
            ShowVmStatus(await _vm.StatusAsync());
        }
        catch (Exception ex)
        {
            _log.Warn($"Virtual media: {label} failed: {ex.Message}");
            VmStatusText.Text = ex.Message;
        }
        finally { VmButtons.IsEnabled = _vm != null; }
    }

    private string? SelectedImage => (VmImageList.SelectedItem as VmItem)?.Image.Name;

    private async void VmUpload_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Disk images (*.iso;*.img)|*.iso;*.img", Title = "Upload to the virtual media module" };
        if (dlg.ShowDialog(this) != true) return;
        var file = dlg.FileName;
        if (!VirtualMediaClient.IsAllowedImageName(System.IO.Path.GetFileName(file)))
        {
            MessageBox.Show(this, "Rename the file to plain letters, numbers, spaces, dots, dashes or brackets, ending in .iso or .img.", "Upload");
            return;
        }
        VmProgress.Visibility = Visibility.Visible;
        VmProgress.Value = 0;
        var progress = new Progress<double>(p => { VmProgress.Value = p; VmStatusText.Text = $"Uploading {System.IO.Path.GetFileName(file)}: {p:P0}"; });
        await VmAction($"uploaded {System.IO.Path.GetFileName(file)}", vm => vm.UploadAsync(file, progress));
        VmProgress.Visibility = Visibility.Collapsed;
    }

    private async void VmMountCd_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedImage is { } n) await VmAction($"mounted {n} as CD-ROM", vm => vm.MountAsync(n, asCdrom: true));
    }

    private async void VmMountDisk_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedImage is { } n) await VmAction($"mounted {n} as USB disk", vm => vm.MountAsync(n, asCdrom: false));
    }

    private async void VmEject_Click(object sender, RoutedEventArgs e) => await VmAction("ejected", vm => vm.EjectAsync());

    private async void VmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedImage is not { } n) return;
        if (MessageBox.Show(this, $"Delete {n} from the module?", "Virtual media", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await VmAction($"deleted {n}", vm => vm.DeleteAsync(n));
    }
}
