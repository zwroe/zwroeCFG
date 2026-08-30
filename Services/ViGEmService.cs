using System;
using System.Threading;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Rebind.Services
{
    public class ViGEmService : IDisposable
    {
        private ViGEmClient? _client;
        private IXbox360Controller? _controller;

        public bool IsConnected { get; private set; }
        public string? ErrorMessage { get; private set; }

        public ViGEmService()
        {
            try
            {
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

        public void SetButton(Xbox360Button button, bool pressed)
        {
            try { _controller?.SetButtonState(button, pressed); } catch { }
        }

        public void SetAxis(Xbox360Axis axis, short value)
        {
            try { _controller?.SetAxisValue(axis, value); } catch { }
        }

        public void Dispose()
        {
            try { _controller?.Disconnect(); } catch { }
            try { _client?.Dispose(); } catch { }
        }
    }
}
