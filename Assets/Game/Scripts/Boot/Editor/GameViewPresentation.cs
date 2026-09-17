using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    internal static class GameViewPresentation
    {
        [MenuItem("Tools/Poké Lab/Repair/Hide Game View Gizmos")]
        public static void HideGizmos()
        {
            var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (type == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var window in Resources.FindObjectsOfTypeAll(type))
            {
                var property = type.GetProperty("drawGizmos", flags);
                if (property?.CanWrite == true) property.SetValue(window, false);
                var field = type.GetField("m_Gizmos", flags);
                if (field?.FieldType == typeof(bool)) field.SetValue(window, false);
                (window as EditorWindow)?.Repaint();
            }
        }
    }
}
