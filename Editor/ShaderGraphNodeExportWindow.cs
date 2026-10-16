using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEngine;

namespace NachoWhy.SgNodeExporter
{
    sealed class SgNodeExportWindow : EditorWindow
    {
        const string k_PrefPrefix = "NachoWhy.SgNodeExporter.";
        const string k_LegacyPrefPrefix = "Nacho.SgNodeExporter.";
        const string k_OldLegacyPrefPrefix = "Nacho.ShaderGraphNodeExporter.";

        static readonly GUIContent s_NodeLabel = new GUIContent("Node", "The Shader Graph node selected for export.");
        static readonly GUIContent s_OutputSlotLabel = new GUIContent("Output", "The node output slot to render. Only previewable numeric and boolean outputs are listed.");
        static readonly GUIContent s_FormatLabel = new GUIContent("Format", "Output file format. EXR preserves HDR float data; PNG, JPG, and TGA are LDR exports.");
        static readonly GUIContent s_WidthLabel = new GUIContent("Width", "Export texture width in pixels.");
        static readonly GUIContent s_HeightLabel = new GUIContent("Height", "Export texture height in pixels.");
        static readonly GUIContent s_AntiAliasingLabel = new GUIContent("Anti-Aliasing", "Renders the node output at a higher internal resolution, then downsamples to the requested texture size.");
        static readonly GUIContent s_DownsampleFilterLabel = new GUIContent("Downsample", "Filter used to reduce the supersampled render to the requested texture size. Sharp keeps edges crisper; Smooth averages more evenly.");
        static readonly GUIContent s_JpgQualityLabel = new GUIContent("JPG Quality", "JPEG compression quality. Higher values preserve more detail and produce larger files.");
        static readonly GUIContent s_PreserveAlphaLabel = new GUIContent("Preserve Vector4 Alpha", "For Vector4 outputs, write the W channel into exported alpha. When off, alpha is forced opaque to match Shader Graph previews.");
        static readonly GUIContent s_StretchUvsLabel = new GUIContent("Stretch UVs", "For non-square 2D exports, stretch UVs to fill the output aspect. When off, the exporter expands the preview viewport instead.");
        static readonly GUIContent s_UseCurrentTimeLabel = new GUIContent("Use Current Time", "Use the Editor's current time when exporting Time nodes. Disable this to enter a fixed time.");
        static readonly GUIContent s_TimeLabel = new GUIContent("Time", "Manual time value passed to Shader Graph Time, Sine Time, and Cosine Time outputs.");
        static readonly GUIContent s_DeltaTimeLabel = new GUIContent("Delta Time", "Manual delta time value passed to Time node Delta Time and Smooth Delta outputs.");
        static readonly GUIContent s_FolderLabel = new GUIContent("Folder", "Destination folder. Relative paths are resolved from the Unity project root.");
        static readonly GUIContent s_BrowseButton = new GUIContent("Browse", "Choose the destination folder on disk.");
        static readonly GUIContent s_FileNameLabel = new GUIContent("File Name", "Base output file name. The selected format extension is added automatically, and existing files get a numeric suffix.");
        static readonly GUIContent s_PropertyOverridesFoldout = new GUIContent("Exposed Property Overrides", "Show or hide the exposed property overrides. Enabled overrides still apply to the export while the section is collapsed.");
        static readonly GUIContent s_PropertyOverrideToggle = new GUIContent(string.Empty, "Enable this property override for the export. When disabled, the graph's preview/default value is used.");
        static readonly GUIContent s_ExportButton = new GUIContent("Export", "Render the selected node output and write it to disk.");
        static readonly GUIContent[] s_AntiAliasingOptions =
        {
            new GUIContent("Off"),
            new GUIContent("2x"),
            new GUIContent("4x"),
            new GUIContent("8x")
        };
        static readonly int[] s_AntiAliasingValues =
        {
            (int)SgNodeExportAntiAliasing.Off,
            (int)SgNodeExportAntiAliasing.TwoX,
            (int)SgNodeExportAntiAliasing.FourX,
            (int)SgNodeExportAntiAliasing.EightX
        };
        static readonly GUIContent[] s_DownsampleFilterOptions =
        {
            new GUIContent("Sharp"),
            new GUIContent("Smooth")
        };
        static readonly int[] s_DownsampleFilterValues =
        {
            (int)SgNodeExportDownsampleFilter.Sharp,
            (int)SgNodeExportDownsampleFilter.Smooth
        };

