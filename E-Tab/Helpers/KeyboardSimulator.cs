using System.Runtime.InteropServices;
using ETab.WinAPI;

namespace ETab.Helpers;

public static class KeyboardSimulator
{
    public static void SendKeyPress(VirtualKey keyCode)
    {
        var inputs = new[] { CreateKeyDown(keyCode), CreateKeyUp(keyCode) };
        WinApi.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendKeyChord(VirtualKey modifier, VirtualKey key)
    {
        var inputs = new[]
        {
            CreateKeyDown(modifier),
            CreateKeyDown(key),
            CreateKeyUp(key),
            CreateKeyUp(modifier),
        };
        WinApi.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = CreateUnicodeInput(text[i], KeyEventFlags.Unicode);
            inputs[i * 2 + 1] = CreateUnicodeInput(text[i], KeyEventFlags.Unicode | KeyEventFlags.KeyUp);
        }

        WinApi.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT CreateKeyDown(VirtualKey keyCode)
    {
        return CreateInput(keyCode, KeyEventFlags.KeyDown);
    }

    private static INPUT CreateKeyUp(VirtualKey keyCode)
    {
        return CreateInput(keyCode, KeyEventFlags.KeyUp);
    }

    private static INPUT CreateInput(VirtualKey keyCode, KeyEventFlags flags)
    {
        var scan = (ushort)(WinApi.MapVirtualKey((uint)keyCode, 0) & 0xFFU);
        return new INPUT
        {
            Type = InputType.Keyboard,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    wVk = keyCode,
                    wScan = scan,
                    dwFlags = flags,
                    dwExtraInfo = 0
                }
            }
        };
    }

    private static INPUT CreateUnicodeInput(char character, KeyEventFlags flags)
    {
        return new INPUT
        {
            Type = InputType.Keyboard,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = character,
                    dwFlags = flags,
                    dwExtraInfo = 0
                }
            }
        };
    }
}
