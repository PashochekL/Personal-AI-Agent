internal static class ContextPolicyChecks
{
    public static int Run()
    {
        if (ModelContextPolicy.For("gpt-6.2-sol", 922_000, null) != (500_000, 475_000)) throw new Exception("Sol default context policy");
        if (ModelContextPolicy.For("gpt-6.2-astra", 922_000, null) != (272_000, 258_400)) throw new Exception("Astra default context policy");
        if (ModelContextPolicy.For("gpt-6.2-sol", 180_000, null) != (180_000, 180_000)) throw new Exception("Gateway input cap");
        if (ModelContextPolicy.For("gpt-6.2-sol", null, 40_000) != (500_000, 40_000)) throw new Exception("Explicit compaction budget");
        if (ModelContextPolicy.For("unknown", 922_000, null) != (30_000, 30_000)) throw new Exception("Unknown model fallback");
        if (ModelContextPolicy.For("unknown", null, 60_000) != (60_000, 60_000)) throw new Exception("Unknown model explicit budget");
        try { ModelContextPolicy.For("gpt-6.2-sol", 1000, null); throw new Exception("Too-small model limit accepted"); }
        catch (ArgumentOutOfRangeException) { }
        return 7;
    }
}
