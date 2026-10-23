using Celeste.Input.Devices.Keyboard;
using Godot;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Input
{
    internal static class GodotSourceKeyCodes
    {
        internal static SourceKeys ToSource(Key key, KeyLocation location = KeyLocation.Unspecified)
        {
            return key switch
            {
                >= Key.A and <= Key.Z or >= Key.Key0 and <= Key.Key9 => (SourceKeys)(int)key,
                >= Key.F1 and <= Key.F24 => (SourceKeys)(112 + (int)(key - Key.F1)),
                >= Key.Kp0 and <= Key.Kp9 => (SourceKeys)(96 + (int)(key - Key.Kp0)),
                Key.Shift => location == KeyLocation.Right ? SourceKeys.RightShift : SourceKeys.LeftShift,
                Key.Ctrl => location == KeyLocation.Right ? SourceKeys.RightControl : SourceKeys.LeftControl,
                Key.Alt => location == KeyLocation.Right ? SourceKeys.RightAlt : SourceKeys.LeftAlt,
                Key.Meta => location == KeyLocation.Right ? SourceKeys.RightWindows : SourceKeys.LeftWindows,
                Key.Escape => SourceKeys.Escape, Key.Tab or Key.Backtab => SourceKeys.Tab,
                Key.Backspace => SourceKeys.Back, Key.Enter or Key.KpEnter => SourceKeys.Enter,
                Key.Insert => SourceKeys.Insert, Key.Delete => SourceKeys.Delete, Key.Pause => SourceKeys.Pause,
                Key.Print => SourceKeys.PrintScreen, Key.Home => SourceKeys.Home, Key.End => SourceKeys.End,
                Key.Left => SourceKeys.Left, Key.Right => SourceKeys.Right, Key.Up => SourceKeys.Up,
                Key.Down => SourceKeys.Down,
                Key.Pageup => SourceKeys.PageUp, Key.Pagedown => SourceKeys.PageDown, Key.Space => SourceKeys.Space,
                Key.Capslock => SourceKeys.CapsLock, Key.Numlock => SourceKeys.NumLock,
                Key.Scrolllock => SourceKeys.Scroll,
                Key.KpMultiply => SourceKeys.Multiply, Key.KpDivide => SourceKeys.Divide, Key.KpAdd => SourceKeys.Add,
                Key.KpSubtract => SourceKeys.Subtract, Key.KpPeriod => SourceKeys.Decimal,
                Key.Semicolon => SourceKeys.OemSemicolon, Key.Equal => SourceKeys.OemPlus,
                Key.Comma => SourceKeys.OemComma, Key.Minus => SourceKeys.OemMinus, Key.Period => SourceKeys.OemPeriod,
                Key.Slash => SourceKeys.OemQuestion, Key.Quoteleft => SourceKeys.OemTilde,
                Key.Bracketleft or Key.Braceleft => SourceKeys.OemOpenBrackets,
                Key.Bracketright or Key.Braceright => SourceKeys.OemCloseBrackets,
                Key.Backslash => SourceKeys.OemPipe, Key.Apostrophe => SourceKeys.OemQuotes,
                Key.Menu => SourceKeys.Apps,
                _ => SourceKeys.None,
            };
        }

        internal static Key ToGodot(SourceKeys key)
        {
            return key switch
            {
                >= SourceKeys.A and <= SourceKeys.Z or >= SourceKeys.D0 and <= SourceKeys.D9 => (Key)(int)key,
                >= SourceKeys.F1 and <= SourceKeys.F24 => Key.F1 + ((int)key - (int)SourceKeys.F1),
                >= SourceKeys.NumPad0 and <= SourceKeys.NumPad9 => Key.Kp0 + ((int)key - (int)SourceKeys.NumPad0),
                SourceKeys.OemSemicolon => Key.Semicolon, SourceKeys.OemPlus => Key.Equal,
                SourceKeys.OemComma => Key.Comma,
                SourceKeys.OemMinus => Key.Minus, SourceKeys.OemPeriod => Key.Period,
                SourceKeys.OemQuestion => Key.Slash,
                SourceKeys.OemTilde => Key.Quoteleft, SourceKeys.OemOpenBrackets => Key.Bracketleft,
                SourceKeys.OemCloseBrackets => Key.Bracketright, SourceKeys.OemPipe => Key.Backslash,
                SourceKeys.OemQuotes => Key.Apostrophe,
                _ => Key.None,
            };
        }
    }
}
