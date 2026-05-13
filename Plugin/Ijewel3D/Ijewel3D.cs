using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Net.Http;



namespace Ijewel3D
{
    public class IJewelViewer : Rhino.Commands.Command
    {
        public static string BaseDirectory;

        protected ServerUtility serverUtility;
        protected string baseUrl = "https://ijewel.design/rhinoceros";



        public IJewelViewer()
        {
            Instance = this;
            RhinoModelObserver.Instance.ToString();
            InitializeBaseDirectory();
            serverUtility = new ServerUtility();
        }

        private void InitializeBaseDirectory()
        {
            string assemblyLocation = Assembly.GetExecutingAssembly().Location;
            string assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
            BaseDirectory = Path.Combine(assemblyDirectory, "resources");

            if (!Directory.Exists(BaseDirectory))
            {
                Directory.CreateDirectory(BaseDirectory);
            }
        }

        public static IJewelViewer Instance { get; private set; }

        public override string EnglishName => "IJewelViewer";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                RhinoApp.WriteLine("Checking internet connectivity...");

                if (!serverUtility.CheckInternetConnectivity())
                {
                    RhinoApp.WriteLine("Error: No internet connection detected.");
                    ShowNoInternetDialog();
                }

                RhinoApp.WriteLine("Internet connection verified.");
                RhinoApp.WriteLine("Starting IJewelViewer...");

                // Only start server if one isn't already running
                serverUtility.StartFileServer();

                if (serverUtility.chosenPort == null)
                {
                    RhinoApp.WriteLine("Error: No free port found.");
                    return Result.Failure;
                }

                RhinoApp.WriteLine("Exporting model...");
                ExportModel(doc, (int)serverUtility.chosenPort);

                string uri = baseUrl;

