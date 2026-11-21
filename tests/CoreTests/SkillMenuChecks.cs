using AiAgent.Core;

internal static class SkillMenuChecks
{
    public static async Task<int> RunAsync()
    {
        var skills = new[] { new SkillMetadata("first", "First skill", "first/SKILL.md"), new SkillMetadata("second", "Second\u001b[31m skill", "second/SKILL.md") };
        using var input = new StringReader("bad\n3\n2\n");
        using var output = new StringWriter();
        var selected = await SkillMenu.ChooseAsync(skills, input, output, CancellationToken.None);
        var menu = output.ToString();
        if (selected != "second" || !menu.Contains("1. first") || !menu.Contains("2. second") || !menu.Contains("Введи номер") || menu.Contains('\u001b')) throw new Exception("Skill menu selection or sanitization failed");
        using var emptyOutput = new StringWriter();
        if (await SkillMenu.ChooseAsync([], new StringReader(""), emptyOutput, CancellationToken.None) is not null || !emptyOutput.ToString().Contains("Навыков пока нет")) throw new Exception("Empty skill menu failed");
        if (await SkillMenu.ChooseAsync(skills, new StringReader("0\n"), new StringWriter(), CancellationToken.None) is not null) throw new Exception("Skill menu cancellation failed");
        if (await SkillMenu.ChooseAsync(skills, new StringReader(""), new StringWriter(), CancellationToken.None) is not null) throw new Exception("Skill menu EOF failed");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await SkillMenu.ChooseAsync(skills, new StringReader("1\n"), new StringWriter(), cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        return 5;
    }
}
