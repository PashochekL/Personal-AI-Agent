/// <summary>Startup art with a compact version for redirected output or narrow terminals.</summary>
internal static class StartupBanner
{
    private const string CompactArt = """
        \m/           \m/
          \  (⌐■_■)  /
           \   /|\  /
              / | \
               / \
        """;

    private const string LargeArt = """
          \m/                     \m/
            \\                     //
             \\     __________    //       ███    ███   █   █  █   █  ████
              \\  /          \  //         █ █   █   █  ██ ██  █  ██  █   █
               \\|   ▄■    ■▄ |//          █ █   █   █  █ █ █  █ █ █  █   █
                 |      ▽      |           █ █   █████  █ █ █  █ █ █  ████
                  \   _____   /            █ █   █   █  █   █  ██  █  █
                   \_________/            █████  █   █  █   █  █   █  █
                      |   |               █   █  █   █  █   █  █   █  █
                    __|   |__
                   /   | |   \
                  /    | |    \
                      /   \
                     /     \
        """;

    public static string Render(bool interactive, int width)
    {
        if (!interactive || width < 19) return "(⌐■_■)";
        if (width < 68) return CompactArt;
        return LargeArt;
    }

    public static void Show(TextWriter output, bool interactive, Func<int> terminalWidth)
    {
        var width = 0;
        if (interactive)
        {
            try { width = terminalWidth(); }
            catch (Exception e) when (e is IOException or InvalidOperationException or PlatformNotSupportedException) { }
        }
        output.WriteLine(Render(interactive, width));
        output.WriteLine();
    }
}
