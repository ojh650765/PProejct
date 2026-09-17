using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    public static class ReleaseBuildBridge
    {
        [Serializable] private sealed class Result { public string state, message; }
        [MenuItem("Tools/Poké Lab/Build/Release WebGL")]
        public static void Build()
        {
            Directory.CreateDirectory("Temp");
            Write("running", "Building this workspace for GitHub Pages");
            try
            {
                GameBuilder.BuildWebGL();
                Write(GameBuilder.LastWebGLBuildSucceeded ? "passed" : "failed", "See Unity build log");
            }
            catch (Exception exception)
            {
                Write("failed", exception.Message);
                Debug.LogException(exception);
            }
        }
        private static void Write(string state, string message) => File.WriteAllText(
            "Temp/release_build.json", JsonUtility.ToJson(new Result { state = state, message = message }));
    }
}
