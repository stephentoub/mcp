using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol.Messages;
using ModelContextProtocol.Protocol.Transport;
using ModelContextProtocol.Utils;
using ModelContextProtocol.Utils.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
#if !NET
using System.Threading.Channels;
#endif

namespace ModelContextProtocol.Shared;

/// <summary>
/// Class for managing an MCP JSON-RPC session. This covers both MCP clients and servers.
/// </summary>
internal sealed partial class McpSession : IDisposable
{
    private static readonly Histogram<double> s_clientSessionDuration = Diagnostics.CreateDurationHistogram(
        "mcp.client.session.duration", "Measures the duration of a client session.", longBuckets: true);
    private static readonly Histogram<double> s_serverSessionDuration = Diagnostics.CreateDurationHistogram(
        "mcp.server.session.duration", "Measures the duration of a server session.", longBuckets: true);
    private static readonly Histogram<double> s_clientRequestDuration = Diagnostics.CreateDurationHistogram(
        "rpc.client.duration", "Measures the duration of outbound RPC.", longBuckets: false);
    private static readonly Histogram<double> s_serverRequestDuration = Diagnostics.CreateDurationHistogram(
        "rpc.server.duration", "Measures the duration of inbound RPC.", longBuckets: false);

    private readonly bool _isServer;
    private readonly string _transportKind;
    private readonly ITransport _transport;
    private readonly RequestHandlers _requestHandlers;
    private readonly NotificationHandlers _notificationHandlers;
    private readonly long _sessionStartingTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Collection of requests sent on this session and waiting for responses.</summary>
    private readonly ConcurrentDictionary<RequestId, TaskCompletionSource<IJsonRpcMessage>> _pendingRequests = [];
    /// <summary>
    /// Collection of requests received on this session and currently being handled. The value provides a <see cref="CancellationTokenSource"/>
    /// that can be used to request cancellation of the in-flight handler.
    /// </summary>
    private readonly ConcurrentDictionary<RequestId, CancellationTokenSource> _handlingRequests = new();
    private readonly ILogger _logger;

