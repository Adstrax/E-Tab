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
}