        static GUIStyle s_BoldFoldoutStyle;

        readonly SgNodeExportSettings m_Settings = new SgNodeExportSettings();
        readonly List<SgPropertyOverride> m_PropertyOverrides = new List<SgPropertyOverride>();

        AbstractMaterialNode m_Node;
        GraphData m_Graph;
        List<MaterialSlot> m_OutputSlots = new List<MaterialSlot>();
        MaterialSlot m_OutputSlot;
        Vector2 m_Scroll;
        bool m_ShowPropertyOverrides;
        string m_Status;
        MessageType m_StatusType = MessageType.None;
        string m_AutoFileName;

        public static void Open(AbstractMaterialNode node, GraphData graph)
        {
            if (node == null || graph == null)
                return;

            var outputSlots = SgNodeExportRenderer.GetRenderableOutputs(node);
            if (outputSlots.Count == 0)
            {
                EditorUtility.DisplayDialog("Export Node Output", "The selected node has no renderable preview output.", "OK");
                return;
            }

            var window = CreateInstance<SgNodeExportWindow>();
            window.titleContent = new GUIContent("Export Node Output");
            window.Initialize(node, graph, outputSlots);
            window.minSize = new Vector2(420, 500);
            window.ShowUtility();
        }

        void Initialize(AbstractMaterialNode node, GraphData graph, List<MaterialSlot> outputSlots)
        {
            m_Node = node;
            m_Graph = graph;
            m_OutputSlots = outputSlots;
            m_OutputSlot = m_OutputSlots[0];

            m_Settings.width = GetIntPref("Width", m_Settings.width);
            m_Settings.height = GetIntPref("Height", m_Settings.height);
            m_Settings.format = GetEnumPref("Format", m_Settings.format);
            m_Settings.antiAliasing = GetEnumPref("AntiAliasing", m_Settings.antiAliasing);
            m_Settings.downsampleFilter = GetEnumPref("DownsampleFilter", m_Settings.downsampleFilter);
            m_Settings.outputFolder = GetStringPref("Folder", m_Settings.outputFolder);
            m_Settings.jpgQuality = GetIntPref("JpgQuality", m_Settings.jpgQuality);
            m_Settings.preserveVector4Alpha = GetBoolPref("PreserveAlpha", m_Settings.preserveVector4Alpha);
            m_Settings.useCurrentTime = GetBoolPref("UseCurrentTime", m_Settings.useCurrentTime);
            m_Settings.time = GetFloatPref("Time", (float)EditorApplication.timeSinceStartup);
            m_Settings.deltaTime = Mathf.Max(0f, GetFloatPref("DeltaTime", m_Settings.deltaTime));
            m_Settings.stretchUVs = GetStretchUvsPref(m_Settings.stretchUVs);
            m_AutoFileName = BuildDefaultFileName(m_OutputSlot);
            m_Settings.fileName = GetStringPref("FileName", m_AutoFileName);
            if (string.IsNullOrWhiteSpace(m_Settings.fileName))
                m_Settings.fileName = m_AutoFileName;

            m_ShowPropertyOverrides = GetBoolPref("ShowPropertyOverrides", m_ShowPropertyOverrides);
            m_PropertyOverrides.Clear();
            foreach (var property in m_Graph.properties.Where(p => p.isExposed))
                m_PropertyOverrides.Add(new SgPropertyOverride(property));
        }

        void OnGUI()
        {
            if (m_Node == null || m_Graph == null)
            {
                EditorGUILayout.HelpBox("The source Shader Graph node is no longer available.", MessageType.Error);
                return;
            }

            using (var scroll = new EditorGUILayout.ScrollViewScope(m_Scroll))
            {
                m_Scroll = scroll.scrollPosition;
                DrawSource();
                EditorGUILayout.Space(8);
                DrawOutputSettings();
                EditorGUILayout.Space(8);
                DrawPropertyOverrides();
                EditorGUILayout.Space(8);
                DrawExportButton();
            }
        }

