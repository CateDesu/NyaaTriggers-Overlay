using System;
using System.Collections.Generic;

namespace NyaaTriggers.Plugin.Bridge;

/// <summary>A bounded backlog measured in retained UTF-16 bytes.</summary>
internal sealed class MessageInbox
{
    internal const int MaxBytes = 4 << 20;
    internal const int FrameBytes = 2 << 20;
    private const int MaxCount = 512;
    private readonly record struct Message(string? Raw, long ReceivedAt);
    private readonly Queue<Message> messages = new();
    private readonly object gate = new();
    private int bytes;

    internal bool TryEnqueue(string? raw, long? receivedAt = null)
    {
        lock (this.gate)
        {
            var cost = (long)(raw?.Length ?? 0) * sizeof(char);
            if (this.messages.Count >= MaxCount || cost > MaxBytes - this.bytes)
            {
                return false;
            }

            this.messages.Enqueue(new Message(raw, receivedAt ?? Environment.TickCount64));
            this.bytes += (int)cost;
            return true;
        }
    }

    internal bool TryDequeue(ref int budget, bool first, out string? raw)
        => this.TryDequeue(ref budget, first, out raw, out _);

    internal bool TryDequeue(ref int budget, bool first, out string? raw, out long receivedAt)
    {
        lock (this.gate)
        {
            raw = null;
            receivedAt = 0;
            if (!this.messages.TryPeek(out var message))
            {
                return false;
            }

            raw = message.Raw;
            receivedAt = message.ReceivedAt;
            var cost = (raw?.Length ?? 0) * sizeof(char);
            // A single large roster frame must still make progress.
            if (cost > budget && !first)
            {
                return false;
            }

            this.messages.Dequeue();
            this.bytes -= cost;
            budget -= cost;
            return true;
        }
    }

    internal void Clear()
    {
        lock (this.gate)
        {
            this.messages.Clear();
            this.bytes = 0;
        }
    }
}
