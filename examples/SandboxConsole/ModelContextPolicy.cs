/// <summary>Reference defaults from the Codex model catalog, not a provider guarantee.</summary>
internal static class ModelContextPolicy
{
    private static readonly IReadOnlyDictionary<string, int> CodexDefaults = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["gpt-6.2-astra"] = 272_000,
        ["gpt-6.2-sol"] = 500_000,
        ["gpt-6-astra"] = 272_000,
        ["gpt-6.1-sol"] = 500_000,
        ["gpt-6.1-terra"] = 500_000,
        ["gpt-6-luna"] = 272_000,
    };

    public static (int DisplayLimit, int WorkingBudget) For(string model, int? gatewayInputLimit, int? configuredBudget)
    {
        var known = CodexDefaults.TryGetValue(model, out var reference);
        var window = known ? reference : configuredBudget ?? 30_000;
        var desired = configuredBudget ?? (known ? (int)((long)window * 95 / 100) : 30_000);
        var budget = Math.Min(desired, window);
        if (gatewayInputLimit is > 0) budget = Math.Min(budget, gatewayInputLimit.Value);
        if (budget < 4096) throw new ArgumentOutOfRangeException(nameof(gatewayInputLimit), "Model input limit is below 4096 tokens.");
        var displayLimit = gatewayInputLimit is > 0 ? Math.Min(window, gatewayInputLimit.Value) : window;
        return (displayLimit, budget);
    }
}
