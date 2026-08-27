using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace DynamicNpcs
{
    /// <summary>
    /// Lets UnityWebRequest be awaited: <c>await request.SendWebRequest();</c>.
    /// Continuations run on the Unity main thread (both in play mode and in the editor).
    /// </summary>
    public static class UnityWebRequestExtensions
    {
        public static TaskAwaiter<UnityWebRequest> GetAwaiter(this UnityWebRequestAsyncOperation operation)
        {
            var tcs = new TaskCompletionSource<UnityWebRequest>();
            if (operation.isDone)
                tcs.TrySetResult(operation.webRequest);
            else
                operation.completed += _ => tcs.TrySetResult(operation.webRequest);
            return tcs.Task.GetAwaiter();
        }
    }
}