        void DrawSource()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(s_NodeLabel, new GUIContent(m_Node.name));
            DrawOutputSlotSelector();
            EditorGUILayout.HelpBox("This exports the selected output using Shader Graph's isolated node-preview render path.", MessageType.Info);
        }

        void DrawOutputSlotSelector()
        {
            if (m_OutputSlots.Count <= 1)
            {
                EditorGUILayout.LabelField(s_OutputSlotLabel, new GUIContent(GetOutputSlotLabel(m_OutputSlot)));
                return;
            }

            var index = Mathf.Max(0, m_OutputSlots.IndexOf(m_OutputSlot));
            var labels = m_OutputSlots.Select(GetOutputSlotLabel).ToArray();
            var selectedIndex = EditorGUILayout.Popup(s_OutputSlotLabel, index, labels);
            if (selectedIndex == index)
                return;

            var oldAutoFileName = m_AutoFileName;
            m_OutputSlot = m_OutputSlots[selectedIndex];
            m_AutoFileName = BuildDefaultFileName(m_OutputSlot);
            if (string.Equals(m_Settings.fileName, oldAutoFileName, StringComparison.Ordinal))
                m_Settings.fileName = m_AutoFileName;
        }

        void DrawOutputSettings()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            m_Settings.format = (SgNodeExportFormat)EditorGUILayout.EnumPopup(s_FormatLabel, m_Settings.format);
            m_Settings.width = Mathf.Clamp(EditorGUILayout.IntField(s_WidthLabel, m_Settings.width), 1, SystemInfo.maxTextureSize);
            m_Settings.height = Mathf.Clamp(EditorGUILayout.IntField(s_HeightLabel, m_Settings.height), 1, SystemInfo.maxTextureSize);
            m_Settings.antiAliasing = (SgNodeExportAntiAliasing)EditorGUILayout.IntPopup(
                s_AntiAliasingLabel,
                (int)m_Settings.antiAliasing,
                s_AntiAliasingOptions,
                s_AntiAliasingValues);

            using (new EditorGUI.DisabledScope(m_Settings.antiAliasing == SgNodeExportAntiAliasing.Off))
            {
                m_Settings.downsampleFilter = (SgNodeExportDownsampleFilter)EditorGUILayout.IntPopup(
                    s_DownsampleFilterLabel,
                    (int)m_Settings.downsampleFilter,
                    s_DownsampleFilterOptions,
                    s_DownsampleFilterValues);
            }

            DrawAntiAliasingValidation();

            if (m_Settings.format == SgNodeExportFormat.JPG)
                m_Settings.jpgQuality = Mathf.Clamp(EditorGUILayout.IntSlider(s_JpgQualityLabel, m_Settings.jpgQuality, 1, 100), 1, 100);

            m_Settings.preserveVector4Alpha = EditorGUILayout.Toggle(s_PreserveAlphaLabel, m_Settings.preserveVector4Alpha);
            m_Settings.stretchUVs = EditorGUILayout.Toggle(s_StretchUvsLabel, m_Settings.stretchUVs);

            DrawTimeSettings();

            using (new EditorGUILayout.HorizontalScope())
            {
                m_Settings.outputFolder = EditorGUILayout.TextField(s_FolderLabel, m_Settings.outputFolder);
                if (GUILayout.Button(s_BrowseButton, GUILayout.Width(70)))
                    BrowseOutputFolder();
            }

