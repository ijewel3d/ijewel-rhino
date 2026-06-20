using Rhino;
using Rhino.DocObjects;
using Rhino.Display;
using RhinoCyclesCore;
using RhinoCyclesCore.Converters;
using RhinoCyclesCore.Database;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ijewel3D
{
    public sealed class IjewelStreamChangeDatabase : ChangeDatabase
    {
        private const int StreamDebounceMilliseconds = 500;
        private const int TransformStreamIntervalMilliseconds = 33;
        private const int StreamSendTimeoutMilliseconds = 10000;

        private readonly object _socketLock = new object();
        private readonly List<IjewelStreamClient> _clients = new List<IjewelStreamClient>();
        private readonly Dictionary<IjewelStreamClient, SemaphoreSlim> _clientSendLocks = new Dictionary<IjewelStreamClient, SemaphoreSlim>();
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
        private readonly ConcurrentQueue<ObjectAttributeChange> _objectAttributeChanges = new ConcurrentQueue<ObjectAttributeChange>();
        private readonly object _directApiLock = new object();
        private readonly Dictionary<Tuple<Guid, int>, CyclesMesh> _directApiMeshes = new Dictionary<Tuple<Guid, int>, CyclesMesh>();
        private readonly Dictionary<uint, CyclesObject> _directApiObjects = new Dictionary<uint, CyclesObject>();
        private readonly Dictionary<uint, CyclesObjectShader> _directApiMaterialAssignments = new Dictionary<uint, CyclesObjectShader>();
        private readonly Dictionary<uint, CyclesObjectTransform> _directApiTransforms = new Dictionary<uint, CyclesObjectTransform>();

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

        public static IjewelStreamChangeDatabase Current { get; private set; }

        public ObjectDatabase ObjectDatabase => StreamObjectDatabase;

        public ObjectShaderDatabase ObjectShaderDatabase => StreamObjectShaderDatabase;

        public void RememberDirectMesh(CyclesMesh mesh)
        {
            if (mesh?.MeshId == null) return;

            lock (_directApiLock)
            {
                _directApiMeshes[mesh.MeshId] = CloneMesh(mesh);
            }
        }

        public void ForgetDirectMesh(Guid id)
        {
            lock (_directApiLock)
            {
                var keys = new List<Tuple<Guid, int>>();
                foreach (var key in _directApiMeshes.Keys)
                {
                    if (key.Item1 == id) keys.Add(key);
                }

                foreach (var key in keys)
                {
                    _directApiMeshes.Remove(key);
                }
            }
        }

        public void RememberDirectObject(CyclesObject ob)
        {
            if (ob == null) return;

            lock (_directApiLock)
            {
                var clone = CloneObject(ob);
                if (_directApiTransforms.TryGetValue(ob.obid, out var transform))
                {
                    clone.Transform = transform.Transform;
                }

                if (_directApiMaterialAssignments.TryGetValue(ob.obid, out var material))
                {
                    ApplyMaterialAssignment(clone, material);
                }

                _directApiObjects[ob.obid] = clone;
            }
        }

        public void ForgetDirectObject(uint obid)
        {
            lock (_directApiLock)
            {
                _directApiObjects.Remove(obid);
                _directApiMaterialAssignments.Remove(obid);
                _directApiTransforms.Remove(obid);
            }
        }

        public void RememberDirectMaterialAssignment(CyclesObjectShader assignment)
        {
            if (assignment == null) return;

            lock (_directApiLock)
            {
                var clone = CloneMaterialAssignment(assignment);
                _directApiMaterialAssignments[clone.Id] = clone;

                if (_directApiObjects.TryGetValue(clone.Id, out var ob))
                {
                    ApplyMaterialAssignment(ob, clone);
                }
            }
        }

        public void RememberDirectTransform(CyclesObjectTransform transform)
        {
            if (transform == null) return;

            lock (_directApiLock)
            {
                var clone = CloneTransform(transform);
                _directApiTransforms[clone.Id] = clone;

                if (_directApiObjects.TryGetValue(clone.Id, out var ob))
                {
                    ob.Transform = clone.Transform;
                }
            }
        }

        public static IjewelStreamChangeDatabase Create(RhinoDoc doc)
        {
            return Create(doc, Ijewel3DPlugin.Instance?.Id ?? Guid.Empty);
        }

        public static IjewelStreamChangeDatabase Create(RhinoDoc doc, Guid pluginId)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var view = doc.Views.ActiveView != null
                ? new ViewInfo(doc.Views.ActiveView.ActiveViewport)
                : new ViewInfo(doc.RuntimeSerialNumber);

            var engine = new RhinoCyclesCore.RenderEngine
            {
                View = view
            };

            var database = new IjewelStreamChangeDatabase(
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

            Current = database;
            return database;
        }

        public void QueueStream(bool immediate = false, bool full = false)
        {
            lock (_drainLock)
            {
                _dirty = true;
                _fullRequested |= full || !_worldCreated;

                lock (_socketLock)
                {
                    if (_clients.Count == 0) return;
                }

                if (_debounceTimer == null)
                {
                    _debounceTimer = new Timer(_ => Task.Run(FlushStreamAsync), null, Timeout.Infinite, Timeout.Infinite);
                }

                _debounceTimer.Change(immediate ? 0 : StreamDebounceMilliseconds, Timeout.Infinite);
            }
        }

        public void QueueObjectAttributesChange(Guid objectId, ObjectAttributes attributes, bool immediate = false)
        {
            if (objectId == Guid.Empty || attributes == null) return;

            var doc = RhinoDoc.FromRuntimeSerialNumber(StreamDocumentSerialNumber);
            var layer = doc != null && attributes.LayerIndex >= 0 && attributes.LayerIndex < doc.Layers.Count
                ? doc.Layers[attributes.LayerIndex]
                : null;

            _objectAttributeChanges.Enqueue(new ObjectAttributeChange
            {
                ObjectId = objectId,
                LayerName = layer?.Name,
                LayerFullPath = layer?.FullPath,
                MaterialSource = attributes.MaterialSource.ToString().ToLowerInvariant()
            });

            QueueStream(immediate);
        }

        public void QueueObjectAttributesChange(RhinoModifyObjectAttributesEventArgs e, bool immediate = false)
        {
            if (e?.OldAttributes == null || e.NewAttributes == null || e.RhinoObject == null)
            {
                QueueStream(immediate);
                return;
            }

            if (e.OldAttributes.LayerIndex != e.NewAttributes.LayerIndex ||
                e.OldAttributes.MaterialSource != e.NewAttributes.MaterialSource ||
                e.OldAttributes.MaterialIndex != e.NewAttributes.MaterialIndex)
            {
                QueueObjectAttributesChange(e.RhinoObject.Id, e.NewAttributes, immediate);
                return;
            }

            QueueStream(immediate);
        }

        public void QueueTransformStream()
        {
            lock (_socketLock)
            {
                if (_clients.Count == 0) return;
                EnsureTransformTimer();
            }
        }

        internal void AddClient(IjewelStreamClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            lock (_socketLock)
            {
                if (_clientSendLocks.ContainsKey(client)) return;
                _clients.Add(client);
                _clientSendLocks[client] = new SemaphoreSlim(1, 1);
                EnsureTransformTimer();
            }

            QueueStream(true, true);
        }

        internal void RemoveClient(IjewelStreamClient client)
        {
            if (client == null) return;

            SemaphoreSlim sendLock = null;
            lock (_socketLock)
            {
                _clients.Remove(client);
                if (_clientSendLocks.TryGetValue(client, out sendLock))
                {
                    _clientSendLocks.Remove(client);
                }

                if (_clients.Count == 0)
                {
                    _transformTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }

            sendLock?.Dispose();
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
                    foreach (var client in _clients.ToArray())
                    {
                        try { client.Close(); }
                        catch { }
                    }

                    _clients.Clear();
                    foreach (var sendLock in _clientSendLocks.Values)
                    {
                        sendLock.Dispose();
                    }
                    _clientSendLocks.Clear();
                }

                _disposeTokenSource.Dispose();
                _streamLock.Dispose();

                if (ReferenceEquals(Current, this))
                {
                    Current = null;
                }
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

                AddObjectToFrame(ob);
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
                UploadObjectAttributeChanges();
                UploadObjectShaderChanges();
                if (full)
                {
                    AddDirectApiStateToFrame();
                }

                var json = _frame.ToJson();
                ResetChangeQueue();
            return Encoding.UTF8.GetBytes(json);
        }
            finally
            {
                _frame = null;
            }
        }

        private void UploadObjectAttributeChanges()
        {
            if (_frame == null) return;

            while (_objectAttributeChanges.TryDequeue(out var change))
            {
                var objectIds = StreamObjectDatabase.FindObjectIdsForSourceId(change.ObjectId);
                foreach (var obid in objectIds)
                {
                    var currentHash = StreamObjectShaderDatabase.FindRenderHashForObjectId(obid);
                    var material = currentHash != uint.MaxValue ? MaterialFromId(currentHash) : null;
                    var assignment = new CyclesObjectShader(obid)
                    {
                        OldShaderHash = currentHash,
                        NewShaderHash = currentHash,
                        MaterialName = material?.Name,
                        LayerName = change.LayerName,
                        LayerFullPath = change.LayerFullPath,
                        MaterialSource = change.MaterialSource
                    };

                    StreamObjectShaderDatabase.ApplyStoredMaterialMetadata(assignment);
                    StreamObjectShaderDatabase.AddObjectShaderChange(assignment);
                }
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

        private sealed class ObjectAttributeChange
        {
            public Guid ObjectId { get; set; }
            public string LayerName { get; set; }
            public string LayerFullPath { get; set; }
            public string MaterialSource { get; set; }
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

        private void AddObjectToFrame(CyclesObject ob)
        {
            if (ob == null) return;

            foreach (var existing in _frame.Objects)
            {
                if (existing.obid == ob.obid) return;
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

        private void AddMaterialAssignmentToFrame(CyclesObjectShader assignment)
        {
            if (assignment == null) return;

            foreach (var existing in _frame.MaterialAssignments)
            {
                if (existing.Id == assignment.Id) return;
            }

            _frame.MaterialAssignments.Add(assignment);
        }

        private void AddTransformToFrame(CyclesObjectTransform transform)
        {
            if (transform == null) return;

            foreach (var existing in _frame.Transforms)
            {
                if (existing.Id == transform.Id) return;
            }

            _frame.Transforms.Add(transform);
        }

        private void AddDirectApiStateToFrame()
        {
            List<CyclesMesh> meshes;
            List<CyclesObject> objects;
            List<CyclesObjectShader> materials;
            List<CyclesObjectTransform> transforms;

            lock (_directApiLock)
            {
                meshes = new List<CyclesMesh>(_directApiMeshes.Count);
                foreach (var mesh in _directApiMeshes.Values)
                {
                    meshes.Add(CloneMesh(mesh));
                }

                objects = new List<CyclesObject>(_directApiObjects.Count);
                foreach (var ob in _directApiObjects.Values)
                {
                    objects.Add(CloneObject(ob));
                }

                materials = new List<CyclesObjectShader>(_directApiMaterialAssignments.Count);
                foreach (var material in _directApiMaterialAssignments.Values)
                {
                    materials.Add(CloneMaterialAssignment(material));
                }

                transforms = new List<CyclesObjectTransform>(_directApiTransforms.Count);
                foreach (var transform in _directApiTransforms.Values)
                {
                    transforms.Add(CloneTransform(transform));
                }
            }

            foreach (var mesh in meshes)
            {
                AddMeshToFrame(mesh);
            }

            foreach (var ob in objects)
            {
                AddObjectToFrame(ob);
            }

            foreach (var transform in transforms)
            {
                AddTransformToFrame(transform);
            }

            foreach (var material in materials)
            {
                AddMaterialAssignmentToFrame(material);
            }
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

        private static CyclesObject CloneObject(CyclesObject ob)
        {
            if (ob == null) return null;

            return new CyclesObject
            {
                obid = ob.obid,
                meshid = ob.meshid,
                Transform = ob.Transform,
                OcsFrame = ob.OcsFrame,
                matid = ob.matid,
                MaterialName = ob.MaterialName,
                LayerName = ob.LayerName,
                LayerFullPath = ob.LayerFullPath,
                MaterialSource = ob.MaterialSource,
                Visible = ob.Visible,
                Shader = ob.Shader,
                CastShadow = ob.CastShadow,
                IsShadowCatcher = ob.IsShadowCatcher,
                IsSolid = ob.IsSolid,
                CastNoShadow = ob.CastNoShadow,
                Cutout = ob.Cutout,
                IgnoreCutout = ob.IgnoreCutout
            };
        }

        private static CyclesObjectShader CloneMaterialAssignment(CyclesObjectShader assignment)
        {
            if (assignment == null) return null;

            return new CyclesObjectShader(assignment.Id)
            {
                OldShaderHash = assignment.OldShaderHash,
                NewShaderHash = assignment.NewShaderHash,
                MaterialName = assignment.MaterialName,
                LayerName = assignment.LayerName,
                LayerFullPath = assignment.LayerFullPath,
                MaterialSource = assignment.MaterialSource
            };
        }

        private static CyclesObjectTransform CloneTransform(CyclesObjectTransform transform)
        {
            return transform == null
                ? null
                : new CyclesObjectTransform(transform.Id, transform.Transform);
        }

        private static void ApplyMaterialAssignment(CyclesObject ob, CyclesObjectShader assignment)
        {
            if (ob == null || assignment == null) return;

            ob.matid = assignment.NewShaderHash;
            if (!string.IsNullOrEmpty(assignment.MaterialName)) ob.MaterialName = assignment.MaterialName;
            if (!string.IsNullOrEmpty(assignment.LayerName)) ob.LayerName = assignment.LayerName;
            if (!string.IsNullOrEmpty(assignment.LayerFullPath)) ob.LayerFullPath = assignment.LayerFullPath;
            if (!string.IsNullOrEmpty(assignment.MaterialSource)) ob.MaterialSource = assignment.MaterialSource;
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
            KeyValuePair<IjewelStreamClient, SemaphoreSlim>[] clients;
            lock (_socketLock)
            {
                clients = new List<KeyValuePair<IjewelStreamClient, SemaphoreSlim>>(_clientSendLocks).ToArray();
            }

            foreach (var item in clients)
            {
                var client = item.Key;
                var sendLock = item.Value;

                if (!client.IsOpen)
                {
                    RemoveClient(client);
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
                                if (!_clientSendLocks.ContainsKey(client)) continue;
                            }

                            if (!client.IsOpen)
                            {
                                RemoveClient(client);
                                continue;
                            }

                            await client.SendAsync(payload, sendTimeout.Token);
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

                    RemoveClient(client);
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"Model WebSocket send failed: {ex.Message}");
                    RemoveClient(client);
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
