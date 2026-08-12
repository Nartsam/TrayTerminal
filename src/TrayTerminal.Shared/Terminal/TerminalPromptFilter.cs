using System.Buffers;
using System.Text;

namespace TrayTerminal.Shared.Terminal;

public sealed class TerminalPromptFilter
{
    private const string MarkerName = "TrayTerminal";
    private const int MaxJobCountDigits = 10;
    private readonly byte[] _prefix;
    private readonly Action<int> _promptObserved;
    private readonly List<byte> _candidate;
    private ParserState _state;
    private int _jobCount;
    private int _jobCountDigits;

    public TerminalPromptFilter(string token, Action<int> promptObserved)
    {
        if (string.IsNullOrEmpty(token)
            || token.Any(ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            throw new ArgumentException("Prompt marker token must be ASCII alphanumeric.", nameof(token));
        }

        _prefix = Encoding.ASCII.GetBytes($"\u001b]633;{MarkerName};{token};");
        _promptObserved = promptObserved;
        _candidate = new List<byte>(_prefix.Length + MaxJobCountDigits + 2);
    }

    public byte[] Process(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return [];
        }

        var output = new ArrayBufferWriter<byte>(data.Length + _candidate.Count);
        foreach (var value in data)
        {
            ProcessByte(value, output);
        }

        return output.WrittenSpan.ToArray();
    }

    public byte[] Flush()
    {
        if (_candidate.Count == 0)
        {
            return [];
        }

        var remaining = _candidate.ToArray();
        ResetCandidate();
        return remaining;
    }

    public static string BuildMarker(string token, int activeShellJobs)
    {
        return $"\u001b]633;{MarkerName};{token};{Math.Max(0, activeShellJobs)}\u001b\\";
    }

    private void ProcessByte(byte value, ArrayBufferWriter<byte> output)
    {
        if (_candidate.Count == 0)
        {
            if (value == _prefix[0])
            {
                _candidate.Add(value);
            }
            else
            {
                WriteByte(output, value);
            }
            return;
        }

        if (_state == ParserState.Prefix)
        {
            if (value == _prefix[_candidate.Count])
            {
                _candidate.Add(value);
                if (_candidate.Count == _prefix.Length)
                {
                    _state = ParserState.JobCount;
                }
                return;
            }

            FlushCandidate(output);
            ProcessByte(value, output);
            return;
        }

        if (_state == ParserState.JobCount)
        {
            if (value is >= (byte)'0' and <= (byte)'9'
                && _jobCountDigits < MaxJobCountDigits)
            {
                _candidate.Add(value);
                _jobCountDigits++;
                _jobCount = Math.Min(
                    int.MaxValue,
                    (int)Math.Min(
                        int.MaxValue,
                        (long)_jobCount * 10 + value - (byte)'0'));
                return;
            }

            if (value == 0x1b && _jobCountDigits > 0)
            {
                _candidate.Add(value);
                _state = ParserState.Terminator;
                return;
            }

            FlushCandidate(output);
            ProcessByte(value, output);
            return;
        }

        if (value == (byte)'\\')
        {
            var activeShellJobs = _jobCount;
            ResetCandidate();
            _promptObserved(activeShellJobs);
            return;
        }

        FlushCandidate(output);
        ProcessByte(value, output);
    }

    private void FlushCandidate(ArrayBufferWriter<byte> output)
    {
        var span = output.GetSpan(_candidate.Count);
        for (var index = 0; index < _candidate.Count; index++)
        {
            span[index] = _candidate[index];
        }
        output.Advance(_candidate.Count);
        ResetCandidate();
    }

    private void ResetCandidate()
    {
        _candidate.Clear();
        _state = ParserState.Prefix;
        _jobCount = 0;
        _jobCountDigits = 0;
    }

    private static void WriteByte(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private enum ParserState
    {
        Prefix,
        JobCount,
        Terminator
    }
}
