using System.Text.Json;
using FluentAssertions;
using Newtonsoft.Json;
using zRover.Core.Coordinates;
using zRover.Core.Tools.InputInjection;

namespace zRover.Mcp.IntegrationTests;

// ═══════════════════════════════════════════════════════════════
//  PointerSessionState — pure state/validation logic
// ═══════════════════════════════════════════════════════════════

public class PointerSessionStateTests
{
    private static PointerSessionState.ActivePointerState Touch(int id) =>
        new PointerSessionState.ActivePointerState
        {
            PointerId = id,
            Device = PointerSessionState.DeviceKind.Touch
        };

    private static PointerSessionState.ActivePointerState Pen(int id) =>
        new PointerSessionState.ActivePointerState
        {
            PointerId = id,
            Device = PointerSessionState.DeviceKind.Pen
        };

    // ── ValidateDown ──────────────────────────────────────────

    [Fact]
    public void ValidateDown_FreshSession_AllowsAnyPointer()
    {
        var s = new PointerSessionState();
        s.ValidateDown(1, PointerSessionState.DeviceKind.Touch).Should().BeNull();
        s.ValidateDown(1, PointerSessionState.DeviceKind.Pen).Should().BeNull();
    }

    [Fact]
    public void ValidateDown_DuplicateId_ReturnsError()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.ValidateDown(1, PointerSessionState.DeviceKind.Touch).Should().NotBeNull()
            .And.Contain("1");
    }

    [Fact]
    public void ValidateDown_SecondPenPointer_ReturnsError()
    {
        var s = new PointerSessionState();
        s.RecordDown(Pen(1));
        s.ValidateDown(2, PointerSessionState.DeviceKind.Pen).Should().NotBeNull()
            .And.Contain("pen", Exactly.Once(), "at most one pen at a time");
    }

    [Fact]
    public void ValidateDown_PenWhileTouchActive_IsAllowed()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.RecordDown(Touch(2));
        // pen and touch may coexist
        s.ValidateDown(10, PointerSessionState.DeviceKind.Pen).Should().BeNull();
    }

    [Fact]
    public void ValidateDown_MultipleTouch_WhilePenActive_IsAllowed()
    {
        var s = new PointerSessionState();
        s.RecordDown(Pen(1));
        s.ValidateDown(2, PointerSessionState.DeviceKind.Touch).Should().BeNull();
        s.ValidateDown(3, PointerSessionState.DeviceKind.Touch).Should().BeNull();
    }

    // ── ValidateMove ─────────────────────────────────────────

    [Fact]
    public void ValidateMove_PointerNotActive_ReturnsError()
    {
        var s = new PointerSessionState();
        s.ValidateMove(1).Should().NotBeNull().And.Contain("1");
    }

    [Fact]
    public void ValidateMove_ActiveTouchPointer_ReturnsNull()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.ValidateMove(1).Should().BeNull();
    }

    [Fact]
    public void ValidateMove_ActivePenPointer_ReturnsNull()
    {
        var s = new PointerSessionState();
        s.RecordDown(Pen(1));
        s.ValidateMove(1).Should().BeNull();
    }

    // ── ValidateUp ───────────────────────────────────────────

    [Fact]
    public void ValidateUp_PointerNotActive_ReturnsError()
    {
        var s = new PointerSessionState();
        s.ValidateUp(99).Should().NotBeNull().And.Contain("99");
    }

    [Fact]
    public void ValidateUp_ActivePointer_ReturnsNull()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.ValidateUp(1).Should().BeNull();
    }

    // ── RecordDown / TryGetPointer ───────────────────────────

    [Fact]
    public void RecordDown_Pen_TouchSessionRemainsInactive()
    {
        var s = new PointerSessionState();
        s.RecordDown(Pen(1));
        s.TouchSessionActive.Should().BeFalse();
        s.Count.Should().Be(1);
    }

    [Fact]
    public void RecordDown_Touch_ActivatesTouchSession()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.TouchSessionActive.Should().BeTrue();
    }

    [Fact]
    public void TryGetPointer_ReturnsTrackedState()
    {
        var s = new PointerSessionState();
        s.RecordDown(new PointerSessionState.ActivePointerState
        {
            PointerId = 5,
            Device = PointerSessionState.DeviceKind.Touch,
            Pressure = 0.75,
            ContactWidth = 8,
            ContactHeight = 12
        });
        s.TryGetPointer(5, out var ptr).Should().BeTrue();
        ptr!.Pressure.Should().BeApproximately(0.75, 0.001);
        ptr.ContactWidth.Should().Be(8);
        ptr.ContactHeight.Should().Be(12);
    }

    // ── UpdatePosition ───────────────────────────────────────

    [Fact]
    public void UpdatePosition_SetsCoordinatesAndPressure()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.UpdatePosition(1, 100, 200, pressure: 0.8);
        var ptr = s.PointerMap[1];
        ptr.LastX.Should().Be(100);
        ptr.LastY.Should().Be(200);
        ptr.Pressure.Should().BeApproximately(0.8, 0.001);
    }

    [Fact]
    public void UpdatePosition_SetsPenSpecificFields()
    {
        var s = new PointerSessionState();
        s.RecordDown(Pen(1));
        s.UpdatePosition(1, 50, 60, tiltX: 15, tiltY: -10, rotation: 45.0);
        var ptr = s.PointerMap[1];
        ptr.TiltX.Should().Be(15);
        ptr.TiltY.Should().Be(-10);
        ptr.Rotation.Should().BeApproximately(45.0, 0.001);
    }

    [Fact]
    public void UpdatePosition_NullOverrides_PreservesPreviousValues()
    {
        var s = new PointerSessionState();
        s.RecordDown(new PointerSessionState.ActivePointerState
        {
            PointerId = 1,
            Device = PointerSessionState.DeviceKind.Pen,
            Pressure = 0.5,
            TiltX = 10
        });
        s.UpdatePosition(1, 0, 0); // no optional overrides
        s.PointerMap[1].Pressure.Should().BeApproximately(0.5, 0.001);
        s.PointerMap[1].TiltX.Should().Be(10);
    }

    [Fact]
    public void UpdatePosition_UnknownPointer_DoesNotThrow()
    {
        var s = new PointerSessionState();
        var act = () => s.UpdatePosition(99, 0, 0);
        act.Should().NotThrow();
    }

    // ── RecordUp ─────────────────────────────────────────────

    [Fact]
    public void RecordUp_OnlyTouchPointer_ReturnsTrueAndClearsTouchSession()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        var wasLast = s.RecordUp(1);
        wasLast.Should().BeTrue("it was the last touch pointer");
        s.TouchSessionActive.Should().BeFalse();
        s.Count.Should().Be(0);
    }

    [Fact]
    public void RecordUp_NotLastTouchPointer_ReturnsFalse()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.RecordDown(Touch(2));
        s.RecordUp(1).Should().BeFalse("pointer 2 is still active");
        s.TouchSessionActive.Should().BeTrue();
        s.Count.Should().Be(1);
    }

    [Fact]
    public void RecordUp_PenPointer_ReturnsFalse()
    {
        var s = new PointerSessionState();
        s.RecordDown(Pen(1));
        s.RecordUp(1).Should().BeFalse("pen never drives touch session teardown");
        s.Count.Should().Be(0);
    }

    [Fact]
    public void RecordUp_PenWhileTouchActive_DoesNotClearTouchSession()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.RecordDown(Pen(2));
        s.RecordUp(2); // release pen only
        s.TouchSessionActive.Should().BeTrue("touch pointer 1 is still active");
        s.Count.Should().Be(1);
    }

    [Fact]
    public void RecordUp_TouchWhilePenActive_WhenLastTouch_ReturnsTrueButPenRemains()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.RecordDown(Pen(2));
        s.RecordUp(1).Should().BeTrue("was last touch pointer");
        s.TouchSessionActive.Should().BeFalse();
        s.Count.Should().Be(1); // pen pointer 2 still active
        s.PointerMap.ContainsKey(2).Should().BeTrue();
    }

    [Fact]
    public void MixedSession_FullLifecycle()
    {
        var s = new PointerSessionState();

        s.ValidateDown(1, PointerSessionState.DeviceKind.Touch).Should().BeNull();
        s.RecordDown(Touch(1));

        s.ValidateDown(2, PointerSessionState.DeviceKind.Touch).Should().BeNull();
        s.RecordDown(Touch(2));

        s.ValidateDown(3, PointerSessionState.DeviceKind.Pen).Should().BeNull();
        s.RecordDown(Pen(3));

        s.Count.Should().Be(3);
        s.TouchSessionActive.Should().BeTrue();

        s.UpdatePosition(1, 10, 20);
        s.UpdatePosition(3, 30, 40, tiltX: 5);

        s.RecordUp(3).Should().BeFalse(); // pen released, not last touch
        s.Count.Should().Be(2);
        s.TouchSessionActive.Should().BeTrue();

        s.RecordUp(1).Should().BeFalse(); // touch 1 released, touch 2 remains
        s.RecordUp(2).Should().BeTrue();  // last touch — caller should uninitialize
        s.Count.Should().Be(0);
        s.TouchSessionActive.Should().BeFalse();
    }

    // ── Clear ────────────────────────────────────────────────

    [Fact]
    public void Clear_ResetsAllState()
    {
        var s = new PointerSessionState();
        s.RecordDown(Touch(1));
        s.RecordDown(Pen(2));
        s.Clear();
        s.Count.Should().Be(0);
        s.TouchSessionActive.Should().BeFalse();
    }
}

