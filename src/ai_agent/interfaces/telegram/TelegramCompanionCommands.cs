using System.Net;
using AiAgent.Http;
using TaskStatus = AiAgent.Shared.TaskStatus;

namespace AiAgent.Telegram;

public sealed partial class TelegramCompanion
{
    private async Task<bool> HandleCommandAsync(long updateId, long user, string text, string command, CompanionConnection state, CancellationToken ct)
    {
        var action = text switch
        {
            "Команды" => "commands", "Назад" => "back", "Сжать контекст" => "compress", "Заполнение контекста" => "context",
            "Расход токенов" => "tokens", "Состояние ответа" => "status", "История диалога" => "history", "Текущий проект" => "project",
            "Выбрать модель" => "models", "Выбрать навык" => "skills", "Без навыка" => "clear-skill",
            _ => command switch { "/commands" => "commands", "/compress" => "compress", "/context" => "context", "/tokens" => "tokens",
                "/status" => "status", "/models" => "models", "/skill" => "skills", _ => "" }
        };
        if (text.StartsWith("Модель: ", StringComparison.Ordinal)) action = "select-model";
        if (text.StartsWith("Навык: ", StringComparison.Ordinal)) action = "select-skill";
        if (action.Length == 0) return false;
        try
        {
            switch (action)
            {
                case "commands": await telegram.SendCommandsAsync(user, ct); break;
                case "back": await telegram.SendMenuAsync(user, "Можно продолжать разговор или выбрать действие.", ct); break;
                case "project":
                    await telegram.SendMenuAsync(user, $"Рабочая папка: {state.WorkspaceName}.\nДиалог сейчас {(state.Channel == "telegram" ? "в Telegram" : "на компьютере")}.\nМодель: {state.Model ?? "по умолчанию"}.\nНавык следующего сообщения: {state.Skill ?? "не выбран"}.", ct); break;
                case "history": await ShowHistoryAsync(state.SessionId, user, ct); await telegram.SendMenuAsync(user, $"Проект: {state.WorkspaceName}.", ct); break;
                case "tokens":
                    var usage = await backend.UsageAsync(state.SessionId, ct);
                    await telegram.SendMenuAsync(user, $"Расход этого диалога: {(usage.MissingUsageResponses > 0 ? "не менее " : "")}{usage.Total} токенов.", ct); break;
                case "status":
                    var task = state.PendingTask is { } id ? await backend.TaskAsync(id, ct) : await backend.LatestAsync(state.SessionId, ct);
                    if (task is not null && task.SessionId != state.SessionId) throw new BackendException("Ответ относится к другому диалогу.");
                    var status = state.PendingMessage is not null ? "Сообщение ожидает отправки." : task?.Status switch
                    { TaskStatus.PENDING => "Запрос в очереди.", TaskStatus.RUNNING => "Дамир отвечает.", TaskStatus.COMPLETED => "Последний ответ готов.",
                        TaskStatus.CANCELLED => "Последний ответ остановлен.", TaskStatus.FAILED => "Последний запрос завершился с ошибкой.", _ => "В этом диалоге пока нет запросов." };
                    await telegram.SendMenuAsync(user, status, ct); break;
                case "context": case "compress":
                    if (state.Channel != "telegram") { await telegram.SendMenuAsync(user, "Сначала нажми «Продолжить здесь», чтобы вернуть диалог в Telegram.", ct); break; }
                    if (state.PendingMessage is not null || state.PendingTask is not null || state.Parts is not null)
                    { await telegram.SendMenuAsync(user, "Сначала дождись текущего ответа или нажми «Остановить».", ct); break; }
                    await CheckpointAsync(state with { UpdateOffset = updateId + 1, PendingUpdate = updateId,
                        PendingMessage = "/" + action, PendingOperation = action }, ct);
                    return true;
                case "models":
                    var models = (await backend.CatalogAsync(ct)).Models;
                    await telegram.SendButtonsAsync(user, models.Length == 0 ? "Модели пока недоступны." : "Выбери модель:",
                        [..models.Select(m => new[] { "Модель: " + m }), ["Назад"]], ct); break;
                case "skills":
                    var skills = (await backend.CatalogAsync(ct)).Skills;
                    await telegram.SendButtonsAsync(user, skills.Length == 0 ? "Навыков в этом проекте пока нет." : "Выбери навык для следующего сообщения:",
                        [..skills.Select(s => new[] { "Навык: " + s.Name }), ["Без навыка", "Назад"]], ct); break;
                case "select-model": case "select-skill": case "clear-skill":
                    var catalog = await backend.CatalogAsync(ct);
                    var model = action == "select-model" ? text[8..] : state.Model;
                    var skill = action == "select-skill" ? text[7..] : action == "clear-skill" ? null : state.Skill;
                    if (model is not null && !catalog.Models.Contains(model) || skill is not null && !catalog.Skills.Any(s => s.Name == skill))
                    { await telegram.SendMenuAsync(user, "Такого варианта больше нет. Открой «Команды» и выбери снова.", ct); break; }
                    await backend.TelegramSelectAsync(_bot, _lease, state.SessionId, user, model, skill, ct);
                    await telegram.SendMenuAsync(user, action == "select-model" ? "Выбрана модель: " + model
                        : skill is null ? "Выбор навыка отменён." : "Навык выбран для следующего сообщения: " + skill, ct); break;
            }
        }
        catch (BackendException e) when (e.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or (HttpStatusCode)423 or HttpStatusCode.NotImplemented)
        { await telegram.SendMenuAsync(user, e.Status == HttpStatusCode.NotImplemented ? "Это действие сейчас недоступно на сервере." : e.Message, ct); }
        await CheckpointAsync(state with { UpdateOffset = updateId + 1 }, ct);
        return true;
    }
}
