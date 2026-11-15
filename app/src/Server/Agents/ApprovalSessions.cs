using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using AGUIWebChat.Contracts.Agents;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Serilog;

namespace AGUIWebChat.Server.Agents;

internal sealed class ApprovalSessionException(string code, string message, int status = 409)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

internal sealed class ApprovalCallerIsolationProvider(IHttpContextAccessor accessor) : AgentIsolationKeyProvider
{
    internal static readonly object CallerKey = new();

    public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(accessor.HttpContext?.Items[CallerKey] as string);
}

internal sealed class ApprovalSessionStore(IHttpContextAccessor accessor, TimeProvider clock) : AgentSessionStore
{
    public const int Capacity = 256;
    public const int MaximumSessionBytes = 256 * 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    internal static readonly object InputKey = new();
    internal static readonly object LeaseKey = new();
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock;
    private readonly Dictionary<(string Agent, AgentSessionStoreKey Key), Entry> _entries = [];

    internal sealed class Entry
    {
        public AgentSession? Session { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        public SemaphoreSlim Semaphore { get; } = new(1);
        public int Users { get; set; }
        public bool Invalid { get; set; }
        public int PendingBytes { get; set; }
        public Dictionary<string, ToolApprovalRequestContent> Pending { get; } = new(StringComparer.Ordinal);
    }

    internal sealed class Lease(ApprovalSessionStore owner, Entry entry) : IDisposable
    {
        public Entry Entry { get; } = entry;
        public Dictionary<string, ToolApprovalRequestContent> Decisions { get; } = new(StringComparer.Ordinal);
        public void Dispose()
        {
            lock (owner._gate)
            {
                Entry.LastUsed = owner._clock.GetUtcNow();
                Entry.Users--;
            }
            Entry.Semaphore.Release();
        }
    }

    public override async ValueTask<AgentSession> GetOrCreateSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext ?? throw new InvalidOperationException("Approval sessions require an HTTP caller.");
        var input = (RunAgentInput)context.Items[InputKey]!;
        bool resume = input.Resume is { Count: > 0 };
        var entryKey = (agent.Name ?? agent.Id, key);
        Entry entry;
        lock (_gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            bool expired = _entries.TryGetValue(entryKey, out var existing)
                && now - existing.LastUsed >= Lifetime && existing.Users == 0;
            foreach (var stale in _entries.Where(pair => pair.Value.Users == 0
                && (pair.Value.Invalid || now - pair.Value.LastUsed >= Lifetime)).Select(pair => pair.Key).ToArray())
            {
                _entries.Remove(stale);
            }

            if (!_entries.TryGetValue(entryKey, out entry!))
            {
                if (resume)
                    throw new ApprovalSessionException(expired ? "approval_session_expired" : "approval_session_missing",
                        "The pending approval session is unavailable. Start a new conversation and request a new proposal.");
                if (_entries.Count >= Capacity)
                    throw new ApprovalSessionException("approval_store_full", "The approval session store is full. Retry later.", 503);
                entry = new Entry { LastUsed = now };
                _entries.Add(entryKey, entry);
            }
            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate) entry.Users--;
            throw;
        }

