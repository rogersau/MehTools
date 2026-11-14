using System.Windows;
using JamesTools;
using JamesTools.Core.Configuration;

namespace JamesTools.UiChecks;

// Supplies view-owned options to the real main-window markup without constructing automation services.
public sealed class MarkupHostWindow : Window
{
    public IReadOnlyList<GameValueOption<VibranceTargetMode>> VibranceTargetOptions { get; } =
        [new(VibranceTargetMode.AllDisplays, "All displays"), new(VibranceTargetMode.SelectedDisplay, "Selected display"), new(VibranceTargetMode.GameWindowDisplay, "Game window display")];
    public IReadOnlyList<VibranceDisplayOption> VibranceDisplayOptions { get; } = [new("synthetic", "Synthetic NVIDIA display")];
}