            m_Settings.fileName = EditorGUILayout.TextField(s_FileNameLabel, m_Settings.fileName);
        }

        void DrawTimeSettings()
        {
            m_Settings.useCurrentTime = EditorGUILayout.Toggle(s_UseCurrentTimeLabel, m_Settings.useCurrentTime);
            using (new EditorGUI.DisabledScope(m_Settings.useCurrentTime))
            {
                m_Settings.time = EditorGUILayout.DoubleField(s_TimeLabel, m_Settings.time);
            }

            m_Settings.deltaTime = Mathf.Max(0f, EditorGUILayout.FloatField(s_DeltaTimeLabel, m_Settings.deltaTime));
        }

        void DrawPropertyOverrides()
        {
            s_BoldFoldoutStyle ??= new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            var showPropertyOverrides = EditorGUILayout.Foldout(m_ShowPropertyOverrides, s_PropertyOverridesFoldout, true, s_BoldFoldoutStyle);
            if (showPropertyOverrides != m_ShowPropertyOverrides)
            {
                m_ShowPropertyOverrides = showPropertyOverrides;
                EditorPrefs.SetBool(GetPrefKey("ShowPropertyOverrides"), m_ShowPropertyOverrides);
            }

            if (!m_ShowPropertyOverrides)
                return;

            if (m_PropertyOverrides.Count == 0)
            {
                EditorGUILayout.HelpBox("This graph has no exposed properties.", MessageType.None);
                return;
            }

            foreach (var propertyOverride in m_PropertyOverrides)
                DrawPropertyOverride(propertyOverride);
        }

        void DrawPropertyOverride(SgPropertyOverride propertyOverride)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!propertyOverride.isEditable))
                        propertyOverride.enabled = EditorGUILayout.Toggle(s_PropertyOverrideToggle, propertyOverride.enabled, GUILayout.Width(18));

                    EditorGUILayout.LabelField(propertyOverride.displayName, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(propertyOverride.propertyType.ToString(), GUILayout.Width(100));
                }

                EditorGUILayout.LabelField(propertyOverride.referenceName, EditorStyles.miniLabel);

                if (!propertyOverride.isEditable)
                {
                    EditorGUILayout.HelpBox("This property type is not editable in the exporter.", MessageType.None);
                    return;
                }

                using (new EditorGUI.DisabledScope(!propertyOverride.enabled))
                {
                    var valueLabel = GetPropertyValueLabel(propertyOverride);
                    switch (propertyOverride.propertyType)
                    {
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Float:
                            propertyOverride.floatValue = EditorGUILayout.FloatField(valueLabel, propertyOverride.floatValue);
                            break;
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Boolean:
                            propertyOverride.boolValue = EditorGUILayout.Toggle(valueLabel, propertyOverride.boolValue);
                            break;
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Color:
                            propertyOverride.colorValue = EditorGUILayout.ColorField(valueLabel, propertyOverride.colorValue);
                            break;
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Vector2:
                        {
                            var value = EditorGUILayout.Vector2Field(valueLabel, new Vector2(propertyOverride.vectorValue.x, propertyOverride.vectorValue.y));
                            propertyOverride.vectorValue = new Vector4(value.x, value.y, propertyOverride.vectorValue.z, propertyOverride.vectorValue.w);
                            break;
                        }
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Vector3:
                        {
                            var value = EditorGUILayout.Vector3Field(valueLabel, new Vector3(propertyOverride.vectorValue.x, propertyOverride.vectorValue.y, propertyOverride.vectorValue.z));
                            propertyOverride.vectorValue = new Vector4(value.x, value.y, value.z, propertyOverride.vectorValue.w);
                            break;
                        }
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Vector4:
                            propertyOverride.vectorValue = EditorGUILayout.Vector4Field(valueLabel, propertyOverride.vectorValue);
                            break;
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Matrix2:
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Matrix3:
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Matrix4:
                            DrawMatrixField(propertyOverride);
                            break;
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Texture2D:
                            propertyOverride.textureValue = (Texture)EditorGUILayout.ObjectField(valueLabel, propertyOverride.textureValue, typeof(Texture2D), false);
                            break;
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Texture2DArray:
                            propertyOverride.textureValue = (Texture)EditorGUILayout.ObjectField(valueLabel, propertyOverride.textureValue, typeof(Texture2DArray), false);
                            break;
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Texture3D:
                            propertyOverride.textureValue = (Texture)EditorGUILayout.ObjectField(valueLabel, propertyOverride.textureValue, typeof(Texture3D), false);
                            break;
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Cubemap:
                            propertyOverride.cubemapValue = (Cubemap)EditorGUILayout.ObjectField(valueLabel, propertyOverride.cubemapValue, typeof(Cubemap), false);
                            break;
                        case UnityEditor.ShaderGraph.Internal.PropertyType.Gradient:
                            propertyOverride.gradientValue ??= new Gradient();
                            propertyOverride.gradientValue = EditorGUILayout.GradientField(valueLabel, propertyOverride.gradientValue);
                            break;
                    }
                }
            }
        }

        static void DrawMatrixField(SgPropertyOverride propertyOverride)
        {
            var matrix = propertyOverride.matrixValue;
            matrix.SetRow(0, EditorGUILayout.Vector4Field(GetMatrixRowLabel(propertyOverride, 0), matrix.GetRow(0)));
            matrix.SetRow(1, EditorGUILayout.Vector4Field(GetMatrixRowLabel(propertyOverride, 1), matrix.GetRow(1)));
            matrix.SetRow(2, EditorGUILayout.Vector4Field(GetMatrixRowLabel(propertyOverride, 2), matrix.GetRow(2)));
            matrix.SetRow(3, EditorGUILayout.Vector4Field(GetMatrixRowLabel(propertyOverride, 3), matrix.GetRow(3)));
            propertyOverride.matrixValue = matrix;
        }

        void DrawExportButton()
        {
            if (m_StatusType != MessageType.None && !string.IsNullOrEmpty(m_Status))
                EditorGUILayout.HelpBox(m_Status, m_StatusType);

            var hasInvalidRenderSize = !SgNodeExportRenderer.TryGetSupersampledSize(m_Settings, out _, out _, out _);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Settings.outputFolder) || string.IsNullOrWhiteSpace(m_Settings.fileName) || hasInvalidRenderSize))
            {
                if (GUILayout.Button(s_ExportButton, GUILayout.Height(32)))
                    Export();
            }
        }

        void DrawAntiAliasingValidation()
        {
            if (!SgNodeExportRenderer.TryGetSupersampledSize(m_Settings, out var renderWidth, out var renderHeight, out var error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                return;
            }

            var usesTiledRendering = SgNodeExportRenderer.UsesTiledRendering(m_Settings, out _, out _);
            if (m_Settings.antiAliasing == SgNodeExportAntiAliasing.Off && !usesTiledRendering)
                return;

            var estimatedBytes = SgNodeExportRenderer.EstimateRenderMemoryBytes(m_Settings);
            const long warningThreshold = 512L * 1024L * 1024L;
            if (estimatedBytes < warningThreshold && !usesTiledRendering)
                return;

            var scale = SgNodeExportRenderer.GetAntiAliasingScale(m_Settings.antiAliasing);
            var renderDescription = scale == 1 ? "This export" : $"Anti-Aliasing {scale}x";
            var message = usesTiledRendering
                ? $"{renderDescription} reaches a {renderWidth}x{renderHeight} internal render size, so it will be rendered in bounded tiles. Estimated temporary render memory is {FormatBytes(estimatedBytes)}."
                : $"{renderDescription} renders internally at {renderWidth}x{renderHeight}. Estimated temporary render memory is {FormatBytes(estimatedBytes)}.";
            EditorGUILayout.HelpBox(
                message,
                estimatedBytes >= warningThreshold ? MessageType.Warning : MessageType.Info);
        }

        void BrowseOutputFolder()
        {
            var currentFolder = ResolveFolderForPanel(m_Settings.outputFolder);
            var selected = EditorUtility.OpenFolderPanel("Export Folder", currentFolder, string.Empty);
            if (string.IsNullOrEmpty(selected))
                return;

            m_Settings.outputFolder = ProjectPathUtility.ToProjectRelativePath(selected) ?? selected;
            GUI.FocusControl(null);
        }

        void Export()
        {
            try
            {
                if (m_Settings.useCurrentTime)
                    m_Settings.time = EditorApplication.timeSinceStartup;
                SavePrefs();

                var path = BuildOutputPath();
                var result = SgNodeExportRenderer.Export(m_Node, m_OutputSlot, m_Settings, m_PropertyOverrides, path);

                AssetDatabase.Refresh();
                var assetPath = ProjectPathUtility.ToProjectRelativePath(result.path);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (texture != null)
                    {
                        Selection.activeObject = texture;
                        EditorGUIUtility.PingObject(texture);
                    }
                }

                m_Status = $"Exported {result.width}x{result.height} {m_Settings.format} to {result.path}";
                m_StatusType = MessageType.Info;
                Debug.Log(m_Status);
            }
            catch (Exception exception)
            {
                m_Status = exception.Message;
                m_StatusType = MessageType.Error;
                Debug.LogException(exception);
            }
        }

        void SavePrefs()
        {
            EditorPrefs.SetInt(GetPrefKey("Width"), m_Settings.width);
            EditorPrefs.SetInt(GetPrefKey("Height"), m_Settings.height);
            EditorPrefs.SetInt(GetPrefKey("Format"), (int)m_Settings.format);
            EditorPrefs.SetInt(GetPrefKey("AntiAliasing"), (int)m_Settings.antiAliasing);
            EditorPrefs.SetInt(GetPrefKey("DownsampleFilter"), (int)m_Settings.downsampleFilter);
            EditorPrefs.SetString(GetPrefKey("Folder"), m_Settings.outputFolder);
            EditorPrefs.SetString(GetPrefKey("FileName"), m_Settings.fileName);
            EditorPrefs.SetInt(GetPrefKey("JpgQuality"), m_Settings.jpgQuality);
            EditorPrefs.SetBool(GetPrefKey("PreserveAlpha"), m_Settings.preserveVector4Alpha);
            EditorPrefs.SetBool(GetPrefKey("UseCurrentTime"), m_Settings.useCurrentTime);
            EditorPrefs.SetFloat(GetPrefKey("Time"), (float)m_Settings.time);
            EditorPrefs.SetFloat(GetPrefKey("DeltaTime"), m_Settings.deltaTime);
            EditorPrefs.SetBool(GetPrefKey("StretchUVs"), m_Settings.stretchUVs);
        }

        string BuildOutputPath()
        {
            var extension = SgNodeExportRenderer.GetExtension(m_Settings.format);
            var cleanName = SanitizeFileName(Path.GetFileNameWithoutExtension(m_Settings.fileName));
            if (string.IsNullOrWhiteSpace(cleanName))
                cleanName = "NodeOutput";

            var folder = m_Settings.outputFolder.Trim();
            var combined = Path.Combine(folder, cleanName + extension);
            if (!Path.IsPathRooted(combined))
                combined = Path.Combine(ProjectPathUtility.ProjectRoot, combined);

            return ProjectPathUtility.GetUniquePath(Path.GetFullPath(combined));
        }

        static string ResolveFolderForPanel(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return Application.dataPath;

            return Path.IsPathRooted(folder)
                ? folder
                : Path.Combine(ProjectPathUtility.ProjectRoot, folder);
        }

        static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars).Trim('_');
        }

        string BuildDefaultFileName(MaterialSlot outputSlot)
        {
            var graphName = SanitizeFileName(m_Graph.path);
            var nodeName = SanitizeFileName(m_Node.name);
            var slotName = SanitizeFileName(outputSlot?.displayName);
            var combined = string.IsNullOrWhiteSpace(slotName)
                ? $"{graphName}_{nodeName}"
                : $"{graphName}_{nodeName}_{slotName}";
            return ObjectNames.NicifyVariableName(combined).Replace(" ", "_");
        }

        static string GetOutputSlotLabel(MaterialSlot slot)
        {
            if (slot == null)
                return "(Missing)";

            return $"{slot.displayName} ({slot.concreteValueType})";
        }

        static GUIContent GetPropertyValueLabel(SgPropertyOverride propertyOverride)
        {
            return new GUIContent("Value", $"Override value for '{propertyOverride.displayName}'. Disable the checkbox to use the graph preview/default value.");
        }

        static GUIContent GetMatrixRowLabel(SgPropertyOverride propertyOverride, int row)
        {
            return new GUIContent($"Row {row}", $"Matrix row {row} for '{propertyOverride.displayName}'. Disable the checkbox to use the graph preview/default value.");
        }

        static string GetPrefKey(string name)
        {
            return k_PrefPrefix + name;
        }

        static string GetLegacyPrefKey(string name)
        {
            return k_LegacyPrefPrefix + name;
        }

        static string GetOldLegacyPrefKey(string name)
        {
            return k_OldLegacyPrefPrefix + name;
        }

        static int GetIntPref(string name, int defaultValue)
        {
            var key = GetPrefKey(name);
            if (EditorPrefs.HasKey(key))
                return EditorPrefs.GetInt(key, defaultValue);

            var legacyKey = GetLegacyPrefKey(name);
            if (EditorPrefs.HasKey(legacyKey))
                return EditorPrefs.GetInt(legacyKey, defaultValue);

            return EditorPrefs.GetInt(GetOldLegacyPrefKey(name), defaultValue);
        }

        static TEnum GetEnumPref<TEnum>(string name, TEnum defaultValue) where TEnum : struct
        {
            var value = GetIntPref(name, Convert.ToInt32(defaultValue));
            var candidate = (TEnum)Enum.ToObject(typeof(TEnum), value);
            return Enum.IsDefined(typeof(TEnum), candidate) ? candidate : defaultValue;
        }

        static float GetFloatPref(string name, float defaultValue)
        {
            var key = GetPrefKey(name);
            if (EditorPrefs.HasKey(key))
                return EditorPrefs.GetFloat(key, defaultValue);

            var legacyKey = GetLegacyPrefKey(name);
            if (EditorPrefs.HasKey(legacyKey))
                return EditorPrefs.GetFloat(legacyKey, defaultValue);

            return EditorPrefs.GetFloat(GetOldLegacyPrefKey(name), defaultValue);
        }

        static bool GetBoolPref(string name, bool defaultValue)
        {
            var key = GetPrefKey(name);
            if (EditorPrefs.HasKey(key))
                return EditorPrefs.GetBool(key, defaultValue);

            var legacyKey = GetLegacyPrefKey(name);
            if (EditorPrefs.HasKey(legacyKey))
                return EditorPrefs.GetBool(legacyKey, defaultValue);

            return EditorPrefs.GetBool(GetOldLegacyPrefKey(name), defaultValue);
        }

        static bool GetStretchUvsPref(bool defaultValue)
        {
            const string currentName = "StretchUVs";
            const string legacyInverseName = "ExpandViewport";

            var key = GetPrefKey(currentName);
            if (EditorPrefs.HasKey(key))
                return EditorPrefs.GetBool(key, defaultValue);

            var legacyKey = GetLegacyPrefKey(currentName);
            if (EditorPrefs.HasKey(legacyKey))
                return EditorPrefs.GetBool(legacyKey, defaultValue);

            var oldLegacyKey = GetOldLegacyPrefKey(currentName);
            if (EditorPrefs.HasKey(oldLegacyKey))
                return EditorPrefs.GetBool(oldLegacyKey, defaultValue);

            key = GetPrefKey(legacyInverseName);
            if (EditorPrefs.HasKey(key))
                return !EditorPrefs.GetBool(key, !defaultValue);

            legacyKey = GetLegacyPrefKey(legacyInverseName);
            if (EditorPrefs.HasKey(legacyKey))
                return !EditorPrefs.GetBool(legacyKey, !defaultValue);

            oldLegacyKey = GetOldLegacyPrefKey(legacyInverseName);
            if (EditorPrefs.HasKey(oldLegacyKey))
                return !EditorPrefs.GetBool(oldLegacyKey, !defaultValue);

            return defaultValue;
        }

        static string GetStringPref(string name, string defaultValue)
        {
            var key = GetPrefKey(name);
            if (EditorPrefs.HasKey(key))
                return EditorPrefs.GetString(key, defaultValue);

            var legacyKey = GetLegacyPrefKey(name);
            if (EditorPrefs.HasKey(legacyKey))
                return EditorPrefs.GetString(legacyKey, defaultValue);

            return EditorPrefs.GetString(GetOldLegacyPrefKey(name), defaultValue);
        }

        static string FormatBytes(long bytes)
        {
            const double gib = 1024d * 1024d * 1024d;
            const double mib = 1024d * 1024d;
            return bytes >= (long)gib
                ? $"{bytes / gib:0.##} GB"
                : $"{bytes / mib:0.#} MB";
        }
    }
}