// ═══════════════════════════════════════════════════════════════
//  DTO serialization
// ═══════════════════════════════════════════════════════════════

public class PointerDtoTests
{
    [Fact]
    public void PointerDownRequest_Defaults_AreCorrect()
    {
        var req = new PointerDownRequest();
        req.PointerId.Should().Be(1);
        req.Device.Should().Be("touch");
        req.Pressure.Should().BeApproximately(1.0, 0.001);
        req.CoordinateSpace.Should().Be("normalized");
        req.Orientation.Should().Be(0);
        req.ContactWidth.Should().Be(4);
        req.ContactHeight.Should().Be(4);
        req.TiltX.Should().Be(0);
        req.TiltY.Should().Be(0);
        req.Rotation.Should().BeApproximately(0.0, 0.001);
        req.Barrel.Should().BeFalse();
        req.Eraser.Should().BeFalse();
    }

    [Fact]
    public void PointerDownRequest_TouchFields_Deserialize()
    {
        var json = """
            {
                "pointerId": 3,
                "x": 0.25,
                "y": 0.75,
                "device": "touch",
                "pressure": 0.9,
                "orientation": 45,
                "contactWidth": 10,
                "contactHeight": 6,
                "coordinateSpace": "pixels"
            }
            """;
        var req = JsonConvert.DeserializeObject<PointerDownRequest>(json)!;
        req.PointerId.Should().Be(3);
        req.X.Should().BeApproximately(0.25, 0.001);
        req.Y.Should().BeApproximately(0.75, 0.001);
        req.Device.Should().Be("touch");
        req.Pressure.Should().BeApproximately(0.9, 0.001);
        req.Orientation.Should().Be(45);
        req.ContactWidth.Should().Be(10);
        req.ContactHeight.Should().Be(6);
        req.CoordinateSpace.Should().Be("pixels");
    }

