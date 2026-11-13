using JamesTools.Core.Configuration;

namespace JamesTools.Core.Recording;

public sealed record ObsSettings(string ExecutablePath, string ConfigRoot, string RecordingDirectory, bool Portable,
    CaptureMode CaptureMode = CaptureMode.Recording, int ReplayBufferSeconds = 60);

public sealed record GameWindow(int ProcessId, string Title, string ClassName, string ExecutableName);

public enum RecorderStartStatus
{
    Started,
    AlreadyRunning,
    WaitingForWindow,
    Failed
}

public sealed record RecorderStartResult(RecorderStartStatus Status, string Message);
