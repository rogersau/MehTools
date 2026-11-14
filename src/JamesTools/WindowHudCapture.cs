using System.Diagnostics;
using System.Runtime.InteropServices;
using JamesTools.Core.Configuration;
using JamesTools.Core.Scanning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace JamesTools;

internal sealed record LiveTarget(nint Hwnd, int Pid, long Started, string Path, string Title)
{
    public override string ToString() => $"{Title} — PID {Pid}";
    public bool Valid()
    {
        try { using var p = Process.GetProcessById(Pid); Native.GetWindowThreadProcessId(Hwnd, out var pid); return Native.IsWindow(Hwnd) && pid == Pid && p.StartTime.ToUniversalTime().Ticks == Started && string.Equals(p.MainModule?.FileName, Path, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    public static LiveTarget[] Enumerate(string path)
    {
        var list = new List<LiveTarget>();
        Native.EnumWindows((h, _) => { try { Native.GetWindowThreadProcessId(h, out var id); using var p = Process.GetProcessById((int)id); if (Native.IsWindowVisible(h) && string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) list.Add(new(h, p.Id, p.StartTime.ToUniversalTime().Ticks, path, p.MainWindowTitle)); } catch { } return true; }, 0);
        return list.ToArray();
    }
}
internal static class Native
{
    internal delegate bool EnumCallback(nint h, nint p);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, nint p);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
}

// All GPU operations and ownership are serialized by gate. No FrameArrived work or CPU queue.
internal sealed unsafe class WindowHudCapture : ILatestHudFrameSource, IDisposable
{
    private readonly object gate = new();
    private readonly LiveTarget target;
    private readonly LiveWardogsSettings settings;
    private readonly System.Threading.Timer focus;
    private nint device, context;
    private IDirect3DDevice? winDevice;
    private GraphicsCaptureItem? item;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private readonly LiveCaptureLifecycle lifecycle = new();
    private readonly TimeSpan origin = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private int width, height;
    private bool disposed;
    private Exception? error;
    private int targetClosed;
    public string Status { get; private set; } = "Paused — focus selected game";
    public WindowHudCapture(LiveTarget target, LiveWardogsSettings settings)
    {
        this.target = target; this.settings = settings.Copy();
        if (!this.settings.IsValid) throw new ArgumentException("Invalid capture settings.", nameof(settings));
        if (!GraphicsCaptureSession.IsSupported()) throw new PlatformNotSupportedException("Windows Graphics Capture is unavailable on this system.");
        focus = new(_ => { lock (gate) { if (disposed) return; try { Check(); } catch (Exception ex) { error = ex; try { CloseCapture(); } catch (Exception cleanup) { error = new AggregateException(ex, cleanup); } } } }, null, 0, 100);
    }
    private bool Check()
    {
        bool active = lifecycle.Observe(Volatile.Read(ref targetClosed) == 0 && target.Valid(), Native.GetForegroundWindow() == target.Hwnd && Native.IsWindowVisible(target.Hwnd), Native.IsIconic(target.Hwnd));
        if (!active) { if (pool != null) CloseCapture(); Status = "Paused — focus selected game"; }
        return active;
    }
    private void TargetClosed(GraphicsCaptureItem sender, object args) => Interlocked.Exchange(ref targetClosed, 1);
    private void Open()
    {
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context));
            var dxgi = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in dxgi, out var d));
            try { Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(d, out var inspectable)); try { winDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); } finally { Marshal.Release(inspectable); } } finally { Marshal.Release(d); }
            var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var iid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            factory.CreateForWindow(target.Hwnd, ref iid, out var ptr);
            try { item = MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr); } finally { Marshal.Release(ptr); }
            item.Closed += TargetClosed;
            width = item.Size.Width; height = item.Size.Height; ValidateSize(width, height);
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = pool.CreateCaptureSession(item); session.IsCursorCaptureEnabled = false; session.StartCapture(); lifecycle.Size(width, height);
            Status = "Waiting for a frame";
        }
        catch { CloseCapture(); throw; }
    }
    public ValueTask<HudFrame?> LatestAsync(CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
            if (error != null) throw new InvalidOperationException("Capture stopped: " + error.Message, error);
            if (!Check()) return ValueTask.FromResult<HudFrame?>(null);
            if (pool == null) Open();
            var newest = pool!.TryGetNextFrame();
            if (newest != null) { var next = pool.TryGetNextFrame(); if (next != null) { newest.Dispose(); newest = next; } }
            using var frame = newest; if (frame == null) return ValueTask.FromResult<HudFrame?>(null);
            if (frame.ContentSize.Width != width || frame.ContentSize.Height != height)
            {
                ValidateSize(frame.ContentSize.Width, frame.ContentSize.Height);
                // Reopen on the following sample, after this frame has been released.
                frame.Dispose(); CloseCapture(); return ValueTask.FromResult<HudFrame?>(null);
            }
            var timestamp = frame.SystemRelativeTime - origin;
            if (!lifecycle.Accept(timestamp)) return ValueTask.FromResult<HudFrame?>(null);
            var gray = ReadCrop(frame, token, out int w, out int h);
            if (!Check()) return ValueTask.FromResult<HudFrame?>(null);
            Status = "Running";
            return ValueTask.FromResult<HudFrame?>(new(target.Pid.ToString(), lifecycle.Epoch, timestamp, w, h, w, gray));
        }
    }
    private static void ValidateSize(int w, int h) { if (w < 16 || h < 16 || w > 16384 || h > 16384) throw new InvalidDataException("Unsupported capture dimensions."); }
    private byte[] ReadCrop(Direct3D11CaptureFrame frame, CancellationToken token, out int w, out int h)
    {
        int x = (int)(width * settings.X), y = (int)(height * settings.Y);
        int cw = Math.Min(width - x, (int)(width * settings.Width)), ch = Math.Min(height - y, (int)(height * settings.Height));
        if ((long)cw * ch > 4194304) throw new InvalidDataException("Crop exceeds 16 MiB GPU readback limit. Choose a smaller normalized crop.");
        if (cw < 16 || ch < 16) throw new InvalidDataException("Crop must be at least 16 × 16 pixels.");
        // GPU copies only ROI to a staging texture; CPU converts/downsamples only at detector cadence.
        var desc = new TextureDesc { W = (uint)cw, H = (uint)ch, Mips = 1, Array = 1, Format = 87, SampleCount = 1, Usage = 3, Cpu = 0x20000 };
        nint staging = 0, texture = 0;
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)(*(nint**)device)[5])(device, &desc, 0, &staging));
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>(); var id = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); access.GetInterface(ref id, out texture);
            var box = new Box { Left = (uint)x, Top = (uint)y, Right = (uint)(x + cw), Bottom = (uint)(y + ch), Back = 1 };
            ((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,uint,nint,uint,Box*,void>)(*(nint**)context)[46])(context, staging, 0, 0, 0, 0, texture, 0, &box);
            // Flush the copy, then use DO_NOT_WAIT so a busy GPU cannot hold Stop indefinitely.
            ((delegate* unmanaged[Stdcall]<nint,void>)(*(nint**)context)[111])(context);
            Mapped mapped;
            long mapStart = Stopwatch.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int hr = ((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,Mapped*,int>)(*(nint**)context)[14])(context, staging, 0, 1, 0x100000, &mapped);
                if (hr != unchecked((int)0x887A000A)) { Marshal.ThrowExceptionForHR(hr); break; }
                if (Stopwatch.GetElapsedTime(mapStart) > TimeSpan.FromMilliseconds(250)) throw new TimeoutException("GPU crop readback timed out. Capture stopped.");
                if (token.WaitHandle.WaitOne(1)) token.ThrowIfCancellationRequested();
            }
            try
            {
                double scale = Math.Min(1, Math.Min(1024d / cw, 512d / ch)); w = Math.Max(16, (int)(cw * scale)); h = Math.Max(16, (int)(ch * scale)); var result = new byte[w * h];
                for (int j = 0; j < h; j++) { token.ThrowIfCancellationRequested(); for (int i = 0; i < w; i++) { byte* p = (byte*)mapped.Data + (j * ch / h) * mapped.Pitch + (i * cw / w) * 4; result[j * w + i] = (byte)((p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8); } }
                return result;
            }
            finally { ((delegate* unmanaged[Stdcall]<nint,nint,uint,void>)(*(nint**)context)[15])(context, staging, 0); }
        }
        finally { if (texture != 0) Marshal.Release(texture); if (staging != 0) Marshal.Release(staging); }
    }
    private void CloseCapture()
    {
        var oldSession = session; session = null; var oldPool = pool; pool = null;
        var oldItem = item; item = null; var oldWinDevice = winDevice; winDevice = null;
        var oldContext = context; context = 0; var oldDevice = device; device = 0;
        try { if (oldItem != null) oldItem.Closed -= TargetClosed; }
        finally { try { oldSession?.Dispose(); }
            finally { try { oldPool?.Dispose(); }
                finally { try { (oldWinDevice as IDisposable)?.Dispose(); }
                    finally { try { if (oldContext != 0) Marshal.Release(oldContext); }
                        finally { if (oldDevice != 0) Marshal.Release(oldDevice); } } } } }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; focus.Dispose(); CloseCapture(); } }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc { public uint W,H,Mips,Array,Format,SampleCount,SampleQuality,Usage,Bind,Cpu,Misc; }
    [StructLayout(LayoutKind.Sequential)] private struct Box { public uint Left,Top,Front,Right,Bottom,Back; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public nint Data; public uint Pitch,Depth; }
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IGraphicsCaptureItemInterop { void CreateForWindow(nint hwnd, ref Guid iid, out nint result); void CreateForMonitor(nint monitor, ref Guid iid, out nint result); }
    [ComImport, Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IDirect3DDxgiInterfaceAccess { void GetInterface(ref Guid iid, out nint result); }
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter,uint driver,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out uint level,out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi,out nint device);
}
