using Rhino;
using Rhino.Commands;
using Rhino.UI;
using Eto.Forms;
using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Reflection;
using System.Net.Http;
using System.Net.Sockets;
using System.Diagnostics;
using System.Collections.Generic;

namespace Ijewel3D
{
    public class IJewelViewer : Rhino.Commands.Command
    {
        private static HttpListener _listener;
        public static string BaseDirectory;
        private const int Port = 8469;
        private const int HttpsPort = 8443;
        private static Thread _serverThread;
        private static readonly HttpClient httpClient = new HttpClient();
        private static CancellationTokenSource _cancellationTokenSource;

        private static readonly string[] reliableHosts = {
            "www.cloudflare.com",
            "www.amazon.com",
            "www.google.com",
            "www.apple.com",
            "1.1.1.1",
            "8.8.8.8"
        };

        public IJewelViewer()
        {
            Instance = this;
            var obs = RhinoModelObserver.Instance;
            InitializeBaseDirectory();
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

        private bool IsPortInUse(int port)
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

        protected void StartFileServer()
        {
            if (IsPortInUse(Port))
            {
                RhinoApp.WriteLine($"Server already running on port {Port}");
                return;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            //_listener.Prefixes.Add($"https://*:{HttpsPort}/");
            _listener.Start();

            _cancellationTokenSource = new CancellationTokenSource();
            _serverThread = new Thread(() => ServerUtility.ServerThreadStart(_listener, _cancellationTokenSource.Token));
            _serverThread.Start();
            RhinoApp.WriteLine($"Started new server on port {Port}");
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

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                RhinoApp.WriteLine("Checking internet connectivity...");

                if (!CheckInternetConnectivity())
                {
                    RhinoApp.WriteLine("Error: No internet connection detected.");
                    ShowNoInternetDialog();
                    return Result.Failure;
                }

                RhinoApp.WriteLine("Internet connection verified.");
                RhinoApp.WriteLine("Starting IJewelViewer...");

                // Only start server if one isn't already running
                StartFileServer();

                RhinoApp.WriteLine("Exporting model...");
                ExportModel(doc);

                string uri = "https://ijewel.design/rhinoceros";

                if (Rhino.Runtime.HostUtils.RunningOnOSX)
                {
                    if (!BrowserLauncher.LaunchBrowserInMac(uri))
                    {
                        //launch webview if none of the supported browsers are available on mac
                        var webViewForm = new WebViewForm(uri);
                        webViewForm.Show();
                    }

                }
                else
                {

                    //use webview directly on windows
                    var webViewForm = new WebViewForm(uri);
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

        protected bool CheckInternetConnectivity()
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    return false;
                }

                using (var ping = new Ping())
                {
                    foreach (string host in reliableHosts)
                    {
                        try
                        {
                            PingReply reply = ping.Send(host, 3000);
                            if (reply?.Status == IPStatus.Success)
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error checking internet connectivity: {ex.Message}");
                return false;
            }
        }

        protected void ShowNoInternetDialog()
        {
            Action showDialog = delegate
            {
                Dialogs.ShowMessage(
                    "This command requires an internet connection to function properly. " +
                    "Please check your internet connection and try again.",
                    "No Internet Connection",
                    ShowMessageButton.OK,
                    ShowMessageIcon.Warning
                );
            };

            RhinoApp.InvokeOnUiThread(showDialog);
        }

        public static void ExportModel(RhinoDoc doc)
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);
                string fullPath = Path.Combine(BaseDirectory, "model.3dm");

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

        public WebViewForm(string uri)
        {
            Title = "iJewel3D";
            WindowState = WindowState.Maximized;
            MinimumSize = new Eto.Drawing.Size(800, 600);

            webView = new WebView
            {
                Url = new Uri("https://ijewel.design/rhinoceros")
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
                IJewelViewer.Instance?.StopFileServer();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error during form cleanup: {ex.Message}");
            }
        }
    }

    static class ServerUtility
    {

        private static Dictionary<string, string> ParseQueryString(string query)
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

        public static void ServerThreadStart(HttpListener listener, CancellationToken cancellationToken)
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

        private static void ProcessRequest(HttpListenerContext context)
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

        private static void ServeFile(HttpListenerContext context, string path)
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

        private static void AddCorsHeaders(HttpListenerResponse response)
        {
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Access-Control-Allow-Methods", "*");
            response.AddHeader("Access-Control-Allow-Headers", "*");
            response.AddHeader("Access-Control-Max-Age", "86400");
        }

        private static void HandleHasChangedEndpoint(HttpListenerContext context)
        {
            AddCorsHeaders(context.Response);

            // 1) Parse the query string for "force"
            var queryString = context.Request.Url.Query; // includes leading "?"
            Dictionary<string, string> parsed = ParseQueryString(queryString);

            bool force = parsed.ContainsKey("force");

            bool hasChanged = RhinoModelObserver.Instance.ModelHasChanged;
            if(hasChanged || force)
            {
                lock (RhinoModelObserver.hasChangedLock)
                {
                    IJewelViewer.ExportModel(Rhino.RhinoDoc.ActiveDoc);
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

        private static void HandleWhoAmI(HttpListenerContext context)
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
                Rhino.RhinoApp.WriteLine($"who_am_i error: {ex.Message}");
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                context.Response.Close();
            }
        }

    }

    public class IJewelDesign : IJewelViewer
    {
        public override string EnglishName => "IJewelDesign";
    }

    public class IJewel : IJewelViewer
    {
        public override string EnglishName => "IJewel";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                RhinoApp.WriteLine("Checking internet connectivity...");

                if (!CheckInternetConnectivity())
                {
                    RhinoApp.WriteLine("Error: No internet connection detected.");
                    ShowNoInternetDialog();
                    return Result.Failure;
                }

                RhinoApp.WriteLine("Internet connection verified.");
                RhinoApp.WriteLine("Starting iJewel client...");

                ExportModel(doc);


                RhinoModelObserver obs = RhinoModelObserver.Instance;
                obs.ModelHasChanged = true;

                // Only start server if one isn't already running
                StartFileServer();
                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error in RunCommand: {ex.Message}");
                RhinoApp.WriteLine($"Stack Trace: {ex.StackTrace}");
                return Result.Failure;
            }
        }
    }


    public static class BrowserLauncher
    {
        public static bool LaunchBrowserInMac(string link)
        {
            if (string.IsNullOrEmpty(link))
                return false;

            var launchAttempts = new List<ProcessStartInfo>
            {
                // 1) Open Chrome via 'open -a "Google Chrome"'
                new ProcessStartInfo{
                    FileName = "open",
                    Arguments = $"-na \"Google Chrome\" --args --new-window \"{link}\"",
                    UseShellExecute = false
                },
                // 2) full path to Chrome in .app
                new ProcessStartInfo{
                    FileName = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                    Arguments = link,
                    UseShellExecute = false
                },

                // Firefox
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

                // Opera
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
                    RhinoApp.WriteLine("Open brwoser Attmpt " + attempts + "failed " + ex.Message + "\n");

                }
                attempts++;
            }

            if (!success)
            {

                Dialogs.ShowMessage(
                    "None of the supported browsers appear to be installed or accessible.\n\n" +
                    "Please install Google Chrome, Firefox, or Opera to use the plugin",
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
