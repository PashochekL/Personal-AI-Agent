using System.Text.Json;
using AiAgent.Memory;
using AiAgent.Shared;
using Npgsql;

internal static class ContextStoreChecks
{
    public static async Task<int> RunAsync(NpgsqlDataSource database)
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("Context store: " + name); }
        var id = "context-test-" + Guid.NewGuid().ToString("N");
        var other = id + "-other";
        var store = new PostgresContextStore(database);
        await store.InitializeAsync();
        try
        {
            Check((await store.GetMessagesAsync(id)).Count == 0, "new history is empty");
            await store.AppendMessageAsync(id, new Message { Role = "user", Content = "question" });
            await store.AppendMessageAsync(id, new Message
            {
                Role = "assistant", ToolCalls = [new ToolCall { Id = "call-1", Name = "test", Arguments = new() { ["path"] = JsonSerializer.SerializeToElement("file.txt") } }],
                Continuation = JsonSerializer.SerializeToElement(new { provider = "test", encrypted_content = "opaque-state" })
            });
            await store.AppendMessageAsync(id, new Message { Role = "tool", ToolCallId = "call-1", Content = "result" });
            var reopened = new PostgresContextStore(database);
            var messages = await reopened.GetMessagesAsync(id);
            Check(messages.Count == 3 && messages[1].ToolCalls[0].Id == "call-1" && messages[2].ToolCallId == "call-1", "tool pairs persist in order");
            Check(messages[1].Continuation?.GetProperty("encrypted_content").GetString() == "opaque-state", "provider continuation is preserved");
            Check((await reopened.GetMessagesAsync(other)).Count == 0, "histories are separated by session");
            await reopened.ReplaceMessagesAsync(id, [new Message { Role = "user", Content = "replacement" }]);
            Check((await store.GetMessagesAsync(id)).Single().Content == "replacement", "replacement is atomic");
            await Task.WhenAll(Enumerable.Range(0, 10).Select(index => store.AppendMessageAsync(id, new Message { Role = "assistant", Content = "concurrent-" + index })));
            messages = await store.GetMessagesAsync(id);
            Check(messages.Count == 11 && messages.Select(x => x.Content).Distinct().Count() == 11, "concurrent appends do not lose messages");
            await store.PutSummaryAsync(id, "key", "first summary");
            await store.PutSummaryAsync(id, "key", "second summary");
            Check(await reopened.GetSummaryAsync(id, "key") == "first summary" && await reopened.GetSummaryAsync(other, "key") is null, "summary cache preserves first value and session isolation");
            Check(!await store.IsManualSummaryAsync(id), "manual compaction is initially off");
            await store.SetManualSummaryAsync(id);
            Check(await reopened.IsManualSummaryAsync(id) && !await reopened.IsManualSummaryAsync(other), "manual compaction preference persists per session");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await store.AppendMessageAsync(id, new Message { Role = "user", Content = "cancelled" }, cancellation.Token); throw new Exception("Cancelled append succeeded"); }
            catch (OperationCanceledException) { checks++; }
            Check((await store.GetMessagesAsync(id)).Count == 11, "cancelled append does not mutate history");
            return checks;
        }
        finally
        {
            foreach (var table in new[] { "damir_demo_history", "damir_demo_summaries", "damir_demo_context" })
            {
                await using var cleanup = database.CreateCommand($"DELETE FROM {table} WHERE session_id = $1 OR session_id = $2");
                cleanup.Parameters.AddWithValue(id);
                cleanup.Parameters.AddWithValue(other);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
