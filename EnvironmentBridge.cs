using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ur
{
    /// <summary>
    /// Stdin/stdout JSON-line IPC bridge for the GameEnvironment.
    /// 
    /// Protocol:
    ///   Each request is a single JSON line on stdin.
    ///   Each response is a single JSON line on stdout.
    ///
    /// Request format:
    ///   { "method": "reset" }
    ///   { "method": "step", "action": 3 }
    ///   { "method": "get_valid_actions" }
    ///   { "method": "get_state" }
    ///   { "method": "close" }
    ///
    /// Response format:
    ///   { "state": [...], "reward": 0.0, "done": false, "info": {}, "valid_actions": [...] }
    /// </summary>
    static class EnvironmentBridge
    {
        public static void RunBridge(string[] args)
        {
            int? seed = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--seed" && int.TryParse(args[i + 1], out int s))
                    seed = s;
            }

            var env = new GameEnvironment(seed);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                try
                {
                    var request = JsonSerializer.Deserialize<BridgeRequest>(line, options);
                    var response = HandleRequest(env, request);
                    string json = JsonSerializer.Serialize(response, options);
                    Console.WriteLine(json);
                    Console.Out.Flush();
                }
                catch (Exception ex)
                {
                    var error = new BridgeResponse
                    {
                        Info = new Dictionary<string, object> { { "error", ex.Message } }
                    };
                    string json = JsonSerializer.Serialize(error, options);
                    Console.WriteLine(json);
                    Console.Out.Flush();
                }
            }
        }

        private static BridgeResponse HandleRequest(GameEnvironment env, BridgeRequest request)
        {
            switch (request.Method?.ToLowerInvariant())
            {
                case "reset":
                {
                    var state = env.Reset();
                    var validActions = env.GetValidActions();
                    return new BridgeResponse
                    {
                        State = state,
                        Reward = 0f,
                        Done = false,
                        ValidActions = validActions,
                        Info = new Dictionary<string, object>()
                    };
                }
                case "step":
                {
                    var (state, reward, done, info) = env.Step(request.Action);
                    var validActions = env.GetValidActions();
                    return new BridgeResponse
                    {
                        State = state,
                        Reward = reward,
                        Done = done,
                        ValidActions = validActions,
                        Info = info
                    };
                }
                case "get_valid_actions":
                {
                    var validActions = env.GetValidActions();
                    return new BridgeResponse
                    {
                        ValidActions = validActions,
                        Info = new Dictionary<string, object>()
                    };
                }
                case "get_state":
                {
                    var state = env.GetState();
                    var validActions = env.GetValidActions();
                    return new BridgeResponse
                    {
                        State = state,
                        ValidActions = validActions,
                        Info = new Dictionary<string, object>()
                    };
                }
                case "close":
                {
                    Environment.Exit(0);
                    return new BridgeResponse(); // unreachable
                }
                default:
                {
                    return new BridgeResponse
                    {
                        Info = new Dictionary<string, object> { { "error", $"Unknown method: {request.Method}" } }
                    };
                }
            }
        }
    }

    class BridgeRequest
    {
        public string Method { get; set; }
        public int Action { get; set; }
    }

    class BridgeResponse
    {
        public float[] State { get; set; }
        public float Reward { get; set; }
        public bool Done { get; set; }
        public bool[] ValidActions { get; set; }
        public Dictionary<string, object> Info { get; set; }
    }
}
