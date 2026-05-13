using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.UI;
using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Ijewel3D
{
    public class IJewelStream : Rhino.Commands.Command
    {
        private readonly IjewelStreamServerUtility _streamServer = new IjewelStreamServerUtility();
        private readonly string _baseUrl = "http://playground.ijewel3d.com/v2-test/?rhino";

        public override string EnglishName => "IJewelStream";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                RhinoApp.WriteLine("Checking internet connectivity...");

                if (!_streamServer.CheckInternetConnectivity())
                {
                    RhinoApp.WriteLine("Error: No internet connection detected.");
                    ShowNoInternetDialog();
                }

                RhinoApp.WriteLine("Internet connection verified.");
                RhinoApp.WriteLine("Starting IJewelStream...");

                _streamServer.StartModelStream(doc);

                if (_streamServer.chosenPort == null)
                {
                    RhinoApp.WriteLine("Error: No free port found.");
                    return Result.Failure;
                }

                if (Rhino.Runtime.HostUtils.RunningOnOSX)
                {
                    if (!BrowserLauncher.LaunchBrowser(_baseUrl, (int)_streamServer.chosenPort))
                    {
                        var webViewForm = new IjewelStreamWebViewForm(_baseUrl, _streamServer);
                        webViewForm.Show();
                    }
                }
                else
                {
                    var webViewForm = new IjewelStreamWebViewForm(_baseUrl, _streamServer);
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

        private static void ShowNoInternetDialog()
        {
            Dialogs.ShowMessage(
                "iJewel requires an internet connection to function properly. " +
                "Please check your internet connection and try again.",
                "No Internet Connection",
                ShowMessageButton.OK,
                ShowMessageIcon.Warning
            );
        }
    }

    internal sealed class IjewelStreamWebViewForm : Form
    {
        private readonly WebView _webView;
        private readonly IjewelStreamServerUtility _streamServer;

        public IjewelStreamWebViewForm(string uri, IjewelStreamServerUtility streamServer)
        {
            _streamServer = streamServer;
            Title = "iJewel3D Stream";
            WindowState = WindowState.Maximized;
            MinimumSize = new Eto.Drawing.Size(800, 600);

            if (uri.Contains("?"))
            {
                uri += "&p=" + (streamServer.chosenPort ?? streamServer.DEFAULT_FALLBACK_PORT);
            }
            else
            {
                uri += "?p=" + (streamServer.chosenPort ?? streamServer.DEFAULT_FALLBACK_PORT);
            }

            _webView = new WebView
            {
                Url = new Uri(uri)
            };

            Content = _webView;
            Closing += WebViewFormClosing;
        }

        private void WebViewFormClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                _webView?.Dispose();
                _streamServer.StopModelStream();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error during stream form cleanup: {ex.Message}");
            }
        }
    }

    internal sealed class IjewelStreamServerUtility
    {
        private static readonly HttpClient HttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        public int? chosenPort = null;
        public int DEFAULT_FALLBACK_PORT = 8469;

        private TCPServer _server;
        private IjewelStreamChangeDatabase _streamDatabase;
        private IjewelStreamModelObserver _observer;

        public void StartModelStream(RhinoDoc doc)
        {
            StopModelStream();

            chosenPort = FindFreePort();
            if (chosenPort == null)
            {
                RhinoApp.WriteLine("No free ports available. Aborting");
                return;
            }

            _streamDatabase = IjewelStreamChangeDatabase.Create(doc);
            _observer = new IjewelStreamModelObserver(doc, _streamDatabase);

            _server = new TCPServer(chosenPort.Value, _streamDatabase);
            _server.Start();

            RhinoApp.WriteLine($"Started stream server on port {chosenPort}");
        }

        public void StopModelStream()
        {
            _server?.Dispose();
            _server = null;

            _observer?.Dispose();
            _observer = null;

            _streamDatabase?.Dispose();
            _streamDatabase = null;
        }

        private int? FindFreePort()
        {
            for (var port = DEFAULT_FALLBACK_PORT; port < DEFAULT_FALLBACK_PORT + 30; port++)
            {
                if (!IsPortInUse(port))
                {
                    return port;
                }
            }

            return null;
        }

        private static bool IsPortInUse(int port)
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

        public bool CheckInternetConnectivity()
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable()) return false;
                using (var response = HttpClient.GetAsync("http://google.com/generate_204").Result)
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

    internal sealed class IjewelStreamModelObserver : IDisposable
    {
        private readonly uint _docSerialNumber;
        private readonly IjewelStreamChangeDatabase _streamDatabase;
        private bool _disposed;

        public IjewelStreamModelObserver(RhinoDoc doc, IjewelStreamChangeDatabase streamDatabase)
        {
            _docSerialNumber = doc.RuntimeSerialNumber;
            _streamDatabase = streamDatabase ?? throw new ArgumentNullException(nameof(streamDatabase));

            RhinoDoc.AddRhinoObject += OnChanged;
            RhinoDoc.DeleteRhinoObject += OnChanged;
            RhinoDoc.UndeleteRhinoObject += OnChanged;
            RhinoDoc.ReplaceRhinoObject += OnChanged;
            RhinoDoc.ModifyObjectAttributes += OnObjectAttributesChanged;
            RhinoDoc.BeforeTransformObjects += OnTransformChanged;
            RhinoDoc.MaterialTableEvent += OnChanged;
            RhinoDoc.LayerTableEvent += OnChanged;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RhinoDoc.AddRhinoObject -= OnChanged;
            RhinoDoc.DeleteRhinoObject -= OnChanged;
            RhinoDoc.UndeleteRhinoObject -= OnChanged;
            RhinoDoc.ReplaceRhinoObject -= OnChanged;
            RhinoDoc.ModifyObjectAttributes -= OnObjectAttributesChanged;
            RhinoDoc.BeforeTransformObjects -= OnTransformChanged;
            RhinoDoc.MaterialTableEvent -= OnChanged;
            RhinoDoc.LayerTableEvent -= OnChanged;
        }

        private void OnChanged(object sender, EventArgs e)
        {
            if (!IsTargetDocument()) return;
            _streamDatabase.QueueStream();
        }

        private void OnObjectAttributesChanged(object sender, RhinoModifyObjectAttributesEventArgs e)
        {
            if (!IsTargetDocument()) return;
            _streamDatabase.QueueObjectAttributesChange(e);
        }

        private void OnTransformChanged(object sender, EventArgs e)
        {
            if (!IsTargetDocument()) return;
            _streamDatabase.QueueTransformStream();
        }

        private bool IsTargetDocument()
        {
            return RhinoDoc.ActiveDoc?.RuntimeSerialNumber == _docSerialNumber;
        }
    }
}
