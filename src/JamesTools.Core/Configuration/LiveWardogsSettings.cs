namespace JamesTools.Core.Configuration;

public sealed class LiveWardogsSettings
{
    public string TargetPath { get; set; } = "";
    public double Rate { get; set; } = 2;
    public double Budget { get; set; } = .25;
    public double X { get; set; } = .703125;
    public double Y { get; set; } = .0416667;
    public double Width { get; set; } = .2864583;
    public double Height { get; set; } = .2222222;
    public bool HighlightKills { get; set; } = true;
    public bool HighlightHeadshots { get; set; } = true;
    public bool HighlightAssists { get; set; }
    public bool HighlightSupport { get; set; }
    public int HighlightPostSeconds { get; set; } = 5;
    public bool HighlightsValid => (HighlightKills || HighlightHeadshots || HighlightAssists || HighlightSupport) && HighlightPostSeconds is >= 3 and <= 10;
    public LiveWardogsSettings Copy() => (LiveWardogsSettings)MemberwiseClone();
    public bool IsValid => double.IsFinite(Rate) && Rate is >= .25 and <= 4 && double.IsFinite(Budget) && Budget is >= .01 and <= 1 &&
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) && X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1 && Y + Height <= 1;
    public void Normalize()
    {
        TargetPath ??= "";
        if (HighlightPostSeconds is < 3 or > 10) HighlightPostSeconds = 5;
        if (!(HighlightKills || HighlightHeadshots || HighlightAssists || HighlightSupport)) HighlightKills = HighlightHeadshots = true;
        if (IsValid) return;
        Rate = 2; Budget = .25; X = .703125; Y = .0416667; Width = .2864583; Height = .2222222;
    }
}
