using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace HHT.Bridge.TechConsole.Capture;

/// <summary>
/// Lists video capture devices (webcams, HDMI-to-USB UVC dongles) in DirectShow order.
/// The index of each name matches OpenCV's DSHOW backend device index.
/// Uses only the documented System Device Enumerator COM API.
/// </summary>
public static class DirectShowDevices
{
    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid IidPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid clsidDeviceClass, out IEnumMoniker? enumMoniker, [In] int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([In, MarshalAs(UnmanagedType.LPWStr)] string propName, [In, Out, MarshalAs(UnmanagedType.Struct)] ref object? value, [In] IntPtr errorLog);

        [PreserveSig]
        int Write([In, MarshalAs(UnmanagedType.LPWStr)] string propName, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    public static IReadOnlyList<string> GetVideoInputNames()
    {
        var names = new List<string>();
        var type = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum);
        if (type == null) return names;

        object? enumObj = null;
        IEnumMoniker? monikers = null;
        try
        {
            enumObj = Activator.CreateInstance(type);
            if (enumObj is not ICreateDevEnum devEnum) return names;
            var category = ClsidVideoInputDeviceCategory;
            int hr = devEnum.CreateClassEnumerator(ref category, out monikers, 0);
            if (hr != 0 || monikers == null) return names; // S_FALSE (1) = no devices

            var one = new IMoniker[1];
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                var moniker = one[0];
                try
                {
                    var iid = IidPropertyBag;
                    moniker.BindToStorage(null!, null, ref iid, out var bagObj);
                    string name = "Unknown video device";
                    if (bagObj is IPropertyBag bag)
                    {
                        object? value = null;
                        if (bag.Read("FriendlyName", ref value, IntPtr.Zero) == 0 && value is string s && s.Length > 0) name = s;
                        Marshal.ReleaseComObject(bagObj);
                    }
                    names.Add(name);
                }
                catch (COMException)
                {
                    names.Add("Unknown video device");
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        finally
        {
            if (monikers != null) Marshal.ReleaseComObject(monikers);
            if (enumObj != null) Marshal.ReleaseComObject(enumObj);
        }
        return names;
    }

    /// <summary>Heuristic: cheap HDMI dongles (MS2109/MS2130) usually report one of these names.</summary>
    public static bool LooksLikeHdmiCapture(string name) =>
        name.Contains("USB Video", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Capture", StringComparison.OrdinalIgnoreCase)
        || name.Contains("HDMI", StringComparison.OrdinalIgnoreCase)
        || name.Contains("MS2109", StringComparison.OrdinalIgnoreCase)
        || name.Contains("MS2130", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Openterface", StringComparison.OrdinalIgnoreCase);
}
