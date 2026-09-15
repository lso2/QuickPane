using System;
using System.Windows.Forms;
using System.Windows.Input;
using NM = QuickPane.Interop.NativeMethods;

namespace QuickPane.Services
{
    /// <summary>
    /// One system-wide shortcut that moves focus into the pane, so a Save dialog can be driven without
    /// the mouse. Registered only when the user turns keyboard navigation on, because a global hotkey
    /// takes a combination away from every other program on the machine.
    /// </summary>
    internal sealed class HotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0xB19;

        private readonly NativeWindow _sink;
        private bool _registered;

        public event Action Pressed;

        private sealed class Sink : NativeWindow
        {
            public Action<Message> OnMessage;
            protected override void WndProc(ref Message m)
            {
                var h = OnMessage;
                if (h != null) h(m);
                base.WndProc(ref m);
            }
        }

        public HotkeyService()
        {
            var sink = new Sink();
            sink.CreateHandle(new CreateParams());
            sink.OnMessage = m =>
            {
                if (m.Msg != WM_HOTKEY || m.WParam.ToInt32() != HotkeyId) return;
                var h = Pressed;
                if (h != null) h();
            };
            _sink = sink;
        }

        /// <summary>Apply the current settings: register, re-register or drop the shortcut.</summary>
        public void Apply(bool enabled, string spec)
        {
            Unregister();
            if (!enabled) return;

            uint mods; uint vk;
            if (!TryParse(spec, out mods, out vk))
            {
                Log.Event("shortcut", "the keyboard navigation shortcut \"" + spec +
                    "\" could not be read, so keyboard navigation is off.");
                return;
            }

            _registered = NM.RegisterHotKey(_sink.Handle, HotkeyId, mods, vk);
            if (!_registered)
                Log.Event("shortcut", "the keyboard navigation shortcut \"" + spec +
                    "\" is already taken by another program, so it was not registered.");
            else
                Log.Info("keyboard navigation shortcut registered as " + spec + ".");
        }

        private void Unregister()
        {
            if (!_registered) return;
            try { NM.UnregisterHotKey(_sink.Handle, HotkeyId); } catch { }
            _registered = false;
        }

        /// <summary>Read "ctrl+shift+Q" into the modifier flags and virtual key RegisterHotKey wants.</summary>
        public static bool TryParse(string spec, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            foreach (var raw in spec.Split('+'))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= 0x0002; continue;
                    case "alt": mods |= 0x0001; continue;
                    case "shift": mods |= 0x0004; continue;
                    case "win": case "windows": mods |= 0x0008; continue;
                }

                Key key;
                if (!Enum.TryParse(part, true, out key)) return false;
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            }
            return vk != 0 && mods != 0;   // a bare key would swallow that key everywhere
        }

        public void Dispose()
        {
            Unregister();
            try { _sink.DestroyHandle(); } catch { }
        }
    }
}
