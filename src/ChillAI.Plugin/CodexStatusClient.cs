using System;
using System.Net.Http;
using System.Threading;
using UnityEngine;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 轮询本地 Bridge /codex/status 的客户端。
    /// 拉取在后台线程进行，主线程只读取快照，绝不阻塞游戏。
    /// </summary>
    public sealed class CodexStatusClient
    {
        /// <summary>一次拉取得到的完整状态快照。</summary>
        public sealed class Snapshot
        {
            public string State = "unknown";
            public string Detail = "";
            public string LastEvent = "";
            public long SecondsSinceLastEvent;
            public bool Connected;
            public string Error = "";
        }

        private readonly string _baseUrl;
        private readonly float _pollIntervalSeconds;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private readonly object _lock = new object();
        private Snapshot _current = new Snapshot();
        private float _lastPollTime;

        public CodexStatusClient(string baseUrl, float pollIntervalSeconds)
        {
            _baseUrl = baseUrl;
            _pollIntervalSeconds = pollIntervalSeconds;
        }

        /// <summary>每帧调用；到点后丢一个后台任务去拉取（不阻塞主线程）。</summary>
        public void Update()
        {
            if (Time.unscaledTime - _lastPollTime < _pollIntervalSeconds)
            {
                return;
            }
            _lastPollTime = Time.unscaledTime;
            ThreadPool.QueueUserWorkItem(_ => Fetch());
        }

        /// <summary>主线程随时读取的最新快照。</summary>
        public Snapshot Current
        {
            get
            {
                lock (_lock)
                {
                    return _current;
                }
            }
        }

        private void Fetch()
        {
            try
            {
                using var resp = _http.GetAsync(_baseUrl + "/codex/status").Result;
                var json = resp.Content.ReadAsStringAsync().Result;
                var snap = new Snapshot();
                if (resp.IsSuccessStatusCode)
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                    snap.State = (string)obj["state"] ?? "unknown";
                    snap.Detail = (string)obj["detail"] ?? "";
                    snap.LastEvent = (string)obj["lastEvent"] ?? "";
                    snap.SecondsSinceLastEvent = (long)(obj["secondsSinceLastEvent"] ?? 0L);
                    snap.Connected = true;
                }
                else
                {
                    snap.Error = "HTTP " + (int)resp.StatusCode;
                }
                lock (_lock)
                {
                    _current = snap;
                }
            }
            catch (Exception ex)
            {
                var snap = new Snapshot { Error = ex.Message };
                lock (_lock)
                {
                    _current = snap;
                }
            }
        }
    }
}
