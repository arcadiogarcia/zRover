namespace zRover.Core.Tools.InputInjection
{
    public static class ToolSchemas
    {
        public const string TapSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate. In normalized space (default): 0.0 (left) to 1.0 (right). Think of this as a percentage — 0.33 means '33% across the window'. Prefer getting exact values from find_element or get_ui_tree bounds over estimating from screenshots."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate. In normalized space (default): 0.0 (top) to 1.0 (bottom). Think of this as a percentage — 0.50 means 'halfway down the window'. Prefer getting exact values from find_element or get_ui_tree bounds over estimating from screenshots."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"", ""description"": ""Coordinate space: 'normalized' (default, 0.0-1.0 relative to window size) or 'pixels' (render pixels, matching windowWidth/windowHeight from capture_current_view)."" },
    ""device"": { ""type"": ""string"", ""enum"": [""touch"", ""mouse""], ""default"": ""touch"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, captures an annotated screenshot showing where the tap would land but does NOT actually inject the input. ALWAYS use dryRun=true when using estimated coordinates (not from find_element or get_ui_tree) to verify before committing."" }
  },
  ""required"": [""x"", ""y""]
}";

        public const string DragSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""points"": { ""type"": ""array"", ""items"": { ""$ref"": ""#/$defs/point"" }, ""minItems"": 2, ""description"": ""Ordered waypoints for the drag gesture."" },
    ""durationMs"": { ""type"": ""integer"", ""default"": 300, ""description"": ""Total duration of the drag in milliseconds."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"", ""description"": ""Coordinate space: 'normalized' (default, 0.0-1.0 relative to window size) or 'pixels' (render pixels, matching windowWidth/windowHeight from capture_current_view)."" },
    ""device"": { ""type"": ""string"", ""enum"": [""touch"", ""mouse""], ""default"": ""touch"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, captures an annotated screenshot showing the drag path but does NOT actually inject the input. Use this to verify the path before committing."" }
  },
  ""required"": [""points""],
  ""$defs"": { ""point"": { ""type"": ""object"", ""properties"": { ""x"": {""type"":""number"", ""description"": ""X position (0.0\u20131.0 in normalized space)""}, ""y"": {""type"":""number"", ""description"": ""Y position (0.0\u20131.0 in normalized space)""} }, ""required"": [""x"",""y""] } }
}";

        public const string KeyPressSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""key"": { ""type"": ""string"", ""description"": ""Virtual key name (e.g. 'Enter', 'Tab', 'A', 'Left', 'Escape', 'Back', 'F5'). Uses Windows VirtualKey names without the 'Number' prefix for digits."" },
    ""modifiers"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""enum"": [""Control"", ""Shift"", ""Menu"", ""Windows""] }, ""default"": [], ""description"": ""Modifier keys to hold during the key press."" },
    ""holdDurationMs"": { ""type"": ""integer"", ""default"": 0, ""description"": ""How long to hold the key down in milliseconds. 0 for a normal press-and-release."" }
  },
  ""required"": [""key""]
}";

        public const string TextSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""text"": { ""type"": ""string"", ""description"": ""The text string to type. Each character is sent as a key press."" },
    ""delayBetweenKeysMs"": { ""type"": ""integer"", ""default"": 30, ""description"": ""Delay in ms between each keystroke."" }
  },
  ""required"": [""text""]
}";

        public const string MouseScrollSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate where the scroll occurs."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate where the scroll occurs."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""deltaY"": { ""type"": ""integer"", ""default"": -120, ""description"": ""Vertical scroll amount. Negative scrolls down, positive scrolls up. 120 = one notch."" },
    ""deltaX"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Horizontal scroll amount. Negative scrolls left, positive scrolls right. 120 = one notch."" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, previews the scroll location without injecting."" }
  },
  ""required"": [""x"", ""y""]
}";

        public const string MouseMoveSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""Target X coordinate."" },
    ""y"": { ""type"": ""number"", ""description"": ""Target Y coordinate."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, previews the move target without injecting."" }
  },
  ""required"": [""x"", ""y""]
}";

        public const string PenTapSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate of the pen tap."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate of the pen tap."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""pressure"": { ""type"": ""number"", ""default"": 0.5, ""description"": ""Pen pressure 0.0 to 1.0."" },
    ""tiltX"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Pen X tilt in degrees (-90 to 90)."" },
    ""tiltY"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Pen Y tilt in degrees (-90 to 90)."" },
    ""rotation"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""Pen rotation in degrees (0.0 to 359.0)."" },
    ""barrel"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Whether the barrel button is pressed."" },
    ""eraser"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Whether the eraser end is active."" },
    ""hover"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, pen hovers (InRange) without touching (no InContact)."" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""x"", ""y""]
}";

        public const string PenStrokeSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""points"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""x"": { ""type"": ""number"" },
          ""y"": { ""type"": ""number"" },
          ""pressure"": { ""type"": ""number"", ""description"": ""Per-point pressure override."" },
          ""tiltX"": { ""type"": ""integer"", ""description"": ""Per-point tiltX override."" },
          ""tiltY"": { ""type"": ""integer"", ""description"": ""Per-point tiltY override."" },
          ""rotation"": { ""type"": ""number"", ""description"": ""Per-point rotation override."" }
        },
        ""required"": [""x"", ""y""]
      },
      ""minItems"": 2,
      ""description"": ""Ordered points for the pen stroke.""
    },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""pressure"": { ""type"": ""number"", ""default"": 0.5, ""description"": ""Default pressure for all points."" },
    ""tiltX"": { ""type"": ""integer"", ""default"": 0 },
    ""tiltY"": { ""type"": ""integer"", ""default"": 0 },
    ""rotation"": { ""type"": ""number"", ""default"": 0.0 },
    ""barrel"": { ""type"": ""boolean"", ""default"": false },
    ""eraser"": { ""type"": ""boolean"", ""default"": false },
    ""durationMs"": { ""type"": ""integer"", ""default"": 400 },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""points""]
}";

        public const string MultiTouchSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pointers"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""id"": { ""type"": ""integer"", ""description"": ""Unique pointer ID (1-based)."" },
          ""path"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""} }, ""required"": [""x"",""y""] }, ""minItems"": 1 },
          ""pressure"": { ""type"": ""number"", ""default"": 1.0 },
          ""orientation"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Contact orientation 0-359 degrees."" },
          ""contactWidth"": { ""type"": ""integer"", ""default"": 4 },
          ""contactHeight"": { ""type"": ""integer"", ""default"": 4 }
        },
        ""required"": [""id"", ""path""]
      },
      ""description"": ""Array of pointer paths to inject simultaneously.""
    },
    ""durationMs"": { ""type"": ""integer"", ""default"": 400, ""description"": ""Total gesture duration in ms."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""pointers""]
}";

        public const string PinchSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""centerX"": { ""type"": ""number"", ""description"": ""Center X of the pinch gesture."" },
    ""centerY"": { ""type"": ""number"", ""description"": ""Center Y of the pinch gesture."" },
    ""startDistance"": { ""type"": ""number"", ""default"": 0.3, ""description"": ""Starting distance between fingers (normalized)."" },
    ""endDistance"": { ""type"": ""number"", ""default"": 0.1, ""description"": ""Ending distance between fingers (normalized). Less than startDistance = pinch in, greater = pinch out."" },
    ""angle"": { ""type"": ""number"", ""default"": 0, ""description"": ""Angle of the pinch axis in degrees."" },
    ""durationMs"": { ""type"": ""integer"", ""default"": 400 },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""centerX"", ""centerY""]
}";

        public const string RotateSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""centerX"": { ""type"": ""number"", ""description"": ""Center X of the rotation."" },
    ""centerY"": { ""type"": ""number"", ""description"": ""Center Y of the rotation."" },
    ""distance"": { ""type"": ""number"", ""default"": 0.2, ""description"": ""Distance of each finger from center (normalized)."" },
    ""startAngle"": { ""type"": ""number"", ""default"": 0, ""description"": ""Starting angle in degrees."" },
    ""endAngle"": { ""type"": ""number"", ""default"": 90, ""description"": ""Ending angle in degrees. Positive = clockwise."" },
    ""durationMs"": { ""type"": ""integer"", ""default"": 400 },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""centerX"", ""centerY""]
}";

        public const string GamepadInputSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""buttons"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""enum"": [""A"", ""B"", ""X"", ""Y"", ""LeftThumbstick"", ""RightThumbstick"", ""LeftShoulder"", ""RightShoulder"", ""View"", ""Menu"", ""DPadUp"", ""DPadDown"", ""DPadLeft"", ""DPadRight"", ""Paddle1"", ""Paddle2"", ""Paddle3"", ""Paddle4""] }, ""default"": [], ""description"": ""Gamepad buttons to press."" },
    ""leftStickX"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""Left thumbstick X axis (-1.0 to 1.0)."" },
    ""leftStickY"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""Left thumbstick Y axis (-1.0 to 1.0)."" },
    ""rightStickX"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""Right thumbstick X axis (-1.0 to 1.0)."" },
    ""rightStickY"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""Right thumbstick Y axis (-1.0 to 1.0)."" },
    ""leftTrigger"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""Left trigger (0.0 to 1.0)."" },
    ""rightTrigger"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""Right trigger (0.0 to 1.0)."" },
    ""holdDurationMs"": { ""type"": ""integer"", ""default"": 100, ""description"": ""How long to hold the gamepad state in ms."" }
  }
}";

        public const string GamepadSequenceSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""frames"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""buttons"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""default"": [] },
          ""leftStickX"": { ""type"": ""number"", ""default"": 0.0 },
          ""leftStickY"": { ""type"": ""number"", ""default"": 0.0 },
          ""rightStickX"": { ""type"": ""number"", ""default"": 0.0 },
          ""rightStickY"": { ""type"": ""number"", ""default"": 0.0 },
          ""leftTrigger"": { ""type"": ""number"", ""default"": 0.0 },
          ""rightTrigger"": { ""type"": ""number"", ""default"": 0.0 },
          ""durationMs"": { ""type"": ""integer"", ""default"": 100, ""description"": ""Duration to hold this frame before moving to the next."" }
        }
      },
      ""description"": ""Sequence of gamepad frames to inject in order.""
    }
  },
  ""required"": [""frames""]
}";

        public const string PointerDownSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pointerId"": { ""type"": ""integer"", ""default"": 1, ""description"": ""Unique pointer ID (1-based). Use different IDs for multi-finger touch gestures. Pen supports only one active pointer."" },
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate. In the default normalized space this is 0.0 (left) to 1.0 (right)."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate. In the default normalized space this is 0.0 (top) to 1.0 (bottom)."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""device"": { ""type"": ""string"", ""enum"": [""touch"", ""pen""], ""default"": ""touch"", ""description"": ""Input device type. 'touch' supports multiple simultaneous pointers; 'pen' supports one pointer with tilt, rotation, barrel, and eraser."" },
    ""pressure"": { ""type"": ""number"", ""default"": 1.0, ""description"": ""Contact pressure 0.0-1.0 (default 1.0 for touch, 0.5 for pen)."" },
    ""orientation"": { ""type"": ""integer"", ""default"": 0, ""description"": ""(touch only) Contact orientation 0-359 degrees."" },
    ""contactWidth"": { ""type"": ""integer"", ""default"": 4, ""description"": ""(touch only) Contact patch width."" },
    ""contactHeight"": { ""type"": ""integer"", ""default"": 4, ""description"": ""(touch only) Contact patch height."" },
    ""tiltX"": { ""type"": ""integer"", ""default"": 0, ""description"": ""(pen only) X tilt in degrees (-90 to 90)."" },
    ""tiltY"": { ""type"": ""integer"", ""default"": 0, ""description"": ""(pen only) Y tilt in degrees (-90 to 90)."" },
    ""rotation"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""(pen only) Pen rotation in degrees (0.0-359.0)."" },
    ""barrel"": { ""type"": ""boolean"", ""default"": false, ""description"": ""(pen only) Whether the barrel button is pressed."" },
    ""eraser"": { ""type"": ""boolean"", ""default"": false, ""description"": ""(pen only) Whether the eraser end is active."" }
  },
  ""required"": [""x"", ""y""]
}";

        public const string PointerMoveSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pointerId"": { ""type"": ""integer"", ""default"": 1, ""description"": ""Pointer ID of an active (held-down) pointer."" },
    ""x"": { ""type"": ""number"", ""description"": ""New X coordinate."" },
    ""y"": { ""type"": ""number"", ""description"": ""New Y coordinate."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""pressure"": { ""type"": ""number"", ""description"": ""Updated pressure (optional, keeps previous value if omitted)."" },
    ""tiltX"": { ""type"": ""integer"", ""description"": ""(pen only) Updated X tilt (optional)."" },
    ""tiltY"": { ""type"": ""integer"", ""description"": ""(pen only) Updated Y tilt (optional)."" },
    ""rotation"": { ""type"": ""number"", ""description"": ""(pen only) Updated rotation (optional)."" }
  },
  ""required"": [""x"", ""y""]
}";

        public const string PointerUpSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pointerId"": { ""type"": ""integer"", ""default"": 1, ""description"": ""Pointer ID of the active pointer to release. When the last touch pointer is released the touch injection session is torn down."" }
  }
}";

        public const string TapElementSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""name"": { ""type"": ""string"", ""description"": ""Element x:Name to match (substring, case-insensitive)."" },
    ""automationName"": { ""type"": ""string"", ""description"": ""AutomationProperties.Name to match (substring, case-insensitive)."" },
    ""type"": { ""type"": ""string"", ""description"": ""XAML element type name (e.g. 'Button', 'TextBlock', 'TextBox'). Exact match, case-insensitive."" },
    ""parent"": { ""type"": ""string"", ""description"": ""Optional parent element name to scope the search under."" },
    ""text"": { ""type"": ""string"", ""description"": ""Text content to match (substring, case-insensitive). Matches TextBlock.Text, TextBox.Text, or ContentControl string content."" },
    ""index"": { ""type"": ""integer"", ""default"": -1, ""description"": ""0-based index when multiple elements match. -1 (default) returns the first match."" },
    ""device"": { ""type"": ""string"", ""enum"": [""touch"", ""mouse""], ""default"": ""touch"" },
    ""button"": { ""type"": ""string"", ""enum"": [""left"", ""right""], ""default"": ""left"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, shows where the tap would land but does NOT inject input."" },
    ""timeout"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Milliseconds to wait for the element to appear. 0 = no wait."" },
    ""poll"": { ""type"": ""integer"", ""default"": 500, ""description"": ""Polling interval in ms during timeout."" }
  }
}";

        public const string FindElementSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""name"": { ""type"": ""string"", ""description"": ""Element x:Name to match (substring, case-insensitive)."" },
    ""automationName"": { ""type"": ""string"", ""description"": ""AutomationProperties.Name to match (substring, case-insensitive)."" },
    ""type"": { ""type"": ""string"", ""description"": ""XAML element type name (e.g. 'Button', 'TextBlock'). Exact match, case-insensitive."" },
    ""parent"": { ""type"": ""string"", ""description"": ""Optional parent element name to scope the search under."" },
    ""text"": { ""type"": ""string"", ""description"": ""Text content to match (substring, case-insensitive). Matches TextBlock.Text, TextBox.Text, or ContentControl string content."" },
    ""all"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, returns all matching elements in a 'matches' array."" },
    ""index"": { ""type"": ""integer"", ""default"": -1, ""description"": ""0-based index when multiple elements match. -1 (default) returns the first match."" },
    ""timeout"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Milliseconds to wait for the element to appear. 0 = no wait."" },
    ""poll"": { ""type"": ""integer"", ""default"": 500, ""description"": ""Polling interval in ms during timeout."" }
  }
}";

        public const string HitTestSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate. In normalized space (default): 0.0 (left) to 1.0 (right)."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate. In normalized space (default): 0.0 (top) to 1.0 (bottom)."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" }
  },
  ""required"": [""x"", ""y""]
}";

        public const string ActivateElementSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""name"": { ""type"": ""string"", ""description"": ""Element x:Name to match (substring, case-insensitive)."" },
    ""automationName"": { ""type"": ""string"", ""description"": ""AutomationProperties.Name to match (substring, case-insensitive)."" },
    ""type"": { ""type"": ""string"", ""description"": ""XAML element type name (e.g. 'Button', 'CheckBox'). Exact match, case-insensitive."" },
    ""parent"": { ""type"": ""string"", ""description"": ""Optional parent element name to scope the search under."" },
    ""text"": { ""type"": ""string"", ""description"": ""Text content to match (substring, case-insensitive). Matches TextBlock.Text, TextBox.Text, or ContentControl string content."" },
    ""action"": { ""type"": ""string"",""enum"": [""invoke"", ""toggle"", ""select"", ""expand"", ""collapse"", ""focus""], ""default"": ""invoke"", ""description"": ""Action to perform via XAML AutomationPeer. 'invoke' clicks buttons, 'toggle' toggles checkboxes/toggle buttons, 'select' selects list items, 'expand'/'collapse' for expandable controls, 'focus' sets keyboard focus."" },
    ""timeout"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Milliseconds to wait for the element to appear. 0 = no wait."" },
    ""poll"": { ""type"": ""integer"", ""default"": 500, ""description"": ""Polling interval in ms during timeout."" }
  }
}";
    }
}
