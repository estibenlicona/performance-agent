using System.Text;

namespace PerfAgent.Adapters.Process;

/// <summary>Acumulador de texto con límite: conserva el principio y avisa si se truncó.</summary>
internal sealed class BoundedText(int maxChars)
{
    private readonly StringBuilder _builder = new();
    private readonly Lock _lock = new();

    public bool Truncated { get; private set; }

    public void AppendLine(string line)
    {
        lock (_lock)
        {
            if (_builder.Length + line.Length + 1 > maxChars)
            {
                Truncated = true;
                return;
            }

            _builder.Append(line).Append('\n');
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            return _builder.ToString();
        }
    }
}

/// <summary>Últimas N líneas (para diagnosticar fallos de arranque sin guardar toda la salida en memoria).</summary>
internal sealed class TailBuffer(int maxLines)
{
    private readonly Queue<string> _lines = new();
    private readonly Lock _lock = new();

    public void Add(string line)
    {
        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > maxLines)
            {
                _lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            return _lines.ToList();
        }
    }
}
