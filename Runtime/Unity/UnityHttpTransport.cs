using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace FlightmodeSdk
{
    sealed class UnityHttpTransport : IHttpTransport
    {
        readonly int _timeoutSeconds;

        public UnityHttpTransport(int timeoutSeconds)
        {
            _timeoutSeconds = timeoutSeconds;
        }

        public Task<HttpResult> PostJsonAsync(string url, string json)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            request.SetRequestHeader("Content-Type", "application/json");
            return Send(request);
        }

        public Task<HttpResult> GetAsync(string url)
        {
            return Send(UnityWebRequest.Get(url));
        }

        Task<HttpResult> Send(UnityWebRequest request)
        {
            request.timeout = _timeoutSeconds;
            var completion = new TaskCompletionSource<HttpResult>();
            request.SendWebRequest().completed += _ =>
            {
                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    completion.SetException(new System.IO.IOException(request.error));
                }
                else
                {
                    completion.SetResult(new HttpResult { Status = (int)request.responseCode, Body = request.downloadHandler.data ?? new byte[0] });
                }
                request.Dispose();
            };
            return completion.Task;
        }
    }
}
