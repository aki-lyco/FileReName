using System;
using System.Windows.Threading;

namespace Explore
{
    /// <summary>
    /// IProgress&lt;T&gt; �̒ʒm�� DispatcherTimer �ŊԈ����� UI �X���b�h�ɓ�����w���p�B
    /// Report �͍��p�x�ŌĂ΂�Ă� OK�B�Ō�̒l���� interval ���� UI �ɔ��f���܂��B
    /// </summary>
    public sealed class ThrottledProgress<T> : IProgress<T>, IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Action<T> _onTick;
        private bool _hasValue;
        private T _latest = default!;
        private bool _disposed;

        public ThrottledProgress(Dispatcher dispatcher, TimeSpan interval, Action<T> onTick)
        {
            _onTick = onTick ?? throw new ArgumentNullException(nameof(onTick));
            _timer = new DispatcherTimer(interval, DispatcherPriority.Background, (s, e) =>
            {
                if (_hasValue)
                {
                    _hasValue = false;
                    _onTick(_latest);
                }
            }, dispatcher);
            _timer.Start();
        }

        public void Report(T value)
        {
            if (_disposed) return;
            _latest = value;
            _hasValue = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
        }
    }
}
