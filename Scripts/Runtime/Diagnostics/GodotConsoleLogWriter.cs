using System.IO;
using System.Text;
using Godot;

namespace Celeste.Godot.Runtime.Diagnostics
{
    // Per-instance source logger sink. Godot owns its normal engine log destination.
    public sealed class GodotConsoleLogWriter : TextWriter
    {
        private readonly object sync = new();
        private readonly StringBuilder pending = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (sync)
            {
                if (value == '\n') WriteLine();
                else if (value != '\r') pending.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (sync)
            {
                if (value == null) return;
                foreach (char character in value) Write(character);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (sync)
            {
                if (pending.Length == 0) GD.Print(value ?? string.Empty);
                else
                {
                    pending.Append(value);
                    GD.Print(pending.ToString());
                    pending.Clear();
                }
            }
        }

        public override void WriteLine() => WriteLine(string.Empty);

        public override void Flush()
        {
            lock (sync)
            {
                if (pending.Length != 0) WriteLine();
            }
        }
    }
}
