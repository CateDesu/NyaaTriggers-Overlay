using System.Collections.Generic;

namespace NyaaTriggers.Plugin.Bridge;

/// <summary>A bounded backlog measured in retained UTF-16 bytes.</summary>
internal sealed class MessageInbox
{
    internal const int MaxBytes = 4 << 20;
    internal const int FrameBytes = 2 << 20;
    private const int MaxCount = 512;
    private readonly Queue<string?> messages = new();
    private readonly object gate = new();
    private int bytes;

    internal bool TryEnqueue(string? raw)
    {
        lock (this.gate)
        {
            var cost = (long)(raw?.Length ?? 0) * sizeof(char);
            if (this.messages.Count >= MaxCount || cost > MaxBytes - this.bytes)
            {
                return false;
            }

            this.messages.Enqueue(raw);
            this.bytes += (int)cost;
            return true;
        }
    }

    internal bool TryDequeue(ref int budget, bool first, out string? raw)
    {
        lock (this.gate)
        {
            if (!this.messages.TryPeek(out raw))
            {
                return false;
            }

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
