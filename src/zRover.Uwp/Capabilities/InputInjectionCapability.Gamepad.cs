using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Tools.InputInjection;
using Windows.Gaming.Input;
using Windows.UI.Input.Preview.Injection;

namespace zRover.Uwp.Capabilities
{
    public sealed partial class InputInjectionCapability
    {
        private const string GamepadInputSchema = @"{
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

        private const string GamepadSequenceSchema = @"{
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

        private void RegisterGamepadTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_gamepad_input",
                "Injects a single gamepad input state — buttons, thumbsticks, and triggers. " +
                "The state is held for holdDurationMs then released (all-zero state sent). " +
                "For complex sequences, use inject_gamepad_sequence instead.",
                GamepadInputSchema,
                InjectGamepadInputAsync);

            registry.RegisterTool(
                "inject_gamepad_sequence",
                "Injects a sequence of gamepad frames in order. " +
                "Each frame specifies buttons, sticks, triggers, and a hold duration. " +
                "After the last frame, a neutral (all-zero) state is sent to release everything. " +
                "Use this for combo inputs, movement sequences, or timed button presses.",
                GamepadSequenceSchema,
                InjectGamepadSequenceAsync);
        }

        private static InjectedInputGamepadInfo BuildGamepadInfo(
            List<string> buttons,
            double leftStickX, double leftStickY,
            double rightStickX, double rightStickY,
            double leftTrigger, double rightTrigger)
        {
            var gamepadButtons = GamepadButtons.None;
            foreach (var btn in buttons)
            {
                if (Enum.TryParse<GamepadButtons>(btn, ignoreCase: true, out var parsed))
                    gamepadButtons |= parsed;
            }

            return new InjectedInputGamepadInfo
            {
                Buttons = gamepadButtons,
                LeftThumbstickX = leftStickX,
                LeftThumbstickY = leftStickY,
                RightThumbstickX = rightStickX,
                RightThumbstickY = rightStickY,
                LeftTrigger = leftTrigger,
                RightTrigger = rightTrigger
            };
        }

        private static InjectedInputGamepadInfo NeutralGamepadState()
        {
            return new InjectedInputGamepadInfo
            {
                Buttons = GamepadButtons.None,
                LeftThumbstickX = 0,
                LeftThumbstickY = 0,
                RightThumbstickX = 0,
                RightThumbstickY = 0,
                LeftTrigger = 0,
                RightTrigger = 0
            };
        }

        private async Task<string> InjectGamepadInputAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectGamepadInputRequest>(argsJson)
                      ?? new InjectGamepadInputRequest();

            LogToFile($"InjectGamepadInputAsync: buttons=[{string.Join(",", req.Buttons)}] hold={req.HoldDurationMs}ms");

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectGamepadInputResponse
                {
                    Success = false,
                    Buttons = req.Buttons,
                    HoldDurationMs = req.HoldDurationMs
                });
            }

            Exception? error = null;
            await _runOnUiThread(() =>
            {
                try
                {
                    var info = BuildGamepadInfo(req.Buttons,
                        req.LeftStickX, req.LeftStickY,
                        req.RightStickX, req.RightStickY,
                        req.LeftTrigger, req.RightTrigger);
                    injector.InjectGamepadInput(info);

                    if (req.HoldDurationMs > 0)
                        System.Threading.Thread.Sleep(req.HoldDurationMs);

                    // Release
                    injector.InjectGamepadInput(NeutralGamepadState());
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
                LogToFile($"GamepadInput FAILED: {error.Message}");
            else
                LogToFile("GamepadInput succeeded");

            return JsonConvert.SerializeObject(new InjectGamepadInputResponse
            {
                Success = error == null,
                Buttons = req.Buttons,
                HoldDurationMs = req.HoldDurationMs
            });
        }

        private async Task<string> InjectGamepadSequenceAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectGamepadSequenceRequest>(argsJson)
                      ?? new InjectGamepadSequenceRequest();

            LogToFile($"InjectGamepadSequenceAsync: {req.Frames.Count} frames");

            var injector = _injector;
            if (injector == null || _runOnUiThread == null || req.Frames.Count == 0)
            {
                return InjectorUnavailableResponse(new InjectGamepadSequenceResponse
                {
                    Success = false,
                    FrameCount = req.Frames.Count,
                    TotalDurationMs = 0
                });
            }

            int totalMs = 0;
            Exception? error = null;

            foreach (var frame in req.Frames)
            {
                await _runOnUiThread(() =>
                {
                    try
                    {
                        var info = BuildGamepadInfo(frame.Buttons,
                            frame.LeftStickX, frame.LeftStickY,
                            frame.RightStickX, frame.RightStickY,
                            frame.LeftTrigger, frame.RightTrigger);
                        injector.InjectGamepadInput(info);
                    }
                    catch (Exception ex) { error = ex; }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                if (error != null) break;

                totalMs += frame.DurationMs;
                if (frame.DurationMs > 0)
                    await Task.Delay(frame.DurationMs).ConfigureAwait(false);
            }

            // Send neutral release state
            if (error == null)
            {
                await _runOnUiThread(() =>
                {
                    try { injector.InjectGamepadInput(NeutralGamepadState()); }
                    catch { /* best-effort release */ }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }

            if (error != null)
                LogToFile($"GamepadSequence FAILED: {error.Message}");
            else
                LogToFile($"GamepadSequence succeeded: {req.Frames.Count} frames, {totalMs}ms");

            return JsonConvert.SerializeObject(new InjectGamepadSequenceResponse
            {
                Success = error == null,
                FrameCount = req.Frames.Count,
                TotalDurationMs = totalMs
            });
        }
    }
}
