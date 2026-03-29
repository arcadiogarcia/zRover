# zRover Input Injection Expansion Plan

> **Validated against SDK:** `Windows.winmd` from Windows SDK 10.0.26100.0.
> All type shapes, enum values, and property names below are verified against the actual WinMD metadata.

## Goal

Expand zRover from the current two MCP tools (`inject_tap`, `inject_drag_path`) to comprehensive coverage of **keyboard**, **mouse** (expanded), **multitouch**, **pen**, and **gamepad** input — all injected from the UWP app via `Windows.UI.Input.Preview.Injection.InputInjector`.

**Scope:** UWP implementation only. Win32 `SendInput` fallbacks are out of scope for now and documented as gaps at the bottom.

---

## Current State

| Tool | Touch | Mouse | Notes |
|------|-------|-------|-------|
| `inject_tap` | ✅ | ✅ | Single tap/click at (x,y). Preview screenshot with crosshair. |
| `inject_drag_path` | ✅ | ✅ | Multi-waypoint drag with smooth interpolation. Preview with path visualization. |

**Architecture (preserved for all new tools):**
1. **zRover.Core** — Request/Response DTOs + JSON schemas
2. **zRover.Uwp / InputInjectionCapability** — UWP `InputInjector` implementation + XAML automation fallback + preview screenshots
3. **zRover.FullTrust.McpServer / Program.cs** — Proxy registration (pass-through to UWP for now)

---

## UWP SDK API Reference (verified from WinMD)

These are the exact types and members available. All tool designs below map directly to these.

### InputInjector Methods

```
void InjectTouchInput(IEnumerable<InjectedInputTouchInfo>)
void InitializeTouchInjection(InjectedInputVisualizationMode)
void UninitializeTouchInjection()
void InjectMouseInput(IEnumerable<InjectedInputMouseInfo>)
void InjectKeyboardInput(IEnumerable<InjectedInputKeyboardInfo>)
void InjectPenInput(InjectedInputPenInfo)
void InjectGamepadInput(InjectedInputGamepadInfo)
void InjectShortcut(InjectedInputShortcut)          // system shortcuts only (Win+D etc)
static InputInjector TryCreate()
```

### InjectedInputTouchInfo [Class]

| Property | Type | Notes |
|----------|------|-------|
| `PointerInfo` | `InjectedInputPointerInfo` | Position + pointer ID + options |
| `Contact` | `InjectedInputRectangle` | Touch contact area (Left/Top/Right/Bottom offsets from pixel location) |
| `Pressure` | `double` | 0.0–1.0 |
| `TouchParameters` | `InjectedInputTouchParameters` | Flags: which optional fields are set |
| `Orientation` | `int` | 0–359 degrees, rotation of the contact area |

### InjectedInputPointerInfo [Struct]

| Field | Type | Notes |
|-------|------|-------|
| `PointerId` | `uint` | Unique per simultaneous pointer (1-based) |
| `PointerOptions` | `InjectedInputPointerOptions` | Flags controlling pointer state |
| `PixelLocation` | `InjectedInputPoint` | Position in **raw screen pixels** |
| `TimeOffsetInMilliseconds` | `uint` | Relative timestamp |
| `PerformanceCount` | `ulong` | High-res timer value |

### InjectedInputPointerOptions [Flags Enum]

| Value | Name | Hex | Usage |
|-------|------|-----|-------|
| 0 | `None` | 0x0 | |
| 1 | `New` | 0x1 | First event for this pointer (used with PointerDown) |
| 2 | `InRange` | 0x2 | Pointer detected but not touching (pen hover) |
| 4 | `InContact` | 0x4 | Pointer is touching surface |
| 16 | `FirstButton` | 0x10 | Primary button pressed (pen tip / touch) |
| 32 | `SecondButton` | 0x20 | Secondary button (pen barrel) |
| 8192 | `Primary` | 0x2000 | Primary pointer in multi-pointer scenario |
| 16384 | `Confidence` | 0x4000 | High-confidence contact (not accidental palm) |
| 32768 | `Canceled` | 0x8000 | Pointer was canceled |
| 65536 | `PointerDown` | 0x10000 | Start of contact |
| 131072 | `Update` | 0x20000 | Movement update |
| 262144 | `PointerUp` | 0x40000 | End of contact |
| 2097152 | `CaptureChanged` | 0x200000 | Capture transferred |

### InjectedInputTouchParameters [Flags Enum]

| Value | Name | Notes |
|-------|------|-------|
| 0 | `None` | |
| 1 | `Contact` | Contact rectangle is set |
| 2 | `Orientation` | Orientation is set |
| 4 | `Pressure` | Pressure is set |

### InjectedInputPoint [Struct]

| Field | Type |
|-------|------|
| `PositionX` | `int` |
| `PositionY` | `int` |

### InjectedInputRectangle [Struct]

| Field | Type | Notes |
|-------|------|-------|
| `Left` | `int` | Offset left of pixel location |
| `Top` | `int` | Offset above pixel location |
| `Right` | `int` | Offset right of pixel location |
| `Bottom` | `int` | Offset below pixel location |

### InjectedInputPenInfo [Class]

| Property | Type | Notes |
|----------|------|-------|
| `PointerInfo` | `InjectedInputPointerInfo` | Same as touch — raw screen pixels |
| `Pressure` | `double` | 0.0–1.0 |
| `TiltX` | `int` | -90 to 90 degrees |
| `TiltY` | `int` | -90 to 90 degrees |
| `Rotation` | `double` | 0.0–359.0 degrees |
| `PenButtons` | `InjectedInputPenButtons` | Flags |
| `PenParameters` | `InjectedInputPenParameters` | Flags: which optional fields are set |

