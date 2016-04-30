using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Es.Net
{
    public sealed class SocketAwaitable : INotifyCompletion, IDisposable
    {
        private static readonly Action Completed = () => { };

        internal bool WasCompleted;
        private Action _continuation;
        internal SocketAsyncEventArgs EventArgs { get; }
        private CancellationTokenRegistration _ctr;
        private bool _cancelled;

        public SocketAwaitable(SocketAsyncEventArgs eventArgs, CancellationToken token)
        {
            if (eventArgs == null)
                throw new ArgumentNullException(nameof(eventArgs));

            EventArgs = eventArgs;

            _ctr = token.Register(() =>
            {
                _cancelled = true;
                OnEventArgsOnCompleted(null, null);
            });

            eventArgs.Completed += OnEventArgsOnCompleted;
        }

        private void OnEventArgsOnCompleted(object sender, SocketAsyncEventArgs args)
        {
            var prev = Interlocked.CompareExchange(ref _continuation, Completed, null); // returns the original value at _continuation

            if (prev == null || prev == Completed)
                return;

            var originalValue = _continuation;
            prev = Interlocked.CompareExchange(ref _continuation, Completed, originalValue); // force to Completed
            if (prev == originalValue) // if we failed someone else already forced it.
                prev?.Invoke(); // otherwise we are the only one to latch onto the continuation callback, so call it.
        }

        internal void Reset()
        {
            if (_cancelled)
                throw new TaskCanceledException();

            WasCompleted = false;
            _continuation = null;
        }

        public SocketAwaitable GetAwaiter() { return this; }

        public bool IsCompleted => WasCompleted;

        public void OnCompleted(Action continuation)
        {
            if (_continuation == Completed  // we've completed
                || Interlocked.CompareExchange(ref _continuation, continuation, null) == Completed // we failed to translation from null -> continuation because OnEventArgsOnComplete run
                )
            {
                // task is done already, continue.
                continuation();
            }
        }

        public void GetResult()
        {
            if (_cancelled)
                throw new TaskCanceledException();

            if (EventArgs.SocketError != SocketError.Success)
                throw new SocketException((int)EventArgs.SocketError);
        }

        public void Dispose()
        {
            _ctr.Dispose();
        }
    }
}