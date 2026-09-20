using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;
using NyaaTriggers.Plugin.Ui;
using static Program;

internal static class FixFollowupTests
{
    private const string Request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n";

    internal static async Task EndedReplay()
    {
        foreach (var partial in new[] { false, true })
        {
            using var f = await Fixture.Open();
            await f.Burst(Me, Zone, Party, Combat, Damage(), End);
            await f.Disconnect();
            await Until(() => !((IinactClient)Field(f.Meter, "client")).IsConnected && Queued(f.Meter) > 0);
            f.Tick();
            await f.Accept();
            await f.Burst(Me, Party);
            if (partial) await f.Burst("{\"type\":\"ChangeZone\",\"zoneName\":\"Arena\"}");
            await f.Burst(Combat, Damage(amount: "00010000"), End);
            var finished = f.Host.Dps;
            Check(finished.Ended && finished.EncDps == 1 && finished.Rows.Single().IsSelf,
                "Control the reconnect completes a new pull before its zone replay", new { partial, finished.Title, finished.EncDps });
            await f.Burst("{\"type\":\"ChangeZone\",\"zoneID\":200,\"zoneName\":\"Arena\"}");
            Check(f.Host.Dps.Ended && f.Host.Dps.EncDps == 1 && f.Host.Dps.Rows.Count == 1 && f.Host.Dps.Rows[0].IsSelf,
                "Delayed replay preserves a completed pull from the current session",
                new { partial, f.Host.Dps.Title, f.Host.Dps.EncDps, f.Host.Dps.Ended, f.Host.Dps.Rows });
        }
    }

    internal static Task ScaledRows()
    {
        try
        {
            foreach (var global in new[] { 1f, 1.5f, 2f, 3f })
            foreach (var ready in new[] { false, true })
            {
                var config = new Configuration { Locked = false, DpsStyle = DpsMeterStyle.Bars, DpsTextEffect = TextEffectStyle.Off };
                using var host = new BridgeHost(config);
                using var fonts = new ScaledFonts { AvailableSize = pixels => ready ? MathF.Ceiling(pixels) : null };
                var window = new DpsWindow(config, host, fonts);
                ImGuiHelpers.GlobalScale = global;
                ImGui.Reset();
                ImGui.Cursor = new Vector2(40, 120);
                window.Draw();
                var rows = ImGui.Commands.Where(c => c.Kind == "text" && (c.Text!.StartsWith("1  ") || c.Text.StartsWith("2  "))).ToArray();
                Check(rows.Length == 2 && rows[0].B.Y <= rows[1].A.Y,
                    "Scaled Bars rows reserve enough height for consecutive player labels",
                    new { global, ready, firstBottom = rows[0].B.Y, secondTop = rows[1].A.Y });
                config.TimelineAnchorBottom = false;
                config.TimelineShowClock = false;
                config.TimelineTextEffect = TextEffectStyle.Off;
                var timeline = new TimelineWindow(config, host, fonts);
                ImGui.Reset();
                ImGui.Cursor = new Vector2(40, 120);
                timeline.Draw();
                var first = ImGui.Commands.Single(c => c.Text?.StartsWith("Sample tankbuster") == true);
                var second = ImGui.Commands.Single(c => c.Text?.StartsWith("Sample raidwide") == true);
                Check(first.B.Y <= second.A.Y, "Scaled timeline rows reserve enough height for consecutive labels",
                    new { global, ready, firstBottom = first.B.Y, secondTop = second.A.Y });
            }
        }
        finally { ImGuiHelpers.GlobalScale = 1; ImGui.Reset(); }
        return Task.CompletedTask;
    }

    internal static async Task RequestBytes()
    {
        foreach (var octet in new byte[] { 0x80, 0xa0, 0xff })
        {
            var port = Port();
            using var server = new WebSocketServer(port, (_, _) => { }, (_, _) => { }, () => null);
            server.Start();
            using var owner = new ClientWebSocket();
            await owner.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
            await Until(() => server.IsConnected);
            using var invalid = new TcpClient();
            await invalid.ConnectAsync(IPAddress.Loopback, port);
            var wire = Encoding.ASCII.GetBytes(Request.Replace("GET / ", "GET /X "));
            wire[5] = octet;
            var response = await Handshake(invalid, wire);
            if (response.StartsWith("HTTP/1.1 101")) await Task.Delay(400);
            server.Send("owner still connected");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var buffer = new byte[256];
            var reply = await owner.ReceiveAsync(buffer, timeout.Token);
            Check(response.StartsWith("HTTP/1.1 400") && reply.MessageType == WebSocketMessageType.Text,
                "Invalid request target bytes cannot become accepted ASCII characters",
                new { octet, status = response.Split('\r')[0], ownerReply = reply.MessageType.ToString() });
            owner.Abort();
        }
    }