### InjectedInputPenParameters [Flags Enum]

| Value | Name |
|-------|------|
| 0 | `None` |
| 1 | `Pressure` |
| 2 | `Rotation` |
| 4 | `TiltX` |
| 8 | `TiltY` |

### InjectedInputPenButtons [Flags Enum]

| Value | Name | Notes |
|-------|------|-------|
| 0 | `None` | |
| 1 | `Barrel` | Side button on pen (typically right-click) |
| 2 | `Inverted` | Pen is flipped (eraser end detected) |
| 4 | `Eraser` | Eraser function active |

**Note:** `Inverted` and `Eraser` are separate flags. `Inverted` means the hardware detects the pen is flipped. `Eraser` means eraser function is active. Typically you set both together for eraser mode, but `Eraser` alone can represent an eraser button without inverting.

### InjectedInputKeyboardInfo [Class]

| Property | Type | Notes |
|----------|------|-------|
| `VirtualKey` | `ushort` | Windows virtual key code (VK_xxx) |
| `ScanCode` | `ushort` | Hardware scan code (or Unicode char value when `Unicode` flag is set) |
| `KeyOptions` | `InjectedInputKeyOptions` | Flags |

### InjectedInputKeyOptions [Flags Enum]

| Value | Name | Notes |
|-------|------|-------|
| 0 | `None` | Key-down event |
| 1 | `ExtendedKey` | Extended key (e.g., right Alt, right Ctrl, numpad Enter) |
| 2 | `KeyUp` | Key-up event |
| 4 | `Unicode` | `ScanCode` is a Unicode character, `VirtualKey` is ignored |
| 8 | `ScanCode` | `ScanCode` is a hardware scan code, `VirtualKey` is ignored |

### InjectedInputMouseInfo [Class]

| Property | Type | Notes |
|----------|------|-------|
| `DeltaX` | `int` | X movement or absolute position (0-65535 when `Absolute`) |
| `DeltaY` | `int` | Y movement or absolute position |
| `MouseOptions` | `InjectedInputMouseOptions` | Flags |
| `MouseData` | `uint` | Wheel delta (multiples of 120) or X button number (1 or 2) |
| `TimeOffsetInMilliseconds` | `uint` | Relative timestamp |

### InjectedInputMouseOptions [Flags Enum]

| Value | Name | Notes |
|-------|------|-------|
| 0 | `None` | |
| 1 | `Move` | Cursor movement |
| 2 | `LeftDown` | |
| 4 | `LeftUp` | |
| 8 | `RightDown` | |
| 16 | `RightUp` | |
| 32 | `MiddleDown` | |
| 64 | `MiddleUp` | |
| 128 | `XDown` | Extended button down (which button: MouseData = 1 or 2) |
| 256 | `XUp` | Extended button up |
| 2048 | `Wheel` | Vertical scroll (MouseData = delta, signed) |
| 4096 | `HWheel` | Horizontal scroll |
| 8192 | `MoveNoCoalesce` | Prevent coalescing of move events |
| 16384 | `VirtualDesk` | Map to entire virtual desktop |
| 32768 | `Absolute` | DeltaX/Y are absolutes (0-65535 range) |

### InjectedInputGamepadInfo [Class]

| Property | Type | Range |
|----------|------|-------|
| `Buttons` | `GamepadButtons` | Flags enum |
| `LeftThumbstickX` | `double` | -1.0 to 1.0 |
| `LeftThumbstickY` | `double` | -1.0 to 1.0 |
| `RightThumbstickX` | `double` | -1.0 to 1.0 |
| `RightThumbstickY` | `double` | -1.0 to 1.0 |
| `LeftTrigger` | `double` | 0.0 to 1.0 |
| `RightTrigger` | `double` | 0.0 to 1.0 |

### GamepadButtons [Flags Enum]

| Value | Name |
|-------|------|
| 0 | `None` |
| 1 | `Menu` |
| 2 | `View` |
| 4 | `A` |
| 8 | `B` |
| 16 | `X` |
| 32 | `Y` |
| 64 | `DPadUp` |
| 128 | `DPadDown` |
| 256 | `DPadLeft` |
| 512 | `DPadRight` |
| 1024 | `LeftShoulder` |
| 2048 | `RightShoulder` |
| 4096 | `LeftThumbstick` |
| 8192 | `RightThumbstick` |
| 16384 | `Paddle1` |
| 32768 | `Paddle2` |
| 65536 | `Paddle3` |
| 131072 | `Paddle4` |

---

## Proposed MCP Tools

### Phase 1: Keyboard

#### `inject_key_press`

Single key press-and-release, or a chord (modifier + key). Most frequently needed input for text fields, shortcuts, navigation.

```jsonc
// Request
{
  "key": "A",                     // Virtual key name or single character (see key mapping table below)
  "modifiers": ["ctrl", "shift"], // Optional: "ctrl", "shift", "alt", "win"
  "holdDurationMs": 0             // Optional: ms to hold before release (0 = instant tap)
}
// Response
{
  "success": true,
  "key": "A",
  "modifiers": ["ctrl", "shift"],
  "timestamp": "..."
}
```

**UWP implementation:** `InputInjector.InjectKeyboardInput(IEnumerable<InjectedInputKeyboardInfo>)`

