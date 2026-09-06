using System.Reflection;
using System.Runtime.InteropServices;
using BattlegroundsVisionAgent.Vision.Capture;

namespace BattlegroundsVisionAgent.Vision.Tests;

public class CaptureNativeBindingsTests
{
    [Fact]
    public void CaptureImportsResolveToRealWindowsExports()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var method in typeof(WindowsFrameSource).GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
        {
            var import = method.GetCustomAttribute<DllImportAttribute>();
            if (import is null) continue;
            var library = NativeLibrary.Load(import.Value);
            try { Assert.True(NativeLibrary.TryGetExport(library, import.EntryPoint ?? method.Name, out _), method.Name); }
            finally { NativeLibrary.Free(library); }
        }
    }
}
