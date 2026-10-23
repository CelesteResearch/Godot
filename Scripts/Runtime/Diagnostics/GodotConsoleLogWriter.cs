using System.IO;
using System.Text;
using Godot;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Diagnostics
{
    // Per-instance source logger sink. Godot owns its normal engine log destination.
    public sealed class GodotConsoleLogWriter : TextWriter
    {
        private readonly object _sync = new();
        private readonly StringBuilder _pending = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_sync)
            {
                if (value == '\n') WriteLine();
                else if (value != '\r') _pending.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_sync)
            {
                if (value == null) return;
                foreach (var character in value) Write(character);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                if (_pending.Length == 0)
                {
                    GD.Print(value ?? string.Empty);
                }
                else
                {
                    _pending.Append(value);
                    GD.Print(_pending.ToString());
                    _pending.Clear();
                }
            }
        }

        public override void WriteLine()
        {
            WriteLine(string.Empty);
        }

        public override void Flush()
        {
            lock (_sync)
            {
                if (_pending.Length != 0) WriteLine();
            }
        }
    }
}
