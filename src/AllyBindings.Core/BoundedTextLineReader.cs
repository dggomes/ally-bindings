using System.Text;

namespace AllyBindings.Core;

/// <summary>
/// Reads bounded newline-delimited frames without discarding bytes that follow a
/// newline in the same underlying read. The instance is stateful and must be reused
/// for the lifetime of the framed stream.
/// </summary>
public sealed class BoundedTextLineReader(TextReader reader) : IDisposable
{
    private readonly TextReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly char[] _buffer = new char[1024];
    private int _offset;
    private int _count;
    private bool _poisoned;

    public async Task<string?> ReadLineAsync(int maximumCharacters, CancellationToken cancellationToken)
    {
        if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        if (_poisoned) throw new InvalidOperationException("The bounded line reader cannot continue after a framing error.");

        var result = new StringBuilder(Math.Min(maximumCharacters, 4096));
        while (true)
        {
            if (_offset == _count)
            {
                _offset = 0;
                _count = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_count == 0) return result.Length == 0 ? null : result.ToString();
            }

            var newline = Array.IndexOf(_buffer, '\n', _offset, _count - _offset);
            var end = newline >= 0 ? newline : _count;
            var appendCount = end - _offset;
            if (result.Length + appendCount > maximumCharacters)
            {
                _poisoned = true;
                throw new InvalidDataException("The framed line exceeded its maximum length.");
            }
            result.Append(_buffer, _offset, appendCount);
            _offset = end;

            if (newline >= 0)
            {
                _offset++;
                if (result.Length > 0 && result[^1] == '\r') result.Length--;
                return result.ToString();
            }
        }
    }

    public void Dispose() => _reader.Dispose();
}
