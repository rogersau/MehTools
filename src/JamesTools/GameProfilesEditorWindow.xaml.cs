using System.Collections.ObjectModel;
using System.Windows;
using JamesTools.Core.Configuration;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace JamesTools;

public partial class GameProfilesEditorWindow : Window
{
    private bool _loading;
    private readonly GameRule[] _games;
    private readonly PowerPlanOption[] _powerPlans;
    public ObservableCollection<GameProfile> Profiles { get; }
    private IReadOnlyList<ProfileValueOption<CaptureMode>> CaptureModes { get; } =
        [new(null, "Inherit"), new(CaptureMode.Recording, "Recording"), new(CaptureMode.Replay, "Replay"),
         new(CaptureMode.Both, "Both"), new(CaptureMode.WardogsHighlights, "WARDOGS highlights")];

    public GameProfilesEditorWindow(IEnumerable<GameProfile> profiles, IEnumerable<PowerPlanOption> powerPlans,
        IEnumerable<GameRule> games)
    {
        InitializeComponent();
        _games = games.Select(game => game.Copy()).ToArray();
        _powerPlans = powerPlans.Where(p => p.Id is not null).DistinctBy(p => p.Id).ToArray();
        Profiles = new ObservableCollection<GameProfile>(profiles.Select(p => p.Copy()));
        ProfileList.ItemsSource = Profiles;
        CaptureModeBox.SelectedValuePath = nameof(ProfileValueOption<CaptureMode>.Value);
        PowerPlanBox.SelectedValuePath = nameof(PowerPlanOption.Id);
        PowerPlanBox.DisplayMemberPath = nameof(PowerPlanOption.Name);
        ProfileList.SelectedIndex = Profiles.Count == 0 ? -1 : 0;
    }

    private GameProfile? Selected => ProfileList.SelectedItem as GameProfile;

    private void ProfileList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _loading = true;
        var profile = Selected;
        ProfileEditor.Visibility = ProfileActions.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        CaptureModeBox.ItemsSource = WithUnsupported(CaptureModes, profile?.CaptureMode, "Unsupported capture mode");
        var availablePlans = new[] { new PowerPlanOption(null, "Inherit") }.Concat(_powerPlans).ToList();
        if (profile?.PowerPlanId is Guid unknownPlan && unknownPlan != Guid.Empty && _powerPlans.All(plan => plan.Id != unknownPlan))
            availablePlans.Add(new PowerPlanOption(unknownPlan, $"Unavailable plan ({unknownPlan})"));
        PowerPlanBox.ItemsSource = availablePlans;
        NameBox.Text = profile?.Name ?? "";
        DirectoryBox.Text = profile?.RecordingDirectory ?? "";
        ReplayBox.Text = profile?.ReplayBufferSeconds?.ToString() ?? "";
        DelayBox.Text = profile?.RecordingStopDelaySeconds?.ToString() ?? "";
        VibranceBox.Text = profile?.VibrancePercent?.ToString() ?? "";
        CaptureModeBox.SelectedItem = CaptureModeBox.Items.Cast<ProfileValueOption<CaptureMode>>().FirstOrDefault(option => option.Value == profile?.CaptureMode);
        Guid? power = profile?.PowerPlanId is Guid id && id != Guid.Empty ? id : null;
        PowerPlanBox.SelectedItem = availablePlans.FirstOrDefault(option => option.Id == power);
        _loading = false;
    }

    private static IReadOnlyList<ProfileValueOption<T>> WithUnsupported<T>(IReadOnlyList<ProfileValueOption<T>> options, T? value, string label)
        where T : struct, Enum
    {
        if (value is T candidate && !Enum.IsDefined(candidate)) return options.Append(new ProfileValueOption<T>(candidate, $"{label} ({Convert.ToInt64(candidate)})")).ToArray();
        return options;
    }

    private void ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || Selected is not { } profile) return;
        profile.Name = NameBox.Text.Trim();
        profile.RecordingDirectory = string.IsNullOrWhiteSpace(DirectoryBox.Text) ? null : DirectoryBox.Text.Trim();
        profile.CaptureMode = CaptureModeBox.SelectedValue is CaptureMode capture ? capture : null;
        profile.PowerPlanId = PowerPlanBox.SelectedValue is Guid power && power != Guid.Empty ? power : null;
        if (TryOptionalInt(ReplayBox.Text, out var replay)) profile.ReplayBufferSeconds = replay;
        if (TryOptionalInt(DelayBox.Text, out var delay)) profile.RecordingStopDelaySeconds = delay;
        if (TryOptionalInt(VibranceBox.Text, out var vibrance)) profile.VibrancePercent = vibrance;
        ProfileList.Items.Refresh();
    }

    private static bool TryOptionalInt(string text, out int? value)
    {
        if (string.IsNullOrWhiteSpace(text)) { value = null; return true; }
        if (int.TryParse(text.Trim(), out var parsed)) { value = parsed; return true; }
        value = null;
        return false;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var profile = new GameProfile { Name = "New profile" };
        Profiles.Add(profile);
        ProfileList.SelectedItem = profile;
        NameBox.Focus(); NameBox.SelectAll();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected) return;
        var copy = selected.Copy();
        copy.Id = Guid.NewGuid();
        copy.Name = selected.Name + " copy";
        Profiles.Add(copy);
        ProfileList.SelectedItem = copy;
        NameBox.Focus(); NameBox.SelectAll();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected) return;
        if (!GameProfileOperations.TryRemove(Profiles, _games, selected.Id, out var message))
        {
            MessageBox.Show(this, message, "Profile is in use", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var index = ProfileList.SelectedIndex;
        ProfileList.SelectedIndex = Math.Min(index, Profiles.Count - 1);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose where this profile saves videos" };
        if (picker.ShowDialog(this) == true) { DirectoryBox.Text = picker.FolderName; ValueChanged(sender, e); }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        ValueChanged(sender, e);
        if (!TryOptionalInt(ReplayBox.Text, out var replay) || replay is < 15 or > 600)
        { MessageBox.Show(this, "Enter a replay length from 15 to 600 seconds, or leave it blank to inherit.", "Check profile", MessageBoxButton.OK, MessageBoxImage.Warning); ReplayBox.Focus(); return; }
        if (!TryOptionalInt(DelayBox.Text, out var delay) || delay is < 0 or > 120)
        { MessageBox.Show(this, "Enter a stop delay from 0 to 120 seconds, or leave it blank to inherit.", "Check profile", MessageBoxButton.OK, MessageBoxImage.Warning); DelayBox.Focus(); return; }
        if (!TryOptionalInt(VibranceBox.Text, out var vibrance) || vibrance is < 0 or > 100)
        { MessageBox.Show(this, "Enter vibrance from 0 to 100, or leave it blank to inherit.", "Check profile", MessageBoxButton.OK, MessageBoxImage.Warning); VibranceBox.Focus(); return; }
        if (Profiles.Any(p => string.IsNullOrWhiteSpace(p.Name)))
        { MessageBox.Show(this, "Every profile needs a name.", "Check profile", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }

    private sealed record ProfileValueOption<T>(T? Value, string Name) where T : struct, Enum
    {
        public override string ToString() => Name;
    }
}
