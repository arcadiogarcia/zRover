using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Rover.FullTrust.McpServer;

/// <summary>
/// Provides real input injection via Win32 SendInput API.
/// This bypasses the UWP InputInjector which fails on many configurations.
/// Runs in the FullTrust process which has full Win32 access.
/// </summary>
internal sealed class Win32InputInjector
{
    #region P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    #endregion

    private readonly string _windowTitle;

    public Win32InputInjector(string windowTitle = "Rover Sample")
    {
        _windowTitle = windowTitle;
    }

    /// <summary>
    /// Brings the target UWP window to the foreground via SetForegroundWindow.
    /// Returns true if the window was found and the call succeeded.
    /// </summary>
    public bool BringToForeground()
    {
        IntPtr hwnd = FindWindow("ApplicationFrameWindow", _windowTitle);
        if (hwnd == IntPtr.Zero)
            hwnd = FindWindow(null, _windowTitle);
        if (hwnd == IntPtr.Zero)
            return false;
        return SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Finds the target UWP window and returns its screen-pixel bounds.
    /// Returns null if the window is not found.
    /// </summary>
    private RECT? GetWindowBounds()
    {
        // UWP apps use "ApplicationFrameWindow" as their window class
        IntPtr hwnd = FindWindow("ApplicationFrameWindow", _windowTitle);
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("[Win32Input] Window not found, trying without class name...");
            hwnd = FindWindow(null, _windowTitle);
        }

        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[Win32Input] Could not find window '{_windowTitle}'");
            return null;
        }

        if (!GetWindowRect(hwnd, out RECT rect))
        {
            Console.Error.WriteLine("[Win32Input] GetWindowRect failed");
            return null;
        }

        return rect;
    }

    /// <summary>
    /// Converts normalized coordinates (0-1) to absolute screen coordinates
    /// suitable for SendInput (0-65535 range).
    /// </summary>
    private (int absX, int absY)? NormalizedToAbsolute(double normX, double normY, string coordinateSpace)
    {
        if (coordinateSpace == "absolute")
        {
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            int absX = (int)(normX / screenW * 65535);
            int absY = (int)(normY / screenH * 65535);
            return (absX, absY);
        }

        var bounds = GetWindowBounds();
        if (bounds == null) return null;
        var rect = bounds.Value;

        int winW = rect.Right - rect.Left;
        int winH = rect.Bottom - rect.Top;

        double screenX, screenY;
        if (coordinateSpace == "client")
        {
            screenX = rect.Left + normX;
            screenY = rect.Top + normY;
        }
        else // normalized (default)
        {
            screenX = rect.Left + normX * winW;
            screenY = rect.Top + normY * winH;
        }

        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);

        return ((int)(screenX / sw * 65535), (int)(screenY / sh * 65535));
    }

    /// <summary>Injects a tap (mouse click) at the specified position.</summary>
    public bool InjectTap(double x, double y, string coordinateSpace = "normalized")
    {
        var abs = NormalizedToAbsolute(x, y, coordinateSpace);
        if (abs == null) return false;

        var (absX, absY) = abs.Value;

        var inputs = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE
                }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE
                }
            }
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Console.Error.WriteLine($"[Win32Input] Tap at ({x:F3},{y:F3}) → screen abs ({absX},{absY}), sent={sent}");
        return sent == (uint)inputs.Length;
    }

    /// <summary>Injects a drag gesture along a path of points.</summary>
    public async Task<bool> InjectDragPath(
        List<(double x, double y)> points,
        int durationMs = 300,
        string coordinateSpace = "normalized")
    {
        if (points.Count < 2) return false;

        // Convert all points to absolute coordinates
        var absPoints = new List<(int absX, int absY)>();
        foreach (var (px, py) in points)
        {
            var abs = NormalizedToAbsolute(px, py, coordinateSpace);
            if (abs == null) return false;
            absPoints.Add(abs.Value);
        }

        // Interpolate between waypoints for smoother drag
        int totalSteps = Math.Max(20, durationMs / 10);
        var interpolated = InterpolatePoints(absPoints, totalSteps);
        int delayMs = Math.Max(1, durationMs / interpolated.Count);

        // Move to start position
        var moveToStart = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = interpolated[0].absX,
                    dy = interpolated[0].absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            }
        };
        SendInput(1, moveToStart, Marshal.SizeOf<INPUT>());
        await Task.Delay(10);

        // Press down at start
        var downInput = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = interpolated[0].absX,
                    dy = interpolated[0].absY,
                    dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE
                }
            }
        };
        SendInput(1, downInput, Marshal.SizeOf<INPUT>());

        // Move through intermediate points
        for (int i = 1; i < interpolated.Count - 1; i++)
        {
            var moveInput = new INPUT[]
            {
                new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT
                    {
                        dx = interpolated[i].absX,
                        dy = interpolated[i].absY,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                    }
                }
            };
            SendInput(1, moveInput, Marshal.SizeOf<INPUT>());
            await Task.Delay(delayMs);
        }

        // Release at end point
        var last = interpolated[interpolated.Count - 1];
        var upInput = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = last.absX,
                    dy = last.absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = last.absX,
                    dy = last.absY,
                    dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE
                }
            }
        };
        SendInput((uint)upInput.Length, upInput, Marshal.SizeOf<INPUT>());

        Console.Error.WriteLine($"[Win32Input] Drag {points.Count} waypoints, {interpolated.Count} steps, {durationMs}ms");
        return true;
    }

    private static List<(int absX, int absY)> InterpolatePoints(
        List<(int absX, int absY)> waypoints, int totalSteps)
    {
        if (waypoints.Count == 2)
        {
            // Simple linear interpolation between two points
            var result = new List<(int, int)>();
            var (x0, y0) = waypoints[0];
            var (x1, y1) = waypoints[1];
            for (int i = 0; i <= totalSteps; i++)
            {
                double t = (double)i / totalSteps;
                result.Add(((int)(x0 + t * (x1 - x0)), (int)(y0 + t * (y1 - y0))));
            }
            return result;
        }

        // Multi-waypoint: distribute steps proportionally across segments
        var allPoints = new List<(int, int)>();
        double totalDist = 0;
        for (int i = 1; i < waypoints.Count; i++)
        {
            double dx = waypoints[i].absX - waypoints[i - 1].absX;
            double dy = waypoints[i].absY - waypoints[i - 1].absY;
            totalDist += Math.Sqrt(dx * dx + dy * dy);
        }

        double accumulated = 0;
        for (int seg = 0; seg < waypoints.Count - 1; seg++)
        {
            double dx = waypoints[seg + 1].absX - waypoints[seg].absX;
            double dy = waypoints[seg + 1].absY - waypoints[seg].absY;
            double segDist = Math.Sqrt(dx * dx + dy * dy);
            int segSteps = Math.Max(1, (int)(totalSteps * segDist / totalDist));

            int startI = seg == 0 ? 0 : 1; // avoid duplicating junction points
            for (int i = startI; i <= segSteps; i++)
            {
                double t = (double)i / segSteps;
                allPoints.Add((
                    (int)(waypoints[seg].absX + t * (waypoints[seg + 1].absX - waypoints[seg].absX)),
                    (int)(waypoints[seg].absY + t * (waypoints[seg + 1].absY - waypoints[seg].absY))));
            }
            accumulated += segDist;
        }

        return allPoints;
    }
}
