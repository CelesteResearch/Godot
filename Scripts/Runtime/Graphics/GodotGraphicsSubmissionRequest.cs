using System;
using System.Threading.Tasks;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Graphics
{
    internal sealed class GodotGraphicsSubmissionRequest
    {
        private readonly Action _operation;

        private readonly TaskCompletionSource<bool>
            _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal GodotGraphicsSubmissionRequest(Action operation)
        {
            _operation = operation;
        }

        internal void Execute()
        {
            try
            {
                _operation();
                _completed.TrySetResult(true);
            }
            catch (Exception error)
            {
                _completed.TrySetException(error);
            }
        }

        internal void Cancel(Exception error)
        {
            _completed.TrySetException(error);
        }

        internal void Wait()
        {
            _completed.Task.GetAwaiter().GetResult();
        }
    }
}