    internal static Task HorizonClipping()
    {
        try
        {
            foreach (var global in new[] { 1f, 2f, 3f })
            foreach (var ready in new[] { false, true })
            {
                var config = new Configuration
                {
                    Locked = true, DpsStyle = DpsMeterStyle.HorizonOverlay, DpsTextEffect = TextEffectStyle.Off,
                    DpsHorizShowIcons = false, DpsHorizMaxBarWidth = 400, DpsHorizCompact = false, DpsHorizDecimals = 0,
                };
                using var host = new BridgeHost(config);
                Call(host, "Apply", Dps());
                using var fonts = new ScaledFonts { AvailableSize = pixels => ready ? MathF.Ceiling(pixels) : null };
                var window = new DpsWindow(config, host, fonts);
                ImGuiHelpers.GlobalScale = global;
                ImGui.Reset();
                window.Draw();
                var number = ImGui.Commands.Single(c => c.Kind == "text" && c.Text == "1000");
                var clip = ImGui.ClipRects.Single();
                Check(number.A.Y >= clip.Min.Y && number.B.Y <= clip.Max.Y,
                    "Scaled Horizon numbers fit vertically inside their clip rectangle",
                    new { global, ready, textTop = number.A.Y, clipTop = clip.Min.Y, textBottom = number.B.Y, clipBottom = clip.Max.Y });
            }
        }
        finally { ImGuiHelpers.GlobalScale = 1; ImGui.Reset(); }
        return Task.CompletedTask;
    }

    internal static async Task Boundaries()
    {
        CompletedResults();
        RowLimits();
        await HeaderOctets();
    }

