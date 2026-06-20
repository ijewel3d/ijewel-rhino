using Rhino;
using Rhino.Render.ChangeQueue;
using RhinoCyclesCore;
using RhinoCyclesCore.Database;
using System;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace Ijewel3D
{
    public static class StreamApi
    {
        internal static IjewelStreamServerUtility Server { get; } = new IjewelStreamServerUtility();

        public static bool IsRunning => Server.IsRunning;

        public static int? Port => Server.chosenPort;

        public static IjewelStreamChangeDatabase Current => IjewelStreamChangeDatabase.Current;

        public static void Start(RhinoDoc doc)
        {
            Start(doc, Ijewel3DPlugin.Instance?.Id ?? Guid.Empty);
        }

        public static void Start(RhinoDoc doc, Guid pluginId)
        {
            Server.StartModelStream(doc, pluginId);
        }

        public static void Stop()
        {
            Server.StopModelStream();
        }

        public static void AddMesh(CyclesMesh mesh, bool immediate = true)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            var database = RequireCurrent();
            database.ObjectDatabase.AddMesh(mesh);
            database.RememberDirectMesh(mesh);
            database.QueueStream(immediate);
        }

        public static void HandleMeshData(
            Guid meshguid,
            int meshIndex,
            RhinoMesh meshdata,
            MappingChannelCollection mappingCollection,
            bool isClippingObject,
            uint materialId,
            ccl.Transform ocsFrame,
            bool immediate = true)
        {
            if (meshdata == null) throw new ArgumentNullException(nameof(meshdata));

            var database = RequireCurrent();
            database.HandleMeshData(
                meshguid,
                meshIndex,
                meshdata,
                mappingCollection,
                isClippingObject,
                materialId,
                ocsFrame ?? ccl.Transform.Identity());
            if (database.ObjectDatabase.MeshChanges.TryGetValue(Tuple.Create(meshguid, meshIndex), out var mesh))
            {
                database.RememberDirectMesh(mesh);
            }
            database.QueueStream(immediate);
        }

        public static void DeleteMesh(Guid id, bool immediate = true)
        {
            var database = RequireCurrent();
            database.ObjectDatabase.DeleteMesh(id);
            database.ForgetDirectMesh(id);
            database.QueueStream(immediate);
        }

        public static void AddOrUpdateObject(CyclesObject ob, bool immediate = true)
        {
            if (ob == null) throw new ArgumentNullException(nameof(ob));

            var database = RequireCurrent();
            database.ObjectDatabase.AddOrUpdateObject(ob);
            database.RememberDirectObject(ob);
            database.QueueStream(immediate);
        }

        public static void DeleteObject(CyclesObject ob, bool immediate = true)
        {
            if (ob == null) throw new ArgumentNullException(nameof(ob));

            var database = RequireCurrent();
            database.ObjectDatabase.DeleteObject(ob);
            database.ForgetDirectObject(ob.obid);
            database.QueueStream(immediate);
        }

        public static void HandleShaderChange(
            uint obid,
            uint oldhash,
            uint newhash,
            Tuple<Guid, int> meshid,
            bool immediate = true)
        {
            var database = RequireCurrent();
            database.HandleShaderChange(obid, oldhash, newhash, meshid);
            var assignment = new CyclesObjectShader(obid)
            {
                OldShaderHash = oldhash,
                NewShaderHash = newhash
            };
            database.ObjectShaderDatabase.ApplyStoredMaterialMetadata(assignment);
            database.RememberDirectMaterialAssignment(assignment);
            database.QueueStream(immediate);
        }

        public static void AddObjectShaderChange(CyclesObjectShader change, bool immediate = true)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));

            var database = RequireCurrent();
            database.ObjectShaderDatabase.ApplyStoredMaterialMetadata(change);
            database.ObjectShaderDatabase.AddObjectShaderChange(change);
            database.RememberDirectMaterialAssignment(change);
            database.QueueStream(immediate);
        }

        public static void AddDynamicObjectTransform(CyclesObjectTransform transform)
        {
            if (transform == null) throw new ArgumentNullException(nameof(transform));

            var database = RequireCurrent();
            database.ObjectDatabase.AddDynamicObjectTransform(transform);
            database.RememberDirectTransform(transform);
            database.QueueTransformStream();
        }

        private static IjewelStreamChangeDatabase RequireCurrent()
        {
            var database = Current;
            if (database == null)
            {
                throw new InvalidOperationException("iJewel stream is not running. Call StreamApi.Start(doc) or run IJewelStream before pushing stream changes.");
            }

            return database;
        }
    }
}