Each `InjectedInputKeyboardInfo` has:
- `VirtualKey` (ushort) — Windows VK code
- `ScanCode` (ushort) — hardware scan code (or Unicode char when `KeyOptions.Unicode` is set)
- `KeyOptions` — `None` (key-down), `KeyUp`, `Unicode`, `ScanCode`, `ExtendedKey`

**Sequence for Ctrl+Shift+A:**
1. Inject `{VirtualKey=0xA2 (VK_LCONTROL), KeyOptions=None}` — Ctrl down
2. Inject `{VirtualKey=0xA0 (VK_LSHIFT), KeyOptions=None}` — Shift down
3. Inject `{VirtualKey=0x41 (VK_A), KeyOptions=None}` — A down
4. Optional: `Task.Delay(holdDurationMs)`
5. Inject `{VirtualKey=0x41, KeyOptions=KeyUp}` — A up
6. Inject `{VirtualKey=0xA0, KeyOptions=KeyUp}` — Shift up
7. Inject `{VirtualKey=0xA2, KeyOptions=KeyUp}` — Ctrl up

**Key mapping:** Accept friendly names mapped to VK codes:

| Name | VK | Name | VK | Name | VK |
|------|-----|------|-----|------|-----|
| `Enter` | 0x0D | `Tab` | 0x09 | `Escape` | 0x1B |
| `Backspace` | 0x08 | `Space` | 0x20 | `Delete` | 0x2E |
| `Left` | 0x25 | `Up` | 0x26 | `Right` | 0x27 |
| `Down` | 0x28 | `Home` | 0x24 | `End` | 0x23 |
| `PageUp` | 0x21 | `PageDown` | 0x22 | `Insert` | 0x2D |
| `F1`–`F12` | 0x70–0x7B | `A`–`Z` | 0x41–0x5A | `0`–`9` | 0x30–0x39 |

Single characters: map to VK code + implicit shift if uppercase.

**No preview screenshot** — keyboard input is position-independent.

#### `inject_text`

Types a string of text character by character using Unicode injection. Supports full Unicode (accented chars, emoji, CJK) — bypasses keyboard layout.

```jsonc
// Request
{
  "text": "Hello, world! 🎉",
  "delayBetweenKeysMs": 30    // Optional: inter-character delay (default 30ms)
}
// Response
{
  "success": true,
  "characterCount": 16,
  "timestamp": "..."
}
```

**UWP implementation:** For each character, inject two `InjectedInputKeyboardInfo`:
1. `{ScanCode=charUtf16, KeyOptions=Unicode}` — char down
2. `{ScanCode=charUtf16, KeyOptions=Unicode|KeyUp}` — char up

For surrogate pairs (emoji, etc.), inject both the high and low surrogates as separate events — the OS reassembles them.

**No preview screenshot** — text input is position-independent.

---

### Phase 2: Mouse (Expand Existing)

The current `inject_tap` supports left-click only. Expand mouse to cover all buttons, scroll, and hover.

#### Expand `inject_tap` — add `button` and `clickCount`

Backward compatible: existing calls without these params work identically.

```jsonc
{
  "x": 0.5, "y": 0.5,
  "coordinateSpace": "normalized",
  "device": "mouse",
  "button": "left",            // NEW: "left" (default), "right", "middle", "x1", "x2"
  "clickCount": 1,             // NEW: 1 (default), 2 = double-click, 3 = triple-click
  "dryRun": false
}
```

**UWP mapping to `InjectedInputMouseOptions`:**

| `button` | Down flag | Up flag | `MouseData` |
|----------|-----------|---------|-------------|
| `"left"` | `LeftDown` (2) | `LeftUp` (4) | 0 |
| `"right"` | `RightDown` (8) | `RightUp` (16) | 0 |
| `"middle"` | `MiddleDown` (32) | `MiddleUp` (64) | 0 |
| `"x1"` | `XDown` (128) | `XUp` (256) | 1 |
| `"x2"` | `XDown` (128) | `XUp` (256) | 2 |

For `clickCount > 1`: inject down-up pairs with ~60ms gap between clicks (matching Windows double-click timing).

#### `inject_mouse_scroll`

Scroll wheel at a position.

```jsonc
// Request
{
  "x": 0.5, "y": 0.5,
  "coordinateSpace": "normalized",
  "deltaY": -3,               // Vertical: positive = scroll up, negative = scroll down (in "notches")
  "deltaX": 0,                // Horizontal: positive = right, negative = left (optional)
  "dryRun": false
}
// Response
{
  "success": true,
  "resolvedCoordinates": { "x": 500, "y": 400 },
  "deltaY": -3,
  "deltaX": 0,
  "previewScreenshotPath": "...",
  "timestamp": "..."
}
```

**UWP implementation:** Two `InjectMouseInput` calls:
1. Move: `{DeltaX=absX, DeltaY=absY, MouseOptions=Move|Absolute}` — position cursor
2. Scroll: `{MouseData=delta*120, MouseOptions=Wheel}` for vertical, or `{MouseData=delta*120, MouseOptions=HWheel}` for horizontal

`MouseData` is `uint` but the scroll delta is signed. Windows treats the value as signed within the uint. One scroll notch = 120 (WHEEL_DELTA).

**Preview:** Crosshair at position + arrow annotation showing scroll direction.

#### `inject_mouse_move`

Move cursor without clicking. For hover effects, tooltips, drag preparation.

```jsonc
// Request
{
  "x": 0.5, "y": 0.5,
  "coordinateSpace": "normalized",
  "dryRun": false
}
// Response
{
  "success": true,
  "resolvedCoordinates": { "x": 500, "y": 400 },
  "previewScreenshotPath": "...",
  "timestamp": "..."
}
```