    internal static Task AnchoredStacks()
    {
        var available = ImGui.Available;
        try
        {
            ImGui.Available = new Vector2(1200, 800);
            foreach (var alerts in new[] { false, true })
            foreach (var global in new[] { 1f, 3f })
            foreach (var ready in new[] { false, true })
            {
                var config = new Configuration
                {
                    Locked = !alerts, AlertsAnchorBottom = true, AlertsTextEffect = TextEffectStyle.Off,
                    AlertsAnimate = false, AlertsAlarmFlash = false, AlertsSeverityTint = true, AlertsLifeline = false,
                    TimelineAnchorBottom = true, TimelineShowClock = false, TimelineTextEffect = TextEffectStyle.Off,
                };
                using var host = new BridgeHost(config);
                foreach (var text in new[] { "First", "Second", "Third" })
                    Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"" + text + "\",\"sev\":\"alert\"}");
                Call(host, "Apply", "{\"c\":\"timeline\",\"v\":[[10,\"First\"],[12,\"Second\"],[14,\"Third\"]]}");
                using var fonts = new ScaledFonts { AvailableSize = pixels => ready ? MathF.Ceiling(pixels) : null };
                OverlayWindow window = alerts ? new AlertsWindow(config, host, fonts) : new TimelineWindow(config, host, fonts);
                ImGuiHelpers.GlobalScale = global;
                ImGui.Reset();
                ImGui.ItemSpacing = new Vector2(9, 7);
                var origin = new Vector2(40, 120);
                ImGui.Cursor = origin;
                window.Draw();
                var drawnBottom = ImGui.Commands.Where(c => c.Kind is "text" or "rect" or "gradient").Max(c => c.B.Y);
                Check(drawnBottom <= origin.Y + ImGui.Available.Y && ImGui.ItemSpacing == new Vector2(9, 7),
                    "Bottom anchored stacks fit their measured area with native item spacing",
                    new { alerts, global, ready, drawnBottom, contentBottom = origin.Y + ImGui.Available.Y });
                ImGui.ThrowForText = ImGui.Commands.First(c => c.Kind == "text").Text;
                var interrupted = false;
                try { window.Draw(); }
                catch (ArithmeticException) { interrupted = true; }
                finally { ImGui.ThrowForText = null; }
                Check(interrupted && ImGui.ItemSpacing == new Vector2(9, 7) && ImGui.FontScale == 1 && ImGui.FontBaseSize == 16,
                    "Drawing errors restore the surrounding font and item spacing", new { alerts, global, ready });
            }
        }
        finally { ImGui.Available = available; ImGuiHelpers.GlobalScale = 1; ImGui.ThrowForText = null; ImGui.Reset(); }
        return Task.CompletedTask;
    }

    private static void CompletedResults()
    {
        var variants = new[]
        {
            (first: "", supplement: "{\"type\":\"ChangeZone\",\"zoneID\":200,\"zoneName\":\"New Arena\"}"),
            (first: "{\"type\":\"ChangeZone\",\"zoneName\":\"Arena\"}", supplement: "{\"type\":\"ChangeZone\",\"zoneID\":200,\"zoneName\":\"Arena\"}"),
            (first: "{\"type\":\"ChangeZone\",\"zoneID\":100}", supplement: "{\"type\":\"ChangeZone\",\"zoneName\":\"New Arena\"}"),
        };
        foreach (var (first, supplement) in variants)
        foreach (var ending in new[] { "combat", "wipe", "empty after damage", "damage taken", "rounded zero", "no damage" })
        {
            using var host = new BridgeHost(new Configuration { DpsHoldLast = true });
            var meter = Field(host, "standalone");
            void Handle(params string[] messages) { foreach (var message in messages) Call(meter, "Handle", message); }
            void At(double seconds) => meter.GetType().GetField("messageTime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(meter, seconds);
            Call(meter, "ResetEngine");
            At(0);
            Handle(Me, Zone, Party, Combat, Damage(), End);
            Call(meter, "ResetEngine");
            Handle(Me, Party, first, Combat);
            if (ending == "damage taken")
            {
                Handle(Damage("40000010", "00010000", "Enemy").Replace("|40000010|Target|", "|10000001|Self Player|"));
            }
            else if (ending != "no damage") Handle(Damage(amount: "00010000"));
            if (ending == "rounded zero")
            {
                At(30);
                Handle(Damage(amount: "00000000"));
            }
            Handle(ending == "wipe" ? "33|ts|x|4000000F" : End);
            if (ending == "empty after damage") Handle(Combat, End);
            var result = host.Dps;
            if (ending != "no damage") Check(result.Ended && result.HasDamage && result.Rows.Count == 1,
                "Control the completed replay fixture produced player damage activity", new { first, ending });
            Handle(supplement, supplement);
            var expectedDamage = ending is "damage taken" or "rounded zero" ? 0 : 1;
            Check(ending == "no damage" ? host.Dps.Rows.Count == 0
                : host.Dps.Ended && host.Dps.HasDamage && host.Dps.Rows.Count == 1 && host.Dps.Rows[0].IsSelf
                    && host.Dps.EncDps == expectedDamage && ReferenceEquals(result, host.Dps),
                "Replay protects current session results by damage activity and clears older results",
                new { first, ending, host.Dps.Ended, host.Dps.HasDamage, host.Dps.EncDps, rows = host.Dps.Rows.Count });
            Handle("{\"type\":\"ChangeZone\",\"zoneID\":300,\"zoneName\":\"Third Arena\"}");
            Check(host.Dps.Rows.Count == 0 && !host.Dps.Ended,
                "A subsequent known zone change clears even protected session results", new { first, ending });
            Handle(Me, Party, Combat, Damage(), End, "01|ts|12C|Third Arena");
            Check(host.Dps.Rows.Count == 0, "A raw zone boundary clears completed session protection", new { first, ending });
            Handle(Me, Party, Combat, Damage(), End);
            Call(meter, "ResetEngine");
            Handle("{\"type\":\"ChangeZone\",\"zoneID\":400,\"zoneName\":\"Fourth Arena\"}");
            Check(host.Dps.Rows.Count == 0, "Session result protection does not survive the next reconnect", new { first, ending });
        }
    }

    private static void RowLimits()
    {
        var available = ImGui.Available;
        try
        {
            ImGui.Available = new Vector2(24000, 16000);
            JobIcons.Available = true;
            foreach (var global in new[] { 0.8f, 1f, 2f, 3f })
            foreach (var scale in new[] { 0.5f, 1f, 6f })
            foreach (var height in new[] { 12f, 22f, 48f })
            foreach (var spacing in new[] { 0f, 4f })
            {
                var config = new Configuration
                {
                    Locked = false, DpsStyle = DpsMeterStyle.Bars, DpsTextEffect = TextEffectStyle.Off,
                    DpsTextScale = scale, DpsBarHeight = height, DpsBarSpacing = spacing, DpsRowsShowIcons = true,
                };
                using var host = new BridgeHost(config);
                var ready = false;
                using var fonts = new ScaledFonts { AvailableSize = pixels => ready ? MathF.Ceiling(pixels) : null };
                var window = new DpsWindow(config, host, fonts);
                var firstHeight = 0f;
                for (var frame = 0; frame < 2; frame++)
                {
                    ready = frame != 0;
                    ImGuiHelpers.GlobalScale = global;
                    ImGui.Reset();
                    ImGui.Cursor = new Vector2(-120, 620);
                    window.Draw();
                    var labels = ImGui.Commands.Where(c => c.Kind == "text" && (c.Text!.StartsWith("1  ") || c.Text.StartsWith("2  "))).ToArray();
                    var track = ImGui.Commands.First(c => c.Kind == "rect");
                    var icon = ImGui.Commands.First(c => c.Kind == "image");
                    var drawnHeight = track.B.Y - track.A.Y;
                    if (frame == 0) firstHeight = drawnHeight;
                    Check(labels[0].A.Y >= track.A.Y && labels[0].B.Y <= track.B.Y
                        && labels[0].B.Y <= labels[1].A.Y && icon.A.Y >= track.A.Y && icon.B.Y <= track.B.Y
                        && Math.Abs(drawnHeight - firstHeight) < 0.02f && ImGui.FontScale == 1,
                        "Row limits keep text and icons inside bars as cached fonts arrive",
                        new { global, scale, height, spacing, ready, drawnHeight });
                }
            }
            foreach (var global in new[] { 0.8f, 3f })
            foreach (var scale in new[] { 0.5f, 6f })
            foreach (var statScale in new[] { 0.4f, 1.5f })
            foreach (var height in new[] { 10f, 60f })
            foreach (var icons in new[] { false, true })
            foreach (var ready in new[] { false, true })
            {
                var config = new Configuration
                {
                    Locked = true, DpsStyle = DpsMeterStyle.HorizonOverlay, DpsTextEffect = TextEffectStyle.Off,
                    DpsTextScale = scale, DpsHorizStatScale = statScale, DpsHorizBarHeight = height,
                    DpsHorizShowIcons = icons, DpsHorizIconSize = 64, DpsHorizMaxBarWidth = 400,
                    DpsHorizCompact = false, DpsHorizDecimals = 0,
                };
                using var host = new BridgeHost(config);
                Call(host, "Apply", Dps("Clip Arena"));
                using var fonts = new ScaledFonts { AvailableSize = pixels => ready ? MathF.Ceiling(pixels) : null };
                var window = new DpsWindow(config, host, fonts);
                ImGuiHelpers.GlobalScale = global;
                ImGui.Reset();
                ImGui.Cursor = new Vector2(-120, 620);
                window.Draw();
                var number = ImGui.Commands.Single(c => c.Kind == "text" && c.Text == "1000");
                var clip = ImGui.ClipRects.Single();
                var percent = ImGui.Commands.Single(c => c.Text == "100%");
                var header = ImGui.Commands.Single(c => c.Text?.Contains("Clip Arena") == true);
                Check(number.A.Y >= clip.Min.Y - 0.02f && number.B.Y <= clip.Max.Y + 0.02f
                    && percent.A.Y >= clip.Max.Y && header.A.Y >= percent.B.Y
                    && (!icons || percent.A.Y >= ImGui.Commands.Single(c => c.Kind == "image").B.Y)
                    && ImGui.FontScale == 1,
                    "Horizon size limits preserve number clipping icon clearance and caption layout",
                        new { global, scale, statScale, height, icons, ready, numberTop = number.A.Y, clipTop = clip.Min.Y });
            }
            foreach (var global in new[] { 0.8f, 3f })
            foreach (var scale in new[] { 0.5f, 6f })
            foreach (var height in new[] { 12f, 48f })
            foreach (var bottom in new[] { false, true })
            foreach (var ready in new[] { false, true })
            {
                var config = new Configuration
                {
                    Locked = false, TimelineTextEffect = TextEffectStyle.Off, TimelineShowClock = true,
                    TimelineTextScale = scale, TimelineBarHeight = height, TimelineAnchorBottom = bottom,
                };
                using var host = new BridgeHost(config);
                using var fonts = new ScaledFonts { AvailableSize = pixels => ready ? MathF.Ceiling(pixels) : null };
                var window = new TimelineWindow(config, host, fonts);
                ImGuiHelpers.GlobalScale = global;
                ImGui.Reset();
                var origin = new Vector2(-120, 620);
                ImGui.Cursor = origin;
                window.Draw();
                var first = ImGui.Commands.Single(c => c.Text?.StartsWith("Sample tankbuster") == true);
                var second = ImGui.Commands.Single(c => c.Text?.StartsWith("Sample raidwide") == true);
                var last = ImGui.Commands.Single(c => c.Text?.StartsWith("Sample mechanic") == true);
                Check(first.B.Y <= second.A.Y && second.B.Y <= last.A.Y
                    && last.B.Y <= origin.Y + ImGui.Available.Y && ImGui.FontScale == 1,
                    "Timeline minimum heights agree with row placement and stack measurement",
                    new { global, scale, height, bottom, ready, firstBottom = first.B.Y, secondTop = second.A.Y, lastBottom = last.B.Y });
            }
        }
        finally { ImGui.Available = available; JobIcons.Available = false; ImGuiHelpers.GlobalScale = 1; ImGui.Reset(); }
    }

    private static async Task HeaderOctets()
    {
        var perform = typeof(WebSocketServer).GetMethod("PerformHandshakeAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        async Task<bool> Accepted(byte[] wire)
        {
            using var stream = new MemoryStream();
            stream.Write(wire);
            stream.Position = 0;
            return await (Task<bool>)perform.Invoke(null, new object[] { stream, CancellationToken.None })!;
        }
        for (var octet = 128; octet <= 255; octet++)
        {
            var wire = Encoding.ASCII.GetBytes(Request.Replace("GET / ", "GET /X "));
            wire[5] = (byte)octet;
            Check(!await Accepted(wire), "Every non ASCII target octet is rejected without substitution", octet);
        }
        foreach (var value in new[] { "\u0085", "\u00a0" })
        foreach (var field in new[] { "Sec-WebSocket-Version: 13", "Connection: Upgrade", "Upgrade: websocket", "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" })
        {
            var changed = Request.Replace(field, field + value);
            Check(!await Accepted(Encoding.Latin1.GetBytes(changed)),
                "Non HTTP whitespace cannot pad required handshake values", new { field, value });
        }
        foreach (var octet in new byte[] { 0x80, 0x85, 0xa0, 0xff })
        {
            var request = Request.Replace("Host: localhost", "Host: localhost\r\nX-Opaque: " + (char)octet);
            Check(await Accepted(Encoding.Latin1.GetBytes(request)),
                "Opaque extension header values preserve permitted high octets", octet);
        }
        var padded = Request.Replace("13\r\n", "\t13 \t\r\n").Replace("Connection: Upgrade", "Connection: keep-alive, \tUpgrade\t")
            .Replace("GET / ", "GET /%F0%9F%98%80?name=%C3%A9 ");
        Check(await Accepted(Encoding.ASCII.GetBytes(padded)),
            "Percent encoded paths and HTTP whitespace remain accepted");
    }

    private static async Task<string> Handshake(TcpClient client, byte[] request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.GetStream().WriteAsync(request, timeout.Token);
        var response = new List<byte>();
        var one = new byte[1];
        while (response.Count < 8192)
        {
            await client.GetStream().ReadExactlyAsync(one, timeout.Token);
            response.Add(one[0]);
            if (response.Count >= 4 && response.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 }))
                return Encoding.ASCII.GetString(response.ToArray());
        }
        throw new InvalidDataException("Oversized response headers");
    }
}
