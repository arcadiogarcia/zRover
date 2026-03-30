using System.Collections.Generic;
using System.Linq;

namespace zRover.Core.Tools.InputInjection
{
    /// <summary>
    /// Tracks active pointer state across multiple tool calls (pointer_down / pointer_move /
    /// pointer_up). Contains all validation and state-mutation logic with no platform dependencies.
    /// </summary>
    public class PointerSessionState
    {
        public enum DeviceKind { Touch, Pen }

        public sealed class ActivePointerState
        {
            public int PointerId { get; set; }
            public DeviceKind Device { get; set; }

            /// <summary>Last injected physical-pixel X (injection coordinate).</summary>
            public int LastX { get; set; }
            /// <summary>Last injected physical-pixel Y (injection coordinate).</summary>
            public int LastY { get; set; }

            public double Pressure { get; set; } = 1.0;

            // Touch-specific
            public int Orientation { get; set; }
            public int ContactWidth { get; set; } = 4;
            public int ContactHeight { get; set; } = 4;

            // Pen-specific
            public int TiltX { get; set; }
            public int TiltY { get; set; }
            public double Rotation { get; set; }
            public bool Barrel { get; set; }
            public bool Eraser { get; set; }
        }

        private readonly Dictionary<int, ActivePointerState> _pointers =
            new Dictionary<int, ActivePointerState>();

        private bool _touchSessionActive;

        public IReadOnlyDictionary<int, ActivePointerState> PointerMap => _pointers;

        public int Count => _pointers.Count;

        /// <summary>
        /// True when at least one touch pointer is active, meaning the platform's touch
        /// injection session (InitializeTouchInjection) is open.
        /// </summary>
        public bool TouchSessionActive => _touchSessionActive;

        /// <summary>
        /// Validates a pointer_down operation.
        /// Returns an error string if invalid, or null if the operation is allowed.
        /// </summary>
        public string? ValidateDown(int pointerId, DeviceKind device)
        {
            if (_pointers.ContainsKey(pointerId))
                return $"Pointer {pointerId} is already active. Call pointer_up first or use a different pointerId.";

            if (device == DeviceKind.Pen && _pointers.Values.Any(p => p.Device == DeviceKind.Pen))
                return "Only one pen pointer can be active at a time.";

            return null;
        }

        /// <summary>
        /// Validates a pointer_move operation.
        /// Returns an error string if invalid, or null if the operation is allowed.
        /// </summary>
        public string? ValidateMove(int pointerId)
        {
            if (!_pointers.ContainsKey(pointerId))
                return $"Pointer {pointerId} is not active. Call pointer_down first.";

            return null;
        }

        /// <summary>
        /// Validates a pointer_up operation.
        /// Returns an error string if invalid, or null if the operation is allowed.
        /// </summary>
        public string? ValidateUp(int pointerId)
        {
            if (!_pointers.ContainsKey(pointerId))
                return $"Pointer {pointerId} is not active.";

            return null;
        }

        public bool TryGetPointer(int pointerId, out ActivePointerState? pointer) =>
            _pointers.TryGetValue(pointerId, out pointer);

        /// <summary>Records a new pointer as active after a successful pointer_down injection.</summary>
        public void RecordDown(ActivePointerState state)
        {
            _pointers[state.PointerId] = state;
            if (state.Device == DeviceKind.Touch)
                _touchSessionActive = true;
        }

        /// <summary>Updates the last-known position and optional properties after a successful pointer_move injection.</summary>
        public void UpdatePosition(int pointerId, int x, int y,
            double? pressure = null, int? tiltX = null, int? tiltY = null, double? rotation = null)
        {
            if (!_pointers.TryGetValue(pointerId, out var s)) return;
            s.LastX = x;
            s.LastY = y;
            if (pressure.HasValue) s.Pressure = pressure.Value;
            if (tiltX.HasValue) s.TiltX = tiltX.Value;
            if (tiltY.HasValue) s.TiltY = tiltY.Value;
            if (rotation.HasValue) s.Rotation = rotation.Value;
        }

        /// <summary>
        /// Records a pointer release after a successful pointer_up injection.
        /// Returns true when the removed pointer was the last active touch pointer —
        /// the caller should then tear down the platform touch injection session.
        /// </summary>
        public bool RecordUp(int pointerId)
        {
            if (!_pointers.TryGetValue(pointerId, out var removed)) return false;

            _pointers.Remove(pointerId);

            bool wasLastTouch = removed.Device == DeviceKind.Touch
                && !_pointers.Values.Any(p => p.Device == DeviceKind.Touch);

            if (wasLastTouch)
                _touchSessionActive = false;

            return wasLastTouch;
        }

        /// <summary>Forcibly clears all tracked state (used during emergency cleanup).</summary>
        public void Clear()
        {
            _pointers.Clear();
            _touchSessionActive = false;
        }
    }
}
