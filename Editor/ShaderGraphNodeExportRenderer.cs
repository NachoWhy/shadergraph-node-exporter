using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace NachoWhy.SgNodeExporter
{
    static class SgNodeExportRenderer
    {
        public readonly struct ExportResult
        {
            public readonly string path;
            public readonly int width;
            public readonly int height;

            public ExportResult(string path, int width, int height)
            {
                this.path = path;
                this.width = width;
                this.height = height;
            }
        }

        static readonly Regex s_SurfaceOutAssignmentRegex = new Regex(
            @"^(?<indent>\s*)surface\.Out\s*=.*?;",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);
        const int k_TiledRenderDimensionThreshold = 8192;
        const int k_MaxTileSupersampledSize = 4096;
        const string k_DownsampleShaderSource = @"Shader ""Hidden/NachoWhy/SgNodeExporter/Downsample""
{
    Properties
    {
        _MainTex (""Texture"", 2D) = ""white"" {}
    }

    SubShader
    {
        Tags { ""RenderType""=""Opaque"" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Scale;
            float _Filter;
            float _ForceOpaqueAlpha;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                int scale = (int)_Scale;
                if (scale < 1)
                    scale = 1;
                if (scale > 8)
                    scale = 8;

                float2 texel = _MainTex_TexelSize.xy;
                float4 sum = 0.0;
                float weightSum = 0.0;
                bool sharp = _Filter < 0.5;

                if (sharp)
                {
                    float centerWeight = max(1.0, _Scale);
                    sum += tex2D(_MainTex, i.uv) * centerWeight;
                    weightSum += centerWeight;
                }

                float radius = max(1.0, _Scale * 0.5);
                [loop]
                for (int y = 0; y < 8; y++)
                {
                    if (y >= scale)
                        break;

                    [loop]
                    for (int x = 0; x < 8; x++)
                    {
                        if (x >= scale)
                            break;

                        float2 sourceOffset = float2(x + 0.5, y + 0.5) - _Scale * 0.5;
                        float weight = 1.0;
                        if (sharp)
                        {
                            float2 axisWeight = saturate(1.0 - abs(sourceOffset) / radius);
                            weight = max(0.0001, axisWeight.x * axisWeight.y);
                        }

                        sum += tex2D(_MainTex, i.uv + sourceOffset * texel) * weight;
                        weightSum += weight;
                    }
                }

                float4 result = sum / max(weightSum, 0.0001);
                if (_ForceOpaqueAlpha > 0.5)
                    result.a = 1.0;
                return result;
            }
            ENDCG
        }
    }
    Fallback Off
}";

        static Shader s_DownsampleShader;
        static Material s_DownsampleMaterial;
        static readonly int s_MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int s_MainTexTexelSizeId = Shader.PropertyToID("_MainTex_TexelSize");
        static readonly int s_ScaleId = Shader.PropertyToID("_Scale");
        static readonly int s_FilterId = Shader.PropertyToID("_Filter");
        static readonly int s_ForceOpaqueAlphaId = Shader.PropertyToID("_ForceOpaqueAlpha");

        public static List<MaterialSlot> GetRenderableOutputs(AbstractMaterialNode node)
        {
            var slots = new List<MaterialSlot>();
            if (node == null || !node.hasPreview)
                return slots;

            using (var outputSlots = PooledList<MaterialSlot>.Get())
            {
                node.GetOutputSlots(outputSlots);
                slots.AddRange(outputSlots.Where(IsRenderableSlot));
            }

            return slots;
        }

        public static string GetExtension(SgNodeExportFormat format)
        {
            switch (format)
            {
                case SgNodeExportFormat.PNG:
                    return ".png";
                case SgNodeExportFormat.EXR:
                    return ".exr";
                case SgNodeExportFormat.JPG:
                    return ".jpg";
                case SgNodeExportFormat.TGA:
                    return ".tga";
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }
        }

        public static int GetAntiAliasingScale(SgNodeExportAntiAliasing antiAliasing)
        {
            switch (antiAliasing)
            {
                case SgNodeExportAntiAliasing.TwoX:
                    return 2;
                case SgNodeExportAntiAliasing.FourX:
                    return 4;
                case SgNodeExportAntiAliasing.EightX:
                    return 8;
                case SgNodeExportAntiAliasing.Off:
                default:
                    return 1;
            }
        }

        public static bool TryGetSupersampledSize(SgNodeExportSettings settings, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;

            if (settings == null)
            {
                error = "Export settings are missing.";
                return false;
            }

            if (settings.width < 1 || settings.height < 1)
            {
                error = "Export width and height must be at least 1 pixel.";
                return false;
            }

            var scale = GetAntiAliasingScale(settings.antiAliasing);
            var requestedWidth = (long)settings.width * scale;
            var requestedHeight = (long)settings.height * scale;
            var maxTextureSize = SystemInfo.maxTextureSize;
            if (settings.width > maxTextureSize || settings.height > maxTextureSize)
            {
                error = $"The final export size {settings.width}x{settings.height} exceeds this system's maximum texture size of {maxTextureSize}x{maxTextureSize}.";
                return false;
            }

            if (requestedWidth > int.MaxValue || requestedHeight > int.MaxValue)
            {
                error = $"{scale}x anti-aliasing requires a virtual {requestedWidth}x{requestedHeight} internal render target, which is too large.";
                return false;
            }

            if (!ShouldRenderTiled(requestedWidth, requestedHeight) && (requestedWidth > maxTextureSize || requestedHeight > maxTextureSize))
            {
                error = $"{scale}x anti-aliasing requires a {requestedWidth}x{requestedHeight} internal render target, but this system supports up to {maxTextureSize}x{maxTextureSize}.";
                return false;
            }

            width = (int)requestedWidth;
            height = (int)requestedHeight;
            return true;
        }

        static bool ShouldRenderTiled(long renderWidth, long renderHeight)
        {
            return renderWidth >= k_TiledRenderDimensionThreshold || renderHeight >= k_TiledRenderDimensionThreshold;
        }

        public static bool UsesTiledRendering(SgNodeExportSettings settings, out int renderWidth, out int renderHeight)
        {
            return TryGetSupersampledSize(settings, out renderWidth, out renderHeight, out _) && ShouldRenderTiled(renderWidth, renderHeight);
        }

        public static long EstimateRenderMemoryBytes(SgNodeExportSettings settings)
        {
            if (!TryGetSupersampledSize(settings, out var renderWidth, out var renderHeight, out _))
                return 0L;

            var scale = GetAntiAliasingScale(settings.antiAliasing);
            var bytesPerPixel = settings.format == SgNodeExportFormat.EXR ? 16L : 4L;
            var finalPixels = (long)settings.width * settings.height;

            if (ShouldRenderTiled(renderWidth, renderHeight))
            {
                var maxTileSize = Math.Max(1, k_MaxTileSupersampledSize / scale);
                var tileWidth = Math.Min(settings.width, maxTileSize);
                var tileHeight = Math.Min(settings.height, maxTileSize);
                var tilePixels = (long)tileWidth * tileHeight;
                var tileSupersampledPixels = tilePixels * scale * scale;
                var tiledBytes = finalPixels * bytesPerPixel;

                tiledBytes += tileSupersampledPixels * bytesPerPixel * 2L;
                for (var remainingScale = scale / 2; remainingScale >= 1; remainingScale /= 2)
                {
                    tiledBytes += tilePixels * remainingScale * remainingScale * bytesPerPixel;
                    if (remainingScale == 1)
                        break;
                }

                return tiledBytes;
            }

            var internalPixels = (long)renderWidth * renderHeight;
            var bytes = internalPixels * bytesPerPixel * 2L;
            for (var remainingScale = scale / 2; remainingScale >= 1; remainingScale /= 2)
            {
                bytes += finalPixels * remainingScale * remainingScale * bytesPerPixel;
                if (remainingScale == 1)
                    break;
            }

            bytes += finalPixels * bytesPerPixel;
            return bytes;
        }

        public static ExportResult Export(
            AbstractMaterialNode node,
            MaterialSlot outputSlot,
            SgNodeExportSettings settings,
            IEnumerable<SgPropertyOverride> propertyOverrides,
            string outputPath)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));
            if (outputSlot == null)
                throw new ArgumentNullException(nameof(outputSlot));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ProjectPathUtility.ProjectRoot);
            if (!TryGetSupersampledSize(settings, out var renderWidth, out var renderHeight, out var sizeError))
                throw new InvalidOperationException(sizeError);

            var shader = default(Shader);
            var material = default(Material);
            var supersampledTexture = default(RenderTexture);
            var resolvedTextures = default(List<RenderTexture>);
            var readableTexture = default(Texture2D);
            var propertyBlock = default(MaterialPropertyBlock);

            var previousActiveRenderTexture = RenderTexture.active;
            var previousAsyncCompilation = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            try
            {
                var shaderSource = GeneratePreviewShader(node, outputSlot, settings);
                shader = ShaderUtil.CreateShaderAsset(shaderSource, false);
                shader.hideFlags = HideFlags.HideAndDontSave;

                material = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };

                propertyBlock = BuildPropertyBlock(node.owner, material, settings, propertyOverrides);
                CompilePreviewMaterial(material);
                ThrowIfShaderHasErrors(shader, node);

                var scale = GetAntiAliasingScale(settings.antiAliasing);
                var previewMode = ResolvePreviewMode(node);
                var readbackTexture = default(RenderTexture);

                if (ShouldRenderTiled(renderWidth, renderHeight))
                {
                    var descriptor = BuildRenderTextureDescriptor(settings, settings.width, settings.height, 0);
                    supersampledTexture = CreateRenderTexture(descriptor, "tiled preview output");
                    RenderTiledPreview(supersampledTexture, material, previewMode, settings, propertyBlock, scale);
                    readbackTexture = supersampledTexture;
                }
                else
                {
                    var descriptor = BuildRenderTextureDescriptor(settings, renderWidth, renderHeight, 24);
                    supersampledTexture = CreateRenderTexture(descriptor, "supersampled preview");
                    RenderPreview(supersampledTexture, material, previewMode, settings, propertyBlock);
                    readbackTexture = supersampledTexture;

                    if (scale > 1)
                    {
                        resolvedTextures = new List<RenderTexture>();
                        readbackTexture = DownsampleChain(supersampledTexture, settings, settings.width, settings.height, scale, resolvedTextures);
                    }
                }

                readableTexture = ReadBack(readbackTexture, settings.format);
                var bytes = Encode(readableTexture, settings);
                File.WriteAllBytes(outputPath, bytes);

                return new ExportResult(outputPath, settings.width, settings.height);
            }
            finally
            {
                RestoreActiveRenderTexture(previousActiveRenderTexture, supersampledTexture, resolvedTextures);

                if (readableTexture != null)
                    Object.DestroyImmediate(readableTexture);
                if (resolvedTextures != null)
                {
                    foreach (var resolvedTexture in resolvedTextures)
                    {
                        if (resolvedTexture != null)
                            Object.DestroyImmediate(resolvedTexture);
                    }
                }

                if (supersampledTexture != null)
                    Object.DestroyImmediate(supersampledTexture);
                if (material != null)
                    Object.DestroyImmediate(material);
                if (shader != null)
                {
                    ShaderUtil.ClearShaderMessages(shader);
                    Object.DestroyImmediate(shader);
                }

                ShaderUtil.allowAsyncCompilation = previousAsyncCompilation;
            }
        }

        static bool IsRenderableSlot(MaterialSlot slot)
        {
            switch (slot.concreteValueType)
            {
                case ConcreteSlotValueType.Vector1:
                case ConcreteSlotValueType.Vector2:
                case ConcreteSlotValueType.Vector3:
                case ConcreteSlotValueType.Vector4:
                case ConcreteSlotValueType.Boolean:
                    return true;
                default:
                    return false;
            }
        }

        static string GeneratePreviewShader(AbstractMaterialNode node, MaterialSlot outputSlot, SgNodeExportSettings settings)
        {
            var generator = new Generator(node.owner, node, GenerationMode.Preview, $"hidden/export/{node.GetVariableNameForNode()}");
            var shaderSource = generator.generatedShader;
            if (string.IsNullOrWhiteSpace(shaderSource))
                throw new InvalidOperationException($"Shader Graph did not generate preview shader code for node '{node.name}'.");

            shaderSource = ReplacePreviewOutput(shaderSource, node, outputSlot, settings);
            return shaderSource;
        }

        static string ReplacePreviewOutput(string shaderSource, AbstractMaterialNode node, MaterialSlot outputSlot, SgNodeExportSettings settings)
        {
            var variableName = node.GetVariableNameForSlot(outputSlot.id);
            var previewOutput = GetPreviewExpression(outputSlot, variableName, settings.preserveVector4Alpha);
            var finiteCheck = outputSlot.concreteValueType == ConcreteSlotValueType.Boolean
                ? "true"
                : $"all(isfinite({variableName}))";
            var match = s_SurfaceOutAssignmentRegex.Match(shaderSource);
            if (!match.Success)
                throw new InvalidOperationException($"Could not locate Shader Graph preview output for node '{node.name}'.");

            var indent = match.Groups["indent"].Value;
            var replacement = $"{indent}surface.Out = {finiteCheck} ? {previewOutput} : float4(1.0f, 0.0f, 1.0f, 1.0f);";
            return s_SurfaceOutAssignmentRegex.Replace(shaderSource, replacement, 1);
        }

        static string GetPreviewExpression(MaterialSlot outputSlot, string variableName, bool preserveVector4Alpha)
        {
            switch (outputSlot.concreteValueType)
            {
                case ConcreteSlotValueType.Vector1:
                    return $"half4({variableName}, {variableName}, {variableName}, 1.0)";
                case ConcreteSlotValueType.Vector2:
                    return $"half4({variableName}.x, {variableName}.y, 0.0, 1.0)";
                case ConcreteSlotValueType.Vector3:
                    return $"half4({variableName}.x, {variableName}.y, {variableName}.z, 1.0)";
                case ConcreteSlotValueType.Vector4:
                    var alpha = preserveVector4Alpha ? $"{variableName}.w" : "1.0";
                    return $"half4({variableName}.x, {variableName}.y, {variableName}.z, {alpha})";
                case ConcreteSlotValueType.Boolean:
                    return $"half4({variableName}, {variableName}, {variableName}, 1.0)";
                default:
                    return "half4(0, 0, 0, 0)";
            }
        }

        static RenderTextureDescriptor BuildRenderTextureDescriptor(SgNodeExportSettings settings, int width, int height, int depthBufferBits)
        {
            var hdr = settings.format == SgNodeExportFormat.EXR;
            return new RenderTextureDescriptor(width, height)
            {
                colorFormat = hdr ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32,
                depthBufferBits = depthBufferBits,
                msaaSamples = 1,
                bindMS = false,
                sRGB = !hdr && QualitySettings.activeColorSpace == ColorSpace.Linear,
                useMipMap = false,
                autoGenerateMips = false
            };
        }

        static void CompilePreviewMaterial(Material material)
        {
            if (material.passCount <= 0)
                throw new InvalidOperationException("Generated preview material has no renderable passes.");

            for (var i = 0; i < material.passCount; i++)
            {
                ShaderUtil.CompilePass(material, i);
                if (!ShaderUtil.IsPassCompiled(material, i))
                    throw new InvalidOperationException("Generated preview shader did not finish compiling.");
            }

            material.shader = material.shader;
        }

        static void Downsample(RenderTexture source, RenderTexture destination, SgNodeExportDownsampleFilter filter, int scale, bool forceOpaqueAlpha)
        {
            var material = GetDownsampleMaterial();
            material.SetTexture(s_MainTexId, source);
            material.SetVector(s_MainTexTexelSizeId, new Vector4(1f / source.width, 1f / source.height, source.width, source.height));
            material.SetFloat(s_ScaleId, scale);
            material.SetFloat(s_FilterId, filter == SgNodeExportDownsampleFilter.Smooth ? 1f : 0f);
            material.SetFloat(s_ForceOpaqueAlphaId, forceOpaqueAlpha ? 1f : 0f);

            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, destination, material, 0);
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        static RenderTexture DownsampleChain(RenderTexture source, SgNodeExportSettings settings, int outputWidth, int outputHeight, int scale, List<RenderTexture> resolvedTextures)
        {
            var current = source;
            var remainingScale = scale;

            while (remainingScale > 1)
            {
                remainingScale /= 2;
                var targetWidth = outputWidth * remainingScale;
                var targetHeight = outputHeight * remainingScale;
                var descriptor = BuildRenderTextureDescriptor(settings, targetWidth, targetHeight, 0);
                var target = CreateRenderTexture(descriptor, "downsample");
                resolvedTextures.Add(target);

                var stepScale = Mathf.Max(1, current.width / Mathf.Max(1, target.width));
                Downsample(current, target, settings.downsampleFilter, stepScale, !settings.preserveVector4Alpha && remainingScale == 1);
                current = target;
            }

            return current;
        }

        static RenderTexture CreateRenderTexture(RenderTextureDescriptor descriptor, string label)
        {
            descriptor = ForceNoMsaa(descriptor);
            var renderTexture = new RenderTexture(descriptor)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            renderTexture.antiAliasing = 1;

            if (renderTexture.Create())
                return renderTexture;

            var width = descriptor.width;
            var height = descriptor.height;
            Object.DestroyImmediate(renderTexture);
            throw new InvalidOperationException($"Could not create {label} render texture {width}x{height}. Lower the export size or anti-aliasing level.");
        }

        static Material GetDownsampleMaterial()
        {
            if (s_DownsampleMaterial != null)
                return s_DownsampleMaterial;

            s_DownsampleShader = ShaderUtil.CreateShaderAsset(k_DownsampleShaderSource, false);
            s_DownsampleShader.hideFlags = HideFlags.HideAndDontSave;
            ThrowIfDownsampleShaderHasErrors(s_DownsampleShader);

            s_DownsampleMaterial = new Material(s_DownsampleShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            ShaderUtil.CompilePass(s_DownsampleMaterial, 0);
            if (ShaderUtil.ShaderHasError(s_DownsampleShader))
            {
                ClearDownsampleCache();
                throw new InvalidOperationException("Downsample shader has errors after compilation.");
            }

            if (!ShaderUtil.IsPassCompiled(s_DownsampleMaterial, 0))
            {
                ClearDownsampleCache();
                throw new InvalidOperationException("Downsample shader did not finish compiling.");
            }

            return s_DownsampleMaterial;
        }

        static void ThrowIfDownsampleShaderHasErrors(Shader shader)
        {
            if (!ShaderUtil.ShaderHasError(shader))
                return;

            var messages = ShaderUtil.GetShaderMessages(shader);
            var message = messages.Length > 0 ? messages[0].message : "Unknown shader compiler error.";
            Object.DestroyImmediate(shader);
            s_DownsampleShader = null;
            throw new InvalidOperationException($"Downsample shader has errors: {message}");
        }

        static void ClearDownsampleCache()
        {
            if (s_DownsampleMaterial != null)
                Object.DestroyImmediate(s_DownsampleMaterial);
            if (s_DownsampleShader != null)
                Object.DestroyImmediate(s_DownsampleShader);

            s_DownsampleMaterial = null;
            s_DownsampleShader = null;
        }

        static void RestoreActiveRenderTexture(RenderTexture previousActive, RenderTexture supersampledTexture, List<RenderTexture> resolvedTextures)
        {
            var active = RenderTexture.active;
            if (active == null || (active != supersampledTexture && (resolvedTextures == null || !resolvedTextures.Contains(active))))
                return;

            var previousWasOwned = previousActive == supersampledTexture || (resolvedTextures != null && resolvedTextures.Contains(previousActive));
            RenderTexture.active = previousWasOwned ? null : previousActive;
        }

        static void ThrowIfShaderHasErrors(Shader shader, AbstractMaterialNode node)
        {
            if (!ShaderUtil.ShaderHasError(shader))
                return;

            var messages = ShaderUtil.GetShaderMessages(shader);
            var message = messages.Length > 0 ? messages[0].message : "Unknown shader compiler error.";
            throw new InvalidOperationException($"Generated preview shader for node '{node.name}' has errors: {message}");
        }

        static MaterialPropertyBlock BuildPropertyBlock(GraphData graph, Material material, SgNodeExportSettings settings, IEnumerable<SgPropertyOverride> propertyOverrides)
        {
            var block = new MaterialPropertyBlock();
            using (var previewProperties = PooledList<PreviewProperty>.Get())
            {
                foreach (var node in graph.GetNodes<AbstractMaterialNode>())
                    node.CollectPreviewMaterialProperties(previewProperties);

                var overrideMap = propertyOverrides?.ToDictionary(item => item.property, item => item)
                    ?? new Dictionary<AbstractShaderProperty, SgPropertyOverride>();

                foreach (var property in graph.properties)
                {
                    if (overrideMap.TryGetValue(property, out var propertyOverride))
                        previewProperties.Add(propertyOverride.ToPreviewProperty());
                    else
                        previewProperties.Add(property.GetPreviewMaterialProperty());
                }

                var previewTime = (float)settings.time;
                previewProperties.Add(new PreviewProperty(PropertyType.Vector4)
                {
                    name = "_TimeParameters",
                    vector4Value = new Vector4(previewTime, Mathf.Sin(previewTime), Mathf.Cos(previewTime), 0f)
                });

                var deltaTime = Mathf.Max(0f, settings.deltaTime);
                var inverseDeltaTime = deltaTime > 0f ? 1f / deltaTime : 0f;
                previewProperties.Add(new PreviewProperty(PropertyType.Vector4)
                {
                    name = "unity_DeltaTime",
                    vector4Value = new Vector4(deltaTime, inverseDeltaTime, deltaTime, inverseDeltaTime)
                });

                foreach (var previewProperty in previewProperties)
                {
                    previewProperty.SetValueOnMaterialPropertyBlock(block);
                    SetValueOnMaterial(material, previewProperty);
                }
            }

            return block;
        }

        static void SetValueOnMaterial(Material material, PreviewProperty property)
        {
            switch (property.propType)
            {
                case PropertyType.Float:
                    material.SetFloat(property.name, property.floatValue);
                    break;
                case PropertyType.Boolean:
                    material.SetFloat(property.name, property.booleanValue ? 1f : 0f);
                    break;
                case PropertyType.Color:
                    material.SetColor(property.name, property.colorValue);
                    break;
                case PropertyType.Vector2:
                case PropertyType.Vector3:
                case PropertyType.Vector4:
                    material.SetVector(property.name, property.vector4Value);
                    break;
                case PropertyType.Matrix2:
                case PropertyType.Matrix3:
                case PropertyType.Matrix4:
                    material.SetMatrix(property.name, property.matrixValue);
                    break;
                case PropertyType.Texture2D:
                    material.SetTexture(property.name, property.textureValue != null ? property.textureValue : GetDefaultTexture(property.texture2DDefaultType));
                    break;
                case PropertyType.Texture2DArray:
                case PropertyType.Texture3D:
                    if (property.textureValue != null)
                        material.SetTexture(property.name, property.textureValue);
                    break;
                case PropertyType.Cubemap:
                    material.SetTexture(property.name, property.cubemapValue != null ? property.cubemapValue : Texture2D.whiteTexture);
                    break;
                case PropertyType.Gradient:
                    SetGradientOnMaterial(material, property.name, property.gradientValue);
                    break;
                case PropertyType.VirtualTexture:
                    SetVirtualTextureOnMaterial(material, property);
                    break;
            }
        }

        static Texture GetDefaultTexture(Texture2DShaderProperty.DefaultType defaultType)
        {
            switch (defaultType)
            {
                case Texture2DShaderProperty.DefaultType.Black:
                    return Texture2D.blackTexture;
                case Texture2DShaderProperty.DefaultType.Grey:
                    return Texture2D.grayTexture;
                case Texture2DShaderProperty.DefaultType.NormalMap:
                    return Texture2D.normalTexture;
                case Texture2DShaderProperty.DefaultType.LinearGrey:
                    return Texture2D.linearGrayTexture;
                case Texture2DShaderProperty.DefaultType.Red:
                    return Texture2D.redTexture;
                case Texture2DShaderProperty.DefaultType.White:
                default:
                    return Texture2D.whiteTexture;
            }
        }

        static void SetGradientOnMaterial(Material material, string name, Gradient gradient)
        {
            if (gradient == null)
                return;

            material.SetFloat($"{name}_Type", (int)gradient.mode);
            material.SetFloat($"{name}_ColorsLength", gradient.colorKeys.Length);
            material.SetFloat($"{name}_AlphasLength", gradient.alphaKeys.Length);
            for (var i = 0; i < 8; i++)
                material.SetVector($"{name}_ColorKey{i}", i < gradient.colorKeys.Length ? GradientUtil.ColorKeyToVector(gradient.colorKeys[i]) : Vector4.zero);
            for (var i = 0; i < 8; i++)
                material.SetVector($"{name}_AlphaKey{i}", i < gradient.alphaKeys.Length ? GradientUtil.AlphaKeyToVector(gradient.alphaKeys[i]) : Vector2.zero);
        }

        static void SetVirtualTextureOnMaterial(Material material, PreviewProperty property)
        {
            var virtualTexture = property.vtProperty?.value;
            if (virtualTexture?.layers == null)
                return;

            foreach (var layer in virtualTexture.layers)
            {
                var propertyIndex = material.shader.FindPropertyIndex(layer.layerRefName);
                if (propertyIndex != -1)
                    material.SetTexture(layer.layerRefName, layer.layerTexture?.texture);
            }
        }

        static void RenderPreview(RenderTexture renderTexture, Material material, PreviewMode previewMode, SgNodeExportSettings settings, MaterialPropertyBlock propertyBlock)
        {
            using (var previewResources = new PreviewSceneResources())
            {
                var previewTexture = default(RenderTexture);
                var previous = RenderTexture.active;
                Mesh expandedQuad = null;
                try
                {
                    previewTexture = CreateRenderTexture(renderTexture.descriptor, "preview render");
                    ConfigurePreviewCamera(previewResources.camera, previewTexture, previewMode);

                    RenderTexture.active = previewTexture;
                    GL.Clear(true, true, Color.black);
                    Graphics.Blit(Texture2D.whiteTexture, previewTexture, previewResources.checkerboardMaterial);

                    EditorUtility.SetCameraAnimateMaterialsTime(previewResources.camera, (float)settings.time);
                    previewResources.light0.enabled = true;
                    previewResources.light0.intensity = 1.0f;
                    previewResources.light0.transform.rotation = Quaternion.Euler(50f, 50f, 0f);
                    previewResources.light1.enabled = true;
                    previewResources.light1.intensity = 1.0f;

                    if (previewMode == PreviewMode.Preview2D && !settings.stretchUVs)
                        expandedQuad = CreateExpandedViewportQuad(previewTexture);

                    var mesh = previewMode == PreviewMode.Preview3D
                        ? previewResources.sphere
                        : expandedQuad != null
                            ? expandedQuad
                            : previewResources.quad;
                    var matrix = previewMode == PreviewMode.Preview3D
                        ? Matrix4x4.TRS(-mesh.bounds.center, Quaternion.identity, Vector3.one)
                        : GetFullFrameQuadMatrix(previewTexture);

                    previewResources.camera.targetTexture = previewTexture;
                    Graphics.DrawMesh(mesh, matrix, material, 1, previewResources.camera, 0, propertyBlock, ShadowCastingMode.Off, false, null, false);

                    var previousUseSrp = Unsupported.useScriptableRenderPipeline;
                    try
                    {
                        Unsupported.useScriptableRenderPipeline = false;
                        previewResources.camera.Render();
                    }
                    finally
                    {
                        Unsupported.useScriptableRenderPipeline = previousUseSrp;
                    }

                    if (settings.preserveVector4Alpha)
                        Graphics.Blit(previewTexture, renderTexture);
                    else
                        Graphics.Blit(previewTexture, renderTexture, previewResources.blitNoAlphaMaterial);
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (previewTexture != null)
                        Object.DestroyImmediate(previewTexture);
                    if (expandedQuad != null)
                        Object.DestroyImmediate(expandedQuad);
                }
            }
        }

        static void RenderTiledPreview(RenderTexture outputTexture, Material material, PreviewMode previewMode, SgNodeExportSettings settings, MaterialPropertyBlock propertyBlock, int scale)
        {
            var maxTileSize = Math.Max(1, k_MaxTileSupersampledSize / Math.Max(1, scale));
            ClearRenderTexture(outputTexture, settings.preserveVector4Alpha ? Color.clear : Color.black);

            using (var previewResources = new PreviewSceneResources())
            {
                Mesh expandedQuad = null;
                try
                {
                    if (previewMode == PreviewMode.Preview2D && !settings.stretchUVs)
                        expandedQuad = CreateExpandedViewportQuad(settings.width, settings.height);

                    for (var y = 0; y < settings.height; y += maxTileSize)
                    {
                        var tileHeight = Math.Min(maxTileSize, settings.height - y);
                        for (var x = 0; x < settings.width; x += maxTileSize)
                        {
                            var tileWidth = Math.Min(maxTileSize, settings.width - x);
                            var tileRenderWidth = tileWidth * scale;
                            var tileRenderHeight = tileHeight * scale;
                            var tileDescriptor = BuildRenderTextureDescriptor(settings, tileRenderWidth, tileRenderHeight, 24);
                            var tileSupersampledTexture = default(RenderTexture);
                            var tileResolvedTextures = default(List<RenderTexture>);

                            try
                            {
                                tileSupersampledTexture = CreateRenderTexture(tileDescriptor, "supersampled preview tile");
                                RenderPreviewTile(
                                    previewResources,
                                    tileSupersampledTexture,
                                    material,
                                    previewMode,
                                    settings,
                                    propertyBlock,
                                    expandedQuad,
                                    x,
                                    y,
                                    tileWidth,
                                    tileHeight);

                                tileResolvedTextures = new List<RenderTexture>();
                                var tileResolvedTexture = DownsampleChain(tileSupersampledTexture, settings, tileWidth, tileHeight, scale, tileResolvedTextures);
                                CopyTextureRegion(tileResolvedTexture, outputTexture, x, y, tileWidth, tileHeight);
                            }
                            finally
                            {
                                if (tileResolvedTextures != null)
                                {
                                    foreach (var resolvedTexture in tileResolvedTextures)
                                    {
                                        if (resolvedTexture != null)
                                            Object.DestroyImmediate(resolvedTexture);
                                    }
                                }

                                if (tileSupersampledTexture != null)
                                    Object.DestroyImmediate(tileSupersampledTexture);
                            }
                        }
                    }
                }
                finally
                {
                    if (expandedQuad != null)
                        Object.DestroyImmediate(expandedQuad);
                }
            }
        }

        static void RenderPreviewTile(
            PreviewSceneResources previewResources,
            RenderTexture renderTexture,
            Material material,
            PreviewMode previewMode,
            SgNodeExportSettings settings,
            MaterialPropertyBlock propertyBlock,
            Mesh expandedQuad,
            int tileX,
            int tileY,
            int tileWidth,
            int tileHeight)
        {
            var previewTexture = default(RenderTexture);
            var previous = RenderTexture.active;
            try
            {
                previewTexture = CreateRenderTexture(renderTexture.descriptor, "preview render tile");
                ConfigurePreviewCamera(previewResources.camera, previewTexture, previewMode);
                previewResources.camera.aspect = settings.height > 0 ? settings.width / (float)settings.height : 1f;
                previewResources.camera.ResetProjectionMatrix();
                previewResources.camera.projectionMatrix = GetTileProjectionMatrix(
                    previewResources.camera.projectionMatrix,
                    tileX,
                    tileY,
                    tileWidth,
                    tileHeight,
                    settings.width,
                    settings.height);

                RenderTexture.active = previewTexture;
                GL.Clear(true, true, Color.black);
                Graphics.Blit(Texture2D.whiteTexture, previewTexture, previewResources.checkerboardMaterial);

                EditorUtility.SetCameraAnimateMaterialsTime(previewResources.camera, (float)settings.time);
                previewResources.light0.enabled = true;
                previewResources.light0.intensity = 1.0f;
                previewResources.light0.transform.rotation = Quaternion.Euler(50f, 50f, 0f);
                previewResources.light1.enabled = true;
                previewResources.light1.intensity = 1.0f;

                var mesh = previewMode == PreviewMode.Preview3D
                    ? previewResources.sphere
                    : expandedQuad != null
                        ? expandedQuad
                        : previewResources.quad;
                var matrix = previewMode == PreviewMode.Preview3D
                    ? Matrix4x4.TRS(-mesh.bounds.center, Quaternion.identity, Vector3.one)
                    : GetFullFrameQuadMatrix(settings.width, settings.height);

                previewResources.camera.targetTexture = previewTexture;
                Graphics.DrawMesh(mesh, matrix, material, 1, previewResources.camera, 0, propertyBlock, ShadowCastingMode.Off, false, null, false);

                var previousUseSrp = Unsupported.useScriptableRenderPipeline;
                try
                {
                    Unsupported.useScriptableRenderPipeline = false;
                    previewResources.camera.Render();
                }
                finally
                {
                    Unsupported.useScriptableRenderPipeline = previousUseSrp;
                }

                if (settings.preserveVector4Alpha)
                    Graphics.Blit(previewTexture, renderTexture);
                else
                    Graphics.Blit(previewTexture, renderTexture, previewResources.blitNoAlphaMaterial);
            }
            finally
            {
                RenderTexture.active = previous;
                if (previewTexture != null)
                    Object.DestroyImmediate(previewTexture);
            }
        }

        static void ClearRenderTexture(RenderTexture renderTexture, Color color)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = renderTexture;
                GL.Clear(true, true, color);
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        static void CopyTextureRegion(RenderTexture source, RenderTexture destination, int destinationX, int destinationY, int width, int height)
        {
            if (SystemInfo.copyTextureSupport != CopyTextureSupport.None)
            {
                try
                {
                    Graphics.CopyTexture(source, 0, 0, 0, 0, width, height, destination, 0, 0, destinationX, destinationY);
                    return;
                }
                catch (Exception)
                {
                    // Fall back to a draw call on platforms that report copy support but reject this texture format.
                }
            }

            var previous = RenderTexture.active;
            var pushedMatrix = false;
            try
            {
                RenderTexture.active = destination;
                GL.PushMatrix();
                pushedMatrix = true;
                GL.LoadPixelMatrix(0f, destination.width, 0f, destination.height);
                Graphics.DrawTexture(new Rect(destinationX, destinationY, width, height), source);
            }
            finally
            {
                if (pushedMatrix)
                    GL.PopMatrix();
                RenderTexture.active = previous;
            }
        }

        static Matrix4x4 GetTileProjectionMatrix(
            Matrix4x4 projection,
            int tileX,
            int tileY,
            int tileWidth,
            int tileHeight,
            int outputWidth,
            int outputHeight)
        {
            var xMin = tileX / (float)Math.Max(1, outputWidth);
            var xMax = (tileX + tileWidth) / (float)Math.Max(1, outputWidth);
            var yMin = tileY / (float)Math.Max(1, outputHeight);
            var yMax = (tileY + tileHeight) / (float)Math.Max(1, outputHeight);
            var tileNormalizedWidth = Mathf.Max(0.0001f, xMax - xMin);
            var tileNormalizedHeight = Mathf.Max(0.0001f, yMax - yMin);

            var crop = Matrix4x4.identity;
            crop.m00 = 1f / tileNormalizedWidth;
            crop.m11 = 1f / tileNormalizedHeight;
            crop.m03 = (1f - xMin - xMax) / tileNormalizedWidth;
            crop.m13 = (1f - yMin - yMax) / tileNormalizedHeight;
            return crop * projection;
        }

        static RenderTextureDescriptor ForceNoMsaa(RenderTextureDescriptor descriptor)
        {
            descriptor.msaaSamples = 1;
            descriptor.bindMS = false;
            return descriptor;
        }

        static void ConfigurePreviewCamera(Camera camera, RenderTexture renderTexture, PreviewMode previewMode)
        {
            camera.targetTexture = renderTexture;
            if (previewMode == PreviewMode.Preview3D)
            {
                camera.transform.position = -Vector3.forward * 5f;
                camera.transform.rotation = Quaternion.identity;
                camera.orthographic = false;
            }
            else
            {
                camera.transform.position = -Vector3.forward * 2f;
                camera.transform.rotation = Quaternion.identity;
                camera.orthographicSize = 1f;
                camera.orthographic = true;
            }
        }

        static Matrix4x4 GetFullFrameQuadMatrix(RenderTexture renderTexture)
        {
            return GetFullFrameQuadMatrix(renderTexture.width, renderTexture.height);
        }

        static Matrix4x4 GetFullFrameQuadMatrix(int width, int height)
        {
            var aspect = height > 0 ? width / (float)height : 1f;
            return Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(2f * aspect, 2f, 1f));
        }

        static Mesh CreateExpandedViewportQuad(RenderTexture renderTexture)
        {
            return CreateExpandedViewportQuad(renderTexture.width, renderTexture.height);
        }

        static Mesh CreateExpandedViewportQuad(int width, int height)
        {
            var aspect = height > 0 ? width / (float)height : 1f;
            var uScale = Mathf.Max(1f, aspect);
            var vScale = Mathf.Max(1f, 1f / Mathf.Max(aspect, 0.0001f));
            var uMin = 0.5f - 0.5f * uScale;
            var uMax = 0.5f + 0.5f * uScale;
            var vMin = 0.5f - 0.5f * vScale;
            var vMax = 0.5f + 0.5f * vScale;

            var mesh = new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f),
                    new Vector3(0.5f, 0.5f, 0f),
                    new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(-0.5f, 0.5f, 0f)
                },
                uv = new[]
                {
                    new Vector2(uMin, vMin),
                    new Vector2(uMax, vMax),
                    new Vector2(uMax, vMin),
                    new Vector2(uMin, vMax)
                },
                triangles = new[] { 0, 1, 2, 1, 0, 3 }
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static PreviewMode ResolvePreviewMode(AbstractMaterialNode node)
        {
            return ResolvePreviewMode(node, new HashSet<AbstractMaterialNode>());
        }

        static PreviewMode ResolvePreviewMode(AbstractMaterialNode node, HashSet<AbstractMaterialNode> visited)
        {
            if (node == null || !visited.Add(node))
                return PreviewMode.Preview2D;

            if (node.previewMode != PreviewMode.Inherit)
                return node.previewMode;

            using (var inputSlots = PooledList<MaterialSlot>.Get())
            using (var edges = PooledList<IEdge>.Get())
            {
                node.GetInputSlots(inputSlots);
                foreach (var slot in inputSlots)
                {
                    edges.Clear();
                    node.owner.GetEdges(slot.slotReference, edges);
                    foreach (var edge in edges)
                    {
                        if (ResolvePreviewMode(edge.outputSlot.node, visited) == PreviewMode.Preview3D)
                            return PreviewMode.Preview3D;
                    }
                }
            }

            return PreviewMode.Preview2D;
        }

        static Texture2D ReadBack(RenderTexture renderTexture, SgNodeExportFormat format)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = renderTexture;

            try
            {
                var textureFormat = format == SgNodeExportFormat.EXR ? TextureFormat.RGBAFloat : TextureFormat.RGBA32;
                var readable = new Texture2D(renderTexture.width, renderTexture.height, textureFormat, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };

                readable.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0, false);
                readable.Apply(false, false);
                return readable;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        static byte[] Encode(Texture2D texture, SgNodeExportSettings settings)
        {
            switch (settings.format)
            {
                case SgNodeExportFormat.PNG:
                    return texture.EncodeToPNG();
                case SgNodeExportFormat.EXR:
                    return texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat);
                case SgNodeExportFormat.JPG:
                    return texture.EncodeToJPG(settings.jpgQuality);
                case SgNodeExportFormat.TGA:
                    return texture.EncodeToTGA();
                default:
                    throw new ArgumentOutOfRangeException(nameof(settings.format), settings.format, null);
            }
        }
    }
}