**UWP:** Single `InjectMouseInput` with `{DeltaX=absX, DeltaY=absY, MouseOptions=Move|Absolute}`.

---

### Phase 3: Multitouch

#### `inject_multi_touch`

Simultaneous multi-finger touch gestures (pinch-to-zoom, two-finger rotate, three-finger swipe, etc.).

```jsonc
// Request
{
  "pointers": [
    {
      "id": 1,                              // PointerId — must be unique per finger, 1-based, max 10
      "path": [                             // Waypoints over time
        { "x": 0.4, "y": 0.5 },            // Start position
        { "x": 0.3, "y": 0.5 }             // End position
      ],
      "pressure": 1.0,                      // Optional: 0.0–1.0 (default 1.0)
      "orientation": 0,                     // Optional: 0–359 contact angle (default 0)
      "contactWidth": 2,                    // Optional: half-width of contact rectangle in raw pixels (default 4)
      "contactHeight": 2                    // Optional: half-height (default 4)
    },
    {
      "id": 2,
      "path": [
        { "x": 0.6, "y": 0.5 },
        { "x": 0.7, "y": 0.5 }
      ]
    }
  ],
  "durationMs": 400,                        // Total gesture duration
  "coordinateSpace": "normalized",
  "dryRun": false
}
// Response
{
  "success": true,
  "pointerCount": 2,
  "durationMs": 400,
  "resolvedPaths": {                        // Per-pointer resolved DIP paths
    "1": [{ "x": 400, "y": 500 }, { "x": 300, "y": 500 }],
    "2": [{ "x": 600, "y": 500 }, { "x": 700, "y": 500 }]
  },
  "previewScreenshotPath": "...",
  "timestamp": "..."
}
```

**How it maps to UWP `InjectTouchInput`:**

The key insight: `InjectTouchInput` accepts `IEnumerable<InjectedInputTouchInfo>`. **Multiple items in a single call represent simultaneous touch contacts in the same input frame.** Each item has a unique `PointerId`. This is exactly how Windows multitouch input works.

**Injection sequence (2-finger pinch example):**

```
Frame 0 (t=0): InitializeTouchInjection(Default)
                InjectTouchInput([
                  { PointerId=1, PointerOptions=PointerDown|InContact|New, PixelLocation=start1, Pressure, Contact },
                  { PointerId=2, PointerOptions=PointerDown|InContact|New, PixelLocation=start2, Pressure, Contact }
                ])

Frame 1..N (interpolated):
                InjectTouchInput([
                  { PointerId=1, PointerOptions=InContact|Update,  PixelLocation=interp1[i] },
                  { PointerId=2, PointerOptions=InContact|Update,  PixelLocation=interp2[i] }
                ])
                Task.Delay(durationMs / frameCount)

Frame N+1:      InjectTouchInput([
                  { PointerId=1, PointerOptions=PointerUp, PixelLocation=end1 },
                  { PointerId=2, PointerOptions=PointerUp, PixelLocation=end2 }
                ])
                UninitializeTouchInjection()
```

**Per-pointer touch info fields:**

| MCP param | Maps to | Default |
|-----------|---------|---------|
| `id` | `PointerInfo.PointerId` | required |
| `path[i].x/y` | `PointerInfo.PixelLocation` (after coord resolve + DPI scale) | required |
| `pressure` | `Pressure` + `TouchParameters |= Pressure` | 1.0 |
| `orientation` | `Orientation` + `TouchParameters |= Orientation` | 0 |
| `contactWidth/Height` | `Contact = {Left=-w, Right=w, Top=-h, Bottom=h}` + `TouchParameters |= Contact` | ±4 |

**Constraints:**
- Max 10 simultaneous pointers (Windows limit)
- All pointers must have the same number of segments once interpolated (they share the same timeline)
- Each pointer's path is independently interpolated to `max(10, durationMs/16)` intermediate frames
- `PointerId` values must be stable across the gesture lifetime — same ID always refers to same finger

**Preview annotation:** Draw each pointer's path in a distinct color:
- Pointer 1: cyan, Pointer 2: magenta, Pointer 3: yellow, Pointer 4: green, etc.
- Start = filled circle, End = diamond, Path = thick line with direction arrows

#### `inject_pinch`

Convenience wrapper for the most common multitouch gesture. Computes two pointer paths automatically.

```jsonc
// Request
{
  "centerX": 0.5,
  "centerY": 0.5,
  "startDistance": 0.3,         // Distance between fingers at start (in coordinate space)
  "endDistance": 0.1,           // Distance at end (< start = zoom out/pinch in, > start = zoom in/pinch out)
  "angle": 0,                  // Axis angle in degrees: 0 = horizontal, 90 = vertical
  "durationMs": 400,
  "coordinateSpace": "normalized",
  "dryRun": false
}
// Response
{
  "success": true,
  "pointer1Start": { "x": 0.35, "y": 0.5 },
  "pointer1End": { "x": 0.45, "y": 0.5 },
  "pointer2Start": { "x": 0.65, "y": 0.5 },
  "pointer2End": { "x": 0.55, "y": 0.5 },
  "previewScreenshotPath": "...",
  "timestamp": "..."
}
```

**Implementation:** Pure math to convert center/distance/angle into two pointer paths, then delegate to the same multitouch injection pipeline:

```
pointer1_start = center - (startDistance/2) * direction(angle)
pointer1_end   = center - (endDistance/2)   * direction(angle)
pointer2_start = center + (startDistance/2) * direction(angle)
pointer2_end   = center + (endDistance/2)   * direction(angle)
```