    [Fact]
    public void PointerDownRequest_PenFields_Deserialize()
    {
        var json = """
            {
                "pointerId": 1,
                "x": 0.5,
                "y": 0.5,
                "device": "pen",
                "pressure": 0.7,
                "tiltX": 20,
                "tiltY": -15,
                "rotation": 90.0,
                "barrel": true,
                "eraser": false
            }
            """;
        var req = JsonConvert.DeserializeObject<PointerDownRequest>(json)!;
        req.Device.Should().Be("pen");
        req.TiltX.Should().Be(20);
        req.TiltY.Should().Be(-15);
        req.Rotation.Should().BeApproximately(90.0, 0.001);
        req.Barrel.Should().BeTrue();
        req.Eraser.Should().BeFalse();
    }

    [Fact]
    public void PointerMoveRequest_Defaults_AreNullable()
    {
        var json = """{ "x": 0.3, "y": 0.4 }""";
        var req = JsonConvert.DeserializeObject<PointerMoveRequest>(json)!;
        req.Pressure.Should().BeNull();
        req.TiltX.Should().BeNull();
        req.TiltY.Should().BeNull();
        req.Rotation.Should().BeNull();
    }

    [Fact]
    public void PointerMoveRequest_PenOverrides_Deserialize()
    {
        var json = """
            {
                "x": 0.3,
                "y": 0.4,
                "pointerId": 2,
                "pressure": 0.6,
                "tiltX": 5,
                "tiltY": -5,
                "rotation": 270.0
            }
            """;
        var req = JsonConvert.DeserializeObject<PointerMoveRequest>(json)!;
        req.PointerId.Should().Be(2);
        req.Pressure!.Value.Should().BeApproximately(0.6, 0.001);
        req.TiltX!.Value.Should().Be(5);
        req.TiltY!.Value.Should().Be(-5);
        req.Rotation!.Value.Should().BeApproximately(270.0, 0.001);
    }

    [Fact]
    public void PointerUpRequest_DefaultPointerId_IsOne()
    {
        var req = new PointerUpRequest();
        req.PointerId.Should().Be(1);
    }

    [Fact]
    public void PointerUpRequest_Deserializes_PointerId()
    {
        var req = JsonConvert.DeserializeObject<PointerUpRequest>("""{ "pointerId": 7 }""")!;
        req.PointerId.Should().Be(7);
    }

    [Fact]
    public void PointerDownResponse_Serializes_AllFields()
    {
        var r = new PointerDownResponse
        {
            Success = true,
            PointerId = 2,
            ActivePointers = 2,
            ResolvedCoordinates = new CoordinatePoint(0.5, 0.5)
        };
        var json = JsonConvert.SerializeObject(r);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("pointerId").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("activePointers").GetInt32().Should().Be(2);
    }

    [Fact]
    public void PointerDownResponse_ErrorField_Serializes()
    {
        var r = new PointerDownResponse
        {
            Success = false,
            PointerId = 1,
            Error = "Pointer 1 is already active."
        };
        var json = JsonConvert.SerializeObject(r);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Contain("already active");
    }

    [Fact]
    public void PointerMoveResponse_Serializes()
    {
        var r = new PointerMoveResponse
        {
            Success = true,
            PointerId = 1,
            ResolvedCoordinates = new CoordinatePoint(0.3, 0.7)
        };
        var json = JsonConvert.SerializeObject(r);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("pointerId").GetInt32().Should().Be(1);
    }

    [Fact]
    public void PointerUpResponse_Serializes_ActivePointers()
    {
        var r = new PointerUpResponse
        {
            Success = true,
            PointerId = 1,
            ActivePointers = 0
        };
        var json = JsonConvert.SerializeObject(r);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("activePointers").GetInt32().Should().Be(0);
    }
}
