#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;


namespace Plugin.BLE.Abstractions.Utils;

public class CancelableTaskSync
{
	/// <summary>
	/// Await the return value of a function from a synchronous context
	/// </summary>
	/// <typeparam name="T"> The type of value the function will return </typeparam>
	/// <param name="inputFunc"> The function that returns the value itself </param>
	/// <param name="cancellationToken"> cancellation token for the operation </param>
	/// <returns> the value or its default </returns>
	public static T? AwaitInput<T>(Func<T?> inputFunc, CancellationToken cancellationToken)
	{
		var tcs = new TaskCompletionSource<T?>();
		using (cancellationToken.Register(() => tcs.TrySetCanceled()))
		{
			Task.Run(() =>
			{
				try
				{
					if (cancellationToken.IsCancellationRequested)
						tcs.TrySetCanceled();
					else
						tcs.TrySetResult(inputFunc());
				}
				catch (OperationCanceledException) { tcs.TrySetCanceled(); }
				catch (Exception ex) { tcs.TrySetException(ex); }
			}, cancellationToken);

			try // Wait for input or cancellation
			{
				var resultTask = tcs.Task;
				resultTask.Wait(cancellationToken);
				return resultTask.Result;
			}
			catch (AggregateException ex) when (ex.InnerException is TaskCanceledException) { return default; }
		}
	}
}
