using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Rebind.Services;

namespace Rebind.Views
{
    public partial class InputTestWindow : Window
    {
        private readonly InputTrace _trace;
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
        private InputTraceEntry? _lastEntry;

        internal InputTestWindow(InputTrace trace)
        {
            InitializeComponent();
            _trace = trace;
            _refreshTimer.Tick += (_, _) => RefreshTrace();
            TraceText.Text = "Start capture, then press a configured key.";
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_trace.IsCapturing)
            {
                _trace.Stop();
                _refreshTimer.Stop();
                RefreshTrace();
                CaptureButton.Content = "Start capture";
                CaptureStatus.Text = "Capture stopped · Select and copy the trace if needed";
                return;
            }

            _trace.Start();
            _lastEntry = null;
            TraceText.Text = "Waiting for a mapped input or app output…";
            CaptureButton.Content = "Stop capture";
            CaptureStatus.Text = "Capturing · Gaps are measured from the previous row";
            _refreshTimer.Start();
        }

        private void RefreshTrace()
        {
            InputTraceEntry[] entries = _trace.Snapshot();
            if (entries.Length == 0 || _lastEntry == entries[^1]) return;
            _lastEntry = entries[^1];

            var output = new StringBuilder("Elapsed ms      Gap ms  Source      Event").AppendLine();
            double? previousMs = null;
            foreach (InputTraceEntry entry in entries)
            {
                string gap = previousMs.HasValue ? FormattableString.Invariant($"{entry.ElapsedMs - previousMs.Value:F2}") : "–";
                output.AppendLine(FormattableString.Invariant($"{entry.ElapsedMs,10:F2}  {gap,10}  {entry.Channel,-10}  {entry.Detail}"));
                previousMs = entry.ElapsedMs;
            }

            TraceText.Text = output.ToString();
            TraceText.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer.Stop();
            _trace.Stop();
            base.OnClosed(e);
        }
    }
}
