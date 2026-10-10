using System;

namespace ETab.Hooks;

public interface IHook : IDisposable
{
    public bool IsHookActive { get; }
    public void StartHook();
    public void StopHook();
}