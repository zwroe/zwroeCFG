using System;
using System.Threading;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Rebind.Services
{
    /// <summary>
    /// Wrapper for the Nefarius.ViGEm.Client library.
    /// Creates and manages a virtual Xbox 360 controller that the game will read from.
    /// </summary>
    public class ViGEmService : IDisposable
    {
        private ViGEmClient? _client;
        private IXbox360Controller? _controller;

        public bool IsConnected { get; private set; }
        public string? ErrorMessage { get; private set; }

        /// <summary>
        /// Connects to the ViGEmBus driver and initializes a virtual Xbox 360 controller.
        /// </summary>
        public ViGEmService()
        {
            try
            {
                // Simple, old-school initialization
                _client = new ViGEmClient();
                _controller = _client.CreateXbox360Controller();
                _controller.Connect();
                IsConnected = true;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                ErrorMessage = ex.Message;
                Console.WriteLine($"VIGEM ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the state of a specific virtual controller button.
        /// </summary>
        public void SetButton(Xbox360Button button, bool pressed)
        {
            try { _controller?.SetButtonState(button, pressed); } catch { }
        }

        /// <summary>
        /// Updates the state of a specific virtual controller axis (e.g., thumbsticks).
        /// Value ranges from -32768 to 32767.
        /// </summary>
        public void SetAxis(Xbox360Axis axis, short value)
        {
            try { _controller?.SetAxisValue(axis, value); } catch { }
        }

        /// <summary>
        /// Disconnects the virtual controller and frees ViGEmBus resources.
        /// </summary>
        public void Dispose()
        {
            try { _controller?.Disconnect(); } catch { }
            try { _client?.Dispose(); } catch { }
        }
    }
}
