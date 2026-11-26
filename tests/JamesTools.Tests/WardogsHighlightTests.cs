using JamesTools.Core.Configuration;
using JamesTools.Core.Scanning;

internal static class WardogsHighlightTests
{
    private static TimeSpan At(int seconds) => TimeSpan.FromSeconds(seconds);
    private static LiveKill Hit(long id, WardogsEventKind kind) => new(id, kind, TimeSpan.Zero);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static Task Run()
    {
        var first = Hit(1, WardogsEventKind.Kill);
        var second = Hit(2, WardogsEventKind.Headshot);
        var assist = Hit(3, WardogsEventKind.Assist);
        var clips = new WardogsHighlights(new(), 20);
        clips.Observe([first], At(1));
        clips.Observe([first, second], At(4));
        clips.Observe([first, second, assist], At(7));
        Check(!clips.TakeDue(At(8)), "second kill must extend the clip");
        Check(clips.TakeDue(At(9)), "an excluded assist must not extend the clip");
        clips.Observe([first, second, assist], At(10));
        Check(!clips.Pending && !clips.TakeDue(At(20)), "published event history must not re-arm a clip");

        var headshots = new WardogsHighlights(new() { HighlightKills = false }, 15);
        headshots.Observe([first], At(1));
        Check(!headshots.Pending, "a standard kill must not trigger headshot-only mode");
        headshots.Observe([first with { Kind = WardogsEventKind.Headshot }], At(3));
        Check(headshots.TakeDue(At(8)), "refinement of an existing event must trigger headshot-only mode");

        var streak = new WardogsHighlights(new() { HighlightPostSeconds = 10 }, 15);
        streak.Observe([first], At(1));
        streak.Observe([first, second], At(8));
        Check(streak.TakeDue(At(11)), "a long streak must save before the first event leaves the buffer");
        streak.Observe([Hit(4, WardogsEventKind.Kill)], At(15));
        Check(!streak.TakeDue(At(32)) && !streak.Pending, "a late save must not publish an expired clip");
        Check(WardogsHighlights.SupportsExecutable(@"C:\Steam\WardogsClient-Win64-Shipping.exe"), "WARDOGS client not recognized");
        Check(!WardogsHighlights.SupportsExecutable(@"C:\Games\AnotherGame.exe"), "other games must not auto-arm WARDOGS OCR");
        return Task.CompletedTask;
    }
}
