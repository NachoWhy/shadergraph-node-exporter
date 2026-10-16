using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;

namespace NachoWhy.SgNodeExporter
{
    enum SgNodeExportFormat
    {
        PNG,
        EXR,
        JPG,
        TGA
    }

    enum SgNodeExportAntiAliasing
    {
        Off = 1,
        TwoX = 2,
        FourX = 4,
        EightX = 8
    }

    enum SgNodeExportDownsampleFilter
    {
        Sharp,
        Smooth
    }

    sealed class SgNodeExportSettings
    {
        public int width = 1024;
        public int height = 1024;
        public SgNodeExportFormat format = SgNodeExportFormat.PNG;
        public SgNodeExportAntiAliasing antiAliasing = SgNodeExportAntiAliasing.TwoX;
        public SgNodeExportDownsampleFilter downsampleFilter = SgNodeExportDownsampleFilter.Smooth;
        public string outputFolder = "Assets/ShaderGraphNodeExports";
        public string fileName = "NodeOutput";
        public int jpgQuality = 95;
        public bool preserveVector4Alpha;
        public bool useCurrentTime = true;
        public float deltaTime = 1f / 60f;
        public bool stretchUVs = true;
        public double time;
    }

    sealed class SgPropertyOverride
    {
        public readonly AbstractShaderProperty property;
        public readonly PropertyType propertyType;

        public bool enabled;
        public float floatValue;
        public bool boolValue;
        public Color colorValue;
        public bool isHdrColor;
        public Vector4 vectorValue;
        public Matrix4x4 matrixValue;
        public Texture textureValue;
        public Cubemap cubemapValue;
        public Gradient gradientValue;

        public string displayName => string.IsNullOrEmpty(property.displayName) ? property.referenceName : property.displayName;
        public string referenceName => property.referenceName;

        public SgPropertyOverride(AbstractShaderProperty property)
        {
            this.property = property;
            propertyType = property.propertyType;

            var previewProperty = property.GetPreviewMaterialProperty();
            switch (propertyType)
            {
                case PropertyType.Float:
                    floatValue = previewProperty.floatValue;
                    break;
                case PropertyType.Boolean:
                    boolValue = previewProperty.booleanValue;
                    break;
                case PropertyType.Color:
                    // Shader Graph previews colors as a Vector4 already converted to the active
                    // color space, so take the authored color from the property instead.
                    if (property is ColorShaderProperty colorProperty)
                    {
                        colorValue = colorProperty.value;
                        isHdrColor = colorProperty.colorMode == ColorMode.HDR;
                    }
                    break;
                case PropertyType.Vector2:
                case PropertyType.Vector3:
                case PropertyType.Vector4:
                    vectorValue = previewProperty.vector4Value;
                    break;
                case PropertyType.Matrix2:
                case PropertyType.Matrix3:
                case PropertyType.Matrix4:
                    matrixValue = previewProperty.matrixValue;
                    break;
                case PropertyType.Texture2D:
                case PropertyType.Texture2DArray:
                case PropertyType.Texture3D:
                    textureValue = previewProperty.textureValue;
                    break;
                case PropertyType.Cubemap:
                    cubemapValue = previewProperty.cubemapValue;
                    break;
                case PropertyType.Gradient:
                    gradientValue = previewProperty.gradientValue;
                    break;
            }
        }

        public bool isEditable
        {
            get
            {
                switch (propertyType)
                {
                    case PropertyType.Color:
                    case PropertyType.Texture2D:
                    case PropertyType.Texture2DArray:
                    case PropertyType.Texture3D:
                    case PropertyType.Cubemap:
                    case PropertyType.Gradient:
                    case PropertyType.Boolean:
                    case PropertyType.Float:
                    case PropertyType.Vector2:
                    case PropertyType.Vector3:
                    case PropertyType.Vector4:
                    case PropertyType.Matrix2:
                    case PropertyType.Matrix3:
                    case PropertyType.Matrix4:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public PreviewProperty ToPreviewProperty()
        {
            var previewProperty = property.GetPreviewMaterialProperty();
            if (!enabled || !isEditable)
                return previewProperty;

            switch (propertyType)
            {
                case PropertyType.Float:
                    previewProperty.floatValue = floatValue;
                    break;
                case PropertyType.Boolean:
                    previewProperty.booleanValue = boolValue;
                    break;
                case PropertyType.Color:
                    if (previewProperty.propType == PropertyType.Color)
                        previewProperty.colorValue = colorValue;
                    else
                        previewProperty.vector4Value = isHdrColor || PlayerSettings.colorSpace != ColorSpace.Linear ? colorValue : colorValue.linear;
                    break;
                case PropertyType.Vector2:
                case PropertyType.Vector3:
                case PropertyType.Vector4:
                    previewProperty.vector4Value = vectorValue;
                    break;
                case PropertyType.Matrix2:
                case PropertyType.Matrix3:
                case PropertyType.Matrix4:
                    previewProperty.matrixValue = matrixValue;
                    break;
                case PropertyType.Texture2D:
                case PropertyType.Texture2DArray:
                case PropertyType.Texture3D:
                    previewProperty.textureValue = textureValue;
                    break;
                case PropertyType.Cubemap:
                    previewProperty.cubemapValue = cubemapValue;
                    break;
                case PropertyType.Gradient:
                    previewProperty.gradientValue = gradientValue;
                    break;
            }

            return previewProperty;
        }
    }
}