        var lease = new Lease(this, entry);
        context.Items[LeaseKey] = lease;
        if (entry.Invalid)
            throw new ApprovalSessionException("approval_session_invalid", "Start a new conversation and request a new proposal.");
        if (resume)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var response in input.Resume!)
            {
                if (string.IsNullOrWhiteSpace(response.InterruptId) || !ids.Add(response.InterruptId)
                    || !entry.Pending.TryGetValue(response.InterruptId, out var request))
                    throw new ApprovalSessionException("approval_invalid_or_replayed", "This approval is unissued, already consumed, or belongs to another caller.");
                ValidateResponse(response, request);
            }
            if (ids.Count != entry.Pending.Count)
                throw new ApprovalSessionException("approval_incomplete_decisions", "Return one accept/reject decision for every pending interrupt in this run.");
            // Burn each decision before execution. Cancellation cannot make it executable twice.
            foreach (string id in ids)
            {
                lease.Decisions.Add(id, entry.Pending[id]);
                entry.Pending.Remove(id);
            }
            entry.PendingBytes = 0;
        }
        else if (entry.Pending.Count > 0)
        {
            throw new ApprovalSessionException("approval_pending", "Accept or reject the pending proposal, or start a new conversation.");
        }

        entry.LastUsed = clock.GetUtcNow();
        entry.Session ??= await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        return entry.Session;
    }

    private static void ValidateResponse(AGUIResume resume, ToolApprovalRequestContent request)
    {
        if (resume.Status != "completed" || resume.Payload is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("approved", out var approved) || approved.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !payload.TryGetProperty("toolCall", out var call) || call.ValueKind != JsonValueKind.Object
            || !call.TryGetProperty("callId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != request.ToolCall.CallId
            || request.ToolCall is not FunctionCallContent function
            || !call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != function.Name
            || !call.TryGetProperty("arguments", out var arguments)
            || !JsonElement.DeepEquals(arguments, JsonSerializer.SerializeToElement(
                function.Arguments, AgentServerSerializerContext.Default.IDictionaryStringObject)))
        {
            throw new ApprovalSessionException("approval_invalid_payload", "Return the exact proposed tool call with an explicit approved boolean.");
        }
    }

    public ToolApprovalRequestContent RecordPending(ToolApprovalRequestContent request)
    {
        if (accessor.HttpContext?.Items[LeaseKey] is Lease lease)
        {
            if (request.ToolCall is not FunctionCallContent function)
                throw new ApprovalSessionException("approval_invalid_tool", "This endpoint only supports function approvals.");
            int bytes = JsonSerializer.SerializeToUtf8Bytes(function.Arguments,
                AgentServerSerializerContext.Default.IDictionaryStringObject).Length
                + Encoding.UTF8.GetByteCount(function.Name)
                + Encoding.UTF8.GetByteCount(function.CallId)
                + Encoding.UTF8.GetByteCount(request.RequestId);
            if (bytes > MaximumSessionBytes - lease.Entry.PendingBytes)
                InvalidateOversizedSession(lease.Entry);
            // Provider call IDs can repeat. Transport IDs are unique to this caller/conversation proposal.
            string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            lease.Entry.Pending.Add(id, request);
            lease.Entry.PendingBytes += bytes;
#pragma warning disable MEAI001
            return new ToolApprovalRequestContent(id, request.ToolCall) { RequiresConfirmation = request.RequiresConfirmation };
#pragma warning restore MEAI001
        }
        return request;
    }

    public IEnumerable<ChatMessage> BindResponses(IEnumerable<ChatMessage> messages)
    {
        if (accessor.HttpContext?.Items[LeaseKey] is not Lease { Decisions.Count: > 0 } lease) return messages;
        return messages.Select(message => new ChatMessage(message.Role, message.Contents.Select(content => content switch
        {
            ToolApprovalRequestContent request when lease.Decisions.TryGetValue(request.RequestId, out var original) => original,
            ToolApprovalResponseContent response when lease.Decisions.TryGetValue(response.RequestId, out var original) =>
                original.CreateResponse(response.Approved, response.Reason),
            _ => content
        }).ToList())
        {
            MessageId = message.MessageId,
            AuthorName = message.AuthorName,
            AdditionalProperties = message.AdditionalProperties,
            CreatedAt = message.CreatedAt,
            RawRepresentation = message.RawRepresentation
        });
    }

    public async ValueTask ValidateSizeAsync(AIAgent agent, AgentSession? session, CancellationToken cancellationToken)
    {
        if (session is null || accessor.HttpContext?.Items[LeaseKey] is not Lease lease) return;
        JsonElement serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) > MaximumSessionBytes)
        {
            InvalidateOversizedSession(lease.Entry);
        }
    }

    private static void InvalidateOversizedSession(Entry entry)
    {
        entry.Invalid = true;
        entry.Session = null;
        entry.Pending.Clear();
        entry.PendingBytes = 0;
        Log.Warning("Approval session exceeded its memory bound; a new conversation is required.");
        throw new ApprovalSessionException("approval_session_too_large", "Start a new conversation; this approval session exceeded its memory limit.");
    }

    public override ValueTask<AgentSession?> GetSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_entries.TryGetValue((agent.Name ?? agent.Id, key), out var entry)
                && !entry.Invalid && clock.GetUtcNow() - entry.LastUsed < Lifetime ? entry.Session : null);
        }
    }

    public override ValueTask SaveSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ApprovalCallerMiddleware
{
    public const string CookieName = "agui-caller";
    private readonly RequestDelegate _next;
    private readonly IDataProtector _protector = new EphemeralDataProtectionProvider().CreateProtector("AGUIWebChat.ApprovalCaller");

    public ApprovalCallerMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        IWebHostEnvironment environment)
    {
        if (context.Request.Path is not { Value: "/ag-ui/approval" or "/ag-ui/predictive-state" })
        {
            await _next(context);
            return;
        }

        try
        {
            if (context.Request.ContentLength is > 64 * 1024)
                throw new ApprovalSessionException("approval_input_too_large", "Approval requests must not exceed 64 KiB.", 413);
            context.Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: 64 * 1024);
            var input = await JsonSerializer.DeserializeAsync<RunAgentInput>(
                context.Request.Body, jsonOptions.Value.SerializerOptions, context.RequestAborted);
            context.Request.Body.Position = 0;
            if (input is null || string.IsNullOrWhiteSpace(input.ThreadId))
                throw new ApprovalSessionException("approval_thread_required", "Supply a non-empty threadId for this conversation.", 400);
            if (input.Resume is { } responses)
            {
                foreach (var response in responses)
                {
                    if (string.IsNullOrWhiteSpace(response.InterruptId) || response.Status != "completed"
                        || response.Payload is not { ValueKind: JsonValueKind.Object } payload
                        || !payload.TryGetProperty("approved", out var approved)
                        || approved.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || !payload.TryGetProperty("toolCall", out var call) || call.ValueKind != JsonValueKind.Object
                        || !call.TryGetProperty("callId", out var callId) || callId.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(callId.GetString())
                        || !call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(name.GetString())
                        || !call.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object
                        || (payload.TryGetProperty("reason", out var reason) && reason.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                    {
                        throw new ApprovalSessionException("approval_invalid_payload", "Supply a completed interrupt with an explicit boolean and a valid proposed tool call.", 400);
                    }
                }
            }

            string? cookie = context.Request.Cookies[CookieName];
            string caller;
            if (string.IsNullOrEmpty(cookie))
            {
                if (input.Resume is { Count: > 0 })
                    throw new ApprovalSessionException("approval_caller_missing", "Keep the server-issued caller cookie when resuming an approval.", 400);
                caller = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            }
            else
            {
                caller = _protector.Unprotect(cookie);
            }

            bool secure = context.Request.IsHttps || !(environment.IsDevelopment() || environment.IsEnvironment("Testing"));
            context.Response.Cookies.Append(CookieName, _protector.Protect(caller), new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = secure ? SameSiteMode.None : SameSiteMode.Lax,
                Path = "/ag-ui",
                MaxAge = ApprovalSessionStore.Lifetime,
                IsEssential = true
            });
            context.Items[ApprovalCallerIsolationProvider.CallerKey] = caller;
            context.Items[ApprovalSessionStore.InputKey] = input;
            await _next(context);
        }
        catch (ApprovalSessionException error) when (!context.Response.HasStarted)
        {
            Log.Warning("Approval request rejected Code={Code}: {Message}", error.Code, error.Message);
            await WriteErrorAsync(context, error.Code, error.Message, error.Status);
        }
        catch (CryptographicException) when (!context.Response.HasStarted)
        {
            Log.Warning("Approval caller cookie is invalid or belongs to a previous server process.");
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/ag-ui" });
            await WriteErrorAsync(context, "approval_caller_invalid", "Clear the caller cookie and start a new conversation.", 400);
        }
        catch (JsonException) when (!context.Response.HasStarted)
        {
            Log.Warning("Approval request contains invalid AG-UI input JSON.");
            await WriteErrorAsync(context, "approval_invalid_input", "Supply valid AG-UI input JSON.", 400);
        }
        catch (IOException error) when (!context.Response.HasStarted && error.Message == "Buffer limit exceeded.")
        {
            Log.Warning("Approval request exceeded the 64 KiB input limit.");
            await WriteErrorAsync(context, "approval_input_too_large", "Approval requests must not exceed 64 KiB.", 413);
        }
        finally
        {
            if (context.Items[ApprovalSessionStore.LeaseKey] is IDisposable lease) lease.Dispose();
        }
    }

    private static Task WriteErrorAsync(HttpContext context, string code, string detail, int status) =>
        Results.Problem(statusCode: status, title: "Approval request cannot continue", detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["restartRequired"] = code is not ("approval_store_full" or "approval_pending")
            }).ExecuteAsync(context);
}
