using System;
using System.Windows.Threading;

namespace Explore
{
    /// <summary>
    /// IProgress ‚ğŠÔˆø‚¢‚Ä UI XV‚ğÅ‘å N ms ‚É 1 ‰ñ‚ÖB
    /// </summary>
    internal sealed class ThrottledProgress<T> : IProgress<T>, IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly TimeSpan _interval;
        private readonly Action<T> _onProgress;
        private readonly DispatcherTimer _timer;
        private readonly object _gate = new();

        private T _last = default!;
        private bool _dirty;
        private int _idleTicks;

        public ThrottledProgress(Dispatcher dispatcher, TimeSpan interval, Action<T> onProgress)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _interval = interval;
            _onProgress = onProgress ?? throw new ArgumentNullException(nameof(onProgress));

            _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = _interval
            };
            _timer.Tick += OnTick;
        }

        public void Report(T value)
        {
            lock (_gate)
            {
                _last = value;
                _dirty = true;
                _idleTicks = 0;
                if (!_timer.IsEnabled) _timer.Start();
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            T snapshot = default!;
            bool hasChange;
            lock (_gate)
            {
                hasChange = _dirty;
                if (hasChange) snapshot = _last;
                _dirty = false;

                if (!hasChange)
                {
                    _idleTicks++;
                    if (_idleTicks >= 2)
                    {
                        _timer.Stop();
                        _idleTicks = 0;
                    }
                }
            }
            if (hasChange) _onProgress(snapshot);
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
