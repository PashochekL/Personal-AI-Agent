using System.Globalization;

namespace AiAgent.Core;

/// <summary>Produces the current local time on demand for model requests.</summary>
public sealed class CurrentTimeContext(TimeProvider clock, TimeZoneInfo timeZone)
{
    public string GetContext()
    {
        var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), timeZone);
        return $"Дата и время на момент этого запроса: {now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}. Часовой пояс: {timeZone.Id}. Время может измениться после отправки запроса.";
    }
}
