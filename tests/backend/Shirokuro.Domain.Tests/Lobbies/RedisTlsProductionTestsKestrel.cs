using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Two *separate OS processes* running the production API on Kestrel
/// HTTPS/WSS sockets, using the CI runner's real certificate-validated Redis
/// TLS service. This still does not simulate an ingress/load balancer.
/// </summary>
[Collection("Production Redis TLS integration")]
public sealed class RedisTlsProductionTestsKestrel
{
    private const char RecordSeparator = '\u001e';

    private sealed class ChildProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task _stdout;
        private readonly Task _stderr;

        public ChildProcess(Process process)
        {
            _process = process;
            // Drain without printing request URLs, guest IDs, cookies,
            // configuration strings, paths or exception details into CI.
            _stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        }

        public bool HasExited => _process.HasExited;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await _process.WaitForExitAsync(deadline.Token);
                await Task.WhenAll(_stdout, _stderr).WaitAsync(deadline.Token);
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private static string ApiAssembly()
    {
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory);
             parent is not null; parent = parent.Parent)
        {
            if (!File.Exists(Path.Combine(parent.FullName, "Shirokuro.slnx")))
                continue;
            var dll = Path.Combine(parent.FullName, "src", "backend",
                "applications", "Shirokuro.Api", "bin", "Release", "net10.0",
                "Shirokuro.Api.dll");
            if (File.Exists(dll) && File.Exists(Path.ChangeExtension(dll, ".runtimeconfig.json")))
                return dll;
            break;
        }
        throw new InvalidOperationException(
            "Release API host has not been built for the isolated Kestrel test.");
    }

    private static int ReserveUnusedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static ChildProcess StartKestrel(
        int port, string apiAssembly, string serverPfx, string password,
        (string Folder, string Pfx, string Password) keyRing, string redis,
        int? trustedEdgePort = null)
    {
        var process = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(apiAssembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.ArgumentList.Add(apiAssembly);
        process.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        process.Environment["DOTNET_ENVIRONMENT"] = "Production";
        process.Environment["ASPNETCORE_URLS"] = $"https://localhost:{port}";
        process.Environment["Kestrel__Certificates__Default__Path"] = serverPfx;
        process.Environment["Kestrel__Certificates__Default__Password"] = password;
        process.Environment["AllowedHosts"] = trustedEdgePort is null
            ? $"localhost:{port}"
            : $"localhost:{port};localhost:{trustedEdgePort.Value}";
        process.Environment["Security__AllowedOrigins__0"] = $"https://localhost:{port}";
        if (trustedEdgePort is not null)
        {
            // Exact single-hop proxy IP; never trust the request's XFH.
            // Both Kestrel and the edge check the canonical public authority.
            process.Environment["Security__AllowedOrigins__1"] =
                $"https://localhost:{trustedEdgePort.Value}";
            process.Environment["Security__KnownProxies__0"] = "127.0.0.1";
        }
        process.Environment["Security__GuestSessions__RedisConnectionString"] = redis;
        process.Environment["Security__DataProtection__KeysDirectory"] = keyRing.Folder;
        process.Environment["Security__DataProtection__CertificatePath"] = keyRing.Pfx;
        process.Environment["Security__DataProtection__CertificatePassword"] =
            keyRing.Password;
        var started = Process.Start(process)
            ?? throw new InvalidOperationException("Unable to start isolated API process.");
        return new ChildProcess(started);
    }

    private static HttpClient Http(int port, string? cookie = null)
    {
        // Deliberately use normal operating-system CA/hostname verification:
        // no custom HttpClientHandler callback and no insecure TLS bypass.
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false
        })
        {
            BaseAddress = new Uri($"https://localhost:{port}"),
            Timeout = TimeSpan.FromSeconds(12)
        };
        if (cookie is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        return client;
    }

    private static async Task ReadyAsync(
        HttpClient client, ChildProcess child, CancellationToken token)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            token.ThrowIfCancellationRequested();
            Assert.False(child.HasExited, "Production Kestrel child exited before readiness.");
            try
            {
                using var response = await client.GetAsync("/api/ready", token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    Assert.True(response.Headers.CacheControl?.NoStore == true);
                    return;
                }
            }
            catch (HttpRequestException) { } // Not listening yet.
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
        Assert.Fail("Production Kestrel child did not become ready.");
    }

    private static HttpRequestMessage Mutation(
        HttpMethod method, HttpClient client, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Origin",
            client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("X-Shirokuro-Request", "1");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<(string Id, string Cookie)> LoginAsync(
        HttpClient visitor, CancellationToken token)
    {
        using var request = Mutation(HttpMethod.Post, visitor, "/api/sessions/guest");
        using var response = await visitor.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.True(cookies.Length == 1, "Exactly one signed guest cookie is required.");
        var cookie = cookies[0].Split(';')[0];
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return (json.RootElement.GetProperty("playerId").GetString()!, cookie);
    }

    private static async Task SendFrameAsync(
        WebSocket socket, string json, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(json + RecordSeparator);
        await socket.SendAsync(new ArraySegment<byte>(payload),
            WebSocketMessageType.Text, true, token);
    }

    private static async Task<JsonElement> ReceiveFrameAsync(
        WebSocket socket, Func<JsonElement, bool> predicate, CancellationToken token)
    {
        var buffer = new byte[8192];
        var pending = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            var text = pending.ToString();
            var separator = text.IndexOf(RecordSeparator);
            while (separator >= 0)
            {
                var frame = text[..separator];
                text = text[(separator + 1)..];
                if (frame.Length > 0)
                {
                    using var json = JsonDocument.Parse(frame);
                    if (predicate(json.RootElement))
                        return json.RootElement.Clone();
                }
                separator = text.IndexOf(RecordSeparator);
            }
            pending.Clear();
            pending.Append(text);
            Assert.True(pending.Length < 65536, "SignalR frame exceeded size limit.");
        }
    }

    private static async Task<ClientWebSocket> WebSocketAsync(
        int port, string cookie, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", cookie);
        socket.Options.SetRequestHeader("Origin", $"https://localhost:{port}");
        try
        {
            // WSS connects over a real TCP socket and validates the runner's
            // temporary CA and localhost SAN. No TLS validation callbacks.
            await socket.ConnectAsync(
                new Uri($"wss://localhost:{port}/hubs/lobbies"), token);
            await SendFrameAsync(socket,
                "{\"protocol\":\"json\",\"version\":1}", token);
            var handshake = await ReceiveFrameAsync(socket,
                m => !m.TryGetProperty("type", out _), token);
            Assert.Equal("{}", handshake.GetRawText());
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<JsonElement> RosterAsync(
        WebSocket socket, string room, string invocation, CancellationToken token)
    {
        await SendFrameAsync(socket, JsonSerializer.Serialize(new
        {
            type = 1, invocationId = invocation,
            target = "GetRoster", arguments = new[] { room }
        }), token);
        return await ReceiveFrameAsync(socket,
            m => m.TryGetProperty("type", out var type) && type.GetInt32() == 3 &&
                 m.TryGetProperty("invocationId", out var id) &&
                 id.GetString() == invocation, token);
    }

    private static void AssertOpaque(
        JsonElement completion, string room, string alice, string bob)
    {
        Assert.True(completion.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
        Assert.False(completion.TryGetProperty("result", out _));
        var json = completion.GetRawText();
        // Never hand potentially private bytes or expected identities to
        // assertion formatters: a failed test also runs in shared CI logs.
        Assert.False(json.Contains(room, StringComparison.Ordinal),
            "Denied SignalR completion disclosed room information.");
        Assert.False(json.Contains(alice, StringComparison.Ordinal) ||
                     json.Contains(bob, StringComparison.Ordinal),
            "Denied SignalR completion disclosed player information.");
        Assert.False(json.Contains("participants", StringComparison.OrdinalIgnoreCase),
            "Denied SignalR completion contained a private roster.");
    }


    private sealed class EphemeralNginxProxy(string containerName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // The container has --rm, no persistent logs, no published ports
            // and its nginx.conf restricts the listener to loopback.
            await DockerAsync(["rm", "-f", containerName], timeout.Token);
        }
    }

    private static async Task<bool> DockerAsync(
        string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start isolated Docker CLI.");
        // Docker may print paths, host configuration, or secrets on failure.
        // Consume both pipes without relaying their contents into CI logs.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr).WaitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<EphemeralNginxProxy> StartNginxAsync(
        int edgePort, int nodeA, int nodeB, string certificates,
        string scratch, CancellationToken cancellationToken)
    {
        var config = Path.Combine(scratch, "nginx.conf");
        // A real nginx reverse proxy (not an ASP.NET test implementation).
        // Both TLS hops validate normal certificates: nginx terminates
        // client TLS, then nginx verifies the upstream's localhost SAN
        // against the runner's ephemeral trusted Redis-TLS CA.
        // All headers from a possible hostile client are REPLACED, not
        // appended. X-Forwarded-Host is explicitly cleared, never trusted.
        // Never write access logs/cookies or private request URLs to disk.
        File.WriteAllText(config, $"""
        worker_processes 1;
        pid /tmp/shirokuro-nginx.pid;
        events { worker_connections 256; }
        http {
            access_log off;
            error_log /dev/null crit;
            server_tokens off;
            map $http_upgrade $upgrade_connection {
                default upgrade;
                '' close;
            }
            upstream shirokuro_backend {
                server 127.0.0.1:{{nodeA}};
                server 127.0.0.1:{{nodeB}};
            }
            server {
                listen 127.0.0.1:{{edgePort}} ssl;
                server_name localhost;
                ssl_certificate /certs/server.crt;
                ssl_certificate_key /certs/server.key;
                ssl_protocols TLSv1.2 TLSv1.3;
                location / {
                    proxy_pass https://shirokuro_backend;
                    proxy_http_version 1.1;
                    proxy_set_header Host $http_host;
                    proxy_set_header X-Forwarded-Host "";
                    proxy_set_header X-Forwarded-For $remote_addr;
                    proxy_set_header X-Forwarded-Proto https;
                    proxy_set_header Upgrade $http_upgrade;
                    proxy_set_header Connection $upgrade_connection;
                    proxy_ssl_trusted_certificate /certs/ca.crt;
                    proxy_ssl_verify on;
                    proxy_ssl_verify_depth 2;
                    proxy_ssl_server_name on;
                    proxy_ssl_name localhost;
                    proxy_next_upstream off;
                    proxy_read_timeout 35s;
                    proxy_send_timeout 35s;
                    # Test-only evidence of real round-robin. No secrets,
                    # cookies, player IDs or room names in this response.
                    add_header X-Shirokuro-CI-Upstream $upstream_addr always;
                }
            }
        }
        """);
        var name = "shirokuro-nginx-ci-" + Guid.NewGuid().ToString("N")[..12];
        var ok = await DockerAsync([
            "run", "-d", "--rm",
            "--network", "host", "--name", name,
            "--log-driver", "none",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "-v", config + ":/etc/nginx/nginx.conf:ro",
            "-v", certificates + ":/certs:ro",
            "nginx:1.27-alpine"
        ], cancellationToken);
        Assert.True(ok, "Isolated nginx TLS reverse proxy failed to start.");
        return new EphemeralNginxProxy(name);
    }

    private static async Task VerifyNginxRoundRobinAsync(
        HttpClient edge, ChildProcess a, ChildProcess b,
        int nodeA, int nodeB, CancellationToken token)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            $"127.0.0.1:{nodeA}", $"127.0.0.1:{nodeB}"
        };
        for (var n = 0; n < 50; n++)
        {
            token.ThrowIfCancellationRequested();
            Assert.False(a.HasExited || b.HasExited, "Kestrel backend terminated.");
            try
            {
                using var ready = await edge.GetAsync("/api/ready", token);
                if (ready.StatusCode == HttpStatusCode.OK &&
                    ready.Headers.TryGetValues("X-Shirokuro-CI-Upstream", out var targets))
                {
                    var destination = targets.Single();
                    if (expected.Contains(destination))
                        observed.Add(destination);
                    if (observed.SetEquals(expected))
                        return;
                }
            }
            catch (HttpRequestException) { } // Nginx may not yet be listening.
            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
        }
        Assert.Fail("Both Kestrel backends must serve HTTPS through real nginx.");
    }

    [Fact]
    public async Task RealNginxTlsEdgeBalancesProductionKestrelHttpAndPrivateWss()
    {
        var redis = Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_TLS");
        if (string.IsNullOrWhiteSpace(redis))
            return; // This Docker+Redis-TLS scenario is opt-in CI only.
        var certs = Environment.GetEnvironmentVariable("SHIROKURO_TLS_CERT_DIR");
        Assert.False(string.IsNullOrWhiteSpace(certs));
        Assert.True(File.Exists(Path.Combine(certs!, "ca.crt")));
        Assert.True(File.Exists(Path.Combine(certs!, "server.crt")));
        Assert.True(File.Exists(Path.Combine(certs!, "server.key")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var scratch = Directory.CreateTempSubdirectory("shirokuro-nginx-ci-");
        try
        {
            var serverPfx = Path.Combine(scratch.FullName, "kestrel.pfx");
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            using (var pem = X509Certificate2.CreateFromPemFile(
                       Path.Combine(certs!, "server.crt"), Path.Combine(certs, "server.key")))
                File.WriteAllBytes(serverPfx, pem.Export(X509ContentType.Pkcs12, password));
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(serverPfx,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var portA = ReserveUnusedLoopbackPort();
            var portB = ReserveUnusedLoopbackPort();
            while (portB == portA)
                portB = ReserveUnusedLoopbackPort();
            var edgePort = ReserveUnusedLoopbackPort();
            while (edgePort == portA || edgePort == portB)
                edgePort = ReserveUnusedLoopbackPort();

            var assembly = ApiAssembly();
            var keyring = TestDataProtectionConfiguration.Values;
            await using var nodeA = StartKestrel(portA, assembly, serverPfx,
                password, keyring, redis, trustedEdgePort: edgePort);
            using var directA = Http(portA);
            await ReadyAsync(directA, nodeA, timeout.Token);
            var (alice, aliceCookie) = await LoginAsync(directA, timeout.Token);

            await using var nodeB = StartKestrel(portB, assembly, serverPfx,
                password, keyring, redis, trustedEdgePort: edgePort);
            using var directB = Http(portB);
            await ReadyAsync(directB, nodeB, timeout.Token);
            await using var nginx = await StartNginxAsync(
                edgePort, portA, portB, certs, scratch.FullName, timeout.Token);
            using var edge = Http(edgePort);
            await VerifyNginxRoundRobinAsync(
                edge, nodeA, nodeB, portA, portB, timeout.Token);

            // Host, XFF and XFP are overwritten by the actual edge, and
            // XFH is explicitly cleared. The backend must use the exact
            // configured public Host without trusting a hostile XFH.
            using (var spoof = new HttpRequestMessage(HttpMethod.Get, "/api/ready"))
            {
                spoof.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.example");
                spoof.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
                spoof.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.77");
                using var clean = await edge.SendAsync(spoof, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, clean.StatusCode);
            }
            using (var hostileHost = new HttpRequestMessage(
                       HttpMethod.Get, "/api/ready"))
            {
                hostileHost.Headers.Host = "attacker.example";
                using var blocked = await edge.SendAsync(hostileHost, timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
                Assert.True(blocked.Headers.CacheControl?.NoStore == true);
            }
            using (var hostileOrigin = Mutation(
                       HttpMethod.Post, edge, "/api/sessions/guest"))
            {
                hostileOrigin.Headers.Remove("Origin");
                hostileOrigin.Headers.TryAddWithoutValidation(
                    "Origin", "https://attacker.example");
                using var denied = await edge.SendAsync(hostileOrigin, timeout.Token);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                Assert.True(denied.Headers.CacheControl?.NoStore == true);
                Assert.False(denied.Headers.Contains("Set-Cookie"));
            }

            var (bob, bobCookie) = await LoginAsync(edge, timeout.Token);
            Assert.False(string.Equals(alice, bob, StringComparison.Ordinal),
                "Separate signed guest accounts must have different IDs.");
            using var aliceEdge = Http(edgePort, aliceCookie);
            using var bobEdge = Http(edgePort, bobCookie);
            var destinations = new HashSet<string>(StringComparer.Ordinal);
            for (var attempt = 0; attempt < 6; attempt++)
            {
                using var reply = await aliceEdge.GetAsync("/api/sessions/me", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
                var upstream = reply.Headers.GetValues("X-Shirokuro-CI-Upstream").Single();
                destinations.Add(upstream);
            }
            Assert.Equal(2, destinations.Count); // Alice's signed cookie works at both.

            string room;
            using (var create = Mutation(HttpMethod.Post, aliceEdge,
                       "/api/lobbies",
                       new { teamIds = new[] { "white", "black" }, teamId = "white" }))
            using (var response = await aliceEdge.SendAsync(create, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(timeout.Token));
                room = json.RootElement.GetProperty("lobbyId").GetString()!;
            }

            // These sockets use SignalR's supported WebSocket-only path.
            // A negotiated connectionId is local to one hub process; without
            // affinity, negotiate + upgrade may hit different upstreams.
            using var aliceSocket1 = await WebSocketAsync(
                edgePort, aliceCookie, timeout.Token);
            using var aliceSocket2 = await WebSocketAsync(
                edgePort, aliceCookie, timeout.Token);
            using var bobSocket = await WebSocketAsync(
                edgePort, bobCookie, timeout.Token);

            var deniedRoom = await RosterAsync(bobSocket, room,
                "edge-outsider", timeout.Token);
            var missing = await RosterAsync(bobSocket,
                "room-00000000000000000000000000000000",
                "edge-missing", timeout.Token);
            AssertOpaque(deniedRoom, room, alice, bob);
            AssertOpaque(missing, room, alice, bob);
            Assert.Equal(missing.GetProperty("error").GetString(),
                deniedRoom.GetProperty("error").GetString());

            using (var join = Mutation(HttpMethod.Post, bobEdge,
                       "/api/lobbies/" + room + "/join",
                       new { teamId = "black", expectedRevision = 1L }))
            using (var response = await bobEdge.SendAsync(join, timeout.Token))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var (socket, id) in new[]
            {
                (aliceSocket1, "edge-alice-1"),
                (aliceSocket2, "edge-alice-2"),
                (bobSocket, "edge-bob")
            })
            {
                var completion = await RosterAsync(socket, room, id, timeout.Token);
                Assert.False(completion.TryGetProperty("error", out _));
                var roster = completion.GetProperty("result");
                Assert.True(string.Equals(
                    room, roster.GetProperty("lobbyId").GetString(),
                    StringComparison.Ordinal), "Roster must belong to requested room.");
                Assert.Equal(2, roster.GetProperty("revision").GetInt64());
                var members = roster.GetProperty("participants").EnumerateArray()
                    .Select(x => x.GetProperty("playerId").GetString()).ToArray();
                Assert.Equal(2, members.Length);
                Assert.True(members.Contains(alice, StringComparer.Ordinal) &&
                            members.Contains(bob, StringComparer.Ordinal),
                    "Only current members may appear in a private roster.");
            }

            using (var logout = Mutation(HttpMethod.Delete,
                       aliceEdge, "/api/sessions/guest"))
            using (var response = await aliceEdge.SendAsync(logout, timeout.Token))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            AssertOpaque(await RosterAsync(aliceSocket1, room,
                "edge-revoked-1", timeout.Token), room, alice, bob);
            AssertOpaque(await RosterAsync(aliceSocket2, room,
                "edge-revoked-2", timeout.Token), room, alice, bob);
            destinations.Clear();
            for (var attempt = 0; attempt < 6; attempt++)
            {
                using var invalid = await aliceEdge.GetAsync(
                    "/api/sessions/me", timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
                Assert.True(invalid.Headers.CacheControl?.NoStore == true);
                destinations.Add(invalid.Headers
                    .GetValues("X-Shirokuro-CI-Upstream").Single());
            }
            Assert.Equal(2, destinations.Count);
            var survivor = await RosterAsync(bobSocket, room,
                "edge-survivor", timeout.Token);
            Assert.False(survivor.TryGetProperty("error", out _));
            var view = survivor.GetProperty("result");
            Assert.Equal(3, view.GetProperty("revision").GetInt64());
            Assert.True(string.Equals(bob,
                Assert.Single(view.GetProperty("participants").EnumerateArray())
                    .GetProperty("playerId").GetString(), StringComparison.Ordinal),
                "Surviving socket must only receive its own membership.");
            Assert.False(survivor.GetRawText().Contains(alice, StringComparison.Ordinal),
                "Survivor view disclosed revoked guest.");
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var stillActive = await bobEdge.GetAsync(
                    "/api/sessions/me", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
            }
            using var renegotiate = await aliceEdge.PostAsync(
                "/hubs/lobbies/negotiate?negotiateVersion=1",
                null, timeout.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, renegotiate.StatusCode);
            Assert.True(renegotiate.Headers.CacheControl?.NoStore == true);
        }
        finally
        {
            Directory.Delete(scratch.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task TwoRealProductionKestrelProcessesShareTlsGuestSessionsAndWssRevocation()
    {
        var redis = Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_TLS");
        if (string.IsNullOrWhiteSpace(redis))
            return; // Only the dedicated ephemeral TLS CI suite runs this fixture.
        var certDirectory = Environment.GetEnvironmentVariable("SHIROKURO_TLS_CERT_DIR");
        Assert.False(string.IsNullOrWhiteSpace(certDirectory));
        var certificate = Path.Combine(certDirectory!, "server.crt");
        var privateKey = Path.Combine(certDirectory!, "server.key");
        Assert.True(File.Exists(certificate));
        Assert.True(File.Exists(privateKey));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var scratch = Directory.CreateTempSubdirectory("shirokuro-kestrel-ci-");
        try
        {
            var pfxPath = Path.Combine(scratch.FullName, "kestrel.pfx");
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            using (var pem = X509Certificate2.CreateFromPemFile(certificate, privateKey))
                File.WriteAllBytes(pfxPath, pem.Export(X509ContentType.Pkcs12, password));
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(pfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var portA = ReserveUnusedLoopbackPort();
            var portB = ReserveUnusedLoopbackPort();
            while (portB == portA)
                portB = ReserveUnusedLoopbackPort();
            var api = ApiAssembly();
            var dp = TestDataProtectionConfiguration.Values;
            await using var first = StartKestrel(portA, api, pfxPath,
                password, dp, redis);
            using var visitorA = Http(portA);
            await ReadyAsync(visitorA, first, timeout.Token);
            // A must persist the shared DP key BEFORE B starts, avoiding
            // concurrent initial key generation by isolated processes.
            var (alice, aliceCookie) = await LoginAsync(visitorA, timeout.Token);

            await using var second = StartKestrel(portB, api, pfxPath,
                password, dp, redis);
            using var visitorB = Http(portB);
            await ReadyAsync(visitorB, second, timeout.Token);
            var (bob, bobCookie) = await LoginAsync(visitorB, timeout.Token);
            Assert.True(!string.Equals(alice, bob, StringComparison.Ordinal),
                "Independent guest sessions must have different identities.");

            using var aliceA = Http(portA, aliceCookie);
            using var aliceB = Http(portB, aliceCookie);
            using var bobA = Http(portA, bobCookie);
            using var bobB = Http(portB, bobCookie);
            using (var crossProcessTicket = await aliceB.GetAsync(
                "/api/sessions/me", timeout.Token))
                Assert.Equal(HttpStatusCode.OK, crossProcessTicket.StatusCode);

            string room;
            using (var create = Mutation(HttpMethod.Post, aliceA, "/api/lobbies",
                       new { teamIds = new[] { "white", "black" }, teamId = "white" }))
            using (var response = await aliceA.SendAsync(create, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var data = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(timeout.Token));
                room = data.RootElement.GetProperty("lobbyId").GetString()!;
            }

            using var socketAliceA = await WebSocketAsync(portA, aliceCookie, timeout.Token);
            using var socketAliceB = await WebSocketAsync(portB, aliceCookie, timeout.Token);
            using var socketBobB = await WebSocketAsync(portB, bobCookie, timeout.Token);

            var outsider = await RosterAsync(socketBobB, room,
                "not-member", timeout.Token);
            var missing = await RosterAsync(socketBobB,
                "room-00000000000000000000000000000000",
                "not-found", timeout.Token);
            AssertOpaque(outsider, room, alice, bob);
            AssertOpaque(missing, room, alice, bob);
            Assert.Equal(missing.GetProperty("error").GetString(),
                outsider.GetProperty("error").GetString());

            using (var join = Mutation(HttpMethod.Post, bobB,
                       "/api/lobbies/" + room + "/join",
                       new { teamId = "black", expectedRevision = 1L }))
            using (var response = await bobB.SendAsync(join, timeout.Token))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            foreach (var (socket, invocation) in new[]
            {
                (socketAliceA, "alice-on-A"),
                (socketAliceB, "alice-on-B"),
                (socketBobB, "bob-on-B")
            })
            {
                var completion = await RosterAsync(socket, room, invocation,
                    timeout.Token);
                Assert.False(completion.TryGetProperty("error", out _));
                var roster = completion.GetProperty("result");
                Assert.True(string.Equals(room,
                    roster.GetProperty("lobbyId").GetString(), StringComparison.Ordinal),
                    "Authorized cross-process roster must use the expected room.");
                Assert.Equal(2, roster.GetProperty("revision").GetInt64());
                var members = roster.GetProperty("participants").EnumerateArray()
                    .Select(x => x.GetProperty("playerId").GetString()).ToArray();
                Assert.Equal(2, members.Length);
                Assert.True(members.Contains(alice, StringComparer.Ordinal) &&
                            members.Contains(bob, StringComparer.Ordinal),
                    "Cross-process roster must contain exactly its two members.");
            }

            using (var logout = Mutation(HttpMethod.Delete,
                       aliceB, "/api/sessions/guest"))
            using (var result = await aliceB.SendAsync(logout, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
                Assert.True(result.Headers.CacheControl?.NoStore == true);
            }

            AssertOpaque(await RosterAsync(socketAliceA, room,
                "revoked-on-A", timeout.Token), room, alice, bob);
            AssertOpaque(await RosterAsync(socketAliceB, room,
                "revoked-on-B", timeout.Token), room, alice, bob);
            using (var meA = await aliceA.GetAsync("/api/sessions/me", timeout.Token))
            using (var meB = await aliceB.GetAsync("/api/sessions/me", timeout.Token))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, meA.StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, meB.StatusCode);
            }
            var surviving = await RosterAsync(socketBobB, room,
                "survivor", timeout.Token);
            Assert.False(surviving.TryGetProperty("error", out _));
            var view = surviving.GetProperty("result");
            Assert.Equal(3, view.GetProperty("revision").GetInt64());
            Assert.True(string.Equals(bob, Assert.Single(view.GetProperty("participants")
                .EnumerateArray()).GetProperty("playerId").GetString(),
                StringComparison.Ordinal), "Only the surviving member may read the roster.");
            Assert.False(surviving.GetRawText().Contains(alice, StringComparison.Ordinal),
                "Survivor roster must not disclose the revoked guest.");
            using (var bobAliveA = await bobA.GetAsync("/api/sessions/me", timeout.Token))
            using (var bobAliveB = await bobB.GetAsync("/api/sessions/me", timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, bobAliveA.StatusCode);
                Assert.Equal(HttpStatusCode.OK, bobAliveB.StatusCode);
            }

            foreach (var client in new[] { aliceA, aliceB })
            {
                using var renegotiated = await client.PostAsync(
                    "/hubs/lobbies/negotiate?negotiateVersion=1",
                    null, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, renegotiated.StatusCode);
                Assert.True(renegotiated.Headers.CacheControl?.NoStore == true);
                Assert.False(renegotiated.Headers.Contains("Set-Cookie"));
                Assert.False(renegotiated.Headers.Contains("Location"));
            }
        }
        finally
        {
            Directory.Delete(scratch.FullName, recursive: true);
        }
    }
}
