using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Es.Net
{
    internal sealed class SocketAwaitable : INotifyCompletion
    {
        private static readonly Action Sentinel = () => { };

        internal bool WasCompleted;
        internal Action Continuation;
        internal SocketAsyncEventArgs EventArgs { get; }

        private bool _cancelled;
        private CancellationTokenRegistration _reg;

        public SocketAwaitable(SocketAsyncEventArgs eventArgs, CancellationToken token)
        {
            if (eventArgs == null) throw new ArgumentNullException(nameof(eventArgs));
            EventArgs = eventArgs;

            eventArgs.Completed += OnEventArgsOnCompleted;

            _reg = token.Register(() =>
            {
                _cancelled = true;
                OnEventArgsOnCompleted(null, null);
                _reg.Dispose();
            });
        }

        private void OnEventArgsOnCompleted(object sender, SocketAsyncEventArgs args)
        {
            var prev = Continuation ?? Interlocked.CompareExchange(ref Continuation, Sentinel, null);
            prev?.Invoke();
        }

        internal void Reset()
        {
            WasCompleted = false;
            Continuation = null;
        }

        public SocketAwaitable GetAwaiter() { return this; }

        public bool IsCompleted => WasCompleted;

        public void OnCompleted(Action continuation)
        {
            if (Continuation == Sentinel ||
                Interlocked.CompareExchange(
                    ref Continuation, continuation, null) == Sentinel)
            {
                Task.Run(continuation);
            }
        }

        public void GetResult()
        {
            if (_cancelled)
                throw new TaskCanceledException();

            if (EventArgs.SocketError != SocketError.Success)
                throw new SocketException((int)EventArgs.SocketError);
        }
    }
}