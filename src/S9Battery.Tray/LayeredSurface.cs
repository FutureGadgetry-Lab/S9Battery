using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace S9Battery.Tray;

// One reusable, premultiplied-alpha DIB. No native allocations per animation frame.
internal sealed class LayeredSurface : IDisposable
{
    private IntPtr dc, dib, previous;
    private Bitmap? image;
    public Bitmap Image => image ?? throw new ObjectDisposedException(nameof(LayeredSurface));

    public LayeredSurface(int width, int height)
    {
        try
        {
            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInfo { Header = new()
            { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 } };
            dib = CreateDIBSection(dc, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(dc, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            image = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, pixels);
        }
        catch { Dispose(); throw; }
    }

    public void Present(IntPtr window, Point location, byte opacity)
    {
        var position = new NativePoint { X = location.X, Y = location.Y };
        var source = new NativePoint();
        var size = new NativeSize { Width = Image.Width, Height = Image.Height };
        var blend = new BlendFunction { SourceConstantAlpha = opacity, AlphaFormat = 1 };
        if (!UpdateLayeredWindow(window, IntPtr.Zero, ref position, ref size, dc, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法绘制通知气泡");
    }

    public void Dispose()
    {
        image?.Dispose(); image = null;
        if (dc != IntPtr.Zero && previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
        if (dib != IntPtr.Zero) DeleteObject(dib);
        if (dc != IntPtr.Zero) DeleteDC(dc);
        dc = dib = previous = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction
    { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Color; }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc,
        ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref NativePoint position,
        ref NativeSize size, IntPtr sourceDC, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
}
