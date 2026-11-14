using JamesTools.Core.Scanning;
internal static class LiveWorkerTests
{
    public static async Task<int> Child(string mode)
    {
        await File.WriteAllTextAsync(mode + ".pid", Environment.ProcessId.ToString());
        if (mode.Contains("startup")) { await Task.Delay(30000); return 0; }
        if (mode.Contains("exit")) return 1;
        await HudWorkerProtocol.WriteAsync(Console.OpenStandardOutput(), "wardogs-ocr-1", default);
        await HudWorkerProtocol.ReadAsync<HudFrame>(Console.OpenStandardInput(), default);
        await Task.Delay(30000); return 0;
    }
    public static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wardogs-worker-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "eng.traineddata"), "fake");
            string helper = Path.Combine(AppContext.BaseDirectory, "JamesTools.Tests.exe");
            foreach (string mode in new[] { "startup", "exit", "request" })
            {
                string dll = Path.Combine(directory, mode + ".fake.dll"); await File.WriteAllTextAsync(dll, "fake");
                try
                {
                    await using var worker = await PersistentHudOcr.StartAsync(helper, dll, directory, startupTimeout: TimeSpan.FromSeconds(1), requestTimeout: TimeSpan.FromSeconds(.1));
                    await worker.ReadAsync(new("test", 1, TimeSpan.Zero, 32, 32, 32, new byte[1024]), default);
                    throw new Exception("Fake hung/exited worker unexpectedly succeeded");
                }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
                catch (EndOfStreamException) { }
                catch (IOException) { }
                var pid = int.Parse(await File.ReadAllTextAsync(dll + ".pid"));
                try { using var process = System.Diagnostics.Process.GetProcessById(pid); if (!process.HasExited) throw new Exception("Fake worker survived cleanup"); }
                catch (ArgumentException) { }
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
