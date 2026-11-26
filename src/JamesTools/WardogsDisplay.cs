using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
using JamesTools.Core.Scanning;

namespace JamesTools;

internal static class WardogsDisplay
{
    public static string EventName(WardogsEventKind kind) => kind switch
    {
        WardogsEventKind.Assist => "Kill assist",
        WardogsEventKind.RevengeKill => "Revenge kill",
        WardogsEventKind.ReviveAssist => "Revive assist",
        WardogsEventKind.Heal => "Healed teammate",
        WardogsEventKind.Revive => "Revived teammate",
        WardogsEventKind.WaitedForRevive => "Waited for revive",
        _ => kind.ToString()
    };

    public static string Cash(long amount) => "$" + amount.ToString("N0", CultureInfo.InvariantCulture);

    public static string VideoTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        var fraction = time.Milliseconds == 0 ? "" : $".{time.Milliseconds:000}".TrimEnd('0');
        return $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}{fraction}";
    }

    public static string Money(WardogsMoneySummary summary) => string.Join(" · ", MoneyParts(summary).Select(part => part.Label));

    public static bool ShowRecordingMoney(TextBlock display, WardogsMoneySummary summary, Func<WardogsBalance, Task> open)
    {
        display.Inlines.Clear();
        foreach (var (label, balance) in MoneyParts(summary))
        {
            if (display.Inlines.Count > 0) display.Inlines.Add(" · ");
            display.Inlines.Add(label);
            if (balance is null) continue;
            display.Inlines.Add(" at ");
            var link = new Hyperlink(new Run(VideoTime(balance.Seconds)));
            link.SetResourceReference(Hyperlink.ForegroundProperty, "AccentBrush");
            link.ToolTip = "Open the video at this balance reading";
            link.Click += async (_, _) => await open(balance);
            display.Inlines.Add(link);
        }
        return display.Inlines.Count > 0;
    }

    private static IEnumerable<(string Label, WardogsBalance? Balance)> MoneyParts(WardogsMoneySummary summary)
    {
        if (summary.Opening is { } opening && summary.Closing is { } closing)
        {
            yield return ($"First balance {Cash(opening.Amount)}", opening);
            yield return ($"Last balance {Cash(closing.Amount)}", closing);
            var change = closing.Amount - opening.Amount;
            yield return ($"Net {(change < 0 ? "−" : change > 0 ? "+" : "")}{Cash(Math.Abs(change))}", null);
        }
        if (summary.DetectedRewards is long rewards) yield return ($"Detected rewards {Cash(rewards)}", null);
    }
}
