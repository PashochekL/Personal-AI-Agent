using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace AiAgent.Api;

public sealed class RedisTaskQueue(IConnectionMultiplexer redis, BackendOptions options)
{
    private readonly string _prefix = "damir:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        options.Scope.OwnerId.Length + ":" + options.Scope.OwnerId + options.Scope.ProjectId))).ToLowerInvariant();
    public Task<TimeSpan> PingAsync() => redis.GetDatabase().PingAsync();

    public async Task NotifyAsync(string sessionId, CancellationToken ct)
    {
        // Short deduplication lease bounds queue size. PostgreSQL rescans repair any lost notification.
        await redis.GetDatabase().ScriptEvaluateAsync("""
            if redis.call('SET',KEYS[2],'1','NX','EX',10) then
                redis.call('RPUSH',KEYS[1],ARGV[1]); return 1
            end
            return 0
            """, [(RedisKey)(_prefix + ":queue"), (RedisKey)(_prefix + ":pending:" + sessionId)], [(RedisValue)sessionId]).WaitAsync(ct);
    }

    public async Task<string?> PopAsync(CancellationToken ct)
    {
        var result = await redis.GetDatabase().ScriptEvaluateAsync("""
            local session=redis.call('LPOP',KEYS[1])
            if session then redis.call('DEL',ARGV[1]..session) end
            return session
            """, [(RedisKey)(_prefix + ":queue")], [(RedisValue)(_prefix + ":pending:")]).WaitAsync(ct);
        return result.IsNull ? null : (string?)result;
    }
}