                if (Rhino.Runtime.HostUtils.RunningOnOSX)
                {
                    if (!BrowserLauncher.LaunchBrowser(uri, (int)serverUtility.chosenPort))
                    {
                        //launch webview if none of the supported browsers are available on mac
                        var webViewForm = new WebViewForm(uri, serverUtility);
                        webViewForm.Show();
                    }

                }
                else
                {

                    //use webview directly on windows
                    var webViewForm = new WebViewForm(uri, serverUtility);
                    webViewForm.Show();

                }

                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error in RunCommand: {ex.Message}");
                RhinoApp.WriteLine($"Stack Trace: {ex.StackTrace}");
                return Result.Failure;
            }
        }

        protected void ShowNoInternetDialog()
        {
            Dialogs.ShowMessage(
                "iJewel requires an internet connection to function properly. " +
                "Please check your internet connection and try again.",
                "No Internet Connection",
                ShowMessageButton.OK,
                ShowMessageIcon.Warning
            );
        }

        public static void ExportModel(RhinoDoc doc, int port)
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);
                string fullPath = Path.Combine(BaseDirectory, $"model{port}.3dm");

                RhinoObject.GetRenderMeshes(RhinoDoc.ActiveDoc.Objects.GetObjectList(ObjectType.AnyObject), true, false);

                if (!doc.Export(fullPath))
                {
                    RhinoApp.WriteLine("Export failed.");
                }
                else
                {
                    RhinoApp.WriteLine("Model exported successfully");
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Export error: {ex.Message}");
            }
        }
    }

    class WebViewForm : Form
    {
        private readonly WebView webView;
        private readonly ServerUtility serverUtility;

        public WebViewForm(string uri, ServerUtility serverUtility)
        {
            this.serverUtility = serverUtility;
            Title = "iJewel3D";
            WindowState = WindowState.Maximized;
            MinimumSize = new Eto.Drawing.Size(800, 600);

            //handle if the uri has some query params
            if (uri.Contains("?"))
            {
                uri += "&p=" + (serverUtility.chosenPort ?? serverUtility.DEFAULT_FALLBACK_PORT);
            }
            else
            {
                uri += "?p=" + (serverUtility.chosenPort ?? serverUtility.DEFAULT_FALLBACK_PORT);
            }

            webView = new WebView
            {
                Url = new Uri(uri)
            };

            Content = webView;

            // Handle form closing
            Closing += WebViewForm_Closing;
        }

        private void WebViewForm_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                // Clean up WebView resources
                webView?.Dispose();

                // Stop the file server
                serverUtility.StopFileServer();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error during form cleanup: {ex.Message}");
            }
        }
    }

    public class ServerUtility
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        public int? chosenPort = null;
        private HttpListener _listener;
        private CancellationTokenSource _cancellationTokenSource;
        private Thread _serverThread;
        public int DEFAULT_FALLBACK_PORT = 8469;

        public int? FindFreePort()
        {
            for (int p = DEFAULT_FALLBACK_PORT; p < DEFAULT_FALLBACK_PORT + 30; p++) 
            {
                if (!IsPortInUse(p)) 
                {
                    return p;
                }
            }

            return null;
        }

        public bool IsPortInUse(int port)
        {
            TcpListener tcpListener = null;
            try
            {
                tcpListener = new TcpListener(IPAddress.Loopback, port);
                tcpListener.Start();
                return false;
            }
            catch
            {
                return true;
            }
            finally
            {
                tcpListener?.Stop();
            }
        }

        public void StartFileServer()
        {
            if (_listener != null && _listener.IsListening) return;

            chosenPort = chosenPort ?? FindFreePort();
            if (chosenPort == null)
            {
                RhinoApp.WriteLine($"No free ports available. Aborting");
                return;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{chosenPort}/");
            _listener.Start();

            _cancellationTokenSource = new CancellationTokenSource();
            _serverThread = new Thread(() => ServerThreadStart(_listener, _cancellationTokenSource.Token));
            _serverThread.Start();
            RhinoApp.WriteLine($"Started new server on port {chosenPort}");
        }

        public void StopFileServer()
        {
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }

            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                _serverThread?.Join();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }
        }


        private Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(query))
                return result;

            // Remove leading '?'
            if (query.StartsWith("?"))
                query = query.Substring(1);

            // Split key/value pairs by '&'
            var pairs = query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                // Each pair might look like "force" or "force=true"
                var parts = pair.Split(new[] { '=' }, 2);
                string key = Uri.UnescapeDataString(parts[0]);
                string value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";

                result[key] = value;
            }

            return result;
        }

        public void ServerThreadStart(HttpListener listener, CancellationToken cancellationToken)
        {
            try
            {
                while (listener.IsListening && !cancellationToken.IsCancellationRequested)
                {
                    var context = listener.GetContext();
                    ProcessRequest(context);
                }
            }
            catch (HttpListenerException)
            {
                // Expected when listener is stopped
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Server thread exception: {ex.Message}");
            }
            finally
            {
                listener?.Close();
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            // Handle CORS preflight
            if (context.Request.HttpMethod == "OPTIONS")
            {
                AddCorsHeaders(context.Response);
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            if (context.Request.Url.AbsolutePath.Equals("/api/has-changed", StringComparison.OrdinalIgnoreCase))
            {
                HandleHasChangedEndpoint(context);
                return;
            }

            if (context.Request.Url.AbsolutePath.Equals("/who_am_i", StringComparison.OrdinalIgnoreCase))
            {
                HandleWhoAmI(context);
                return;
            }

            string filename = Path.GetFileName(context.Request.Url.AbsolutePath);
            string path = Path.Combine(IJewelViewer.BaseDirectory, filename);

            if (File.Exists(path))
            {
                ServeFile(context, path);
            }
            else
            {
                AddCorsHeaders(context.Response);
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
            }
        }

        private void ServeFile(HttpListenerContext context, string path)
        {
            try
            {
                byte[] content = File.ReadAllBytes(path);
                AddCorsHeaders(context.Response);
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength64 = content.Length;
                context.Response.OutputStream.Write(content, 0, content.Length);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"File serving error: {ex.Message}");
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private void AddCorsHeaders(HttpListenerResponse response)
        {
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Access-Control-Allow-Methods", "*");
            response.AddHeader("Access-Control-Allow-Headers", "*");
            response.AddHeader("Access-Control-Max-Age", "86400");
        }

        private void HandleHasChangedEndpoint(HttpListenerContext context)
        {
            AddCorsHeaders(context.Response);

            // 1) Parse the query string for "force"
            var queryString = context.Request.Url.Query; // includes leading "?"
            Dictionary<string, string> parsed = ParseQueryString(queryString);

            bool force = parsed.ContainsKey("force");

            bool hasChanged = RhinoModelObserver.Instance.ModelHasChanged;
            if (hasChanged || force)
            {
                lock (RhinoModelObserver.hasChangedLock)
                {
                    if (chosenPort == null)
                    {
                        return;
                    }
                    
                    IJewelViewer.ExportModel(RhinoDoc.ActiveDoc, (int)chosenPort);
                    if (hasChanged)
                    {
                        RhinoModelObserver.Instance.ResetModelChangedFlag();
                    }
                }
            }

            bool responseBool = hasChanged || force;

            byte[] responseBytes = System.Text.Encoding.UTF8.GetBytes(responseBool ? "true" : "false");

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = responseBytes.Length;
            context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
            context.Response.Close();
        }

        private void HandleWhoAmI(HttpListenerContext context)
        {
            try
            {
                AddCorsHeaders(context.Response);

                var pluginGuid = Ijewel3DPlugin.Instance?.Id ?? Guid.Empty; //replace this with your guid
                var text = pluginGuid == Guid.Empty ? "" : pluginGuid.ToString("D");
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);

                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"who_am_i error: {ex.Message}");
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                context.Response.Close();
            }
        }

        public bool CheckInternetConnectivity()
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable()) return false;
                using (var response = _httpClient.GetAsync("http://google.com/generate_204").Result)
                {
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

    }

    public class IJewelDesign : IJewelViewer
    {
        public override string EnglishName => "IJewelDesign";
    }

    public class IJewelPlayground : IJewelViewer
    {
        public override string EnglishName => "IJewelPlayground";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            this.baseUrl = "https://playground.ijewel3d.com/v2/?rhino";
            return base.RunCommand(doc, mode);
        }
    }

    public class IJewelPlatform : IJewelViewer
    {
        public override string EnglishName => "IJewelPlatform";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            this.baseUrl = "https://ijewel3d.com/drive/playground?rhino";
            return base.RunCommand(doc, mode);
        }
    }

    public class IJewelEnterprise : IJewelViewer
    {
        public override string EnglishName => "iJewelEnterprise";
        
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var gs = new Rhino.Input.Custom.GetString();
            gs.SetCommandPrompt("Enter your drive name");
            gs.AcceptNothing(true);
            gs.Get();

            string drive = null;
            if (gs.CommandResult() == Result.Success)
            {
                drive = gs.StringResult();
            }

            if (!string.IsNullOrWhiteSpace(drive))
            {
                this.baseUrl = $"https://ijewel3d.com/{drive}/playground?rhino";
            }
            else
            {
                this.baseUrl = "https://ijewel3d.com/drive/playground?rhino";
            }

            return base.RunCommand(doc, mode);
        }
    }


    public static class BrowserLauncher
    {
        public static bool LaunchBrowser(string link , int port)
        {
            if (string.IsNullOrEmpty(link))
                return false;

            if(link.Contains("?"))
            {
                link += "&p=" + port;
            }
            else
            {
                link += "?p=" + port;
            }

            List<ProcessStartInfo> launchAttempts;

            if (Rhino.Runtime.HostUtils.RunningOnOSX)
            {
                launchAttempts = new List<ProcessStartInfo>
                {
                    new ProcessStartInfo{
                        FileName = "open",
                        Arguments = $"-na \"Google Chrome\" --args --new-window \"{link}\"",
                        UseShellExecute = false
                    },
                    new ProcessStartInfo{
                        FileName = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                        Arguments = link,
                        UseShellExecute = false
                    },
                    new ProcessStartInfo{
                        FileName = "open",
                        Arguments = $"-a \"Firefox\" \"{link}\"",
                        UseShellExecute = false
                    },
                    new ProcessStartInfo
                    {
                        FileName = "/Applications/Firefox.app/Contents/MacOS/firefox",
                        Arguments = link,
                        UseShellExecute = false
                    },
                    new ProcessStartInfo{
                        FileName = "open",
                        Arguments = $"-a \"Opera\" \"{link}\"",
                        UseShellExecute = false
                    },
                    new ProcessStartInfo
                    {
                        FileName = "/Applications/Opera.app/Contents/MacOS/Opera",
                        Arguments = link,
                        UseShellExecute = false
                    }
                };
            }
            else
            {
                launchAttempts = new List<ProcessStartInfo>
                {
                    new ProcessStartInfo
                    {
                        FileName = link,
                        UseShellExecute = true,   // let Windows shell pick the handler (default browser)
                        Verb = "open"
                    }
                };
            }

            bool success = false;
            int attempts = 0;
            foreach (var psi in launchAttempts)
            {
                try
                {
                    Process.Start(psi);
                    success = true;
                    break;
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("Open browser Attempt " + attempts + " failed " + ex.Message + "\n");
                }
                attempts++;
            }

            if (!success)
            {
                string browserList = Rhino.Runtime.HostUtils.RunningOnOSX
                    ? "Google Chrome, Firefox, or Opera"
                    : "Google Chrome, Firefox, Opera, or Microsoft Edge";

                Dialogs.ShowMessage(
                    "None of the supported browsers appear to be installed or accessible.\n\n" +
                    $"Please install {browserList} to use the plugin",
                    "Error Launching Browser",
                    ShowMessageButton.OK,
                    ShowMessageIcon.Error
                );
            }

            return success;
        }
    }

    public sealed class RhinoModelObserver
    {
        private static RhinoModelObserver _instance;
        private static readonly object _lock = new object();
        public static readonly object hasChangedLock = new object();


        private bool _modelHasChanged;

        public static RhinoModelObserver Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance ?? (_instance = new RhinoModelObserver());
                }
            }
        }

        public bool ModelHasChanged
        {
            get { return _modelHasChanged; }
            set { _modelHasChanged = value; }
        }

        private RhinoModelObserver()
        {

            RhinoDoc.AddRhinoObject += (s, e) => MarkChanged();
            RhinoDoc.DeleteRhinoObject += (s, e) => MarkChanged();
            RhinoDoc.UndeleteRhinoObject += (s, e) => MarkChanged();
            RhinoDoc.ReplaceRhinoObject += (s, e) => MarkChanged();
            RhinoDoc.ModifyObjectAttributes += (s, e) => MarkChanged();
            RhinoDoc.BeforeTransformObjects += (s, e) => MarkChanged();
            RhinoDoc.MaterialTableEvent += (s, e) => MarkChanged();
            RhinoDoc.LayerTableEvent += (s, e) => MarkChanged();

        }

        private void MarkChanged()
        {
            lock (hasChangedLock)
            {
                _modelHasChanged = true;
            }
        }

        public void ResetModelChangedFlag()
        {
            _modelHasChanged = false;
        }
    }

}
