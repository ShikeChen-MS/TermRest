using System.Text;
using TermRest.Core.Http;

namespace TermRest.Tui.Views;

/// <summary>
/// A response that streams (server-sent events, JSON lines) while it arrives, for the response pane: its head, how much has
/// arrived, and the end of its text, the last <see cref="Kept"/> bytes, which is all the pane shows until the stream ends
/// (then it shows it whole). The sending thread writes it; the UI thread reads it a few times a second (see
/// <see cref="ResponseView.ShowLive"/>). <see cref="Stop"/> ends the stream, keeping what arrived.
/// </summary>
internal sealed class LiveResponse : IStreamWatcher, IDisposable
{
    internal const int Kept = 64 * 1024;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _tail = new byte[Kept];
    private int _tailLength;
    private long _received;
    private int _version;
    private ResponseSnapshot? _head;

    public CancellationToken Stopping => _stop.Token;

    /// <summary>Whether the response streams: its head arrived, and its body is arriving.</summary>
    public bool Streaming
    {
        get
        {
            lock (_gate)
            {
                return _head is not null;
            }
        }
    }

    /// <summary>Changes when more arrives: the pane shows it again only then.</summary>
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>Ends the stream: what arrived is its body.</summary>
    public void Stop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // it ended meanwhile
        }
    }

    public void Started(ResponseSnapshot head)
    {
        lock (_gate)
        {
            _head = head;
            _version++;
        }
    }

    public void Received(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _received += data.Length;
            if (data.Length >= Kept)
            {
                data[^Kept..].CopyTo(_tail);
                _tailLength = Kept;
            }
            else
            {
                var keep = Math.Min(_tailLength, Kept - data.Length);
                _tail.AsSpan(_tailLength - keep, keep).CopyTo(_tail);
                data.CopyTo(_tail.AsSpan(keep));
                _tailLength = keep + data.Length;
            }

            _version++;
        }
    }

    /// <summary>What arrived so far: the head (null until it arrives), how many bytes, and the text of the end, from the start of a line when it is cut.</summary>
    public (ResponseSnapshot? Head, long Received, string Text, bool Cut) Read()
    {
        lock (_gate)
        {
            var tail = _tail.AsSpan(0, _tailLength);
            var cut = _received > _tailLength;
            if (cut)
            {
                var line = tail.IndexOf((byte)'\n');
                tail = line >= 0 && line < tail.Length - 1 ? tail[(line + 1)..] : tail;
                while (tail.Length > 0 && (tail[0] & 0xC0) == 0x80)
                {
                    tail = tail[1..]; // not inside a character
                }
            }

            return (_head, _received, Encoding.UTF8.GetString(tail), cut);
        }
    }

    public void Dispose() => _stop.Dispose();
}
