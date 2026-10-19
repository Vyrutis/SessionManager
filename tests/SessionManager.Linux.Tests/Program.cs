using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using SessionManager;
using SessionManager.Terminal;

// Separate data folder so the tests never touch real sessions.
var dataDir = Path.Combine(Path.GetTempPath(), "sm-linux-tests-" + Guid.NewGuid().ToString("N")[..8]);
Environment.SetEnvironmentVariable("SESSIONMANAGER_DATA", dataDir);
var shots = args.Length > 0 ? args[0] : null;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  -> " + detail)}");
}

TerminalTests.Run(Check);
ImportTests.Run(Check);
FreeRdpTests.Run(Check);
VncTests.Run(Check);

// ── Windows rendered off-screen (Skia, no display needed) ──
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();
RenderTests.Run(Check, shots);

try { Directory.Delete(dataDir, true); } catch { }
Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
