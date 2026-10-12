using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using S9Battery.Core;

namespace S9Battery.Tray;

internal record HidDevice(string Path, ushort Vendor, ushort Product, ushort FeatureLength,
    ushort InputLength, ushort UsagePage, ushort Usage, string Name, Guid ContainerId)
{
    public bool Candidate => Protocol.IsCandidate(Vendor, Product) &&
        UsagePage >= 0xFF00 && FeatureLength >= 64 && FeatureLength <= 4096;

    public SafeFileHandle Open()
    {
        // HID feature IOCTLs work with zero desired access; ordinary mouse input is untouched.
        var handle = Native.CreateFile(Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return handle;
    }

    public FileStream OpenInput()
    {
        if (!Protocol.IsCandidate(Vendor, Product) || UsagePage < 0xFF00 || InputLength is < 4 or > 4096)
            throw new InvalidOperationException("只允许监听目标鼠标的厂商状态报告。");
        var handle = Native.CreateFile(Path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(error, "无法打开电量事件通道");
        }
        try { return new FileStream(handle, FileAccess.Read, InputLength, isAsync: true); }
        catch { handle.Dispose(); throw; }
    }

    public byte[] Query(SafeFileHandle handle, Query query, Action<string> log)
    {
        var request = Protocol.Encode(query, FeatureLength);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!Native.HidD_SetFeature(handle, request, request.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "发送状态查询失败");
            log($"VID={Vendor:X4} PID={Product:X4} {query} TX={Convert.ToHexString(request)}");
            for (int read = 0; read < 4; read++)
            {
                Thread.Sleep(100);
                var response = new byte[FeatureLength];
                response[0] = Protocol.ReportId;
                if (!Native.HidD_GetFeature(handle, response, response.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "读取状态响应失败");
                log($"{query} RX={Convert.ToHexString(response)}");
                try { return Protocol.Decode(query, response); }
                catch (InvalidDataException) when (read < 3 || attempt < 1) { }
            }
        }
        throw new IOException("设备未返回匹配的状态响应。");
    }

    public static IReadOnlyList<HidDevice> Enumerate()
    {
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new List<HidDevice>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                Native.SetupDiGetDeviceInterfaceDetail(set, ref info, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size < 6 || size > 65536) continue;
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    var devInfo = new Native.DeviceInfo { Size = Marshal.SizeOf<Native.DeviceInfo>() };
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref info, buffer, size, out _, ref devInfo)) continue;
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (path is null) continue;
                    using var handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attr = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
                    if (!Native.HidD_GetAttributes(handle, ref attr)) continue;
                    // Diagnostics only enumerate this vendor, never unrelated keyboards or mice.
                    if (attr.VendorId != Protocol.VendorId) continue;
                    if (!Native.HidD_GetPreparsedData(handle, out var data)) continue;
                    try
                    {
                        if (Native.HidP_GetCaps(data, out var caps) != 0x00110000) continue;
                        var name = new StringBuilder(256);
                        Native.HidD_GetProductString(handle, name, 512);
                        var key = new Native.PropertyKey
                        { FormatId = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), PropertyId = 2 };
                        var containerBytes = new byte[16];
                        Guid container = Native.SetupDiGetDeviceProperty(set, ref devInfo, ref key,
                            out uint type, containerBytes, 16, out uint required, 0) && type == 0xD && required == 16 ?
                            new(containerBytes) : Guid.Empty;
                        result.Add(new(path, attr.VendorId, attr.ProductId, caps.FeatureLength,
                            caps.InputLength, caps.UsagePage, caps.Usage, name.ToString(), container));
                    }
                    finally { Native.HidD_FreePreparsedData(data); }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData
    { public int Size; public Guid Guid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes
    { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo
    { public int Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey
    { public Guid FormatId; public uint PropertyId; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputData,
            OutputButtons, OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
    }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_SetFeature(SafeFileHandle handle, [In] byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] buffer, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid,
        uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data,
        IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data,
        IntPtr detail, uint size, out uint required, ref DeviceInfo device);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DeviceInfo device, ref PropertyKey key,
        out uint type, [Out] byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr icon);
}