    private readonly string _id = Guid.NewGuid().ToString("N");
    private long _nextRequestId;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpSession"/> class.
    /// </summary>
    /// <param name="isServer">true if this is a server; false if it's a client.</param>
    /// <param name="transport">An MCP transport implementation.</param>
    /// <param name="endpointName">The name of the endpoint for logging and debug purposes.</param>
    /// <param name="requestHandlers">A collection of request handlers.</param>
    /// <param name="notificationHandlers">A collection of notification handlers.</param>
    /// <param name="logger">The logger.</param>
    public McpSession(
        bool isServer,
        ITransport transport,
        string endpointName,
        RequestHandlers requestHandlers,
        NotificationHandlers notificationHandlers,
        ILogger logger)
    {
        Throw.IfNull(transport);

        _transportKind = transport switch
        {
            StdioClientSessionTransport or StdioServerTransport => "stdio",
            StreamClientSessionTransport or StreamServerTransport => "stream",
            SseClientSessionTransport or SseResponseStreamTransport => "sse",
            _ => "unknownTransport"
        };

        _isServer = isServer;
        _transport = transport;
        EndpointName = endpointName;
        _requestHandlers = requestHandlers;
        _notificationHandlers = notificationHandlers;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Gets and sets the name of the endpoint for logging and debug purposes.
    /// </summary>
    public string EndpointName { get; set; }

    /// <summary>
    /// Starts processing messages from the transport. This method will block until the transport is disconnected.
    /// This is generally started in a background task or thread from the initialization logic of the derived class.
    /// </summary>
    public async Task ProcessMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _transport.MessageReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                LogMessageRead(EndpointName, message.GetType().Name);

                _ = ProcessMessageAsync();
                async Task ProcessMessageAsync()
                {
                    IJsonRpcMessageWithId? messageWithId = message as IJsonRpcMessageWithId;
                    CancellationTokenSource? combinedCts = null;
                    try
                    {
                        // Register before we yield, so that the tracking is guaranteed to be there
                        // when subsequent messages arrive, even if the asynchronous processing happens
                        // out of order.
                        if (messageWithId is not null)
                        {
                            combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            _handlingRequests[messageWithId.Id] = combinedCts;
                        }

                        // Fire and forget the message handling to avoid blocking the transport
                        // If awaiting the task, the transport will not be able to read more messages,
                        // which could lead to a deadlock if the handler sends a message back

#if NET
                        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
#else
                        await default(ForceYielding);
#endif

                        // Handle the message.
                        await HandleMessageAsync(message, combinedCts?.Token ?? cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Only send responses for request errors that aren't user-initiated cancellation.
                        bool isUserCancellation =
                            ex is OperationCanceledException &&
                            !cancellationToken.IsCancellationRequested &&
                            combinedCts?.IsCancellationRequested is true;

                        if (!isUserCancellation && message is JsonRpcRequest request)
                        {
                            LogRequestHandlerException(EndpointName, request.Method, ex);
                            await _transport.SendMessageAsync(new JsonRpcError
                            {
                                Id = request.Id,
                                JsonRpc = "2.0",
                                Error = new JsonRpcErrorDetail
                                {
                                    Code = (ex as McpException)?.ErrorCode ?? ErrorCodes.InternalError,
                                    Message = ex.Message
                                }
                            }, cancellationToken).ConfigureAwait(false);
                        }
                        else if (ex is not OperationCanceledException)
                        {
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                LogMessageHandlerExceptionSensitive(EndpointName, message.GetType().Name, JsonSerializer.Serialize(message, McpJsonUtilities.JsonContext.Default.IJsonRpcMessage), ex);
                            }
                            else
                            {
                                LogMessageHandlerException(EndpointName, message.GetType().Name, ex);
                            }
                        }
                    }
                    finally
                    {
                        if (messageWithId is not null)
                        {
                            _handlingRequests.TryRemove(messageWithId.Id, out _);
                            combinedCts!.Dispose();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
            LogEndpointMessageProcessingCanceled(EndpointName);
        }
        finally
        {
            // Fail any pending requests, as they'll never be satisfied.
            foreach (var entry in _pendingRequests)
            {
                entry.Value.TrySetException(new InvalidOperationException("The server shut down unexpectedly."));
            }
        }
    }

    private async Task HandleMessageAsync(IJsonRpcMessage message, CancellationToken cancellationToken)
    {
        Histogram<double> durationMetric = _isServer ? s_serverRequestDuration : s_clientRequestDuration;
        string method = GetMethodName(message);

        long? startingTimestamp = durationMetric.Enabled ? Stopwatch.GetTimestamp() : null;
        Activity? activity = Diagnostics.ActivitySource.HasListeners() ?
            Diagnostics.ActivitySource.StartActivity(CreateActivityName(method)) :
            null;

        TagList tags = default;
        bool addTags = activity is { IsAllDataRequested: true } || startingTimestamp is not null;
        try
        {
            if (addTags)
            {
                AddStandardTags(ref tags, method);
            }

            switch (message)
            {
                case JsonRpcRequest request:
                    if (addTags)
                    {
                        AddRpcRequestTags(ref tags, activity, request);
                    }

                    await HandleRequest(request, cancellationToken).ConfigureAwait(false);
                    break;

                case JsonRpcNotification notification:
                    await HandleNotification(notification, cancellationToken).ConfigureAwait(false);
                    break;

                case IJsonRpcMessageWithId messageWithId:
                    HandleMessageWithId(message, messageWithId);
                    break;

                default:
                    LogEndpointHandlerUnexpectedMessageType(EndpointName, message.GetType().Name);
                    break;
            }
        }
        catch (Exception e) when (addTags)
        {
            AddExceptionTags(ref tags, e);
            throw;
        }
        finally
        {
            FinalizeDiagnostics(activity, startingTimestamp, durationMetric, ref tags);
        }
    }

    private async Task HandleNotification(JsonRpcNotification notification, CancellationToken cancellationToken)
    {
        // Special-case cancellation to cancel a pending operation. (We'll still subsequently invoke a user-specified handler if one exists.)
        if (notification.Method == NotificationMethods.CancelledNotification)
        {
            try
            {
                if (GetCancelledNotificationParams(notification.Params) is CancelledNotification cn &&
                    _handlingRequests.TryGetValue(cn.RequestId, out var cts))
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                    LogRequestCanceled(EndpointName, cn.RequestId, cn.Reason);
                }
            }
            catch
            {
                // "Invalid cancellation notifications SHOULD be ignored"
            }
        }

        // Handle user-defined notifications.
        await _notificationHandlers.InvokeHandlers(notification.Method, notification, cancellationToken).ConfigureAwait(false);
    }

    private void HandleMessageWithId(IJsonRpcMessage message, IJsonRpcMessageWithId messageWithId)
    {
        if (_pendingRequests.TryRemove(messageWithId.Id, out var tcs))
        {
            tcs.TrySetResult(message);
        }
        else
        {
            LogNoRequestFoundForMessageWithId(EndpointName, messageWithId.Id);
        }
    }

    private async Task HandleRequest(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!_requestHandlers.TryGetValue(request.Method, out var handler))
        {
            LogNoHandlerFoundForRequest(EndpointName, request.Method);
            throw new McpException("The method does not exist or is not available.", ErrorCodes.MethodNotFound);
        }

        LogRequestHandlerCalled(EndpointName, request.Method);
        JsonNode? result = await handler(request, cancellationToken).ConfigureAwait(false);
        LogRequestHandlerCompleted(EndpointName, request.Method);

        await _transport.SendMessageAsync(new JsonRpcResponse
        {
            Id = request.Id,
            JsonRpc = "2.0",
            Result = result
        }, cancellationToken).ConfigureAwait(false);
    }

