using System.Diagnostics;
using System.Runtime.InteropServices;
using JamesTools.Core.Scanning;

// All native state exists only in this expendable process. stdout is exclusively framed protocol.
internal static unsafe class Program
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32")] private static extern bool FreeLibrary(nint handle);
    private static nint Export(nint dll, string name) => NativeLibrary.GetExport(dll, name);
    private static int Main(string[] args)
    {
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine("OCR worker failed: " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    private static Task<int> Run(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1])) throw new ArgumentException();
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
        // Dependencies may resolve only beside the selected DLL and in System32, never cwd/PATH.
        nint dll = LoadLibraryExW(args[0], 0, 0x100 | 0x800);
        if (dll == 0) throw new System.ComponentModel.Win32Exception();
        nint api = 0;
        try
        {
            var version = (delegate* unmanaged[Cdecl]<nint>)Export(dll, "TessVersion");
            if (!(Marshal.PtrToStringUTF8(version()) ?? "").TrimStart('v').StartsWith("5.")) throw new InvalidDataException("Expected Tesseract 5, got " + Marshal.PtrToStringUTF8(version()));
            var init = (delegate* unmanaged[Cdecl]<nint, byte*, byte*, int>)Export(dll, "TessBaseAPIInit3");
            var mode = (delegate* unmanaged[Cdecl]<nint, int, void>)Export(dll, "TessBaseAPISetPageSegMode");
            var image = (delegate* unmanaged[Cdecl]<nint, byte*, int, int, int, int, void>)Export(dll, "TessBaseAPISetImage");
            var text = (delegate* unmanaged[Cdecl]<nint, int, nint>)Export(dll, "TessBaseAPIGetTsvText");
            var free = (delegate* unmanaged[Cdecl]<nint, void>)Export(dll, "TessDeleteText");
            var clear = (delegate* unmanaged[Cdecl]<nint, void>)Export(dll, "TessBaseAPIClear");
            var create = (delegate* unmanaged[Cdecl]<nint>)Export(dll, "TessBaseAPICreate");
            _ = Export(dll, "TessBaseAPIEnd"); _ = Export(dll, "TessBaseAPIDelete");
            api = create(); if (api == 0) throw new OutOfMemoryException();
            byte[] path = System.Text.Encoding.UTF8.GetBytes(args[1] + "\0"), lang = "eng\0"u8.ToArray();
            fixed (byte* p = path, l = lang) if (init(api, p, l) != 0) throw new InvalidDataException("English initialization failed.");
            mode(api, 11);
            var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
            HudWorkerProtocol.WriteAsync(output, "wardogs-ocr-1", default).GetAwaiter().GetResult();
            while (true)
            {
                HudFrame frame;
                try { frame = HudWorkerProtocol.ReadAsync<HudFrame>(input, default).GetAwaiter().GetResult(); } catch (EndOfStreamException) { break; }
                frame.Validate();
                fixed (byte* pixels = frame.Gray) image(api, pixels, frame.Width, frame.Height, 1, frame.Stride);
                nint result = text(api, 0);
                OcrRow[] rows;
                try
                {
                    if (result == 0) throw new InvalidDataException();
                    int length = 0; while (length < 65536 && Marshal.ReadByte(result, length) != 0) length++;
                    if (length == 65536) throw new InvalidDataException();
                    rows = FfmpegTesseractFrames.ParseTsv("level\n" + Marshal.PtrToStringUTF8(result, length)!).ToArray();
                    if (rows.Length > 256) throw new InvalidDataException();
                }
                finally { if (result != 0) free(result); clear(api); }
                HudWorkerProtocol.WriteAsync(output, new HudWorkerReply(rows, Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds), default).GetAwaiter().GetResult();
            }
            return Task.FromResult(0);
        }
        finally { if (api != 0) { ((delegate* unmanaged[Cdecl]<nint, void>)Export(dll, "TessBaseAPIEnd"))(api); ((delegate* unmanaged[Cdecl]<nint, void>)Export(dll, "TessBaseAPIDelete"))(api); } FreeLibrary(dll); }
    }
}
