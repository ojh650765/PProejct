using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace PokeLab.Online
{
    /// <summary>
    /// Where the Worker lives, and how the game is told.
    ///
    /// The URL is not compiled in. The Worker is deployed to the developer's own Cloudflare
    /// account and its hostname is not knowable when this is written, so the build ships a
    /// blank and the value is set once — from the account screen, or from a launch argument on
    /// a desktop test — and kept in <see cref="PlayerPrefs"/> from then on. A blank base URL is
    /// not an error state to recover from; it is simply "this build has no server", and every
    /// online entry in the menu says so rather than failing when pressed.
    /// </summary>
    public static class OnlineConfig
    {
        private const string UrlKey = "pokelab.online.baseUrl";

        /// <summary>
        /// The deployed Worker, so a fresh install can reach the backend without anybody being
        /// told to type a hostname into a settings field first.
        ///
        /// Filled in on 2026-08-20 from an actual deploy — <c>wrangler deploy</c> printed this
        /// URL and <c>/health</c> answered from it with the 53-species pool and the odds table.
        /// It is a compiled DEFAULT and not a constant: the account screen writes PlayerPrefs,
        /// which wins, so a local <c>wrangler dev</c> or a second deployment is still reachable
        /// without a rebuild.
        ///
        /// Nothing secret is here. It is a public endpoint; every route on it that touches an
        /// account requires the bearer token that only a correct recovery answer produces.
        /// </summary>
        private const string CompiledDefault = "https://pokelab-online.okh6507652.workers.dev";

        public static string BaseUrl
        {
            get
            {
                var stored = PlayerPrefs.GetString(UrlKey, "");
                return string.IsNullOrWhiteSpace(stored) ? CompiledDefault : stored.Trim();
            }
            set
            {
                PlayerPrefs.SetString(UrlKey, (value ?? "").Trim().TrimEnd('/'));
                PlayerPrefs.Save();
            }
        }

        /// <summary>False when this build has nowhere to talk to. Read by the menu, not guessed at.</summary>
        public static bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

        /// <summary>The websocket origin for the same deployment: https becomes wss, http becomes ws.</summary>
        public static string SocketBase
        {
            get
            {
                var http = BaseUrl;
                if (string.IsNullOrEmpty(http)) return "";
                if (http.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return "wss://" + http.Substring("https://".Length);
                if (http.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    return "ws://" + http.Substring("http://".Length);
                return http;
            }
        }
    }

    /// <summary>
    /// Every call the game makes to the Worker, as coroutines.
    ///
    /// Coroutines and <see cref="UnityWebRequest"/> rather than <c>HttpClient</c> and async,
    /// and the reason is the target: this game ships to WebGL, where there are no threads and
    /// <c>System.Net.Http</c> does not work at all. UnityWebRequest is the one HTTP path that
    /// behaves the same in the editor, in a desktop player and in a browser.
    ///
    /// <b>A failure is a response, never an exception.</b> Every method completes with a
    /// <c>T</c> whose <c>ok</c> is false and whose <c>error</c> says what happened — a
    /// timeout, a 500, a body that would not parse. Callers are UI screens, and a UI screen
    /// that has to try/catch around a coroutine is a UI screen that will forget to.
    /// </summary>
    public static class OnlineClient
    {
        /// <summary>Seconds before a request is abandoned. Long enough for a cold Worker start.</summary>
        private const int TimeoutSeconds = 20;

        public static IEnumerator Post<TResponse>(string path, object body, string token,
                                                  Action<TResponse> done)
            where TResponse : class, new()
        {
            yield return Send("POST", path, body, token, done);
        }

        public static IEnumerator Get<TResponse>(string path, string token, Action<TResponse> done)
            where TResponse : class, new()
        {
            yield return Send("GET", path, null, token, done);
        }

        private static IEnumerator Send<TResponse>(string method, string path, object body,
                                                   string token, Action<TResponse> done)
            where TResponse : class, new()
        {
            if (!OnlineConfig.IsConfigured)
            {
                done?.Invoke(Failed<TResponse>("no_server"));
                yield break;
            }

            var url = OnlineConfig.BaseUrl.TrimEnd('/') + path;

            using (var request = new UnityWebRequest(url, method))
            {
                if (body != null)
                {
                    var json = JsonUtility.ToJson(body);
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    request.SetRequestHeader("Content-Type", "application/json");
                }

                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = TimeoutSeconds;
                if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success
                    && request.responseCode == 0)
                {
                    // No response at all: DNS, CORS, an offline device. Distinguished from a
                    // server that answered with an error, because the two want different words
                    // on screen and only one of them is worth retrying.
                    Debug.LogWarning($"[Online] {method} {path} did not reach the server: {request.error}");
                    done?.Invoke(Failed<TResponse>("offline"));
                    yield break;
                }

                var text = request.downloadHandler != null ? request.downloadHandler.text : "";
                if (string.IsNullOrEmpty(text))
                {
                    Debug.LogWarning($"[Online] {method} {path} answered {request.responseCode} with an empty body.");
                    done?.Invoke(Failed<TResponse>("empty_response"));
                    yield break;
                }

                TResponse parsed = null;
                try { parsed = JsonUtility.FromJson<TResponse>(text); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Online] {method} {path} answered with a body that would " +
                                     $"not parse: {e.Message}");
                }

                if (parsed == null)
                {
                    done?.Invoke(Failed<TResponse>("bad_response"));
                    yield break;
                }

                done?.Invoke(parsed);
            }
        }

        /// <summary>
        /// A failure shaped like the response it stands in for.
        ///
        /// Reflection once per failed call, which is nothing next to the network round trip it
        /// replaces, and it keeps every response type free of a special failure constructor.
        /// </summary>
        private static TResponse Failed<TResponse>(string error) where TResponse : class, new()
        {
            var response = new TResponse();
            var okField = typeof(TResponse).GetField("ok");
            var errorField = typeof(TResponse).GetField("error");
            okField?.SetValue(response, false);
            errorField?.SetValue(response, error);
            return response;
        }

        /// <summary>
        /// The player-facing sentence for an error code.
        ///
        /// Kept in one place because the same handful of codes come back from six screens, and
        /// a message written per call site is how "offline" ends up phrased three ways.
        /// </summary>
        public static string Explain(string error)
        {
            switch (error)
            {
                case null:
                case "":
                    return Loc("Something went wrong.", "문제가 발생했어요.");
                case "no_server":
                    return Loc("No server is configured for this build.",
                               "이 빌드에는 서버 주소가 설정되어 있지 않아요.");
                case "offline":
                    return Loc("Could not reach the server.", "서버에 연결할 수 없어요.");
                case "empty_response":
                case "bad_response":
                    return Loc("The server answered with something unreadable.",
                               "서버 응답을 읽을 수 없었어요.");
                case "name_taken":
                    return Loc("That trainer name is already taken.", "이미 사용 중인 트레이너 이름이에요.");
                case "no_account":
                    return Loc("No account with that name.", "그런 이름의 계정이 없어요.");
                case "wrong_answer":
                    return Loc("That answer does not match.", "답이 일치하지 않아요.");
                case "rate_limited":
                    return Loc("Too many attempts. Try again in a minute.",
                               "시도가 너무 많아요. 잠시 후 다시 시도해 주세요.");
                case "unauthorised":
                    return Loc("Signed out. Please sign in again.", "로그인이 만료되었어요. 다시 로그인해 주세요.");
                case "already_rolled":
                    return Loc("This account already has a team.", "이미 팀을 뽑은 계정이에요.");
                case "version_mismatch":
                    return Loc("This build is out of date for the server.",
                               "빌드가 서버 버전과 맞지 않아요.");
                default:
                    return error;
            }
        }

        private static string Loc(string english, string korean) =>
            PokeLab.Core.Loc.Pick(english, korean);
    }
}
