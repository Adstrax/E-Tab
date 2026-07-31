// ReSharper disable IdentifierTypo

namespace ETab.WinAPI;

public delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);