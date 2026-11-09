namespace Loupedeck.HomeAssistantPlugin.Tests.Helpers
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Net.WebSockets;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public class StateWidgetTransportTests
    {
        [Theory]
        [InlineData("wss://ha.example:8123/", "ws")]
        [InlineData("https://ha.example:8123/", "wss")]
        [InlineData("ws://ha.example:8123/", "ws")]
        [InlineData("http://ha.example:8123/", "ws")]
        public void BothConnectionsRetainOriginalBaseUrlMapping(String url, String scheme)
        {
            foreach (var type in new[] { typeof(HaWebSocketClient), typeof(HaEventListener) })
            {
                var uri = (Uri)type.GetMethod("BuildWebSocketUri", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new Object[] { url })!;
                uri.Scheme.Should().Be(scheme);
                uri.AbsolutePath.Should().Be("/api/websocket");
                uri.Port.Should().Be(8123);
            }
        }

        [Fact]
        public async Task RealSubscriptionDispatchesGenericAndExistingSwitchEvents()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            var url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
            using var listener = new HaEventListener();
            var generic = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var existing = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.EntityStateChanged += (id, state) =>
            {
                if (id == "switch.room")
                {
                    generic.TrySetResult(state);
                }
            };
            listener.SwitchStateChanged += (id, on) => existing.TrySetResult(on);

            var serve = ServeAsync(server, async socket =>
            {
                await SendAsync(socket, "{\"type\":\"auth_required\"}", timeout.Token);
                await ReceiveAsync(socket, timeout.Token);
                await SendAsync(socket, "{\"type\":\"auth_ok\"}", timeout.Token);
                using var subscription = JsonDocument.Parse(await ReceiveAsync(socket, timeout.Token));
                subscription.RootElement.GetProperty("type").GetString().Should().Be("subscribe_events");
                var id = subscription.RootElement.GetProperty("id").GetInt32();
                await SendAsync(socket, JsonSerializer.Serialize(new { id, type = "result", success = true }), timeout.Token);
                await SendAsync(socket, "{\"type\":\"event\",\"event\":{\"event_type\":\"state_changed\",\"data\":{\"entity_id\":\"switch.room\",\"new_state\":{\"entity_id\":\"switch.room\",\"state\":\"on\",\"attributes\":{}}}}}", timeout.Token);
                await existing.Task.WaitAsync(timeout.Token);
            }, timeout.Token);

            (await listener.ConnectAndSubscribeAsync(url, "test-token", timeout.Token)).Should().BeTrue();
            var received = await generic.Task.WaitAsync(timeout.Token);
            received!.Value.GetProperty("state").GetString().Should().Be("on");
            (await existing.Task.WaitAsync(timeout.Token)).Should().BeTrue();
            await serve;
            await listener.SafeCloseAsync();
        }

        [Fact]
        public async Task ClientClosesWithinBoundWhenPeerIgnoresCloseHandshake()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            var url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
            using var client = new HaWebSocketClient();
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serve = ServeAsync(server, async socket =>
            {
                await SendAsync(socket, "{\"type\":\"auth_required\"}", timeout.Token);
                await ReceiveAsync(socket, timeout.Token);
                await SendAsync(socket, "{\"type\":\"auth_ok\"}", timeout.Token);
                await finish.Task.WaitAsync(timeout.Token);
            }, timeout.Token);
            try
            {
                (await client.ConnectAndAuthenticateAsync(url, "test-token", TimeSpan.FromSeconds(5), timeout.Token)).ok.Should().BeTrue();
                await client.SafeCloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
                client.IsAuthenticated.Should().BeFalse();
            }
            finally
            {
                finish.TrySetResult();
                await serve;
            }
        }

        private static async Task ServeAsync(TcpListener server, Func<WebSocket, Task> handle, CancellationToken token)
        {
            using var connection = await server.AcceptTcpClientAsync(token);
            using var stream = connection.GetStream();
            var header = new StringBuilder();
            var single = new Byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(single, token) == 0 || header.Length > 8192)
                {
                    throw new InvalidOperationException("Invalid WebSocket handshake");
                }
                header.Append((Char)single[0]);
            }
            var key = Array.Find(header.ToString().Split("\r\n"), line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))!
                .Substring("Sec-WebSocket-Key:".Length).Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
            await handle(socket);
        }

        private static Task SendAsync(WebSocket socket, String text, CancellationToken token) =>
            socket.SendAsync(new ArraySegment<Byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, token);

        private static async Task<String> ReceiveAsync(WebSocket socket, CancellationToken token)
        {
            var buffer = new Byte[8192];
            var result = await socket.ReceiveAsync(new ArraySegment<Byte>(buffer), token);
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
    }
}