#### `inject_rotate`

Convenience wrapper for two-finger rotation. Both fingers orbit around a center point at constant distance.

```jsonc
// Request
{
  "centerX": 0.5,
  "centerY": 0.5,
  "distance": 0.2,             // Finger separation (in coordinate space)
  "startAngle": 0,             // Starting angle in degrees (0 = horizontal right)
  "endAngle": 90,              // Ending angle in degrees
  "durationMs": 400,
  "coordinateSpace": "normalized",
  "dryRun": false
}
// Response
{
  "success": true,
  "pointer1Start": { "x": ..., "y": ... },
  "pointer1End": { "x": ..., "y": ... },
  "pointer2Start": { "x": ..., "y": ... },
  "pointer2End": { "x": ..., "y": ... },
  "previewScreenshotPath": "...",
  "timestamp": "..."
}
```

**Implementation:** Compute two pointer paths where each finger traces an arc around the center:

```
pointer1[t] = center + (distance/2) * direction(lerp(startAngle, endAngle, t))
pointer2[t] = center - (distance/2) * direction(lerp(startAngle, endAngle, t))
```

The path is sampled into `max(10, durationMs/16)` frames and delegated to the multitouch injection pipeline.

---

### Phase 4: Pen / Stylus

#### `inject_pen_tap`

Single pen tap with pressure, tilt, rotation, and button state.

```jsonc
// Request
{
  "x": 0.5, "y": 0.5,
  "coordinateSpace": "normalized",
  "pressure": 0.5,             // 0.0–1.0 (default 0.5)
  "tiltX": 0,                  // -90 to 90 degrees (default 0)
  "tiltY": 0,                  // -90 to 90 degrees (default 0)
  "rotation": 0.0,             // 0.0–359.0 degrees (default 0.0) — NOTE: double, not int
  "barrel": false,             // Barrel button pressed (side button = right-click in most apps)
  "eraser": false,             // Eraser mode (sets both Inverted + Eraser flags)
  "hover": false,              // If true: InRange only, no InContact (pen hover without touching)
  "dryRun": false
}
// Response
{
  "success": true,
  "resolvedCoordinates": { "x": 500, "y": 400 },
  "pressure": 0.5,
  "previewScreenshotPath": "...",
  "timestamp": "..."
}
```

**UWP mapping to `InjectedInputPenInfo`:**

| MCP param | Maps to | Notes |
|-----------|---------|-------|
| `x, y` | `PointerInfo.PixelLocation` | After coord resolve + DPI scale to raw pixels |
| `pressure` | `Pressure` + `PenParameters |= Pressure` | |
| `tiltX` | `TiltX` + `PenParameters |= TiltX` | int, -90 to 90 |
| `tiltY` | `TiltY` + `PenParameters |= TiltY` | int, -90 to 90 |
| `rotation` | `Rotation` + `PenParameters |= Rotation` | **double** (0.0–359.0) |
| `barrel` | `PenButtons |= Barrel` | Also set `PointerOptions |= SecondButton` |
| `eraser` | `PenButtons |= Eraser \| Inverted` | Flipped pen end |
| `hover` | `PointerOptions = InRange` (no `InContact`) | Hover without touch |

**Injection sequence (tap):**
```
1. InjectPenInput({ PointerInfo={PointerDown|InContact|New, PixelLocation=pos},
                     Pressure, TiltX, TiltY, Rotation, PenButtons, PenParameters })
2. InjectPenInput({ PointerInfo={PointerUp, PixelLocation=pos},
                     Pressure=0, PenParameters=Pressure })
```

**Injection sequence (hover):**
```
1. InjectPenInput({ PointerInfo={InRange|New, PixelLocation=pos}, ... })  // hover in
2. Task.Delay(100)
3. InjectPenInput({ PointerInfo={Update, PixelLocation=pos}, ... })       // hover held
4. InjectPenInput({ PointerInfo={PointerUp, PixelLocation=pos}, ... })    // hover out
```

**Preview:** Crosshair annotation + circle scaled by pressure. Eraser mode shown with distinct marker style.

**Design note on `PointerOptions` flags for pen:**
- Normal contact: `PointerDown | InContact | New | FirstButton | Confidence`
- Barrel button: add `SecondButton`
- Hover: `InRange | New` with NO `InContact`
- Eraser: `PointerDown | InContact | New` with `PenButtons = Eraser | Inverted`

#### `inject_pen_stroke`

Pen stroke along a path with varying pressure, tilt, and rotation at each waypoint.

```jsonc
// Request
{
  "points": [
    { "x": 0.3, "y": 0.3, "pressure": 0.2 },
    { "x": 0.4, "y": 0.4, "pressure": 0.8 },
    { "x": 0.5, "y": 0.5, "pressure": 0.5 },
    { "x": 0.6, "y": 0.4, "pressure": 0.1 }
  ],
  "coordinateSpace": "normalized",
  "barrel": false,              // Barrel held throughout stroke
  "eraser": false,              // Eraser mode throughout stroke
  "tiltX": 0,                   // Global tilt (applies to all points unless overridden)
  "tiltY": 0,
  "rotation": 0.0,              // Global rotation
  "durationMs": 500,
  "dryRun": false
}
// Response
{
  "success": true,
  "pointCount": 4,
  "durationMs": 500,
  "resolvedPath": [{ "x": 300, "y": 300 }, ...],
  "previewScreenshotPath": "...",
  "timestamp": "..."
}
```

