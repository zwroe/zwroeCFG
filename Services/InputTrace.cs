using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Rebind.Services
{
    internal readonly record struct InputTraceEntry(double ElapsedMs, string Channel, string Detail);

    internal sealed class InputTrace
    {
        private const int Capacity = 300;
        private readonly object _gate = new();
        private readonly Queue<InputTraceEntry> _entries = new(Capacity);
        private volatile bool _isCapturing;
        private long _startTicks;
        private (int X, int Y)? _stickDirection;

        public bool IsCapturing => _isCapturing;

        public void Start()
        {
            lock (_gate)
            {
                _entries.Clear();
                _stickDirection = null;
                _startTicks = Stopwatch.GetTimestamp();
                _isCapturing = true;
            }
        }

        public void Stop()
        {
            lock (_gate)
                _isCapturing = false;
        }

        public InputTraceEntry[] Snapshot()
        {
            lock (_gate)
                return _entries.ToArray();
        }

        public void Record(string channel, string detail)
        {
            lock (_gate)
            {
                if (_isCapturing) AddEntry(channel, detail);
            }
        }

        public void RecordStick(short x, short y)
        {
            lock (_gate)
            {
                var direction = (Math.Sign(x), Math.Sign(y));
                if (!_isCapturing || _stickDirection == direction) return;
                _stickDirection = direction;
                AddEntry("Controller", $"Left stick X={x} Y={y}");
            }
        }

        private void AddEntry(string channel, string detail)
        {
            if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new InputTraceEntry(Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds, channel, detail));
        }
    }
}
