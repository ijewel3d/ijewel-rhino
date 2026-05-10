using System;
using System.Collections.Generic;
using Rhino.Display;
using Rhino.Render;

namespace ccl
{
    public sealed class Object
    {
    }

    public sealed class Mesh
    {
        public RhinoCyclesCore.CyclesMesh StreamPayload { get; set; }
    }

    public sealed class Light
    {
    }

    public sealed class Shader
    {
    }

    public sealed class ClippingPlane
    {
    }

    public enum LightType
    {
        Background,
        Point,
        Distant,
        Area,
        Spot
    }

    public enum CameraType
    {
        Perspective,
        Orthographic,
        Panorama
    }

    public struct float4
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public float4(float value)
        {
            x = value;
            y = value;
            z = value;
            w = value;
        }

        public float4(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            w = 0.0f;
        }

        public float4(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public float Length()
        {
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }

        public static float4 operator ^(float4 value, float gamma)
        {
            if (Math.Abs(gamma - 1.0f) <= float.Epsilon) return value;

            return new float4(
                (float)Math.Pow(value.x, gamma),
                (float)Math.Pow(value.y, gamma),
                (float)Math.Pow(value.z, gamma),
                value.w);
        }
    }

    public sealed class Transform
    {
        public float4 x;
        public float4 y;
        public float4 z;

        public Transform(float m00, float m01, float m02, float m03, float m10, float m11, float m12, float m13, float m20, float m21, float m22, float m23)
        {
            x = new float4(m00, m01, m02, m03);
            y = new float4(m10, m11, m12, m13);
            z = new float4(m20, m21, m22, m23);
        }

        public Transform(Transform other)
            : this(other.x.x, other.x.y, other.x.z, other.x.w, other.y.x, other.y.y, other.y.z, other.y.w, other.z.x, other.z.y, other.z.z, other.z.w)
        {
        }

        public static Transform Identity()
        {
            return new Transform(
                1.0f, 0.0f, 0.0f, 0.0f,
                0.0f, 1.0f, 0.0f, 0.0f,
                0.0f, 0.0f, 1.0f, 0.0f);
        }

        public static Transform RhinoToCyclesCam => Identity();

        public static Transform RhinoToCyclesCamReflected => Identity();

        public static Transform RhinoToCyclesCamNoFlip => Identity();

        public static Transform operator *(Transform left, Transform right)
        {
            return left ?? right;
        }
    }
}

namespace ccl.ShaderNodes.Sockets
{
    public interface ISocket
    {
    }
}

namespace RhinoCyclesCore.Core
{
    public sealed class RcCore
    {
        public static RcCore It { get; } = new RcCore();

        public RhinoCyclesSettings AllSettings { get; } = new RhinoCyclesSettings();

        private RcCore()
        {
        }

        public static void OutputDebugString(string message)
        {
        }

        public void AddLogString(string message)
        {
        }

        public void AddLogStringIfVerbose(string message)
        {
        }
    }

    public sealed class RhinoCyclesSettings
    {
        public int Blades { get; set; } = 0;

        public float BladesRotation { get; set; } = 0.0f;

        public float ApertureRatio { get; set; } = 1.0f;

        public float ApertureFactor { get; set; } = 0.0f;

        public float SensorHeight { get; set; } = 24.0f;

        public float SensorWidth { get; set; } = 36.0f;

        public float JiggleFactor { get; set; } = 0.0f;

        public float GpJiggleDistance { get; set; } = 0.0f;

        public float LinearLightFactor { get; set; } = 1.0f;
    }
}

namespace RhinoCyclesCore
{
    public partial class RenderEngine
    {
        public object RenderWindow { get; } = null;

        public bool ShouldBreak { get; set; }

        public bool Flush { get; set; }

        public System.Drawing.Size FullSize { get; set; } = new System.Drawing.Size(1, 1);

        public System.Drawing.Size RenderDimension { get; set; } = new System.Drawing.Size(1, 1);

        public Rhino.DocObjects.ViewInfo View { get; set; }

        public int _textureBakeQuality = 0;

        public void SetProgress(object renderWindow, string status, float progress)
        {
        }

        public void TriggerBeginChangesNotified()
        {
        }

        public static ccl.float4 CreateFloat4(float x, float y, float z, float w = 1.0f)
        {
            return new ccl.float4(x, y, z, w);
        }

        public static ccl.float4 CreateFloat4(System.Drawing.Color color)
        {
            return new ccl.float4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
        }
    }
}

namespace RhinoCyclesCore.Converters
{
    public sealed class BitmapConverter : IDisposable
    {
        public void Dispose()
        {
        }
    }

    // IJWEL_STREAM: Shader/light conversion to Cycles runtime is disabled.
    // public sealed class ShaderConverter
    // {
    //     public CyclesShader RecordDataToSetupCyclesShader(RenderMaterial material, LinearWorkflow linearWorkflow, uint materialId, BitmapConverter bitmapConverter, List<CyclesDecal> decals, uint documentSerialNumber)
    //     {
    //         var shader = new CyclesShader(materialId, bitmapConverter, documentSerialNumber)
    //         {
    //             Decals = decals,
    //             Gamma = linearWorkflow?.PreProcessGamma ?? 1.0f
    //         };
    //
    //         shader.SetupShaderShim();
    //         return shader;
    //     }
    //
    //     internal CyclesLight ConvertLight(Rhino.Render.ChangeQueue.ChangeQueue changeQueue, Rhino.Render.ChangeQueue.Light light, Rhino.DocObjects.ViewInfo view, float gamma, Rhino.Geometry.Transform viewTransform)
    //     {
    //         return ConvertLight(light.Data, gamma, viewTransform);
    //     }
    //
    //     internal CyclesLight ConvertLight(Rhino.Geometry.Light light, float gamma, Rhino.Geometry.Transform viewTransform)
    //     {
    //         var converted = new CyclesLight
    //         {
    //             Id = light.Id,
    //             Gamma = gamma,
    //             Type = ccl.LightType.Point,
    //             CastShadow = true,
    //             UseMis = true,
    //             Strength = (float)light.Intensity
    //         };
    //
    //         converted.Co = new ccl.float4((float)light.Location.X, (float)light.Location.Y, (float)light.Location.Z);
    //         converted.Dir = new ccl.float4((float)light.Direction.X, (float)light.Direction.Y, (float)light.Direction.Z);
    //
    //         var color = new Color4f(light.Diffuse);
    //         converted.DiffuseColor = new ccl.float4(color.R, color.G, color.B, color.A);
    //
    //         return converted;
    //     }
    // }
}
