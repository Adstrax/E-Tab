using ETab.Helpers;

var failures = 0;
var tests = 0;

Run("blank input becomes empty", () =>
    Equal(string.Empty, Helper.NormalizeLocation("  ")));

Run("UNC path keeps its root", () =>
    Equal(@"\\server\share\folder", Helper.NormalizeLocation(@"\\server\share\folder")));

Run("forward-slash UNC path is normalised", () =>
    Equal(@"\\server\share\folder", Helper.NormalizeLocation("//server/share/folder")));

Run("drive root keeps its trailing separator", () =>
    Equal(@"C:\", Helper.NormalizeLocation(@"C:\")));

Run("file URI becomes a local path", () =>
    Equal(@"C:\Temp\folder", Helper.NormalizeLocation("file:///C:/Temp/folder")));

Run("HTTPS URL is preserved", () =>
    Equal(
        "https://example.com/a/b?x=1#part",
        Helper.NormalizeLocation("https://example.com/a/b?x=1#part")));

Run("shell namespace is preserved", () =>
    Equal(
        "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
        Helper.NormalizeLocation("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")));

Run("short shell namespace is expanded", () =>
    Equal("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", Helper.NormalizeLocation("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")));

Run("GUID shorthand becomes a shell path", () =>
    Equal(
        "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
        Helper.NormalizeLocation("{20D04FE0-3AEA-1069-A2D8-08002B30309D}")));

Run("environment variables expand in file paths", () =>
{
    var expected = @"C:\Temp\folder";
    Environment.SetEnvironmentVariable("ETAB_NORMALISE_TEST", @"C:\Temp");
    Equal(expected, Helper.NormalizeLocation(@"%ETAB_NORMALISE_TEST%\folder"));
});

Run("quoted path is accepted", () =>
    Equal(@"C:\Temp\folder", Helper.NormalizeLocation(@"""C:\Temp\folder""")));

Run("web shorthand is preserved", () =>
    Equal("www.example.com", Helper.NormalizeLocation("www.example.com")));

Run("hidden window state can be forgotten", () =>
{
    nint handle = 42;
    Helper.HiddenWindows.TryAdd(handle, null);
    Helper.ForgetHiddenWindow(handle);
    if (Helper.HiddenWindows.ContainsKey(handle))
        throw new InvalidOperationException("Hidden window state was not removed.");
});

Console.WriteLine($"{tests - failures}/{tests} tests passed.");
return failures == 0 ? 0 : 1;

void Run(string name, Action test)
{
    tests++;
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

static void Equal(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}
