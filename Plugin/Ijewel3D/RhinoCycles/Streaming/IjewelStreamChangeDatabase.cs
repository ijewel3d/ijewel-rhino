using Rhino;
using Rhino.DocObjects;
using Rhino.Display;
using RhinoCyclesCore;
using RhinoCyclesCore.Converters;
using RhinoCyclesCore.Database;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ijewel3D
{
    internal sealed class IjewelStreamChangeDatabase : ChangeDatabase
    {
        private const int StreamDebounceMilliseconds = 500;
        private const int TransformStreamIntervalMilliseconds = 33;
        private const int StreamSendTimeoutMilliseconds = 10000;
        private const int StreamCloseTimeoutMilliseconds = 2000;

        private readonly object _socketLock = new object();
        private readonly List<WebSocket> _sockets = new List<WebSocket>();
        private readonly Dictionary<WebSocket, SemaphoreSlim> _socketSendLocks = new Dictionary<WebSocket, SemaphoreSlim>();
        private readonly object _drainLock = new object();
        private readonly SemaphoreSlim _streamLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _disposeTokenSource = new CancellationTokenSource();

        private Timer _debounceTimer;
        private Timer _transformTimer;
        private bool _dirty;
        private bool _fullRequested;
        private bool _draining;
        private bool _transformDraining;
        private bool _worldCreated;
        private bool _disposedStreaming;
        private long _sequence;
        private StreamFrame _frame;

        private IjewelStreamChangeDatabase(
            Guid pluginId,
            RhinoCyclesCore.RenderEngine engine,
            uint doc,
            ViewInfo view,
            DisplayPipelineAttributes attributes,
            bool modal,
            BitmapConverter bitmapConverter)
            : base(pluginId, engine, doc, view, attributes, modal, bitmapConverter)
        {
        }

        public static IjewelStreamChangeDatabase Create(RhinoDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var pluginId = Ijewel3DPlugin.Instance?.Id ?? Guid.Empty;
            var view = doc.Views.ActiveView != null
                ? new ViewInfo(doc.Views.ActiveView.ActiveViewport)
                : new ViewInfo(doc.RuntimeSerialNumber);

            var engine = new RhinoCyclesCore.RenderEngine
            {
                View = view
            };

            return new IjewelStreamChangeDatabase(
                pluginId,
                engine,
                doc.RuntimeSerialNumber,
                view,
                null,
                false,
                new BitmapConverter())
            {
                ModelAbsoluteTolerance = doc.ModelAbsoluteTolerance,
                ModelAngleToleranceRadians = doc.ModelAngleToleranceRadians,
                ModelUnits = doc.ModelUnitSystem
            };
        }

        public void QueueStream(bool immediate = false, bool full = false)
        {
            lock (_drainLock)
            {
                _dirty = true;
                _fullRequested |= full || !_worldCreated;

                lock (_socketLock)
                {
                    if (_sockets.Count == 0) return;
                }

                if (_debounceTimer == null)
                {
                    _debounceTimer = new Timer(_ => Task.Run(FlushStreamAsync), null, Timeout.Infinite, Timeout.Infinite);
                }

                _debounceTimer.Change(immediate ? 0 : StreamDebounceMilliseconds, Timeout.Infinite);
            }
        }

        public void QueueTransformStream()
        {
            lock (_socketLock)
            {
                if (_sockets.Count == 0) return;
                EnsureTransformTimer();
            }
        }

        public async Task HandleModelWebSocket(HttpListenerContext context, CancellationToken cancellationToken)
        {
            WebSocket socket = null;
            using (var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeTokenSource.Token))
            {
                try
                {
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    socket = wsContext.WebSocket;

                    lock (_socketLock)
                    {
                        _sockets.Add(socket);
                        _socketSendLocks[socket] = new SemaphoreSlim(1, 1);
                        EnsureTransformTimer();
                    }

                    QueueStream(true, true);

                    var buffer = new byte[1];
                    while (socket.State == WebSocketState.Open && !linkedTokenSource.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linkedTokenSource.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await CloseSocketAsync(socket, linkedTokenSource.Token);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"Model WebSocket error: {ex.Message}");
                }
                finally
                {
                    if (socket != null)
                    {
                        RemoveSocket(socket);
                    }
                }
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                if (_disposedStreaming)
                {
                    return;
                }

                _disposedStreaming = true;
                _disposeTokenSource.Cancel();
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                _transformTimer?.Dispose();
                _transformTimer = null;

                lock (_socketLock)
                {
                    foreach (var socket in _sockets.ToArray())
                    {
                        try { socket.Abort(); socket.Dispose(); }
                        catch { }
                    }

                    _sockets.Clear();
                    _socketSendLocks.Clear();
                }

                _disposeTokenSource.Dispose();
                _streamLock.Dispose();
            }

            base.Dispose(isDisposing);
        }

        public override void UploadMeshChanges()
        {
            if (_frame == null) return;

            foreach (var deletedMesh in StreamObjectDatabase.MeshesToDelete)
            {
                _frame.DeletedMeshes.Add(deletedMesh);
            }

            foreach (var meshChange in StreamObjectDatabase.MeshChanges)
            {
                var mesh = CloneMesh(meshChange.Value);
                if (mesh?.MeshId == null) continue;

                var streamMesh = StreamObjectDatabase.FindMeshRelation(mesh.MeshId);
                if (streamMesh == null)
                {
                    streamMesh = new ccl.Mesh();
                    StreamObjectDatabase.RecordObjectMeshRelation(mesh.MeshId, streamMesh);
                }

                streamMesh.StreamPayload = mesh;
                AddMeshToFrame(mesh);
            }
        }

        public override void UploadDynamicObjectTransforms()
        {
            if (_frame == null) return;

            _frame.Transforms.AddRange(StreamObjectDatabase.ObjectTransforms);
        }

        public override void UploadCameraChanges()
        {
            if (_frame == null) return;

            _frame.Camera = StreamCameraDatabase.LatestView();
        }

        public override void UploadObjectChanges()
        {
            if (_frame == null) return;

            _frame.DeletedObjects.AddRange(StreamObjectDatabase.DeletedObjects);

            foreach (var ob in StreamObjectDatabase.NewOrUpdatedObjects)
            {
                if (ob.meshid != null)
                {
                    var mesh = StreamObjectDatabase.FindMeshRelation(ob.meshid);
                    if (mesh?.StreamPayload == null)
                    {
                        RhinoApp.WriteLine($"Skipping stream object {ob.obid}: missing mesh relation {ob.meshid.Item1}:{ob.meshid.Item2}");
                        continue;
                    }

                    if (_frame.Full)
                    {
                        AddMeshToFrame(mesh.StreamPayload);
                    }
                }

                _frame.Objects.Add(ob);
                if (ob.meshid != null)
                {
                    StreamObjectDatabase.RecordObjectIdMeshIdRelation(ob.obid, ob.meshid);
                }

                if (StreamObjectDatabase.FindObjectRelation(ob.obid) == null)
                {
                    StreamObjectDatabase.RecordObjectRelation(ob.obid, new ccl.Object());
                }
            }
        }

        public override void UploadObjectShaderChanges()
        {
            if (_frame == null) return;

            _frame.MaterialAssignments.AddRange(StreamObjectShaderDatabase.ObjectShaderChanges);
        }

        public override void UploadGammaChanges()
        {
        }

        public override void UploadClippingPlaneChanges()
        {
        }

        public override void UploadShaderChanges()
        {
        }

        public override void UploadLightChanges()
        {
        }

        public override void UploadEnvironmentChanges()
        {
        }

        public override bool UploadIntegratorChanges()
        {
            return false;
        }

        public override bool UploadDisplayPipelineAttributesChanges()
        {
            return false;
        }

        private async Task FlushStreamAsync()
        {
            lock (_drainLock)
            {
                if (_draining) return;
                _draining = true;
            }

            try
            {
                while (true)
                {
                    bool full;
                    lock (_drainLock)
                    {
                        if (!_dirty) break;

                        full = _fullRequested || !_worldCreated;
                        _dirty = false;
                        _fullRequested = false;
                    }

                    await _streamLock.WaitAsync(_disposeTokenSource.Token);
                    try
                    {
                        var payload = BuildFrameOnMainThread(full);
                        if (payload != null && payload.Length > 0)
                        {
                            await Broadcast(payload);
                        }
                    }
                    finally
                    {
                        _streamLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Model WebSocket stream error: {ex.Message}");
            }
            finally
            {
                bool runAgain;
                lock (_drainLock)
                {
                    _draining = false;
                    runAgain = _dirty;
                }

                if (runAgain)
                {
                    _debounceTimer?.Change(0, Timeout.Infinite);
                }
            }
        }

        private async Task FlushTransformStreamAsync()
        {
            lock (_drainLock)
            {
                if (_transformDraining || _draining || _dirty || !_worldCreated) return;
                _transformDraining = true;
            }

            try
            {
                await _streamLock.WaitAsync(_disposeTokenSource.Token);
                try
                {
                    byte[] payload;
                    lock (_drainLock)
                    {
                        if (_dirty || !_worldCreated) return;
                    }

                    payload = BuildTransformFrameOnMainThread();
                    if (payload != null && payload.Length > 0)
                    {
                        await Broadcast(payload);
                    }
                }
                finally
                {
                    _streamLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Model WebSocket transform stream error: {ex.Message}");
            }
            finally
            {
                lock (_drainLock)
                {
                    _transformDraining = false;
                }
            }
        }

        private byte[] BuildFrameOnMainThread(bool full)
        {
            byte[] payload = null;
            Exception error = null;

            using (var wait = new ManualResetEventSlim(false))
            {
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        payload = BuildFrame(full);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        wait.Set();
                    }
                }));

                wait.Wait(_disposeTokenSource.Token);
            }

            if (error != null) throw error;
            return payload;
        }

        private byte[] BuildTransformFrameOnMainThread()
        {
            byte[] payload = null;
            Exception error = null;

            using (var wait = new ManualResetEventSlim(false))
            {
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        payload = BuildTransformFrame();
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        wait.Set();
                    }
                }));

                wait.Wait(_disposeTokenSource.Token);
            }

            if (error != null) throw error;
            return payload;
        }

        private byte[] BuildFrame(bool full)
        {
            _frame = new StreamFrame(++_sequence, full);
            try
            {
                if (full)
                {
                    CreateWorld(true);
                    _worldCreated = true;
                }
                else
                {
                    Flush();
                }

                UploadDisplayPipelineAttributesChanges();
                UploadIntegratorChanges();
                UploadClippingPlaneChanges();
                UploadGammaChanges();
                UploadEnvironmentChanges();
                UploadDynamicObjectTransforms();
                UploadCameraChanges();
                UploadShaderChanges();
                UploadMeshChanges();
                UploadLightChanges();
                UploadObjectChanges();
                UploadObjectShaderChanges();

                var json = _frame.ToJson();
                ResetChangeQueue();
            return Encoding.UTF8.GetBytes(json);
        }
            finally
            {
                _frame = null;
            }
        }

        private byte[] BuildTransformFrame()
        {
            if (!_worldCreated) return null;

            Flush();

            if (StreamObjectDatabase.ObjectTransforms.Count == 0) return null;

            var latestTransforms = new Dictionary<uint, CyclesObjectTransform>();
            foreach (var transform in StreamObjectDatabase.ObjectTransforms)
            {
                if (transform == null) continue;
                latestTransforms[transform.Id] = transform;
            }

            if (latestTransforms.Count == 0) return null;

            _frame = new StreamFrame(++_sequence, false);
            try
            {
                _frame.Transforms.AddRange(latestTransforms.Values);
                var json = _frame.ToJson();
                StreamObjectDatabase.ResetDynamicObjectTransformChangeQueue();
                return Encoding.UTF8.GetBytes(json);
            }
            finally
            {
                _frame = null;
            }
        }

        private void AddMeshToFrame(CyclesMesh mesh)
        {
            if (mesh?.MeshId == null) return;

            foreach (var existing in _frame.Meshes)
            {
                if (Equals(existing.MeshId, mesh.MeshId)) return;
            }

            _frame.Meshes.Add(mesh);
        }

        private static CyclesMesh CloneMesh(CyclesMesh mesh)
        {
            if (mesh == null) return null;

            return new CyclesMesh
            {
                MeshId = mesh.MeshId,
                MatId = mesh.MatId,
                Verts = CloneArray(mesh.Verts),
                Faces = CloneArray(mesh.Faces),
                Uvs = CloneUvs(mesh.Uvs),
                VertexNormals = CloneArray(mesh.VertexNormals),
                VertexColors = CloneArray(mesh.VertexColors),
                IsSolid = mesh.IsSolid,
                OcsFrame = mesh.OcsFrame
            };
        }

        private static float[] CloneArray(float[] values)
        {
            return values == null ? null : (float[])values.Clone();
        }

        private static int[] CloneArray(int[] values)
        {
            return values == null ? null : (int[])values.Clone();
        }

        private static List<Tuple<int, float[]>> CloneUvs(List<Tuple<int, float[]>> uvs)
        {
            if (uvs == null) return null;

            var cloned = new List<Tuple<int, float[]>>(uvs.Count);
            foreach (var uv in uvs)
            {
                cloned.Add(new Tuple<int, float[]>(uv.Item1, CloneArray(uv.Item2)));
            }

            return cloned;
        }

        private async Task Broadcast(byte[] payload)
        {
            KeyValuePair<WebSocket, SemaphoreSlim>[] sockets;
            lock (_socketLock)
            {
                sockets = new List<KeyValuePair<WebSocket, SemaphoreSlim>>(_socketSendLocks).ToArray();
            }

            foreach (var item in sockets)
            {
                var socket = item.Key;
                var sendLock = item.Value;

                if (socket.State != WebSocketState.Open)
                {
                    RemoveSocket(socket);
                    continue;
                }

                try
                {
                    using (var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(_disposeTokenSource.Token))
                    {
                        sendTimeout.CancelAfter(StreamSendTimeoutMilliseconds);

                        var lockTaken = false;
                        await sendLock.WaitAsync(sendTimeout.Token);
                        lockTaken = true;

                        try
                        {
                            lock (_socketLock)
                            {
                                if (!_socketSendLocks.ContainsKey(socket)) continue;
                            }

                            if (socket.State != WebSocketState.Open)
                            {
                                RemoveSocket(socket);
                                continue;
                            }

                            await socket.SendAsync(
                                new ArraySegment<byte>(payload),
                                WebSocketMessageType.Binary,
                                true,
                                sendTimeout.Token);
                        }
                        finally
                        {
                            if (lockTaken)
                            {
                                sendLock.Release();
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!_disposeTokenSource.IsCancellationRequested)
                    {
                        RhinoApp.WriteLine("Model WebSocket send timed out. Removing stale client.");
                    }

                    RemoveSocket(socket);
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"Model WebSocket send failed: {ex.Message}");
                    RemoveSocket(socket);
                }
            }
        }

        private void EnsureTransformTimer()
        {
            if (_transformTimer == null)
            {
                _transformTimer = new Timer(_ => Task.Run(FlushTransformStreamAsync), null, Timeout.Infinite, Timeout.Infinite);
            }

            _transformTimer.Change(TransformStreamIntervalMilliseconds, TransformStreamIntervalMilliseconds);
        }

        private async Task CloseSocketAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            SemaphoreSlim sendLock = null;
            lock (_socketLock)
            {
                _socketSendLocks.TryGetValue(socket, out sendLock);
            }

            if (sendLock == null)
            {
                RemoveSocket(socket);
                return;
            }

            using (var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeTokenSource.Token))
            {
                closeTimeout.CancelAfter(StreamCloseTimeoutMilliseconds);

                var lockTaken = false;
                try
                {
                    await sendLock.WaitAsync(closeTimeout.Token);
                    lockTaken = true;

                    try
                    {
                        if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, closeTimeout.Token);
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            sendLock.Release();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!_disposeTokenSource.IsCancellationRequested)
                    {
                        RhinoApp.WriteLine("Model WebSocket close timed out. Removing stale client.");
                    }

                    RemoveSocket(socket);
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"Model WebSocket close failed: {ex.Message}");
                    RemoveSocket(socket);
                }
            }
        }

        private void RemoveSocket(WebSocket socket)
        {
            lock (_socketLock)
            {
                _sockets.Remove(socket);
                _socketSendLocks.Remove(socket);
                if (_sockets.Count == 0)
                {
                    _transformTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }

            try { socket.Abort(); }
            catch { }

            try { socket.Dispose(); }
            catch { }
        }

        private sealed class StreamFrame
        {
            public StreamFrame(long sequence, bool full)
            {
                Sequence = sequence;
                Full = full;
            }

            public long Sequence { get; }
            public bool Full { get; }
            public List<CyclesMesh> Meshes { get; } = new List<CyclesMesh>();
            public List<CyclesObject> Objects { get; } = new List<CyclesObject>();
            public List<CyclesObject> DeletedObjects { get; } = new List<CyclesObject>();
            public List<Guid> DeletedMeshes { get; } = new List<Guid>();
            public List<CyclesObjectTransform> Transforms { get; } = new List<CyclesObjectTransform>();
            public List<CyclesObjectShader> MaterialAssignments { get; } = new List<CyclesObjectShader>();
            public CyclesView Camera { get; set; }

            public string ToJson()
            {
                var sb = new StringBuilder(1024);
                sb.Append('{');
                AppendProperty(sb, "type", "ijewel.rhino.patch");
                sb.Append(',');
                AppendProperty(sb, "seq", Sequence);
                sb.Append(',');
                AppendProperty(sb, "full", Full);
                sb.Append(',');
                AppendMeshes(sb);
                sb.Append(',');
                AppendObjects(sb);
                sb.Append(',');
                AppendDeletedObjects(sb);
                sb.Append(',');
                AppendDeletedMeshes(sb);
                sb.Append(',');
                AppendTransforms(sb);
                sb.Append(',');
                AppendMaterialAssignments(sb);
                if (Camera != null)
                {
                    sb.Append(',');
                    AppendCamera(sb);
                }
                sb.Append('}');
                return sb.ToString();
            }

            private void AppendMeshes(StringBuilder sb)
            {
                sb.Append("\"meshes\":[");
                for (var i = 0; i < Meshes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var mesh = Meshes[i];
                    sb.Append('{');
                    AppendMeshIdProperty(sb, "id", mesh.MeshId);
                    sb.Append(',');
                    AppendProperty(sb, "matid", mesh.MatId);
                    sb.Append(',');
                    AppendProperty(sb, "isSolid", mesh.IsSolid);
                    sb.Append(',');
                    AppendFloatArrayProperty(sb, "verts", mesh.Verts);
                    sb.Append(',');
                    AppendIntArrayProperty(sb, "faces", mesh.Faces);
                    sb.Append(',');
                    AppendFloatArrayProperty(sb, "normals", mesh.VertexNormals);
                    sb.Append(',');
                    AppendFloatArrayProperty(sb, "vertexColors", mesh.VertexColors);
                    sb.Append(',');
                    AppendUvs(sb, mesh.Uvs);
                    sb.Append(',');
                    AppendTransformProperty(sb, "ocsFrame", mesh.OcsFrame);
                    sb.Append('}');
                }
                sb.Append(']');
            }

            private void AppendObjects(StringBuilder sb)
            {
                sb.Append("\"objects\":[");
                for (var i = 0; i < Objects.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var ob = Objects[i];
                    sb.Append('{');
                    AppendProperty(sb, "obid", ob.obid);
                    sb.Append(',');
                    AppendMeshIdProperty(sb, "meshid", ob.meshid);
                    sb.Append(',');
                    AppendProperty(sb, "matid", ob.matid);
                    sb.Append(',');
                    AppendProperty(sb, "materialName", ob.MaterialName);
                    sb.Append(',');
                    AppendProperty(sb, "layerName", ob.LayerName);
                    sb.Append(',');
                    AppendProperty(sb, "layerFullPath", ob.LayerFullPath);
                    sb.Append(',');
                    AppendProperty(sb, "materialSource", ob.MaterialSource);
                    sb.Append(',');
                    AppendProperty(sb, "visible", ob.Visible);
                    sb.Append(',');
                    AppendProperty(sb, "castShadow", ob.CastShadow);
                    sb.Append(',');
                    AppendProperty(sb, "castNoShadow", ob.CastNoShadow);
                    sb.Append(',');
                    AppendProperty(sb, "isShadowCatcher", ob.IsShadowCatcher);
                    sb.Append(',');
                    AppendProperty(sb, "isSolid", ob.IsSolid);
                    sb.Append(',');
                    AppendTransformProperty(sb, "transform", ob.Transform);
                    sb.Append(',');
                    AppendTransformProperty(sb, "ocsFrame", ob.OcsFrame);
                    sb.Append('}');
                }
                sb.Append(']');
            }

            private void AppendDeletedObjects(StringBuilder sb)
            {
                sb.Append("\"deletedObjects\":[");
                for (var i = 0; i < DeletedObjects.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(DeletedObjects[i].obid);
                }
                sb.Append(']');
            }

            private void AppendDeletedMeshes(StringBuilder sb)
            {
                sb.Append("\"deletedMeshes\":[");
                for (var i = 0; i < DeletedMeshes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendStringValue(sb, DeletedMeshes[i].ToString());
                }
                sb.Append(']');
            }

            private void AppendTransforms(StringBuilder sb)
            {
                sb.Append("\"transforms\":[");
                for (var i = 0; i < Transforms.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var transform = Transforms[i];
                    sb.Append('{');
                    AppendProperty(sb, "obid", transform.Id);
                    sb.Append(',');
                    AppendTransformProperty(sb, "transform", transform.Transform);
                    sb.Append('}');
                }
                sb.Append(']');
            }

            private void AppendMaterialAssignments(StringBuilder sb)
            {
                sb.Append("\"materialAssignments\":[");
                for (var i = 0; i < MaterialAssignments.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var assignment = MaterialAssignments[i];
                    sb.Append('{');
                    AppendProperty(sb, "obid", assignment.Id);
                    sb.Append(',');
                    AppendProperty(sb, "oldMatid", assignment.OldShaderHash);
                    sb.Append(',');
                    AppendProperty(sb, "matid", assignment.NewShaderHash);
                    sb.Append(',');
                    AppendProperty(sb, "materialName", assignment.MaterialName);
                    sb.Append(',');
                    AppendProperty(sb, "layerName", assignment.LayerName);
                    sb.Append(',');
                    AppendProperty(sb, "layerFullPath", assignment.LayerFullPath);
                    sb.Append(',');
                    AppendProperty(sb, "materialSource", assignment.MaterialSource);
                    sb.Append('}');
                }
                sb.Append(']');
            }

            private void AppendCamera(StringBuilder sb)
            {
                sb.Append("\"camera\":{");
                AppendProperty(sb, "projection", Camera.Projection.ToString());
                sb.Append(',');
                AppendProperty(sb, "lensLength", Camera.LensLength);
                sb.Append(',');
                AppendProperty(sb, "near", Camera.Near);
                sb.Append(',');
                AppendProperty(sb, "far", Camera.Far);
                sb.Append(',');
                AppendProperty(sb, "width", Camera.Width);
                sb.Append(',');
                AppendProperty(sb, "height", Camera.Height);
                sb.Append(',');
                AppendTransformProperty(sb, "transform", Camera.Transform);
                sb.Append(',');
                AppendTransformProperty(sb, "rhinoTransform", Camera.RhinoTransform);
                sb.Append('}');
            }

            private static void AppendUvs(StringBuilder sb, List<Tuple<int, float[]>> uvs)
            {
                sb.Append("\"uvs\":[");
                if (uvs != null)
                {
                    for (var i = 0; i < uvs.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('{');
                        AppendProperty(sb, "channel", uvs[i].Item1);
                        sb.Append(',');
                        AppendFloatArrayProperty(sb, "values", uvs[i].Item2);
                        sb.Append('}');
                    }
                }
                sb.Append(']');
            }

            private static void AppendProperty(StringBuilder sb, string name, string value)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                AppendStringValue(sb, value);
            }

            private static void AppendProperty(StringBuilder sb, string name, bool value)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                sb.Append(value ? "true" : "false");
            }

            private static void AppendProperty(StringBuilder sb, string name, int value)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                sb.Append(value.ToString(CultureInfo.InvariantCulture));
            }

            private static void AppendProperty(StringBuilder sb, string name, uint value)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                sb.Append(value.ToString(CultureInfo.InvariantCulture));
            }

            private static void AppendProperty(StringBuilder sb, string name, long value)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                sb.Append(value.ToString(CultureInfo.InvariantCulture));
            }

            private static void AppendProperty(StringBuilder sb, string name, double value)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }

            private static void AppendMeshIdProperty(StringBuilder sb, string name, Tuple<Guid, int> meshId)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                if (meshId == null)
                {
                    sb.Append("null");
                    return;
                }

                sb.Append('{');
                AppendProperty(sb, "guid", meshId.Item1.ToString());
                sb.Append(',');
                AppendProperty(sb, "index", meshId.Item2);
                sb.Append('}');
            }

            private static void AppendFloatArrayProperty(StringBuilder sb, string name, float[] values)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                sb.Append('[');
                if (values != null)
                {
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
                    }
                }
                sb.Append(']');
            }

            private static void AppendIntArrayProperty(StringBuilder sb, string name, int[] values)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                sb.Append('[');
                if (values != null)
                {
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
                    }
                }
                sb.Append(']');
            }

            private static void AppendTransformProperty(StringBuilder sb, string name, ccl.Transform transform)
            {
                AppendStringValue(sb, name);
                sb.Append(':');
                AppendTransformValue(sb, transform);
            }

            private static void AppendTransformValue(StringBuilder sb, ccl.Transform transform)
            {
                if (transform == null)
                {
                    sb.Append("null");
                    return;
                }

                sb.Append('[');
                AppendFloat4Values(sb, transform.x);
                sb.Append(',');
                AppendFloat4Values(sb, transform.y);
                sb.Append(',');
                AppendFloat4Values(sb, transform.z);
                sb.Append(']');
            }

            private static void AppendFloat4Values(StringBuilder sb, ccl.float4 value)
            {
                sb.Append('[');
                sb.Append(value.x.ToString("R", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(value.y.ToString("R", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(value.z.ToString("R", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(value.w.ToString("R", CultureInfo.InvariantCulture));
                sb.Append(']');
            }

            private static void AppendStringValue(StringBuilder sb, string value)
            {
                if (value == null)
                {
                    sb.Append("null");
                    return;
                }

                sb.Append('"');
                foreach (var ch in value)
                {
                    switch (ch)
                    {
                        case '\\':
                            sb.Append("\\\\");
                            break;
                        case '"':
                            sb.Append("\\\"");
                            break;
                        case '\b':
                            sb.Append("\\b");
                            break;
                        case '\f':
                            sb.Append("\\f");
                            break;
                        case '\n':
                            sb.Append("\\n");
                            break;
                        case '\r':
                            sb.Append("\\r");
                            break;
                        case '\t':
                            sb.Append("\\t");
                            break;
                        default:
                            if (ch < 32)
                            {
                                sb.Append("\\u");
                                sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(ch);
                            }
                            break;
                    }
                }
                sb.Append('"');
            }
        }
    }
}
