using AiAgent.Core;

internal static class SkillMenu
{
    public static async Task<string?> ChooseAsync(IReadOnlyList<SkillMetadata> skills, TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        if (skills.Count == 0)
        {
            await output.WriteLineAsync("Навыков пока нет. Добавь их в .agents/skills/.");
            return null;
        }
        await output.WriteLineAsync("Доступные навыки:");
        for (var i = 0; i < skills.Count; i++)
        {
            var description = string.Join(" ", skills[i].Description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            description = new string(description.Where(c => !char.IsControl(c)).Take(160).ToArray());
            await output.WriteLineAsync($"{i + 1}. {skills[i].Name} — {description}");
        }
        while (true)
        {
            await output.WriteAsync("Выбери номер навыка или 0 для возврата: ");
            await output.FlushAsync(cancellationToken);
            var choice = await input.ReadLineAsync(cancellationToken);
            if (choice is null || choice.Trim() == "0") return null;
            if (int.TryParse(choice, out var number) && number >= 1 && number <= skills.Count) return skills[number - 1].Name;
            await output.WriteLineAsync("Введи номер из списка или 0 для возврата.");
        }
    }
}
