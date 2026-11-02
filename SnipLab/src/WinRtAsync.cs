using System.Threading.Tasks;
using Windows.Foundation;

namespace SnipLab
{
    // Blocks until a WinRT async call finishes. The usual awaiter in
    // System.Runtime.WindowsRuntime needs the combined Windows.winmd, which this
    // machine does not have, so these hook the Completed callback directly.
    // Call them from a background thread, never the UI thread.
    internal static class WinRtAsync
    {
        public static T Wait<T>(IAsyncOperation<T> operation)
        {
            var done = new TaskCompletionSource<T>();
            operation.Completed = (op, status) =>
            {
                if (status == AsyncStatus.Completed) done.SetResult(op.GetResults());
                else if (status == AsyncStatus.Error) done.SetException(op.ErrorCode);
                else done.SetCanceled();
            };
            return done.Task.Result;
        }

        public static void Wait(IAsyncAction action)
        {
            var done = new TaskCompletionSource<bool>();
            action.Completed = (op, status) =>
            {
                if (status == AsyncStatus.Completed) done.SetResult(true);
                else if (status == AsyncStatus.Error) done.SetException(op.ErrorCode);
                else done.SetCanceled();
            };
            done.Task.Wait();
        }

        public static void Wait<TProgress>(IAsyncActionWithProgress<TProgress> action)
        {
            var done = new TaskCompletionSource<bool>();
            action.Completed = (op, status) =>
            {
                if (status == AsyncStatus.Completed) done.SetResult(true);
                else if (status == AsyncStatus.Error) done.SetException(op.ErrorCode);
                else done.SetCanceled();
            };
            done.Task.Wait();
        }
    }
}
