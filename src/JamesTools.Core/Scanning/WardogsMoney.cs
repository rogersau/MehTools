using System.Globalization;
using System.Text.RegularExpressions;

namespace JamesTools.Core.Scanning;

public sealed record WardogsBalance(long Amount, double Seconds);
public sealed record WardogsBalances(WardogsBalance Opening, WardogsBalance Closing);
public sealed record WardogsMoneySummary(WardogsBalance? Opening, WardogsBalance? Closing, long? DetectedRewards, int ScannedRecordings, int TotalRecordings)
{
    public static WardogsMoneySummary ForRecording(WardogsScanResult result) => FromRecordings([result], 1);

    internal static WardogsMoneySummary FromRecordings(IReadOnlyList<WardogsScanResult> scans, int totalRecordings)
    {
        var opening = scans.FirstOrDefault(scan => scan.Balances is not null)?.Balances?.Opening;
        var closing = scans.LastOrDefault(scan => scan.Balances is not null)?.Balances?.Closing;
        var rewards = scans.SelectMany(scan => scan.Candidates).Where(candidate => candidate.Reward.HasValue).Sum(candidate => candidate.Reward);
        return new(opening, closing, rewards is > 0 ? rewards : null, scans.Count, totalRecordings);
    }
}

internal static partial class WardogsMoney
{
    internal const long MaximumAmount = 999_999_999_999;

    [GeneratedRegex(@"(?<negative>[-−])?\s*\$\s*(?<amount>[0-9]{1,3}(?:(?:,\s*| )[0-9]{3})+|[0-9]{1,12})(?![0-9,])", RegexOptions.CultureInvariant)]
    internal static partial Regex Currency();

    internal static bool TryAmount(Match match, out long amount)
    {
        amount = 0;
        if (!match.Success || !long.TryParse(string.Concat(match.Groups["amount"].Value.Where(char.IsAsciiDigit)),
                NumberStyles.None, CultureInfo.InvariantCulture, out amount) || amount > MaximumAmount) return false;
        if (match.Groups["negative"].Success) amount = -amount;
        return true;
    }

    internal static bool IsValid(WardogsBalances balances, double duration) =>
        ValidReading(balances.Opening, duration) && ValidReading(balances.Closing, duration)
        && balances.Opening.Seconds <= balances.Closing.Seconds;

    private static bool ValidReading(WardogsBalance? balance, double duration) => balance is { Amount: >= 0 and <= MaximumAmount }
        && double.IsFinite(balance.Seconds) && balance.Seconds >= 0 && balance.Seconds <= duration;
}

internal sealed class WardogsBalanceTracker(WardogsScanOptions settings)
{
    private WardogsBalance? pending;
    private WardogsBalance? opening;
    private WardogsBalance? closing;

    internal WardogsBalances? Balances => opening is { } first && closing is { } last ? new(first, last) : null;

    internal void Observe(IReadOnlyList<OcrRow> rows, double seconds)
    {
        var amount = Read(rows);
        if (amount is null) { pending = null; return; }
        if (pending is { } previous && previous.Amount == amount.Value)
        {
            opening ??= previous;
            closing = new(amount.Value, seconds);
        }
        pending = new(amount.Value, seconds);
    }

    private long? Read(IReadOnlyList<OcrRow> rows)
    {
        long? right = null;
        long? menu = null;
        bool matchChangeVisible = false;
        foreach (var row in rows)
        {
            if (row.X is not (>= 0 and <= 1) || row.Y is not (>= 0 and <= 1)
                || row.Text is null || row.Text.Length > 4096) continue;
            double x = settings.Crop.X + row.X.Value * settings.Crop.Width;
            double y = settings.Crop.Y + row.Y * settings.Crop.Height;
            // Match HUD shows the signed match change left of the account balance. A lone far-right
            // amount can be the waiting-area allowance. The menu has its own balance position.
            if (y is < .012 or > .048 || x is < .795 or > .985) continue;
            var matches = WardogsMoney.Currency().Matches(row.Text);
            if (matches.Count == 2 && x is >= .855 and < .938 && WardogsMoney.TryAmount(matches[0], out _)
                && WardogsMoney.TryAmount(matches[1], out long second) && second >= 0) return second;
            if (matches.Count != 1 || !WardogsMoney.TryAmount(matches[0], out long single)) continue;
            if (x is >= .938 and <= .985 && single >= 0) right = single;
            else if (x is >= .855 and < .938) matchChangeVisible = true;
            else if (x is >= .795 and <= .824 && single >= 0) menu = single;
        }
        return matchChangeVisible ? right : menu;
    }
}