    private CancellationTokenRegistration RegisterCancellation(CancellationToken cancellationToken, RequestId requestId)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return default;
        }

        return cancellationToken.Register(static objState =>
        {
            var state = (Tuple<McpSession, RequestId>)objState!;
            _ = state.Item1.SendMessageAsync(new JsonRpcNotification
            {
                Method = NotificationMethods.CancelledNotification,
                Params = JsonSerializer.SerializeToNode(new CancelledNotification { RequestId = state.Item2 }, McpJsonUtilities.JsonContext.Default.CancelledNotification)
            });
        }, Tuple.Create(this, requestId));
    }

    public IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, Task> handler)
    {
        Throw.IfNullOrWhiteSpace(method);
        Throw.IfNull(handler);

        return _notificationHandlers.Register(method, handler);
    }

    /// <summary>
    /// Sends a JSON-RPC request to the server.
    /// It is strongly recommended use the capability-specific methods instead of this one.
    /// Use this method for custom requests or those not yet covered explicitly by the endpoint implementation.
    /// </summary>
    /// <param name="request">The JSON-RPC request to send.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>A task containing the server's response.</returns>
    public async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!_transport.IsConnected)
        {
            throw new McpException("Transport is not connected");
        }

        cancellationToken.ThrowIfCancellationRequested();

        Histogram<double> durationMetric = _isServer ? s_serverRequestDuration : s_clientRequestDuration;
        string method = request.Method;

        long? startingTimestamp = durationMetric.Enabled ? Stopwatch.GetTimestamp() : null;
        using Activity? activity = Diagnostics.ActivitySource.HasListeners() ?
            Diagnostics.ActivitySource.StartActivity(CreateActivityName(method)) :
            null;

        // Set request ID
        if (request.Id.Id is null)
        {
            request.Id = new RequestId($"{_id}-{Interlocked.Increment(ref _nextRequestId)}");
        }

        TagList tags = default;
        bool addTags = activity is { IsAllDataRequested: true } || startingTimestamp is not null;

        var tcs = new TaskCompletionSource<IJsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[request.Id] = tcs;
        try
        {
            if (addTags)
            {
                AddStandardTags(ref tags, method);
                AddRpcRequestTags(ref tags, activity, request);
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                LogSendingRequestSensitive(EndpointName, request.Method, JsonSerializer.Serialize(request, McpJsonUtilities.JsonContext.Default.IJsonRpcMessage));
            }
            else
            {
                LogSendingRequest(EndpointName, request.Method);
            }

            await _transport.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);

            // Now that the request has been sent, register for cancellation. If we registered before,
            // a cancellation request could arrive before the server knew about that request ID, in which
            // case the server could ignore it.
            LogRequestSentAwaitingResponse(EndpointName, request.Method, request.Id);
            IJsonRpcMessage? response;
            using (var registration = RegisterCancellation(cancellationToken, request.Id))
            {
                response = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (response is JsonRpcError error)
            {
                LogSendingRequestFailed(EndpointName, request.Method, error.Error.Message, error.Error.Code);
                throw new McpException($"Request failed (remote): {error.Error.Message}", error.Error.Code);
            }

            if (response is JsonRpcResponse success)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    LogRequestResponseReceivedSensitive(EndpointName, request.Method, success.Result?.ToJsonString() ?? "null");
                }
                else
                {
                    LogRequestResponseReceived(EndpointName, request.Method);
                }

                return success;
            }

            // Unexpected response type
            LogSendingRequestInvalidResponseType(EndpointName, request.Method);
            throw new McpException("Invalid response type");
        }
        catch (Exception ex) when (addTags)
        {
            AddExceptionTags(ref tags, ex);
            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(request.Id, out _);
            FinalizeDiagnostics(activity, startingTimestamp, durationMetric, ref tags);
        }
    }

    public async Task SendMessageAsync(IJsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        Throw.IfNull(message);

        if (!_transport.IsConnected)
        {
            throw new McpException("Transport is not connected");
        }

        cancellationToken.ThrowIfCancellationRequested();

        Histogram<double> durationMetric = _isServer ? s_serverRequestDuration : s_clientRequestDuration;
        string method = GetMethodName(message);

        long? startingTimestamp = durationMetric.Enabled ? Stopwatch.GetTimestamp() : null;
        using Activity? activity = Diagnostics.ActivitySource.HasListeners() ?
            Diagnostics.ActivitySource.StartActivity(CreateActivityName(method)) :
            null;

        TagList tags = default;
        bool addTags = activity is { IsAllDataRequested: true } || startingTimestamp is not null;

        try
        {
            if (addTags)
            {
                AddStandardTags(ref tags, method);
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                LogSendingMessageSensitive(EndpointName, JsonSerializer.Serialize(message, McpJsonUtilities.JsonContext.Default.IJsonRpcMessage));
            }
            else
            {
                LogSendingMessage(EndpointName);
            }

            await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);

            // If the sent notification was a cancellation notification, cancel the pending request's await, as either the
            // server won't be sending a response, or per the specification, the response should be ignored. There are inherent
            // race conditions here, so it's possible and allowed for the operation to complete before we get to this point.
            if (message is JsonRpcNotification { Method: NotificationMethods.CancelledNotification } notification &&
                GetCancelledNotificationParams(notification.Params) is CancelledNotification cn &&
                _pendingRequests.TryRemove(cn.RequestId, out var tcs))
            {
                tcs.TrySetCanceled(default);
            }
        }
        catch (Exception ex) when (addTags)
        {
            AddExceptionTags(ref tags, ex);
            throw;
        }
        finally
        {
            FinalizeDiagnostics(activity, startingTimestamp, durationMetric, ref tags);
        }
    }

    private static CancelledNotification? GetCancelledNotificationParams(JsonNode? notificationParams)
    {
        try
        {
            return JsonSerializer.Deserialize(notificationParams, McpJsonUtilities.JsonContext.Default.CancelledNotification);
        }
        catch
        {
            return null;
        }
    }

    private string CreateActivityName(string method) =>
        $"mcp.{(_isServer ? "server" : "client")}.{_transportKind}/{method}";

    private static string GetMethodName(IJsonRpcMessage message) =>
        message switch
        {
            JsonRpcRequest request => request.Method,
            JsonRpcNotification notification => notification.Method,
            _ => "unknownMethod",
        };

    private void AddStandardTags(ref TagList tags, string method)
    {
        tags.Add("session.id", _id);
        tags.Add("rpc.system", "jsonrpc");
        tags.Add("rpc.jsonrpc.version", "2.0");
        tags.Add("rpc.method", method);
        tags.Add("network.transport", _transportKind);

        // RPC spans convention also includes:
        // server.address, server.port, client.address, client.port, network.peer.address, network.peer.port, network.type
    }

    private static void AddRpcRequestTags(ref TagList tags, Activity? activity, JsonRpcRequest request)
    {
        tags.Add("rpc.jsonrpc.request_id", request.Id.ToString());

        if (request.Params is JsonObject paramsObj)
        {
            switch (request.Method)
            {
                case RequestMethods.ToolsCall:
                case RequestMethods.PromptsGet:
                    if (paramsObj.TryGetPropertyValue("name", out var prop) && prop?.GetValueKind() is JsonValueKind.String)
                    {
                        string name = prop.GetValue<string>();
                        tags.Add("mcp.request.params.name", name);
                        if (activity is not null)
                        {
                            activity.DisplayName = $"{request.Method}({name})";
                        }
                    }
                    break;

                case RequestMethods.ResourcesRead:
                    if (paramsObj.TryGetPropertyValue("uri", out prop) && prop?.GetValueKind() is JsonValueKind.String)
                    {
                        string uri = prop.GetValue<string>();
                        tags.Add("mcp.request.params.uri", uri);
                        if (activity is not null)
                        {
                            activity.DisplayName = $"{request.Method}({uri})";
                        }
                    }
                    break;
            }
        }
    }

    private static void AddExceptionTags(ref TagList tags, Exception e)
    {
        if (e is AggregateException ae && ae.InnerException is not null and not AggregateException)
        {
            e = ae.InnerException;
        }

        tags.Add("error.type", e.GetType().FullName);
        tags.Add("rpc.jsonrpc.error_code",
            (e as McpException)?.ErrorCode is int errorCode ? errorCode :
            e is JsonException ? ErrorCodes.ParseError :
            ErrorCodes.InternalError);
    }

    private static void FinalizeDiagnostics(
        Activity? activity, long? startingTimestamp, Histogram<double> durationMetric, ref TagList tags)
    {
        try
        {
            if (startingTimestamp is not null)
            {
                durationMetric.Record(GetElapsed(startingTimestamp.Value).TotalSeconds, tags);
            }

            if (activity is { IsAllDataRequested: true })
            {
                foreach (var tag in tags)
                {
                    activity.AddTag(tag.Key, tag.Value);
                }
            }
        }
        finally
        {
            activity?.Dispose();
        }
    }

    public void Dispose()
    {
        Histogram<double> durationMetric = _isServer ? s_serverSessionDuration : s_clientSessionDuration;
        if (durationMetric.Enabled)
        {
            TagList tags = default;
            tags.Add("session.id", _id);
            tags.Add("network.transport", _transportKind);
            durationMetric.Record(GetElapsed(_sessionStartingTimestamp).TotalSeconds, tags);
        }

        // Complete all pending requests with cancellation
        foreach (var entry in _pendingRequests)
        {
            entry.Value.TrySetCanceled();
        }

        _pendingRequests.Clear();
    }

