using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace zRover.FullTrust.McpServer.AppServiceTest
{
    /// <summary>
    /// Test client to validate AppServiceConnection works for FullTrust↔UWP communication.
    /// </summary>
    public static class AppServiceConnectionTest
    {
        public static async Task<bool> RunTestAsync()
        {
            Console.WriteLine("=== AppServiceConnection Test ===");
            Console.WriteLine("Attempting to connect to UWP AppService...\n");

            AppServiceConnection? connection = null;

            try
            {
                connection = new AppServiceConnection
                {
                    AppServiceName = "com.zrover.toolinvocation",
                    PackageFamilyName = "zRover.Uwp.Sample_xaf3bmhg52ma0" // Must match actual PFN
                };

                Console.WriteLine($"Service Name: {connection.AppServiceName}");
                Console.WriteLine($"Package Family Name: {connection.PackageFamilyName}");
                Console.Write("Opening connection... ");

                var status = await connection.OpenAsync();
                
                if (status != AppServiceConnectionStatus.Success)
                {
                    Console.WriteLine($"❌ FAILED: {status}");
                    Console.WriteLine("\nPossible causes:");
                    Console.WriteLine("  - UWP app not running");
                    Console.WriteLine("  - Package family name mismatch");
                    Console.WriteLine("  - AppService not registered in manifest");
                    Console.WriteLine($"  - AppService entry point incorrect");
                    return false;
                }

                Console.WriteLine("✅ Connected!\n");

                // Test 1: Ping
                Console.WriteLine("Test 1: Ping command");
                var pingMessage = new ValueSet
                {
                    { "command", "ping" }
                };

                var pingResponse = await connection.SendMessageAsync(pingMessage);
                
                if (pingResponse.Status == AppServiceResponseStatus.Success)
                {
                    Console.WriteLine($"  ✅ Response status: {pingResponse.Status}");
                    if (pingResponse.Message.ContainsKey("message"))
                    {
                        Console.WriteLine($"  Message: {pingResponse.Message["message"]}");
                    }
                    if (pingResponse.Message.ContainsKey("timestamp"))
                    {
                        Console.WriteLine($"  Timestamp: {pingResponse.Message["timestamp"]}");
                    }
                }
                else
                {
                    Console.WriteLine($"  ❌ Response status: {pingResponse.Status}");
                    return false;
                }

                Console.WriteLine();

                // Test 2: Echo
                Console.WriteLine("Test 2: Echo command");
                var echoMessage = new ValueSet
                {
                    { "command", "echo" },
                    { "testData", "Hello from FullTrust!" },
                    { "number", 42 },
                    { "timestamp", DateTimeOffset.Now.ToString("O") }
                };

                var echoResponse = await connection.SendMessageAsync(echoMessage);
                
                if (echoResponse.Status == AppServiceResponseStatus.Success)
                {
                    Console.WriteLine($"  ✅ Response status: {echoResponse.Status}");
                    Console.WriteLine("  Echoed data:");
                    foreach (var kvp in echoResponse.Message)
                    {
                        Console.WriteLine($"    {kvp.Key} = {kvp.Value}");
                    }
                }
                else
                {
                    Console.WriteLine($"  ❌ Response status: {echoResponse.Status}");
                    return false;
                }

                Console.WriteLine("\n✅✅✅ All tests passed! AppServiceConnection works for FullTrust↔UWP IPC!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Exception: {ex.GetType().Name}");
                Console.WriteLine($"   Message: {ex.Message}");
                Console.WriteLine($"   HRESULT: 0x{ex.HResult:X8}");
                
                if (ex.HResult == unchecked((int)0x80040154))
                {
                    Console.WriteLine("\n   This usually means the AppService background task is not registered.");
                    Console.WriteLine("   Check that the manifest has the correct EntryPoint.");
                }
                
                return false;
            }
            finally
            {
                connection?.Dispose();
                Console.WriteLine("\n=== Test Complete ===\n");
            }
        }
    }
}
