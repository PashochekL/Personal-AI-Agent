using AiAgent.Telegram;

internal static class AllowedUsersChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        Check(TelegramOptions.ParseUserIds("900001, 900002,900001").SequenceEqual(new long[] { 900001, 900002 }), "multiple owner IDs parsed and deduplicated");
        foreach (var invalid in new[] { "", " ", "0", "-1", "+1", "900001,", "900001,,900002", "900001,user", "9223372036854775808" })
        {
            try { TelegramOptions.ParseUserIds(invalid); throw new Exception("Expected invalid allowlist"); }
            catch (ArgumentException) { Check(true, "invalid allowlist entry rejected"); }
        }
        try { TelegramOptions.ParseUserIds(string.Join(',', Enumerable.Range(1, 33))); throw new Exception("Expected member limit"); }
        catch (ArgumentException) { Check(true, "allowlist size is bounded"); }
        var single = Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USER_ID");
        var multiple = Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USER_IDS");
        try
        {
            Environment.SetEnvironmentVariable("TELEGRAM_ALLOWED_USER_ID", "900001");
            Environment.SetEnvironmentVariable("TELEGRAM_ALLOWED_USER_IDS", null);
            Check(TelegramOptions.FromEnvironment().EffectiveUserIds.SequenceEqual(new long[] { 900001 }), "legacy single-ID configuration remains supported");
            Environment.SetEnvironmentVariable("TELEGRAM_ALLOWED_USER_IDS", "900002,900003");
            var options = TelegramOptions.FromEnvironment();
            Check(options.Allows(900002) && options.Allows(900003) && !options.Allows(900001), "explicit list takes precedence over legacy ID");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEGRAM_ALLOWED_USER_ID", single);
            Environment.SetEnvironmentVariable("TELEGRAM_ALLOWED_USER_IDS", multiple);
        }
        return count;
    }
}