#if !NET
    private static readonly double s_timestampToTicks = TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency;
#endif

    private static TimeSpan GetElapsed(long startingTimestamp) =>
#if NET
        Stopwatch.GetElapsedTime(startingTimestamp);
#else
        new((long)(s_timestampToTicks * (Stopwatch.GetTimestamp() - startingTimestamp)));
#endif

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} message processing canceled.")]
    private partial void LogEndpointMessageProcessingCanceled(string endpointName);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} method '{Method}' request handler called.")]
    private partial void LogRequestHandlerCalled(string endpointName, string method);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} method '{Method}' request handler completed.")]
    private partial void LogRequestHandlerCompleted(string endpointName, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} method '{Method}' request handler failed.")]
    private partial void LogRequestHandlerException(string endpointName, string method, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} received request for unknown request ID '{RequestId}'.")]
    private partial void LogNoRequestFoundForMessageWithId(string endpointName, RequestId requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} request failed for method '{Method}': {ErrorMessage} ({ErrorCode}).")]
    private partial void LogSendingRequestFailed(string endpointName, string method, string errorMessage, int errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} received invalid response for method '{Method}'.")]
    private partial void LogSendingRequestInvalidResponseType(string endpointName, string method);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{EndpointName} sending method '{Method}' request.")]
    private partial void LogSendingRequest(string endpointName, string method);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EndpointName} sending method '{Method}' request. Request: '{Request}'.")]
    private partial void LogSendingRequestSensitive(string endpointName, string method, string request);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} canceled request '{RequestId}' per client notification. Reason: '{Reason}'.")]
    private partial void LogRequestCanceled(string endpointName, RequestId requestId, string? reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{EndpointName} Request response received for method {method}")]
    private partial void LogRequestResponseReceived(string endpointName, string method);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EndpointName} Request response received for method {method}. Response: '{Response}'.")]
    private partial void LogRequestResponseReceivedSensitive(string endpointName, string method, string response);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{EndpointName} read {MessageType} message from channel.")]
    private partial void LogMessageRead(string endpointName, string messageType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} message handler {MessageType} failed.")]
    private partial void LogMessageHandlerException(string endpointName, string messageType, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EndpointName} message handler {MessageType} failed. Message: '{Message}'.")]
    private partial void LogMessageHandlerExceptionSensitive(string endpointName, string messageType, string message, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} received unexpected {MessageType} message type.")]
    private partial void LogEndpointHandlerUnexpectedMessageType(string endpointName, string messageType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} received request for method '{Method}', but not handler is available.")]
    private partial void LogNoHandlerFoundForRequest(string endpointName, string method);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{EndpointName} waiting for response to request '{RequestId}' for method '{Method}'.")]
    private partial void LogRequestSentAwaitingResponse(string endpointName, string method, RequestId requestId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{EndpointName} sending message.")]
    private partial void LogSendingMessage(string endpointName);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EndpointName} sending message. Message: '{Message}'.")]
    private partial void LogSendingMessageSensitive(string endpointName, string message);
}