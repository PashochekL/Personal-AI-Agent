internal static class StartupBannerChecks
{
    public static int Run()
    {
        var art = StartupBanner.Render(true, 80);
        if (!art.Contains("\\m/", StringComparison.Ordinal) || !art.Contains("█████", StringComparison.Ordinal) || art.Split('\n').Length < 10) throw new Exception("Large startup art is missing");
        if (art.Split('\n').Any(line => line.TrimEnd('\r').Length > 68)) throw new Exception("Large startup art exceeds its minimum width");
        var compact = StartupBanner.Render(true, 35);
        if (!compact.Contains("(⌐■_■)", StringComparison.Ordinal) || compact.Split('\n').Any(line => line.TrimEnd('\r').Length > 19)) throw new Exception("Compact startup art failed");
        if (StartupBanner.Render(true, 10) != "(⌐■_■)") throw new Exception("Narrow-terminal fallback failed");
        if (StartupBanner.Render(false, 80) != "(⌐■_■)") throw new Exception("Redirected-output fallback failed");
        using var writer = new StringWriter();
        StartupBanner.Show(writer, true, () => throw new IOException());
        if (!writer.ToString().Contains("(⌐■_■)", StringComparison.Ordinal)) throw new Exception("Unavailable terminal width not handled");
        return 6;
    }
}
