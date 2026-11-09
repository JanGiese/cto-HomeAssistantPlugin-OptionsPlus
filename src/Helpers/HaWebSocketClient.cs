namespace Loupedeck.HomeAssistantPlugin
{
    using System;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class HaWebSocketClient : IAsyncDisposable, IDisposable
    {
        private const Int32 BufferSize = 65536; // 64KB - enough for large HA responses
        private const Int32 InitialMessageId = 1;

        private ClientWebSocket? _ws;
        private readonly Object _lock = new();
        private Int32 _nextId = InitialMessageId;
        private readonly Byte[] _buffer = new Byte[BufferSize];

        public Boolean IsAuthenticated { get; private set; }
        public Uri? EndpointUri { get; private set; }

        private String? _lastBaseUrl;
        private String? _lastAccessToken;

        public async Task<(Boolean ok, String message)> ConnectAndAuthenticateAsync(
            String baseUrl, String accessToken, TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(baseUrl) || String.IsNullOrWhiteSpace(accessToken))
                {
                    return (false, "Invalid parameters");
                }

                // Try reuse first
                if (await TryReuseConnectionAsync(baseUrl, accessToken, ct))
                {
                    return (true, "Connection reused");
                }

                // Create new connection
                var wsUri = BuildWebSocketUri(baseUrl);
                this.EndpointUri = wsUri;

                lock (this._lock)
                {
                    this._ws?.Dispose();
                    this._ws = new ClientWebSocket();
                    this._nextId = InitialMessageId;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                await this._ws.ConnectAsync(wsUri, cts.Token);

                // 1) Expect auth_required
                var first = await ReceiveMessageAsync(cts.Token);
                var type = GetMessageType(first);
                if (type != "auth_required")
                {
                    return (false, $"Expected auth_required, got {type}");
                }

                // 2) Send auth
                var auth = JsonSerializer.Serialize(new { type = "auth", access_token = accessToken });
                await SendMessageAsync(auth, cts.Token);

                // 3) Expect auth_ok
                var authReply = await ReceiveMessageAsync(cts.Token);
                var authType = GetMessageType(authReply);

                if (authType == "auth_ok")
                {
                    this.IsAuthenticated = true;
                    this._lastBaseUrl = baseUrl;
                    this._lastAccessToken = accessToken;
                    return (true, "Authenticated");
                }

                if (authType == "auth_invalid")
                {
                    var msg = GetMessageField(authReply, "message") ?? "Invalid credentials";
                    await SafeCloseAsync();
                    return (false, msg);
                }

                await SafeCloseAsync();
                return (false, $"Unexpected auth response: {authType}");
            }
            catch (OperationCanceledException)
            {
                await SafeCloseAsync();
                return (false, "Timeout");
            }
            catch (Exception ex)
            {
                await SafeCloseAsync();
                return (false, ex.Message);
            }
        }

        public async Task<(Boolean ok, String? resultJson, String? errorMessage)> RequestAsync(
            String type, CancellationToken ct)
        {
            if (!this.IsAuthenticated)
            {
                return (false, null, "Not authenticated");
            }

            try
            {
                var id = Interlocked.Increment(ref this._nextId);
                var request = JsonSerializer.Serialize(new { id, type });
                await SendMessageAsync(request, ct);

                // Wait for matching response
                while (true)
                {
                    var msg = await ReceiveMessageAsync(ct);
                    using var doc = JsonDocument.Parse(msg);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("id", out var idProp) || idProp.GetInt32() != id)
                    {
                        continue; // Not our response
                    }

                    var msgType = root.GetProperty("type").GetString();
                    if (msgType != "result")
                    {
                        continue;
                    }

                    var success = root.GetProperty("success").GetBoolean();
                    if (success)
                    {
                        var result = root.GetProperty("result");
                        return (true, result.GetRawText(), null);
                    }
                    else
                    {
                        var error = "Request failed";
                        if (root.TryGetProperty("error", out var err))
                        {
                            var message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                            error = message ?? error;
                        }
                        return (false, null, error);
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        public async Task<(Boolean ok, String? error)> CallServiceAsync(
            String domain, String service, String entityId, JsonElement? data, CancellationToken ct)
        {
            try
            {
                // Ensure connection
                if (this._ws?.State != WebSocketState.Open || !this.IsAuthenticated)
                {
                    var reconnected = await EnsureConnectedAsync(TimeSpan.FromSeconds(10), ct);
                    if (!reconnected)
                    {
                        return (false, "Connection failed");
                    }
                }

                var id = Interlocked.Increment(ref this._nextId);
                var request = new Dictionary<String, Object>
                {
                    ["id"] = id,
                    ["type"] = "call_service",
                    ["domain"] = domain,
                    ["service"] = service,
                    ["target"] = new Dictionary<String, Object> { ["entity_id"] = entityId }
                };

                if (data.HasValue)
                {
                    request["service_data"] = data.Value;
                }

                var json = JsonSerializer.Serialize(request);
                await SendMessageAsync(json, ct);

                // Wait for response
                while (true)
                {
                    var msg = await ReceiveMessageAsync(ct);
                    using var doc = JsonDocument.Parse(msg);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("id", out var idProp) || idProp.GetInt32() != id)
                    {
                        continue;
                    }

                    if (root.GetProperty("type").GetString() != "result")
                    {
                        continue;
                    }

                    var success = root.GetProperty("success").GetBoolean();
                    if (success)
                    {
                        return (true, null);
                    }
                    else
                    {
                        var error = "Service call failed";
                        if (root.TryGetProperty("error", out var err))
                        {
                            var message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                            error = message ?? error;
                        }
                        return (false, error);
                    }
                }
            }
            catch (Exception ex)
            {
                await SafeCloseAsync();
                return (false, ex.Message);
            }
        }

        public async Task SendPingAsync(CancellationToken ct)
        {
            if (!this.IsAuthenticated || this._ws?.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                var id = Interlocked.Increment(ref this._nextId);
                var ping = JsonSerializer.Serialize(new { id, type = "ping" });
                await SendMessageAsync(ping, ct);
            }
            catch
            {
                // Ping failures are not critical
            }
        }

        public async Task<Boolean> EnsureConnectedAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (String.IsNullOrWhiteSpace(this._lastBaseUrl) || String.IsNullOrWhiteSpace(this._lastAccessToken))
            {
                return false;
            }

            if (await TryReuseConnectionAsync(this._lastBaseUrl, this._lastAccessToken, ct))
            {
                return true;
            }

            var (ok, _) = await ConnectAndAuthenticateAsync(this._lastBaseUrl, this._lastAccessToken, timeout, ct);
            return ok;
        }

        private async Task<Boolean> TryReuseConnectionAsync(String baseUrl, String accessToken, CancellationToken ct)
        {
            if (this._ws?.State != WebSocketState.Open || !this.IsAuthenticated)
            {
                return false;
            }

            if (this._lastBaseUrl != baseUrl || this._lastAccessToken != accessToken)
            {
                return false;
            }

            // Test with ping
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await SendPingAsync(cts.Token);
                await Task.Delay(50, cts.Token); // Brief wait
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<String> ReceiveMessageAsync(CancellationToken ct)
        {
            if (this._ws == null)
            {
                throw new InvalidOperationException("WebSocket not initialized");
            }

            var messageBuffer = new System.Collections.Generic.List<Byte>();
            var buffer = new ArraySegment<Byte>(this._buffer);

            WebSocketReceiveResult result;
            do
            {
                result = await this._ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("Connection closed");
                }

                if (result.Count > 0)
                {
                    for (var i = 0; i < result.Count; i++)
                    {
                        messageBuffer.Add(this._buffer[i]);
                    }
                }

            } while (!result.EndOfMessage);

            return messageBuffer.Count > 0
                ? Encoding.UTF8.GetString(messageBuffer.ToArray())
                : String.Empty;
        }

        private async Task SendMessageAsync(String message, CancellationToken ct)
        {
            if (this._ws == null)
            {
                throw new InvalidOperationException("WebSocket not initialized");
            }

            var bytes = Encoding.UTF8.GetBytes(message);
            await this._ws.SendAsync(new ArraySegment<Byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        public async Task SafeCloseAsync()
        {
            try
            {
                if (this._ws?.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await this._ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Close", timeout.Token);
                }
            }
            catch
            {
                // Ignore close errors
            }
            finally
            {
                lock (this._lock)
                {
                    this._ws?.Dispose();
                    this._ws = null;
                    this.IsAuthenticated = false;
                    this._nextId = InitialMessageId;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await SafeCloseAsync();
            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            SafeCloseAsync().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }

        private static Uri BuildWebSocketUri(String baseUrl)
        {
            var uri = new Uri(baseUrl.TrimEnd('/'));
            var builder = new UriBuilder(uri)
            {
                Scheme = uri.Scheme == "https" ? "wss" : "ws",
                Path = String.Join("/", uri.AbsolutePath.TrimEnd('/'), "api", "websocket")
            };
            return builder.Uri;
        }

        private static String? GetMessageType(String json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static String? GetMessageField(String json, String fieldName)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty(fieldName, out var v) ? v.GetString() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Splits multiple concatenated JSON messages into individual messages.
        /// Handles Home Assistant's WebSocket responses which may contain multiple JSON objects.
        /// </summary>
        /// <param name="combinedData">Combined JSON string potentially containing multiple messages</param>
        /// <returns>List of individual JSON message strings</returns>
        private List<String> SplitCombinedJsonMessages(String combinedData)
        {
            var messages = new List<String>();

            // Return empty list if input is null/whitespace
            if (String.IsNullOrWhiteSpace(combinedData))
            {
                return messages;
            }

            var i = 0;
            var len = combinedData.Length;

            while (i < len)
            {
                // Skip leading whitespace
                while (i < len && Char.IsWhiteSpace(combinedData[i]))
                {
                    i++;
                }

                if (i >= len)
                {
                    break;
                }

                // Must start with '{'
                if (combinedData[i] != '{')
                {
                    break;
                }

                // Track brace depth and find the end of this JSON object
                var startIndex = i;
                var braceDepth = 0;
                var inString = false;
                var escapeNext = false;

                while (i < len)
                {
                    var c = combinedData[i];

                    if (escapeNext)
                    {
                        // Skip this character as it's escaped
                        escapeNext = false;
                        i++;
                        continue;
                    }

                    if (c == '\\')
                    {
                        escapeNext = true;
                        i++;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = !inString;
                    }
                    else if (!inString)
                    {
                        if (c == '{')
                        {
                            braceDepth++;
                        }
                        else if (c == '}')
                        {
                            braceDepth--;
                            if (braceDepth == 0)
                            {
                                // Found the end of this JSON object
                                i++;
                                break;
                            }
                        }
                    }

                    i++;
                }

                // Extract the message
                var messageLength = i - startIndex;
                if (messageLength > 0)
                {
                    var message = combinedData.Substring(startIndex, messageLength);

                    // Validate that it's valid JSON
                    if (IsValidJson(message))
                    {
                        messages.Add(message);
                    }
                }
            }

            return messages;
        }

        /// <summary>
        /// Validates if a string is valid JSON
        /// </summary>
        private static Boolean IsValidJson(String json)
        {
            if (String.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}