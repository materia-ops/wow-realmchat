// Test stand-in for ollama.exe (compile with the legacy C# 5 csc; no deps).
// Speaks just enough of the Ollama CLI + HTTP API for RealmChat's E2E tests:
//   ollama-stub -v            -> prints a version line
//   ollama-stub serve         -> HTTP on 127.0.0.1:<port from OLLAMA_HOST>
//   ollama-stub pull <model>  -> creates the model marker + progress lines
// State lives in the directory pointed at by OLLAMA_MODELS: a "model.present"
// marker (created by pull) and a per-serve "loaded" flag after a generate.
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

internal static class OllamaStub
{
    private static bool loaded;

    private static int Main(string[] args)
    {
        string version = Environment.GetEnvironmentVariable("STUB_VERSION") ?? "0.31.2";
        string stateDir = Environment.GetEnvironmentVariable("OLLAMA_MODELS") ?? Path.GetTempPath();
        string marker = Path.Combine(stateDir, "model.present");

        if (args.Length > 0 && args[0] == "-v")
        {
            Console.WriteLine("ollama version is " + version);
            return 0;
        }

        if (args.Length > 1 && args[0] == "rm")
        {
            string extra = Path.Combine(stateDir, "extra.present");
            if (File.Exists(extra) && File.ReadAllText(extra).Trim() == args[1])
            {
                File.Delete(extra);
                Console.WriteLine("deleted '" + args[1] + "'");
                return 0;
            }
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == args[1])
            {
                File.Delete(marker);
                Console.WriteLine("deleted '" + args[1] + "'");
                return 0;
            }
            Console.Error.WriteLine("model '" + args[1] + "' not found");
            return 1;
        }

        if (args.Length > 1 && args[0] == "pull")
        {
            Console.WriteLine("pulling manifest");
            Console.WriteLine("pulling 00000000: 100%");
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(marker, args[1]);
            Console.WriteLine("success");
            return 0;
        }

        if (args.Length > 0 && args[0] == "serve")
        {
            int port = 11434;
            string host = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "";
            int colon = host.LastIndexOf(':');
            if (colon >= 0) int.TryParse(host.Substring(colon + 1), out port);

            var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Start();
            Console.WriteLine("stub listening on " + port);
            while (true)
            {
                var ctx = listener.GetContext();
                string path = ctx.Request.Url.AbsolutePath;
                string body;
                if (path == "/api/version")
                {
                    body = "{\"version\":\"" + version + "\"}";
                }
                else if (path == "/api/tags")
                {
                    var entries = new System.Collections.Generic.List<string>();
                    if (File.Exists(marker))
                        entries.Add("{\"name\":\"" + File.ReadAllText(marker).Trim() + "\",\"size\":5368709120}");
                    string extra = Path.Combine(stateDir, "extra.present");
                    if (File.Exists(extra))
                        entries.Add("{\"name\":\"" + File.ReadAllText(extra).Trim() + "\",\"size\":2147483648}");
                    body = "{\"models\":[" + string.Join(",", entries.ToArray()) + "]}";
                }
                else if (path == "/api/ps")
                {
                    body = (loaded && File.Exists(marker))
                        ? "{\"models\":[{\"name\":\"" + File.ReadAllText(marker) + "\"}]}"
                        : "{\"models\":[]}";
                }
                else if (path == "/api/generate")
                {
                    Thread.Sleep(200);
                    loaded = true;
                    body = "{\"response\":\"ready\"}";
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    body = "{}";
                }
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
        }

        Console.Error.WriteLine("unknown args");
        return 2;
    }
}
