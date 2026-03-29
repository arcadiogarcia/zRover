using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Tools.InputInjection;
using Windows.UI.Input.Preview.Injection;

namespace zRover.Uwp.Capabilities
{
    public sealed partial class InputInjectionCapability
    {
        private const string KeyPressSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""key"": { ""type"": ""string"", ""description"": ""Virtual key name (e.g. 'Enter', 'Tab', 'A', 'Left', 'Escape', 'Back', 'F5'). Uses Windows VirtualKey names without the 'Number' prefix for digits."" },
    ""modifiers"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""enum"": [""Control"", ""Shift"", ""Menu"", ""Windows""] }, ""default"": [], ""description"": ""Modifier keys to hold during the key press."" },
    ""holdDurationMs"": { ""type"": ""integer"", ""default"": 0, ""description"": ""How long to hold the key down in milliseconds. 0 for a normal press-and-release."" }
  },
  ""required"": [""key""]
}";

        private const string TextSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""text"": { ""type"": ""string"", ""description"": ""The text string to type. Each character is sent as a key press."" },
    ""delayBetweenKeysMs"": { ""type"": ""integer"", ""default"": 30, ""description"": ""Delay in ms between each keystroke."" }
  },
  ""required"": [""text""]
}";

        private void RegisterKeyboardTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_key_press",
                "Injects a keyboard key press with optional modifier keys. " +
                "Supports all Windows virtual key names. " +
                "Use holdDurationMs for long-press scenarios (e.g. holding a key in a game).",
                KeyPressSchema,
                InjectKeyPressAsync);

            registry.RegisterTool(
                "inject_text",
                "Types a string of text by injecting individual key presses for each character. " +
                "Handles uppercase letters and common symbols by automatically applying Shift. " +
                "For special keys (Enter, Tab, etc.), use inject_key_press instead.",
                TextSchema,
                InjectTextAsync);
        }

        private async Task<string> InjectKeyPressAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectKeyPressRequest>(argsJson)
                      ?? new InjectKeyPressRequest();

            LogToFile($"InjectKeyPressAsync: key={req.Key} modifiers=[{string.Join(",", req.Modifiers)}] hold={req.HoldDurationMs}ms");

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectKeyPressResponse
                {
                    Success = false,
                    Key = req.Key,
                    Modifiers = req.Modifiers
                });
            }

            Exception? error = null;
            await _runOnUiThread(() =>
            {
                try
                {
                    var vk = ParseVirtualKey(req.Key);

                    // Press modifiers
                    foreach (var mod in req.Modifiers)
                    {
                        var modVk = ParseVirtualKey(mod);
                        injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
                        {
                            VirtualKey = (ushort)modVk,
                            KeyOptions = InjectedInputKeyOptions.None
                        }});
                    }

                    // Press key
                    injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
                    {
                        VirtualKey = (ushort)vk,
                        KeyOptions = InjectedInputKeyOptions.None
                    }});

                    if (req.HoldDurationMs > 0)
                        System.Threading.Thread.Sleep(req.HoldDurationMs);

                    // Release key
                    injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
                    {
                        VirtualKey = (ushort)vk,
                        KeyOptions = InjectedInputKeyOptions.KeyUp
                    }});

                    // Release modifiers in reverse order
                    for (int i = req.Modifiers.Count - 1; i >= 0; i--)
                    {
                        var modVk = ParseVirtualKey(req.Modifiers[i]);
                        injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
                        {
                            VirtualKey = (ushort)modVk,
                            KeyOptions = InjectedInputKeyOptions.KeyUp
                        }});
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
            {
                LogToFile($"InjectKeyPress FAILED: {error.Message}");
                return JsonConvert.SerializeObject(new InjectKeyPressResponse
                {
                    Success = false,
                    Key = req.Key,
                    Modifiers = req.Modifiers
                });
            }

            LogToFile("InjectKeyPress succeeded");
            return JsonConvert.SerializeObject(new InjectKeyPressResponse
            {
                Success = true,
                Key = req.Key,
                Modifiers = req.Modifiers
            });
        }

        private async Task<string> InjectTextAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectTextRequest>(argsJson)
                      ?? new InjectTextRequest();

            LogToFile($"InjectTextAsync: length={req.Text?.Length ?? 0} delay={req.DelayBetweenKeysMs}ms");

            var injector = _injector;
            if (injector == null || _runOnUiThread == null || string.IsNullOrEmpty(req.Text))
            {
                return InjectorUnavailableResponse(new InjectTextResponse
                {
                    Success = false,
                    CharacterCount = 0
                });
            }

            int typed = 0;
            Exception? error = null;

            foreach (char c in req.Text!)
            {
                if (typed > 0 && req.DelayBetweenKeysMs > 0)
                    await Task.Delay(req.DelayBetweenKeysMs).ConfigureAwait(false);

                char ch = c;
                await _runOnUiThread(() =>
                {
                    try
                    {
                        InjectCharacter(injector, ch);
                        typed++;
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                if (error != null) break;
            }

            if (error != null)
                LogToFile($"InjectText FAILED at char {typed}: {error.Message}");
            else
                LogToFile($"InjectText succeeded: {typed} chars");

            return JsonConvert.SerializeObject(new InjectTextResponse
            {
                Success = error == null,
                CharacterCount = typed
            });
        }

        private void InjectCharacter(InputInjector injector, char c)
        {
            // Use Unicode scan code injection for broad character support
            injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
            {
                ScanCode = (ushort)c,
                KeyOptions = InjectedInputKeyOptions.Unicode
            }});
            injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
            {
                ScanCode = (ushort)c,
                KeyOptions = InjectedInputKeyOptions.Unicode | InjectedInputKeyOptions.KeyUp
            }});
        }

        private static Windows.System.VirtualKey ParseVirtualKey(string keyName)
        {
            if (Enum.TryParse<Windows.System.VirtualKey>(keyName, ignoreCase: true, out var vk))
                return vk;

            // Common aliases
            switch (keyName.ToLowerInvariant())
            {
                case "ctrl": return Windows.System.VirtualKey.Control;
                case "alt": return Windows.System.VirtualKey.Menu;
                case "win": return Windows.System.VirtualKey.LeftWindows;
                case "backspace": return Windows.System.VirtualKey.Back;
                case "return": return Windows.System.VirtualKey.Enter;
                case "esc": return Windows.System.VirtualKey.Escape;
                case "del": return Windows.System.VirtualKey.Delete;
                case "ins": return Windows.System.VirtualKey.Insert;
                case "pgup": return Windows.System.VirtualKey.PageUp;
                case "pgdn":
                case "pgdown": return Windows.System.VirtualKey.PageDown;
                case "up": return Windows.System.VirtualKey.Up;
                case "down": return Windows.System.VirtualKey.Down;
                case "left": return Windows.System.VirtualKey.Left;
                case "right": return Windows.System.VirtualKey.Right;
                case "space": return Windows.System.VirtualKey.Space;
                default:
                    // Try single digit/letter
                    if (keyName.Length == 1)
                    {
                        char ch = char.ToUpperInvariant(keyName[0]);
                        if (ch >= 'A' && ch <= 'Z')
                            return (Windows.System.VirtualKey)(int)ch;
                        if (ch >= '0' && ch <= '9')
                            return (Windows.System.VirtualKey)((int)Windows.System.VirtualKey.Number0 + (ch - '0'));
                    }
                    throw new ArgumentException($"Unknown virtual key: '{keyName}'");
            }
        }
    }
}
