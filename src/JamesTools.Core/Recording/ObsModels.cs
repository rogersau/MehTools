namespace JamesTools.Core.Recording;

public sealed record ObsSettings(string ExecutablePath, string ConfigRoot, string RecordingDirectory, bool Portable);

public sealed record GameWindow(int ProcessId, string Title, string ClassName, string ExecutableName);

public enum RecorderStartStatus
{
    Started,
    AlreadyRunning,
    WaitingForWindow,
    Failed
}

public sealed record RecorderStartResult(RecorderStartStatus Status, string Message);
