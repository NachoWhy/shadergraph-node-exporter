using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEngine;
using UnityEngine.UIElements;

namespace NachoWhy.SgNodeExporter
{
    [InitializeOnLoad]
    static class SgNodeExportMenu
    {
        const string k_MenuPath = "Export Node Output...";

        static readonly Dictionary<MaterialGraphView, ContextualMenuManipulator> s_AttachedGraphViews = new Dictionary<MaterialGraphView, ContextualMenuManipulator>();
        static double s_NextScanTime;

        static SgNodeExportMenu()
        {
            EditorApplication.update += ScanForShaderGraphWindows;
        }

        static void ScanForShaderGraphWindows()
        {
            if (EditorApplication.timeSinceStartup < s_NextScanTime)
                return;

            s_NextScanTime = EditorApplication.timeSinceStartup + 1.0;
            PruneDetachedGraphViews();

            foreach (var window in Resources.FindObjectsOfTypeAll<MaterialGraphEditWindow>())
            {
                var graphView = window?.graphEditorView?.graphView;
                if (graphView == null || s_AttachedGraphViews.ContainsKey(graphView))
                    continue;

                var manipulator = new ContextualMenuManipulator(BuildContextualMenu);
                graphView.AddManipulator(manipulator);
                s_AttachedGraphViews.Add(graphView, manipulator);
            }
        }

        static void PruneDetachedGraphViews()
        {
            var detachedGraphViews = s_AttachedGraphViews.Keys
                .Where(view => view == null || view.panel == null)
                .ToList();

            foreach (var graphView in detachedGraphViews)
            {
                if (graphView != null && s_AttachedGraphViews.TryGetValue(graphView, out var manipulator))
                    graphView.RemoveManipulator(manipulator);

                s_AttachedGraphViews.Remove(graphView);
            }
        }

        static void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            var graphView = evt.currentTarget as MaterialGraphView
                ?? (evt.target as VisualElement)?.GetFirstAncestorOfType<MaterialGraphView>();
            if (graphView == null)
                return;

            var nodeView = FindNodeView(evt, graphView);
            var node = nodeView?.node;
            var canExport = node != null && SgNodeExportRenderer.GetRenderableOutputs(node).Count > 0;

            evt.menu.AppendSeparator();
            evt.menu.AppendAction(
                k_MenuPath,
                _ => SgNodeExportWindow.Open(node, graphView.graph),
                _ => canExport ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        static IShaderNodeView FindNodeView(ContextualMenuPopulateEvent evt, MaterialGraphView graphView)
        {
            var targetElement = evt.target as VisualElement;
            var targetNode = targetElement?.GetFirstAncestorOfType<MaterialNodeView>();
            if (targetNode != null)
                return targetNode;

            return graphView.selection.OfType<IShaderNodeView>().FirstOrDefault();
        }
    }
}
