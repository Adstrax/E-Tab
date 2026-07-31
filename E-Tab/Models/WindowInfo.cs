using System.Diagnostics;

namespace ETab.Models;

public sealed class WindowInfo
{
    public long CreatedAt { get; } = Stopwatch.GetTimestamp();
    public nint WindowHandle { get; set; }
    public nint TabHandle { get; set; }
    public string? Location { get; set; }
}
