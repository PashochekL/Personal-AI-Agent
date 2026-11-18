using System.Text.Json;
using AiAgent.Shared;
using TaskStatus = AiAgent.Shared.TaskStatus;

var checks = 0;
void Check(bool condition) { checks++; if (!condition) throw new Exception($"Check {checks} failed"); }
void Reject<T>(string json) where T : ExchangeModel
{
    checks++;
    try { ContractJson.Deserialize<T>(json); }
    catch (Exception e) when (e is JsonException or ArgumentException) { return; }
    throw new Exception($"Check {checks}: invalid {typeof(T).Name} was accepted");
}

var task = ContractJson.Deserialize<AgentTask>("""{"task_id":"t1","session_id":"s1","user_message":"  Привет\n","status":"COMPLETED","result":{"session_id":"s1","status":"COMPLETED","response":"ok"}}""");
Check(ContractJson.Deserialize<AgentTask>(ContractJson.Serialize(task)).UserMessage == "  Привет\n");
Check(task.Result?.Status == TaskStatus.COMPLETED);
foreach (var status in new[] { "PENDING", "RUNNING", "CANCELLED" })
    Check(ContractJson.Deserialize<AgentTask>($$"""{"task_id":"t","session_id":"s","user_message":"x","status":"{{status}}"}""").Result is null);
Reject<AgentTask>("""{"task_id":"t","session_id":"s","user_message":"x","status":"COMPLETED"}""");
Reject<AgentTask>("""{"task_id":"t","session_id":"s","user_message":"x","status":"completed"}""");
Reject<AgentTask>("""{"task_id":"t","session_id":"s","user_message":"x","status":1}""");
Reject<AgentTask>("""{"task_id":"t","session_id":"s","user_message":"x","status":"CANCELLED","result":{"session_id":"other","status":"CANCELLED"}}""");
Reject<AgentTask>("""{"task_id":"t","session_id":"s","user_message":"x","status":"CANCELLED","result":{"session_id":"s","status":"CANCELLED","extra":1}}""");
Reject<AgentTask>("""{"task_id":" ","session_id":"s","user_message":"x"}""");
Reject<AgentResult>("""{"session_id":"s","status":"COMPLETED","response":" "}""");
Reject<AgentResult>("""{"session_id":"s","status":"FAILED"}""");
Reject<AgentResult>("""{"session_id":"s","status":"PENDING"}""");
Reject<ToolResult>("""{"success":false,"content":""}""");
Reject<ToolResult>("""{"success":true,"content":"ok","error":"bad"}""");
Reject<Message>("""{"role":"tool","content":"x"}""");
Reject<Message>("""{"role":"user","content":"x","tool_calls":[{"id":"1","name":"x","arguments":{}}]}""");
Reject<Message>("""{"role":"assistant"}""");
var message = ContractJson.Deserialize<Message>("""{"role":"assistant","tool_calls":[{"id":"1","name":"read","arguments":{"path":"file"}}]}""");
Check(ContractJson.Deserialize<Message>(ContractJson.Serialize(message)).ToolCalls[0].Arguments["path"].GetString() == "file");
Reject<Message>("""{"role":"assistant","tool_calls":[{"id":"1","name":"read","arguments":{},"extra":1}]}""");
Check(ContractJson.Deserialize<TokenUsage>("""{"input_tokens":0}""").TotalTokens is null);
foreach (var invalid in new[] { "-1", "1.5", "true", "\"1\"" }) Reject<TokenUsage>($$"""{"input_tokens":{{invalid}}}""");
Reject<LlmResponse>("""{"finish_reason":"end_turn"}""");
Check(ContractJson.Deserialize<LlmResponse>("{}").ToolCalls.Count == 0);
Check(ContractJson.Deserialize<CommandResult>("""{"exit_code":null,"timed_out":true}""").ExitCode is null);
Reject<CommandResult>("{}");
Reject<Session>("""{"session_id":"s","unexpected":1}""");
var defaults = Settings.FromEnvironment(new Dictionary<string, string>());
Check(defaults.AgentMaxIterations == 20 && defaults.AgentWorkspace == "./workspace");
var settings = Settings.FromEnvironment(new Dictionary<string, string> { ["AGENT_MAX_ITERATIONS"] = "3", ["LITELLM_API_KEY"] = "secret" });
Check(settings.AgentMaxIterations == 3 && settings.LitellmApiKey == "secret");
Check(!JsonSerializer.Serialize(settings).Contains("secret") && !settings.ToString()!.Contains("secret"));
try { Settings.FromEnvironment(new Dictionary<string, string> { ["AGENT_MAX_ITERATIONS"] = "0" }); throw new Exception("Invalid limit accepted"); } catch (ArgumentException) { checks++; }
Console.WriteLine($"{checks} contract checks passed");