**Point schema allows optional per-point override:**
```jsonc
{
  "x": 0.4, "y": 0.4,
  "pressure": 0.8,        // Optional per-point (linear interpolate between waypoints if omitted)
  "tiltX": 15,             // Optional per-point tilt override
  "tiltY": -5,
  "rotation": 45.0
}
```

**UWP implementation:** Same structure as touch drag but using `InjectPenInput` per frame:
1. Resolve all waypoints → DIP → raw pixels
2. Interpolate `max(10, durationMs/16)` frames per segment
3. Interpolate pressure linearly between waypoints
4. Frame 0: `{PointerDown|InContact|New}` with first point
5. Frame 1..N: `{InContact|Update}` with interpolated position + pressure
6. Frame N+1: `{PointerUp}` at last position

**Important:** Unlike touch, pen injection uses `InjectPenInput(single InjectedInputPenInfo)` — NOT an array. Each call = one pen event. This means pen is always single-pointer (which matches reality — you can't have two pens).

**Preview:** Variable-width line where width represents pressure at each point. Thicker segments = higher pressure.

**Design question — should pen points support per-point tilt/rotation?**

→ **Recommendation:** Yes, include in the schema as optional. Most callers will use global tilt/rotation, but drawing apps that test detailed pen dynamics benefit from per-point control. If omitted, inherit from the global value.

---

### Phase 5: Gamepad

#### `inject_gamepad_input`

Single gamepad state snapshot: buttons + sticks + triggers. Held for a duration then released.

```jsonc
// Request
{
  "buttons": ["a", "dpadUp"],         // Buttons to press
  "leftStickX": 0.0,                  // -1.0 to 1.0 (default 0.0)
  "leftStickY": 0.0,                  // -1.0 to 1.0 (default 0.0)
  "rightStickX": 0.0,                 // -1.0 to 1.0 (default 0.0)
  "rightStickY": 0.0,                 // -1.0 to 1.0 (default 0.0)
  "leftTrigger": 0.0,                 // 0.0 to 1.0 (default 0.0)
  "rightTrigger": 0.0,                // 0.0 to 1.0 (default 0.0)
  "holdDurationMs": 100               // Default 100
}
// Response
{
  "success": true,
  "buttons": ["a", "dpadUp"],
  "timestamp": "..."
}
```

**Valid button names** (mapped to `GamepadButtons` flags):

| MCP string | Enum value | | MCP string | Enum value |
|------------|-----------|---|------------|-----------|
| `"menu"` | Menu (1) | | `"view"` | View (2) |
| `"a"` | A (4) | | `"b"` | B (8) |
| `"x"` | X (16) | | `"y"` | Y (32) |
| `"dpadUp"` | DPadUp (64) | | `"dpadDown"` | DPadDown (128) |
| `"dpadLeft"` | DPadLeft (256) | | `"dpadRight"` | DPadRight (512) |
| `"leftShoulder"` | LeftShoulder (1024) | | `"rightShoulder"` | RightShoulder (2048) |
| `"leftThumbstick"` | LeftThumbstick (4096) | | `"rightThumbstick"` | RightThumbstick (8192) |
| `"paddle1"` | Paddle1 (16384) | | `"paddle2"` | Paddle2 (32768) |
| `"paddle3"` | Paddle3 (65536) | | `"paddle4"` | Paddle4 (131072) |

**Note on flattened stick/trigger params:** The original plan used nested objects `leftStick: {x, y}`. Changed to flat `leftStickX`, `leftStickY` because:
- Simpler JSON schema (no nested objects)
- Direct 1:1 mapping to `InjectedInputGamepadInfo` properties
- Less error-prone for LLM callers

**UWP mapping:**

| MCP param | Maps to |
|-----------|---------|
| `buttons` | OR'd `GamepadButtons` flags |
| `leftStickX` | `LeftThumbstickX` |
| `leftStickY` | `LeftThumbstickY` |
| `rightStickX` | `RightThumbstickX` |
| `rightStickY` | `RightThumbstickY` |
| `leftTrigger` | `LeftTrigger` |
| `rightTrigger` | `RightTrigger` |

**Injection sequence:**
1. `InjectGamepadInput({ Buttons=flags, LeftThumbstickX=..., ... })` — pressed state
2. `Task.Delay(holdDurationMs)`
3. `InjectGamepadInput({ Buttons=None, all sticks=0, all triggers=0 })` — neutral release

**No preview screenshot** — gamepad has no screen position.

#### `inject_gamepad_sequence`

Multiple gamepad frames for complex inputs (stick sweeps, combos, racing inputs).

```jsonc
// Request
{
  "frames": [
    {
      "buttons": ["a"],
      "leftStickX": 0.0, "leftStickY": 0.0,
      "rightStickX": 0.0, "rightStickY": 0.0,
      "leftTrigger": 0.0, "rightTrigger": 0.0,
      "durationMs": 100
    },
    {
      "buttons": [],
      "leftStickX": 1.0, "leftStickY": 0.0,
      "leftTrigger": 0.5,
      "durationMs": 500
    }
  ]
}
// Response
{
  "success": true,
  "frameCount": 2,
  "totalDurationMs": 600,
  "timestamp": "..."
}
```

**Implementation:** For each frame: inject `InjectedInputGamepadInfo` with the frame's state → `Task.Delay(frame.durationMs)` → next frame. After last frame, inject neutral state as release.

**Note:** Omitted fields in a frame default to 0/empty (neutral). Each frame is a complete snapshot, not a diff.

---

## Implementation Roadmap

### Per-tool checklist (UWP-focused)

1. **zRover.Core — DTOs**
   - Create `[ToolName]Request.cs` and `[ToolName]Response.cs` in `Tools/InputInjection/`
   - Dual serialization attributes (`#if !WINDOWS_UWP` for `System.Text.Json`, always `Newtonsoft.Json`)

2. **zRover.Uwp — InputInjectionCapability**
   - Add JSON Schema string constant
   - Register tool in `RegisterTools()` 
   - Implement async handler (`Inject[Tool]Async(string argsJson)`)
   - UWP `InputInjector` path (on UI thread via `_runOnUiThread`)
   - XAML automation fallback where applicable
   - Preview screenshot annotation (extend `ScreenshotAnnotator`)

3. **zRover.FullTrust.McpServer — Pass-through proxy**
   - Register as transparent proxy: `backend.InvokeToolAsync(name, argsJson)`
   - No Win32 fallback for now (documented gap)

4. **Testing** in `zRover.Uwp.Sample`

### Phase order

```
Phase 1: Keyboard (inject_key_press, inject_text)
  └── Highest utility — needed for almost any app interaction
  └── Simplest to implement: no coordinates, no preview

Phase 2: Mouse expansion (expand inject_tap, inject_mouse_scroll, inject_mouse_move)
  └── High utility — scroll/right-click/hover are pervasive
  └── Extends existing InjectMouseInput patterns

Phase 3: Multitouch (inject_multi_touch, inject_pinch)
  └── Medium utility — pinch-to-zoom, swipe gestures
  └── Extends existing InjectTouchInput with multiple PointerIds per call

Phase 4: Pen (inject_pen_tap, inject_pen_stroke)
  └── Niche — inking/drawing apps
  └── New API: InjectPenInput

Phase 5: Gamepad (inject_gamepad_input, inject_gamepad_sequence)
  └── Niche — game testing
  └── New API: InjectGamepadInput
```

---

## Detailed Design Decisions

### Code organization: Split InputInjectionCapability

The current `InputInjectionCapability` is ~700 lines handling just tap and drag. Adding 10 more tools would push it past 2000+ lines.

**Recommended approach:** Keep a single `InputInjectionCapability` class that owns the `InputInjector` lifecycle, but extract handler methods into partial classes or helper methods grouped by input type. All share the same `_injector`, `_resolver`, `_runOnUiThread`, and `_logDir`.

```
InputInjectionCapability.cs          — StartAsync, StopAsync, RegisterTools, shared helpers
InputInjectionCapability.Keyboard.cs — InjectKeyPressAsync, InjectTextAsync (partial class)
InputInjectionCapability.Mouse.cs    — Expanded InjectTapAsync, InjectMouseScrollAsync, InjectMouseMoveAsync
InputInjectionCapability.Touch.cs    — InjectMultiTouchAsync, InjectPinchAsync (+ existing touch drag)
InputInjectionCapability.Pen.cs      — InjectPenTapAsync, InjectPenStrokeAsync
InputInjectionCapability.Gamepad.cs  — InjectGamepadInputAsync, InjectGamepadSequenceAsync
```

UWP projects support `partial class` — this keeps registration centralized while splitting implementation.

### Coordinate pipeline (reused across all position-based tools)

All spatial tools share the same pipeline (already implemented):
1. Parse `coordinateSpace` string → `CoordinateSpace` enum
2. `_resolver.Resolve(point, space)` → DIP coordinates relative to screen
3. DIP × `DisplayInformation.RawPixelsPerViewPixel` → raw screen pixels
4. Raw pixels → `InjectedInputPoint.PositionX/Y` (touch, pen) or normalized 0–65535 (mouse)

### Preview screenshots

| Tool | Preview? | Annotation style |
|------|----------|-----------------|
| `inject_key_press` | No | — |
| `inject_text` | No | — |
| `inject_tap` (expanded) | Yes | Crosshair (existing) |
| `inject_mouse_scroll` | Yes | Crosshair + directional scroll arrows |
| `inject_mouse_move` | Yes | Crosshair (lighter, no click indicator) |
| `inject_multi_touch` | Yes | Multi-colored paths per pointer |
| `inject_pinch` | Yes | Two converging/diverging paths from center |
| `inject_pen_tap` | Yes | Crosshair + pressure circle scaled by value |
| `inject_pen_stroke` | Yes | Path with variable-thickness = pressure |
| `inject_gamepad_input` | No | — |
| `inject_gamepad_sequence` | No | — |

---

## Known Gaps (Win32 Fallback — Deferred)

The FullTrust `Win32InputInjector` currently only supports mouse tap and drag via `SendInput`. The following Win32 expansions are deferred:

| Tool | Win32 feasibility | What's needed |
|------|-------------------|---------------|
| `inject_key_press` | ✅ Straightforward | `SendInput` + `INPUT_KEYBOARD` + `KEYBDINPUT`. Need to refactor `INPUT` struct to support union layout (`LayoutKind.Explicit`). |
| `inject_text` | ✅ Straightforward | `KEYBDINPUT` with `KEYEVENTF_UNICODE` + `wScan` = char code |
| `inject_tap` (new buttons) | ✅ Straightforward | Add `MOUSEEVENTF_RIGHTDOWN/UP`, `MIDDLEDOWN/UP`, `XDOWN/XUP` constants |
| `inject_mouse_scroll` | ✅ Straightforward | `MOUSEEVENTF_WHEEL` / `MOUSEEVENTF_HWHEEL`, `mouseData` = delta × 120 |
| `inject_mouse_move` | ✅ Already possible | Existing `MOUSEEVENTF_MOVE \| MOUSEEVENTF_ABSOLUTE` |
| `inject_multi_touch` | ❌ Not possible | Win32 has no multitouch injection API. Would need custom touch injection driver. |
| `inject_pinch` | ❌ Not possible | Same as multitouch |
| `inject_pen_tap` | ⚠️ Degrades to mouse | Lose pressure/tilt — just a click at position |
| `inject_pen_stroke` | ⚠️ Degrades to mouse | Lose pressure/tilt — just a mouse drag |
| `inject_gamepad_input` | ❌ Not possible | No Win32 gamepad injection. Could use ViGEm driver in future. |
| `inject_gamepad_sequence` | ❌ Not possible | Same as gamepad |

**Structural blocker:** The current `Win32InputInjector.INPUT` struct only has `MOUSEINPUT`. Must be refactored to explicit-layout union to support `KEYBDINPUT`:
```csharp
[StructLayout(LayoutKind.Explicit)]
private struct INPUT {
    [FieldOffset(0)] public int type;
    [FieldOffset(4)] public MOUSEINPUT mi;
    [FieldOffset(4)] public KEYBDINPUT ki;
}
```

---

## New File Inventory

### zRover.Core/Tools/InputInjection/ (new DTOs)
| File | Phase |
|------|-------|
| `InjectKeyPressRequest.cs` | 1 |
| `InjectKeyPressResponse.cs` | 1 |
| `InjectTextRequest.cs` | 1 |
| `InjectTextResponse.cs` | 1 |
| `InjectMouseScrollRequest.cs` | 2 |
| `InjectMouseScrollResponse.cs` | 2 |
| `InjectMouseMoveRequest.cs` | 2 |
| `InjectMouseMoveResponse.cs` | 2 |
| `InjectMultiTouchRequest.cs` | 3 |
| `InjectMultiTouchResponse.cs` | 3 |
| `InjectPinchRequest.cs` | 3 |
| `InjectPinchResponse.cs` | 3 |
| `InjectRotateRequest.cs` | 3 |
| `InjectRotateResponse.cs` | 3 |
| `InjectPenTapRequest.cs` | 4 |
| `InjectPenTapResponse.cs` | 4 |
| `PenStrokePoint.cs` | 4 |
| `InjectPenStrokeRequest.cs` | 4 |
| `InjectPenStrokeResponse.cs` | 4 |
| `InjectGamepadInputRequest.cs` | 5 |
| `InjectGamepadInputResponse.cs` | 5 |
| `GamepadFrame.cs` | 5 |
| `InjectGamepadSequenceRequest.cs` | 5 |
| `InjectGamepadSequenceResponse.cs` | 5 |

### zRover.Uwp/Capabilities/ (partial class files)
| File | Phase |
|------|-------|
| `InputInjectionCapability.Keyboard.cs` | 1 |
| `InputInjectionCapability.Mouse.cs` | 2 |
| `InputInjectionCapability.Touch.cs` | 3 |
| `InputInjectionCapability.Pen.cs` | 4 |
| `InputInjectionCapability.Gamepad.cs` | 5 |

### Modified existing files
| File | Changes |
|------|---------|
| `InputInjectionCapability.cs` | Make `partial`, move existing handlers into `.Mouse.cs` / `.Touch.cs`, add `RegisterTools` entries |
| `InjectTapRequest.cs` | Add `Button` and `ClickCount` properties |
| `InjectTapResponse.cs` | Echo back `Button` and `ClickCount` |
| `ScreenshotAnnotator.cs` | Add scroll arrow, multi-pointer path, pen pressure line annotations |
| `Program.cs` (FullTrust) | Register new tools as pass-through proxies |

---

## Summary: Final MCP Tool Surface

| # | Tool | Input Type | Preview | UWP API |
|---|------|-----------|---------|---------|
| 1 | `inject_tap` | Touch + Mouse (expanded: all buttons, multi-click) | ✅ | `InjectTouchInput` / `InjectMouseInput` |
| 2 | `inject_drag_path` | Touch + Mouse | ✅ | `InjectTouchInput` / `InjectMouseInput` |
| 3 | `inject_key_press` | Keyboard (key + modifiers) | ❌ | `InjectKeyboardInput` |
| 4 | `inject_text` | Keyboard (Unicode text) | ❌ | `InjectKeyboardInput` (Unicode mode) |
| 5 | `inject_mouse_scroll` | Mouse wheel (V + H) | ✅ | `InjectMouseInput` (Wheel/HWheel) |
| 6 | `inject_mouse_move` | Mouse cursor | ✅ | `InjectMouseInput` (Move\|Absolute) |
| 7 | `inject_multi_touch` | Multitouch (N pointers) | ✅ | `InjectTouchInput` (batch per frame) |
| 8 | `inject_pinch` | Multitouch (convenience) | ✅ | `InjectTouchInput` (computed paths) |
| 9 | `inject_rotate` | Multitouch (convenience) | ✅ | `InjectTouchInput` (computed arcs) |
| 10 | `inject_pen_tap` | Pen (pressure/tilt/barrel/eraser/hover) | ✅ | `InjectPenInput` |
| 11 | `inject_pen_stroke` | Pen stroke (variable pressure path) | ✅ | `InjectPenInput` (per frame) |
| 12 | `inject_gamepad_input` | Gamepad (buttons/sticks/triggers) | ❌ | `InjectGamepadInput` |
| 13 | `inject_gamepad_sequence` | Gamepad (multi-frame) | ❌ | `InjectGamepadInput` (per frame) |

**Current:** 2 tools → **Target:** 13 tools covering all Windows input modalities.
